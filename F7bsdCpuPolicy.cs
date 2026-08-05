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

/// <summary>A single-byte step in a bounded normal-row update.</summary>
internal readonly record struct F7bsdCpuTransitionStep(
    int RowIndex,
    EcWrite Write,
    F7bsdCpuRowState ResultingState);

/// <summary>
/// Compiles a requested CPU code into a flat target across all seven normal
/// B1 temperature bands. The separate critical row remains exact B1 and takes
/// over at 94 C.
/// </summary>
internal static class F7bsdCpuPolicy
{
    internal const byte Selector = 0xb1;
    internal const int NormalRowCount = 7;
    internal const int TotalRowCount = 8;
    internal const int MaximumWritesPerRow = 3;
    internal const int MaximumWritesPerTransition =
        NormalRowCount * MaximumWritesPerRow;
    internal const int CriticalTemperatureC = 94;

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
    /// Returns seven equal base values with zero slopes. The EC therefore
    /// selects the requested target at every temperature below the untouched
    /// critical row.
    /// </summary>
    internal static F7bsdCpuRowState[] CompileTarget(byte requestedCode)
    {
        AssertCode(requestedCode, nameof(requestedCode));
        return Enumerable
            .Repeat(new F7bsdCpuRowState(requestedCode, 0), NormalRowCount)
            .ToArray();
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
    /// Tests whether a row's target remains in the native code 0..51 range
    /// throughout its immutable B1 temperature band.
    /// </summary>
    internal static bool IsTransitionBounded(
        int rowIndex,
        F7bsdCpuRowState state)
    {
        AssertNormalRowIndex(rowIndex);
        F7bsdCpuPolicyRow band = BalancedRows[rowIndex];
        for (int temperature = band.Lower;
            temperature <= band.Upper;
            temperature++)
        {
            int target = TargetAt(rowIndex, state, temperature);
            if (target < 0 || target > F7bsdProfile.MaximumCode)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Plans a deterministic three-phase transaction: zero every changing
    /// slope, change bases, then apply destination slopes. Every step changes
    /// exactly one allowlisted byte and leaves the affected row in code 0..51.
    /// </summary>
    internal static F7bsdCpuTransitionStep[] PlanTransition(
        ReadOnlySpan<F7bsdCpuRowState> from,
        ReadOnlySpan<F7bsdCpuRowState> to)
    {
        AssertTableLength(from, nameof(from));
        AssertTableLength(to, nameof(to));
        for (int row = 0; row < NormalRowCount; row++)
        {
            if (!IsTransitionBounded(row, from[row]))
            {
                throw new ArgumentException(
                    $"CPU row {row} start state is outside target bounds.",
                    nameof(from));
            }
            if (!IsTransitionBounded(row, to[row]))
            {
                throw new ArgumentException(
                    $"CPU row {row} destination state is outside target bounds.",
                    nameof(to));
            }
        }

        F7bsdCpuRowState[] current = from.ToArray();
        List<F7bsdCpuTransitionStep> steps = [];

        // A zero slope makes each current base safe to combine with later
        // base writes and removes all temperature-dependent intermediates.
        for (int row = 0; row < NormalRowCount; row++)
        {
            if (current[row].Slope != 0 && current[row] != to[row])
            {
                AddStep(row, new(current[row].Base, 0));
            }
        }

        for (int row = 0; row < NormalRowCount; row++)
        {
            if (current[row].Base != to[row].Base)
            {
                AddStep(row, new(to[row].Base, 0));
            }
        }

        for (int row = 0; row < NormalRowCount; row++)
        {
            if (current[row].Slope != to[row].Slope)
            {
                AddStep(row, new(to[row].Base, to[row].Slope));
            }
        }

        if (!current.AsSpan().SequenceEqual(to))
        {
            throw new InvalidOperationException(
                "The CPU transition did not materialize its destination.");
        }
        if (steps.Count > MaximumWritesPerTransition)
        {
            throw new InvalidOperationException(
                $"CPU transition exceeded {MaximumWritesPerTransition} writes.");
        }
        return steps.ToArray();

        void AddStep(int rowIndex, F7bsdCpuRowState next)
        {
            F7bsdCpuTransitionStep step = CreateTransitionStep(
                rowIndex,
                current[rowIndex],
                next);
            steps.Add(step);
            current[rowIndex] = next;
        }
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
    /// plan. Arbitrary bounded states are deliberately rejected for recovery.
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
                "target bounds.");
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
