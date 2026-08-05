namespace FanControl.MinisforumUM780XTX;

/// <summary>The mutable part of one normal CPU curve row.</summary>
internal readonly record struct F7bsdCpuRowState(byte Base, byte Slope);

/// <summary>One complete decoded CPU curve row.</summary>
internal readonly record struct F7bsdCpuPolicyRow(
    byte Base,
    byte Upper,
    byte Lower,
    byte Slope)
{
    internal F7bsdCpuRowState State => new(Base, Slope);
}

/// <summary>A single-byte step in a transition-bounded normal-row update.</summary>
internal readonly record struct F7bsdCpuTransitionStep(
    int RowIndex,
    EcWrite Write,
    F7bsdCpuRowState ResultingState);

/// <summary>
/// Compiles a requested CPU target into the immutable B1 temperature bands.
/// Fan Control owns the low-temperature request while an EC-resident thermal
/// envelope begins with a proven sustainable code 10 above 66 C and reaches
/// code 51 at 93 C. The separate critical row remains exact B1 and takes over
/// at 94 C.
/// </summary>
internal static class F7bsdCpuPolicy
{
    internal const byte Selector = 0xb1;
    internal const int NormalRowCount = 7;
    internal const int TotalRowCount = 8;
    internal const int DirectMaximumWritesPerRow = 5;
    internal const int MaximumWritesPerRow = DirectMaximumWritesPerRow * 2;
    internal const int MaximumWritesPerTransition =
        NormalRowCount * MaximumWritesPerRow;
    internal const int CriticalTemperatureC = 94;

    private const int SlopeValueCount = 0x100;
    private const int StateCount = (F7bsdProfile.MaximumCode + 1) * SlopeValueCount;
    private const int Unvisited = -2;
    internal const int MaximumDirectPlannerNeighborChecks = StateCount * 2;

    private static readonly F7bsdCpuPolicyRow[] BalancedRows =
    [
        new(0, 25, 0, 0),
        new(16, 45, 25, 10),
        new(18, 54, 45, 33),
        new(21, 66, 54, 58),
        new(28, 76, 66, 60),
        new(32, 88, 76, 16),
        new(33, 93, 88, 200),
        new(51, 100, 93, 0),
    ];

    private static readonly F7bsdCpuRowState[][] CompiledTargets =
        BuildCompiledTargets();

    /// <summary>Returns one immutable value from the exact OEM B1 table.</summary>
    internal static F7bsdCpuPolicyRow GetB1Row(int rowIndex)
    {
        AssertTotalRowIndex(rowIndex);
        return BalancedRows[rowIndex];
    }

    /// <summary>Returns a copy of the exact seven-row mutable B1 baseline.</summary>
    internal static F7bsdCpuRowState[] GetB1MutableStates() =>
        BalancedRows[..NormalRowCount].Select(row => row.State).ToArray();

    /// <summary>
    /// Compiles a requested code as a low-temperature target. The result also
    /// dominates the monotone EC thermal envelope at every temperature in each
    /// immutable B1 band. Code 0 is a genuine cool-temperature stop request;
    /// all 52 requested codes compile to distinct physical policies.
    /// </summary>
    internal static F7bsdCpuRowState[] CompileTarget(byte requestedCode)
    {
        AssertCode(requestedCode, nameof(requestedCode));
        return (F7bsdCpuRowState[])CompiledTargets[requestedCode].Clone();
    }

    /// <summary>
    /// Returns a complete eight-row target table for inspection. Temperature
    /// bands and the critical row are always the exact B1 values.
    /// </summary>
    internal static F7bsdCpuPolicyRow[] CompileTargetRows(byte requestedCode)
    {
        F7bsdCpuRowState[] states = CompileTarget(requestedCode);
        F7bsdCpuPolicyRow[] rows = new F7bsdCpuPolicyRow[TotalRowCount];
        for (int row = 0; row < NormalRowCount; row++)
        {
            F7bsdCpuPolicyRow stock = BalancedRows[row];
            rows[row] = new(
                states[row].Base,
                stock.Upper,
                stock.Lower,
                states[row].Slope);
        }
        rows[^1] = BalancedRows[^1];
        return rows;
    }

    /// <summary>Decodes the base-then-slope byte layout used by the backend.</summary>
    internal static F7bsdCpuRowState[] FromMutableBytes(ReadOnlySpan<byte> values)
    {
        if (values.Length != NormalRowCount * 2)
        {
            throw new ArgumentException(
                "A CPU mutable table must contain seven bases and seven slopes.",
                nameof(values));
        }

        F7bsdCpuRowState[] states = new F7bsdCpuRowState[NormalRowCount];
        for (int row = 0; row < NormalRowCount; row++)
        {
            states[row] = new(values[row], values[NormalRowCount + row]);
        }
        return states;
    }

    /// <summary>Encodes seven row states as bases followed by slopes.</summary>
    internal static byte[] ToMutableBytes(ReadOnlySpan<F7bsdCpuRowState> states)
    {
        AssertTableLength(states, nameof(states));
        byte[] values = new byte[NormalRowCount * 2];
        for (int row = 0; row < NormalRowCount; row++)
        {
            values[row] = states[row].Base;
            values[NormalRowCount + row] = states[row].Slope;
        }
        return values;
    }

    /// <summary>Evaluates a normal row at an integer temperature.</summary>
    internal static int TargetAt(
        int rowIndex,
        F7bsdCpuRowState state,
        int temperatureC)
    {
        AssertNormalRowIndex(rowIndex);
        F7bsdCpuPolicyRow band = BalancedRows[rowIndex];
        if (temperatureC < band.Lower || temperatureC > band.Upper)
        {
            throw new ArgumentOutOfRangeException(
                nameof(temperatureC),
                $"Temperature must be within B1 row {rowIndex}'s " +
                $"{band.Lower}..{band.Upper} C band.");
        }

        return state.Base +
            ((state.Slope * (temperatureC - band.Lower)) / 100);
    }

    /// <summary>
    /// Evaluates the complete table on the EC's heating or cooling hysteresis
    /// path. Temperatures at or above 94 C use the untouched critical row.
    /// </summary>
    internal static int EvaluateTable(
        ReadOnlySpan<F7bsdCpuRowState> states,
        int temperatureC,
        bool cooling)
    {
        AssertTableLength(states, nameof(states));
        if (temperatureC < 0 || temperatureC > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(temperatureC));
        }
        if (temperatureC >= CriticalTemperatureC)
        {
            return F7bsdProfile.MaximumCode;
        }

        int row;
        if (cooling)
        {
            row = NormalRowCount - 1;
            while (row > 0 && temperatureC < BalancedRows[row].Lower)
            {
                row--;
            }
        }
        else
        {
            row = 0;
            while (row < NormalRowCount - 1 &&
                temperatureC > BalancedRows[row].Upper)
            {
                row++;
            }
        }
        return TargetAt(row, states[row], temperatureC);
    }

    /// <summary>
    /// Returns the EC-resident thermal minimum in target-code units. It permits
    /// a cool stop through 66 C, requires the proven sustainable code 10 from
    /// 67 through 74 C, then follows the validated high-temperature tail to
    /// 5100 RPM at 93 C.
    /// </summary>
    internal static int ThermalFloorCode(int temperatureC)
    {
        if (temperatureC < 0 || temperatureC >= CriticalTemperatureC)
        {
            throw new ArgumentOutOfRangeException(nameof(temperatureC));
        }
        if (temperatureC <= 66)
        {
            return 0;
        }
        if (temperatureC <= 74)
        {
            return 10;
        }
        if (temperatureC <= 76)
        {
            return (3 * (temperatureC - 66)) / 2;
        }
        if (temperatureC <= 82)
        {
            int rpm = 1_000 + ((temperatureC - 74) * 250);
            return (rpm + 99) / 100;
        }
        if (temperatureC <= 88)
        {
            int sixthsOfRpm = (3_000 * 6) + ((temperatureC - 82) * 1_000);
            return (sixthsOfRpm + 599) / 600;
        }

        int highRpm = Math.Min(
            5_100,
            4_000 + ((temperatureC - 88) * 220));
        return (highRpm + 99) / 100;
    }

    /// <summary>
    /// Returns the absolute floor accepted for a transition intermediate.
    /// Exact B1 is trusted, while generated endpoints dominate the stronger
    /// thermal envelope; their pointwise minimum is safe for transitions.
    /// </summary>
    internal static int TransitionFloorAt(int rowIndex, int temperatureC)
    {
        AssertNormalRowIndex(rowIndex);
        return Math.Min(
            TargetAt(rowIndex, BalancedRows[rowIndex].State, temperatureC),
            ThermalFloorCode(temperatureC));
    }

    /// <summary>
    /// Tests whether a generated endpoint dominates the requested target and
    /// thermal envelope throughout one complete B1 band.
    /// </summary>
    internal static bool DominatesThermalEnvelopeAndRequest(
        int rowIndex,
        F7bsdCpuRowState state,
        byte requestedCode)
    {
        AssertCode(requestedCode, nameof(requestedCode));
        AssertNormalRowIndex(rowIndex);
        F7bsdCpuPolicyRow band = BalancedRows[rowIndex];
        for (int temperature = band.Lower;
            temperature <= band.Upper;
            temperature++)
        {
            int target = TargetAt(rowIndex, state, temperature);
            if (target < requestedCode ||
                target < ThermalFloorCode(temperature) ||
                target > F7bsdProfile.MaximumCode)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Tests whether a row remains within the conservative B1-or-thermal
    /// transition floor and code-51 ceiling.
    /// </summary>
    internal static bool IsTransitionBounded(int rowIndex, F7bsdCpuRowState state)
    {
        AssertNormalRowIndex(rowIndex);
        F7bsdCpuPolicyRow band = BalancedRows[rowIndex];
        for (int temperature = band.Lower;
            temperature <= band.Upper;
            temperature++)
        {
            int target = TargetAt(rowIndex, state, temperature);
            if (target < TransitionFloorAt(rowIndex, temperature) ||
                target > F7bsdProfile.MaximumCode)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Plans one row's deterministic transition. A direct path is limited to
    /// five writes and code 51. If that state graph is disconnected, the row
    /// routes through its exact B1 state, with at most ten writes total.
    /// </summary>
    internal static F7bsdCpuRowState[] PlanRowTransition(
        int rowIndex,
        F7bsdCpuRowState from,
        F7bsdCpuRowState to)
    {
        AssertNormalRowIndex(rowIndex);
        if (!IsTransitionBounded(rowIndex, from))
        {
            throw new ArgumentException(
                $"CPU row {rowIndex} start state is outside transition bounds.",
                nameof(from));
        }
        if (!IsTransitionBounded(rowIndex, to))
        {
            throw new ArgumentException(
                $"CPU row {rowIndex} destination state is outside transition bounds.",
                nameof(to));
        }
        if (from == to)
        {
            return [];
        }

        if (TryPlanDirectRowTransition(rowIndex, from, to, out var direct))
        {
            return direct;
        }

        F7bsdCpuRowState anchor = BalancedRows[rowIndex].State;
        if (!TryPlanDirectRowTransition(rowIndex, from, anchor, out var toAnchor) ||
            !TryPlanDirectRowTransition(rowIndex, anchor, to, out var fromAnchor))
        {
            throw new InvalidOperationException(
                $"No bounded code-51 CPU row {rowIndex} transition exists, " +
                "including the exact-B1 anchor fallback.");
        }

        F7bsdCpuRowState[] result = [.. toAnchor, .. fromAnchor];
        if (result.Length > MaximumWritesPerRow)
        {
            throw new InvalidOperationException(
                $"CPU row {rowIndex} transition exceeded " +
                $"{MaximumWritesPerRow} writes.");
        }
        return result;
    }

    /// <summary>
    /// Attempts a direct row transition without the B1 anchor fallback. This
    /// helper is exposed for exhaustive reachability and traffic-bound tests.
    /// </summary>
    internal static bool TryPlanDirectRowTransition(
        int rowIndex,
        F7bsdCpuRowState from,
        F7bsdCpuRowState to,
        out F7bsdCpuRowState[] path) => TryPlanDirectRowTransition(
            rowIndex,
            from,
            to,
            out path,
            out _);

    /// <summary>
    /// Test-visible overload that reports the deterministic search budget.
    /// Each safe base/slope row or column is expanded at most once.
    /// </summary>
    internal static bool TryPlanDirectRowTransition(
        int rowIndex,
        F7bsdCpuRowState from,
        F7bsdCpuRowState to,
        out F7bsdCpuRowState[] path,
        out int neighborChecks)
    {
        AssertNormalRowIndex(rowIndex);
        path = [];
        neighborChecks = 0;
        if (!IsTransitionBounded(rowIndex, from) ||
            !IsTransitionBounded(rowIndex, to))
        {
            return false;
        }
        if (from == to)
        {
            return true;
        }

        // Zero means unknown, +1 allowed, and -1 rejected. Computing safety
        // lazily avoids enumerating the entire 13,312-state graph when the
        // destination is found in the first few BFS layers.
        sbyte[] allowed = new sbyte[StateCount];
        int start = Encode(from);
        int destination = Encode(to);
        int[] parents = new int[StateCount];
        Array.Fill(parents, Unvisited);
        byte[] depths = new byte[StateCount];
        int[] queue = new int[StateCount];
        int head = 0;
        int tail = 0;
        int checks = 0;
        bool[] expandedBases = new bool[F7bsdProfile.MaximumCode + 1];
        bool[] expandedSlopes = new bool[SlopeValueCount];
        parents[start] = -1;
        queue[tail++] = start;

        while (head < tail && parents[destination] == Unvisited)
        {
            int current = queue[head++];
            if (depths[current] >= DirectMaximumWritesPerRow)
            {
                continue;
            }

            F7bsdCpuRowState state = Decode(current);
            // The first expansion of a slope visits every allowed state in
            // that column. Re-expanding it can never discover a new state.
            // Destination-first then ascending order exactly matches the
            // original exhaustive BFS and therefore preserves its paths.
            if (!expandedSlopes[state.Slope])
            {
                expandedSlopes[state.Slope] = true;
                TryVisit(Encode(new(to.Base, state.Slope)), current);
                for (int candidateBase = 0;
                    candidateBase <= F7bsdProfile.MaximumCode;
                    candidateBase++)
                {
                    if (candidateBase != to.Base)
                    {
                        TryVisit(
                            Encode(new((byte)candidateBase, state.Slope)),
                            current);
                    }
                }
            }

            // Likewise, the first expansion of a base visits its complete
            // allowed row, in the same deterministic neighbor order.
            if (!expandedBases[state.Base])
            {
                expandedBases[state.Base] = true;
                TryVisit(Encode(new(state.Base, to.Slope)), current);
                for (int candidateSlope = 0;
                    candidateSlope < SlopeValueCount;
                    candidateSlope++)
                {
                    if (candidateSlope != to.Slope)
                    {
                        TryVisit(
                            Encode(new(state.Base, (byte)candidateSlope)),
                            current);
                    }
                }
            }
        }

        neighborChecks = checks;
        if (parents[destination] == Unvisited)
        {
            return false;
        }

        List<F7bsdCpuRowState> reverse = [];
        for (int current = destination; current != start; current = parents[current])
        {
            reverse.Add(Decode(current));
        }
        reverse.Reverse();
        path = reverse.ToArray();
        return true;

        void TryVisit(int candidate, int parent)
        {
            checks++;
            if (candidate == parent ||
                parents[candidate] != Unvisited ||
                !IsAllowed(candidate))
            {
                return;
            }

            parents[candidate] = parent;
            depths[candidate] = checked((byte)(depths[parent] + 1));
            queue[tail++] = candidate;
        }

        bool IsAllowed(int candidate)
        {
            if (allowed[candidate] == 0)
            {
                allowed[candidate] = IsTransitionStateAllowed(
                    rowIndex,
                    Decode(candidate),
                    from,
                    to)
                    ? (sbyte)1
                    : (sbyte)-1;
            }
            return allowed[candidate] > 0;
        }
    }

    /// <summary>
    /// Plans all seven rows and emits concrete one-byte EC writes. Completing
    /// one transition-bounded row before the next keeps every selectable row
    /// within the transition floor and code-51 ceiling.
    /// </summary>
    internal static F7bsdCpuTransitionStep[] PlanTransition(
        ReadOnlySpan<F7bsdCpuRowState> from,
        ReadOnlySpan<F7bsdCpuRowState> to)
    {
        AssertTableLength(from, nameof(from));
        AssertTableLength(to, nameof(to));
        List<F7bsdCpuTransitionStep> steps = [];

        for (int row = 0; row < NormalRowCount; row++)
        {
            F7bsdCpuRowState previous = from[row];
            foreach (F7bsdCpuRowState next in PlanRowTransition(row, previous, to[row]))
            {
                steps.Add(CreateTransitionStep(row, previous, next));
                previous = next;
            }
        }

        if (steps.Count > MaximumWritesPerTransition)
        {
            throw new InvalidOperationException(
                $"CPU transition exceeded {MaximumWritesPerTransition} writes.");
        }
        return steps.ToArray();
    }

    /// <summary>Convenience overload for transitions between compiled targets.</summary>
    internal static F7bsdCpuTransitionStep[] PlanTransition(
        byte fromRequestedCode,
        byte toRequestedCode) => PlanTransition(
            CompileTarget(fromRequestedCode),
            CompileTarget(toRequestedCode));

    /// <summary>Plans restoration from a known table to the exact B1 baseline.</summary>
    internal static F7bsdCpuTransitionStep[] PlanTransitionToB1(
        ReadOnlySpan<F7bsdCpuRowState> from) => PlanTransition(
            from,
            GetB1MutableStates());

    /// <summary>
    /// Materializes an exact full-table prefix of a deterministic issued plan.
    /// Invalid or internally inconsistent plans are rejected.
    /// </summary>
    internal static F7bsdCpuRowState[] MaterializeTransitionPrefix(
        ReadOnlySpan<F7bsdCpuRowState> source,
        ReadOnlySpan<F7bsdCpuTransitionStep> issuedPlan,
        int completedStepCount)
    {
        AssertTableLength(source, nameof(source));
        AssertIssuedPlanLength(issuedPlan);
        if (completedStepCount < 0 || completedStepCount > issuedPlan.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(completedStepCount));
        }

        F7bsdCpuRowState[] states = source.ToArray();
        for (int index = 0; index < completedStepCount; index++)
        {
            ApplyTransitionStep(states, issuedPlan[index]);
        }
        return states;
    }

    /// <summary>
    /// Matches an observed table only against exact prefixes of the issued
    /// plan. The first exact prefix count is returned; arbitrary
    /// transition-bounded states are deliberately not accepted for recovery.
    /// </summary>
    internal static bool TryMatchTransitionPrefix(
        ReadOnlySpan<F7bsdCpuRowState> source,
        ReadOnlySpan<F7bsdCpuTransitionStep> issuedPlan,
        ReadOnlySpan<F7bsdCpuRowState> observed,
        out int completedStepCount)
    {
        AssertTableLength(source, nameof(source));
        AssertTableLength(observed, nameof(observed));
        AssertIssuedPlanLength(issuedPlan);
        F7bsdCpuRowState[] states = source.ToArray();
        if (states.AsSpan().SequenceEqual(observed))
        {
            completedStepCount = 0;
            return true;
        }

        for (int index = 0; index < issuedPlan.Length; index++)
        {
            ApplyTransitionStep(states, issuedPlan[index]);
            if (states.AsSpan().SequenceEqual(observed))
            {
                completedStepCount = index + 1;
                return true;
            }
        }

        completedStepCount = -1;
        return false;
    }

    private static F7bsdCpuRowState[][] BuildCompiledTargets()
    {
        F7bsdCpuRowState[][] tables =
            new F7bsdCpuRowState[F7bsdProfile.MaximumCode + 1][];
        for (int requested = 0;
            requested <= F7bsdProfile.MaximumCode;
            requested++)
        {
            F7bsdCpuRowState[] table = new F7bsdCpuRowState[NormalRowCount];
            F7bsdCpuRowState[]? previous = requested == 0
                ? null
                : tables[requested - 1];
            for (int row = 0; row < NormalRowCount; row++)
            {
                table[row] = CompileRowTarget(row, (byte)requested);
                if (!DominatesThermalEnvelopeAndRequest(
                    row,
                    table[row],
                    (byte)requested))
                {
                    throw new InvalidOperationException(
                        $"The compiled CPU row {row} target {requested} is unsafe.");
                }
                if (previous is not null &&
                    !DominatesRow(row, table[row], previous[row]))
                {
                    throw new InvalidOperationException(
                        $"CPU target {requested} lowered row {row} relative to " +
                        $"target {requested - 1}.");
                }
            }
            ValidateCompleteTarget(table, (byte)requested, previous);
            tables[requested] = table;
        }
        return tables;
    }

    private static F7bsdCpuRowState CompileRowTarget(
        int rowIndex,
        byte requestedCode)
    {
        // These are the closed forms of the former exhaustive 52-by-256
        // candidate search, including its total-overshoot, maximum-overshoot,
        // base, then slope tie-breaks. BuildCompiledTargets still exhaustively
        // validates the resulting 52 tables against every integer temperature,
        // both hysteresis paths, and the preceding requested code.
        if (rowIndex <= 3)
        {
            return new(requestedCode, 0);
        }

        if (rowIndex == 4)
        {
            if (requestedCode <= 10)
            {
                return new(10, 50);
            }
            return requestedCode <= 15
                ? new(requestedCode, (byte)((15 - requestedCode) * 10))
                : new(requestedCode, 0);
        }

        if (rowIndex == 5)
        {
            if (requestedCode <= 19)
            {
                return new(19, 188);
            }
            if (requestedCode <= 41)
            {
                int rise = 41 - requestedCode;
                return new(
                    requestedCode,
                    (byte)(((rise * 100) + 11) / 12));
            }
            return new(requestedCode, 0);
        }

        if (rowIndex == 6)
        {
            return requestedCode <= 41
                ? new(41, 200)
                : new(requestedCode, (byte)((51 - requestedCode) * 20));
        }

        throw new ArgumentOutOfRangeException(nameof(rowIndex));
    }

    private static void ValidateCompleteTarget(
        ReadOnlySpan<F7bsdCpuRowState> states,
        byte requestedCode,
        F7bsdCpuRowState[]? previous)
    {
        foreach (bool cooling in new[] { false, true })
        {
            int priorTemperatureTarget = -1;
            for (int temperature = 0;
                temperature < CriticalTemperatureC;
                temperature++)
            {
                int target = EvaluateTable(states, temperature, cooling);
                if (target < requestedCode ||
                    target < ThermalFloorCode(temperature) ||
                    target > F7bsdProfile.MaximumCode ||
                    target < priorTemperatureTarget)
                {
                    throw new InvalidOperationException(
                        $"CPU target {requestedCode} failed its " +
                        $"{(cooling ? "cooling" : "heating")} thermal validation " +
                        $"at {temperature} C.");
                }
                if (previous is not null &&
                    target < EvaluateTable(previous, temperature, cooling))
                {
                    throw new InvalidOperationException(
                        $"CPU target {requestedCode} lowered the " +
                        $"{(cooling ? "cooling" : "heating")} path at " +
                        $"{temperature} C.");
                }
                priorTemperatureTarget = target;
            }
            if (EvaluateTable(states, CriticalTemperatureC, cooling) !=
                F7bsdProfile.MaximumCode)
            {
                throw new InvalidOperationException(
                    "The CPU critical row did not remain full speed.");
            }
        }
    }

    private static bool DominatesRow(
        int rowIndex,
        F7bsdCpuRowState candidate,
        F7bsdCpuRowState lower)
    {
        F7bsdCpuPolicyRow band = BalancedRows[rowIndex];
        for (int temperature = band.Lower;
            temperature <= band.Upper;
            temperature++)
        {
            if (TargetAt(rowIndex, candidate, temperature) <
                TargetAt(rowIndex, lower, temperature))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsTransitionStateAllowed(
        int rowIndex,
        F7bsdCpuRowState candidate,
        F7bsdCpuRowState from,
        F7bsdCpuRowState to)
    {
        F7bsdCpuPolicyRow band = BalancedRows[rowIndex];
        for (int temperature = band.Lower;
            temperature <= band.Upper;
            temperature++)
        {
            int target = TargetAt(rowIndex, candidate, temperature);
            int lowerEndpoint = Math.Min(
                TargetAt(rowIndex, from, temperature),
                TargetAt(rowIndex, to, temperature));
            if (target < lowerEndpoint ||
                target > F7bsdProfile.MaximumCode)
            {
                return false;
            }
        }
        return true;
    }

    private static F7bsdCpuTransitionStep CreateTransitionStep(
        int rowIndex,
        F7bsdCpuRowState from,
        F7bsdCpuRowState to)
    {
        AssertNormalRowIndex(rowIndex);
        EcWrite write;
        if (to.Base != from.Base && to.Slope == from.Slope)
        {
            write = new(F7bsdProfile.CpuBaseAddresses[rowIndex], to.Base);
        }
        else if (to.Base == from.Base && to.Slope != from.Slope)
        {
            write = new(F7bsdProfile.CpuSlopeAddresses[rowIndex], to.Slope);
        }
        else
        {
            throw new InvalidOperationException(
                "A CPU transition edge did not change exactly one byte.");
        }
        if (!IsTransitionBounded(rowIndex, to))
        {
            throw new InvalidOperationException(
                $"CPU transition row {rowIndex} produced a prefix outside " +
                "transition bounds.");
        }
        return new(rowIndex, write, to);
    }

    private static void ApplyTransitionStep(
        F7bsdCpuRowState[] states,
        F7bsdCpuTransitionStep step)
    {
        AssertTableLength(states, nameof(states));
        AssertNormalRowIndex(step.RowIndex);
        F7bsdCpuRowState previous = states[step.RowIndex];
        F7bsdCpuTransitionStep expected = CreateTransitionStep(
            step.RowIndex,
            previous,
            step.ResultingState);
        if (step.Write != expected.Write)
        {
            throw new InvalidOperationException(
                "A CPU transition step's EC write does not match its resulting state.");
        }
        states[step.RowIndex] = step.ResultingState;
    }

    private static int Encode(F7bsdCpuRowState state) =>
        (state.Base * SlopeValueCount) + state.Slope;

    private static F7bsdCpuRowState Decode(int encoded) => new(
        (byte)(encoded / SlopeValueCount),
        (byte)(encoded % SlopeValueCount));

    private static void AssertCode(byte code, string parameterName)
    {
        if (code > F7bsdProfile.MaximumCode)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void AssertNormalRowIndex(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= NormalRowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(rowIndex));
        }
    }

    private static void AssertTotalRowIndex(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= TotalRowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(rowIndex));
        }
    }

    private static void AssertTableLength(
        ReadOnlySpan<F7bsdCpuRowState> states,
        string parameterName)
    {
        if (states.Length != NormalRowCount)
        {
            throw new ArgumentException(
                "A CPU mutable table must contain seven row states.",
                parameterName);
        }
    }

    private static void AssertIssuedPlanLength(
        ReadOnlySpan<F7bsdCpuTransitionStep> issuedPlan)
    {
        if (issuedPlan.Length > MaximumWritesPerTransition)
        {
            throw new ArgumentException(
                $"An issued CPU transition may contain at most " +
                $"{MaximumWritesPerTransition} writes.",
                nameof(issuedPlan));
        }
    }

}
