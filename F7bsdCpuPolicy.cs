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

/// <summary>A single-byte step in a safe normal-row transition.</summary>
internal readonly record struct F7bsdCpuTransitionStep(
    int RowIndex,
    EcWrite Write,
    F7bsdCpuRowState ResultingState);

/// <summary>
/// Compiles CPU requests as minimum targets above the validated OEM B1 policy
/// and plans byte-wise transitions that never fall below either endpoint's
/// lower envelope.
/// </summary>
internal static class F7bsdCpuPolicy
{
    internal const byte Selector = 0xb1;
    internal const int NormalRowCount = 7;
    internal const int TotalRowCount = 8;
    internal const int MaximumWritesPerRow = 5;

    private const int SlopeValueCount = 0x100;
    private const int StateCount = (F7bsdProfile.MaximumCode + 1) * SlopeValueCount;
    private const int Unvisited = -2;

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

    private static readonly F7bsdCpuRowState[][] CompiledFloors =
        BuildCompiledFloors();

    /// <summary>Returns one immutable value from the exact OEM B1 table.</summary>
    internal static F7bsdCpuPolicyRow GetB1Row(int rowIndex)
    {
        AssertTotalRowIndex(rowIndex);
        return BalancedRows[rowIndex];
    }

    /// <summary>
    /// Compiles a requested code as a floor beneath which the OEM B1 policy
    /// may not fall. The returned array contains rows 0 through 6 only.
    /// </summary>
    internal static F7bsdCpuRowState[] CompileFloor(byte requestedCode)
    {
        AssertCode(requestedCode, nameof(requestedCode));
        return (F7bsdCpuRowState[])CompiledFloors[requestedCode].Clone();
    }

    /// <summary>
    /// Returns a complete eight-row table for inspection. Temperature bands
    /// and the critical row are always the exact B1 values.
    /// </summary>
    internal static F7bsdCpuPolicyRow[] CompileFloorRows(byte requestedCode)
    {
        F7bsdCpuRowState[] states = CompileFloor(requestedCode);
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
    /// Tests whether a generated endpoint dominates both B1 and the requested
    /// floor throughout the complete row band, without exceeding code 51.
    /// </summary>
    internal static bool DominatesB1AndFloor(
        int rowIndex,
        F7bsdCpuRowState state,
        byte requestedCode)
    {
        AssertCode(requestedCode, nameof(requestedCode));
        AssertNormalRowIndex(rowIndex);
        F7bsdCpuPolicyRow stock = BalancedRows[rowIndex];
        for (int temperature = stock.Lower;
            temperature <= stock.Upper;
            temperature++)
        {
            int target = TargetAt(rowIndex, state, temperature);
            int stockTarget = TargetAt(rowIndex, stock.State, temperature);
            if (target < stockTarget ||
                target < requestedCode ||
                target > F7bsdProfile.MaximumCode)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Tests whether a row state is never less safe than OEM B1.</summary>
    internal static bool IsB1Safe(int rowIndex, F7bsdCpuRowState state)
    {
        AssertNormalRowIndex(rowIndex);
        F7bsdCpuPolicyRow stock = BalancedRows[rowIndex];
        for (int temperature = stock.Lower;
            temperature <= stock.Upper;
            temperature++)
        {
            int target = TargetAt(rowIndex, state, temperature);
            if (target < TargetAt(rowIndex, stock.State, temperature) ||
                target > F7bsdProfile.MaximumCode)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Plans one row's shortest deterministic transition. Each returned state
    /// changes exactly one byte from its predecessor, stays at or above the
    /// pointwise lower endpoint, and never exceeds code 51.
    /// </summary>
    internal static F7bsdCpuRowState[] PlanRowTransition(
        int rowIndex,
        F7bsdCpuRowState from,
        F7bsdCpuRowState to)
    {
        AssertNormalRowIndex(rowIndex);
        if (!IsB1Safe(rowIndex, from))
        {
            throw new ArgumentException(
                $"CPU row {rowIndex} start state is not B1-safe.",
                nameof(from));
        }
        if (!IsB1Safe(rowIndex, to))
        {
            throw new ArgumentException(
                $"CPU row {rowIndex} destination state is not B1-safe.",
                nameof(to));
        }
        if (from == to)
        {
            return [];
        }

        bool[] allowed = BuildTransitionStateSet(rowIndex, from, to);
        int start = Encode(from);
        int destination = Encode(to);
        int[] parents = new int[StateCount];
        Array.Fill(parents, Unvisited);
        byte[] depths = new byte[StateCount];
        int[] queue = new int[StateCount];
        int head = 0;
        int tail = 0;
        parents[start] = -1;
        queue[tail++] = start;

        while (head < tail && parents[destination] == Unvisited)
        {
            int current = queue[head++];
            if (depths[current] >= MaximumWritesPerRow)
            {
                continue;
            }

            F7bsdCpuRowState state = Decode(current);
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

        if (parents[destination] == Unvisited)
        {
            throw new InvalidOperationException(
                $"No B1-safe CPU row {rowIndex} transition was found within " +
                $"{MaximumWritesPerRow} byte writes.");
        }

        List<F7bsdCpuRowState> reverse = [];
        for (int current = destination; current != start; current = parents[current])
        {
            reverse.Add(Decode(current));
        }
        reverse.Reverse();
        return reverse.ToArray();

        void TryVisit(int candidate, int parent)
        {
            if (candidate == parent ||
                parents[candidate] != Unvisited ||
                !allowed[candidate])
            {
                return;
            }

            parents[candidate] = parent;
            depths[candidate] = checked((byte)(depths[parent] + 1));
            queue[tail++] = candidate;
        }
    }

    /// <summary>
    /// Plans all seven rows and emits concrete one-byte EC writes. Rows are
    /// independent, so completing one safe row before the next keeps every
    /// firmware-selectable row safe throughout the transaction.
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
                EcWrite write;
                if (next.Base != previous.Base && next.Slope == previous.Slope)
                {
                    write = new(F7bsdProfile.CpuBaseAddresses[row], next.Base);
                }
                else if (next.Base == previous.Base && next.Slope != previous.Slope)
                {
                    write = new(F7bsdProfile.CpuSlopeAddresses[row], next.Slope);
                }
                else
                {
                    throw new InvalidOperationException(
                        "A CPU transition edge did not change exactly one byte.");
                }

                steps.Add(new(row, write, next));
                previous = next;
            }
        }

        return steps.ToArray();
    }

    /// <summary>Convenience overload for transitions between compiled floors.</summary>
    internal static F7bsdCpuTransitionStep[] PlanTransition(
        byte fromRequestedCode,
        byte toRequestedCode) => PlanTransition(
            CompileFloor(fromRequestedCode),
            CompileFloor(toRequestedCode));

    private static F7bsdCpuRowState[][] BuildCompiledFloors()
    {
        F7bsdCpuRowState[][] tables =
            new F7bsdCpuRowState[F7bsdProfile.MaximumCode + 1][];
        for (int requested = 0;
            requested <= F7bsdProfile.MaximumCode;
            requested++)
        {
            F7bsdCpuRowState[] table = new F7bsdCpuRowState[NormalRowCount];
            for (int row = 0; row < NormalRowCount; row++)
            {
                table[row] = CompileRowFloor(row, (byte)requested);
                if (!DominatesB1AndFloor(row, table[row], (byte)requested))
                {
                    throw new InvalidOperationException(
                        $"The compiled CPU row {row} floor {requested} is unsafe.");
                }
            }
            tables[requested] = table;
        }

        for (int row = 0; row < NormalRowCount; row++)
        {
            if (tables[0][row] != BalancedRows[row].State)
            {
                throw new InvalidOperationException(
                    "CPU floor zero must reproduce the exact B1 table.");
            }
        }
        return tables;
    }

    private static F7bsdCpuRowState CompileRowFloor(
        int rowIndex,
        byte requestedCode)
    {
        F7bsdCpuPolicyRow stock = BalancedRows[rowIndex];
        if (requestedCode <= stock.Base)
        {
            return stock.State;
        }

        bool found = false;
        int bestTotalOvershoot = int.MaxValue;
        int bestMaximumOvershoot = int.MaxValue;
        F7bsdCpuRowState best = default;

        for (int candidateBase = 0;
            candidateBase <= F7bsdProfile.MaximumCode;
            candidateBase++)
        {
            for (int candidateSlope = 0;
                candidateSlope < SlopeValueCount;
                candidateSlope++)
            {
                F7bsdCpuRowState candidate = new(
                    (byte)candidateBase,
                    (byte)candidateSlope);
                int totalOvershoot = 0;
                int maximumOvershoot = 0;
                bool valid = true;

                for (int temperature = stock.Lower;
                    temperature <= stock.Upper;
                    temperature++)
                {
                    int target = TargetAt(rowIndex, candidate, temperature);
                    int required = Math.Max(
                        requestedCode,
                        TargetAt(rowIndex, stock.State, temperature));
                    if (target < required || target > F7bsdProfile.MaximumCode)
                    {
                        valid = false;
                        break;
                    }

                    int overshoot = target - required;
                    totalOvershoot += overshoot;
                    maximumOvershoot = Math.Max(maximumOvershoot, overshoot);
                }

                if (!valid ||
                    !IsBetterCandidate(
                        totalOvershoot,
                        maximumOvershoot,
                        candidate,
                        found,
                        bestTotalOvershoot,
                        bestMaximumOvershoot,
                        best))
                {
                    continue;
                }

                found = true;
                bestTotalOvershoot = totalOvershoot;
                bestMaximumOvershoot = maximumOvershoot;
                best = candidate;
            }
        }

        return found
            ? best
            : throw new InvalidOperationException(
                $"No B1-safe CPU row {rowIndex} encoding exists for " +
                $"floor {requestedCode}.");
    }

    private static bool IsBetterCandidate(
        int totalOvershoot,
        int maximumOvershoot,
        F7bsdCpuRowState candidate,
        bool found,
        int bestTotalOvershoot,
        int bestMaximumOvershoot,
        F7bsdCpuRowState best)
    {
        if (!found || totalOvershoot != bestTotalOvershoot)
        {
            return !found || totalOvershoot < bestTotalOvershoot;
        }
        if (maximumOvershoot != bestMaximumOvershoot)
        {
            return maximumOvershoot < bestMaximumOvershoot;
        }
        if (candidate.Base != best.Base)
        {
            return candidate.Base < best.Base;
        }
        return candidate.Slope < best.Slope;
    }

    private static bool[] BuildTransitionStateSet(
        int rowIndex,
        F7bsdCpuRowState from,
        F7bsdCpuRowState to)
    {
        F7bsdCpuPolicyRow band = BalancedRows[rowIndex];
        bool[] allowed = new bool[StateCount];
        for (int candidateBase = 0;
            candidateBase <= F7bsdProfile.MaximumCode;
            candidateBase++)
        {
            for (int candidateSlope = 0;
                candidateSlope < SlopeValueCount;
                candidateSlope++)
            {
                F7bsdCpuRowState candidate = new(
                    (byte)candidateBase,
                    (byte)candidateSlope);
                bool safe = true;
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
                        safe = false;
                        break;
                    }
                }
                allowed[Encode(candidate)] = safe;
            }
        }
        return allowed;
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
}
