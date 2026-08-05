using FanControl.Plugins;
using System.Security.Cryptography;

namespace FanControl.MinisforumUM780XTX.Tests;

internal static class Program
{
    private static readonly HostIdentitySnapshot ExactHost = new(
        "Venus series",
        "F7BSD",
        "1.1",
        "1.06",
        0,
        8);

    private static readonly ushort[] CpuBaseAddresses =
        Enumerable.Range(0, 7)
            .Select(row => (ushort)(0x0310 + (row * 3)))
            .ToArray();

    private static readonly ushort[] CpuSlopeAddresses =
        Enumerable.Range(0x08b0, 7)
            .Select(address => (ushort)address)
            .ToArray();

    private static readonly ushort[] CpuRestoreAddresses =
        [.. CpuBaseAddresses, .. CpuSlopeAddresses];

    private static readonly ushort[] CpuCriticalAddresses =
        [0x0325, 0x0326, 0x0327, 0x08b7];

    private static int Main()
    {
        (string Name, Action Body)[] tests =
        [
            ("linear percentage conversion", LinearPercentageConversion),
            ("CPU thermal-envelope compiler exhaustive", CpuThermalEnvelopeCompilerExhaustive),
            ("CPU one-row firmware transitions exhaustive", CpuOneRowFirmwareTransitionsExhaustive),
            ("CPU bytewise transitions exhaustive", CpuBytewiseTransitionsExhaustive),
            ("CPU exact B1 reset exhaustive", CpuExactB1ResetExhaustive),
            ("CPU exact-prefix direct recovery", CpuExactPrefixDirectRecovery),
            ("thin write allowlist", ThinWriteAllowlist),
            ("tach low-high-low decoder", TachLowHighLowDecoder),
            ("PawnIO parks every EC and PNP byte", PawnIoParksEveryByte),
            ("PawnIO byte failures park and poison", PawnIoByteFailuresParkAndPoison),
            ("PawnIO clean mismatch stays recoverable", PawnIoCleanMismatchIsRecoverable),
            ("PawnIO rejects before hardware access", PawnIoRejectsBeforeHardwareAccess),
            ("PawnIO mutex timeout stays recoverable", PawnIoMutexTimeoutIsRecoverable),
            ("PawnIO abandoned mutex poisons", PawnIoAbandonedMutexPoisons),
            ("PawnIO exact transaction budgets", PawnIoTransactionBudgets),
            ("exact host identity gate", ExactHostIdentityGate),
            ("initialization identity and critical-row gates", InitializationIdentityAndCriticalGates),
            ("initialization rejects non-stock controller policy state", InitializationRejectsNonStockPolicyState),
            ("external system ownership is refused on initialize", ExternalSystemOwnershipIsRefused),
            ("native CPU target lifecycle", CpuNativeTargetLifecycle),
            ("CPU target failure restores exact B1", CpuTargetFailureRestoresB1),
            ("every partial CPU transition restores B1", PartialCpuTransitionsRestoreB1),
            ("interrupted CPU recovery resumes certified suffix", InterruptedCpuRecoveryResumesCertifiedSuffix),
            ("CPU precondition drift writes nothing", CpuPreconditionDriftWritesNothing),
            ("CPU restore refuses immutable profile drift", CpuRestoreIgnoresMutableConfiguration),
            ("CPU restore tolerates firmware target transient", CpuRestoreToleratesFirmwareTargetTransient),
            ("system ownership lifecycle", SystemOwnershipLifecycle),
            ("system ownership polling is bounded", SystemOwnershipPollingIsBounded),
            ("verified system faults do not poison CPU control", VerifiedSystemFaultDoesNotPoisonCpu),
            ("unsafe initial system temperature only allows full", UnsafeInitialSystemTemperatureOnlyAllowsFull),
            ("system release failures are bounded and recoverable", SystemReleaseFailurePaths),
            ("system thermal guard precedes tach retries", SystemThermalGuardPrecedesTachRetries),
            ("persistent owned telemetry failure releases system", PersistentOwnedTelemetryFailureReleasesSystem),
            ("system guard gap releases to firmware", SystemGuardGapReleasesToFirmware),
            ("system drift faults without reengaging", SystemDriftFaultsWithoutReengaging),
            ("CPU zero uses cool-stop thermal tail", CpuZeroUsesCoolStopThermalTail),
            ("CPU mutations apply immediately", CpuMutationsApplyImmediately),
            ("CPU burst requests stay bounded", CpuBurstRequestsStayBounded),
            ("CPU writes ignore the system guard clock", CpuWritesIgnoreSystemGuardClock),
            ("duplicate control requests are cached", DuplicateControlRequestsAreCached),
            ("telemetry guards system control", TelemetryGuardsSystemControl),
            ("close restores both controls", CloseRestoresBothControls),
            ("close continues restoration after failure", CloseContinuesAfterRestoreFailure),
            ("plugin sensor and dual-control lifecycle", PluginSensorAndRawControlLifecycle),
            ("plugin reports immediate CPU confirmation", PluginReportsImmediateCpuConfirmation),
            ("plugin isolates synchronous CPU control failure", PluginIsolatesSynchronousCpuControlFailure),
            ("plugin control failures are contained", PluginControlFailuresAreContained),
            ("plugin telemetry error clears stale values", PluginTelemetryErrorClearsStaleValues),
            ("plugin persistent telemetry failure latches", PluginPersistentTelemetryFailureLatches),
            ("plugin initialization failure cleanup", PluginInitializationFailureCleanup),
        ];

        int failures = 0;
        foreach ((string name, Action body) in tests)
        {
            try
            {
                body();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception}");
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void LinearPercentageConversion()
    {
        Equal((byte)0, F7bsdProfile.ToCode(0f));
        Equal((byte)1, F7bsdProfile.ToCode(1f));
        Equal((byte)13, F7bsdProfile.ToCode(25f));
        Equal((byte)26, F7bsdProfile.ToCode(50f));
        Equal((byte)38, F7bsdProfile.ToCode(75f));
        Equal((byte)51, F7bsdProfile.ToCode(100f));
        Equal(0f, F7bsdProfile.ToPercentage(0));
        Equal(26 * 100f / 51f, F7bsdProfile.ToPercentage(26));
        Equal(100f, F7bsdProfile.ToPercentage(51));

        for (byte code = 0; code <= 51; code++)
        {
            Equal(code, F7bsdProfile.ToCode(F7bsdProfile.ToPercentage(code)));
        }

        Throws<ArgumentOutOfRangeException>(() => F7bsdProfile.ToCode(-0.01f));
        Throws<ArgumentOutOfRangeException>(() => F7bsdProfile.ToCode(100.01f));
        Throws<ArgumentOutOfRangeException>(() => F7bsdProfile.ToCode(float.NaN));
        Throws<ArgumentOutOfRangeException>(() =>
            F7bsdProfile.ToCode(float.PositiveInfinity));
        Throws<ArgumentOutOfRangeException>(() => F7bsdProfile.ToPercentage(52));
    }

    private static void ThinWriteAllowlist()
    {
        EcWrite[] allowed = CpuBaseAddresses
            .Select(address => new EcWrite(address, 51))
            .Concat(CpuSlopeAddresses.Select(address => new EcWrite(address, 0)))
            .Append(new EcWrite(0x0885, 0))
            .Append(new EcWrite(0x088b, 0xff))
            .Append(new EcWrite(0x088b, 0))
            .ToArray();
        F7bsdProfile.AssertWritesAllowed(allowed);

        Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
            [new EcWrite(CpuBaseAddresses[0], 0xff)]));

        Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
            [new EcWrite(0x0325, 51)]));
        Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
            [new EcWrite(0x0331, 0)]));
        Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
            [new EcWrite(0x1803, 20)]));
        Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
            [new EcWrite(0x1804, 20)]));
        Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
            [new EcWrite(0x088a, 0xff)]));
        Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
            [new EcWrite(0x0885, 52)]));
        Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
            [new EcWrite(0x088b, 1)]));
    }

    private static void CpuThermalEnvelopeCompilerExhaustive()
    {
        byte[] exactB1 =
        {
            0, 16, 18, 21, 28, 32, 33,
            0, 10, 33, 58, 60, 16, 200,
        };
        SequenceEqual(
            exactB1,
            F7bsdCpuPolicy.ToMutableBytes(F7bsdCpuPolicy.GetB1MutableStates()));
        Equal(
            new F7bsdCpuPolicyRow(51, 100, 93, 0),
            F7bsdCpuPolicy.GetB1Row(7));

        (int Temperature, int Code)[] envelopeAnchors =
        [
            (0, 0), (66, 0),
            (67, 10), (68, 10), (69, 10), (70, 10),
            (71, 10), (72, 10), (73, 10), (74, 10),
            (75, 13), (76, 15), (77, 18),
            (78, 20), (79, 23), (80, 25), (81, 28), (82, 30),
            (83, 32), (84, 34), (85, 35), (86, 37), (87, 39),
            (88, 40), (89, 43), (90, 45), (91, 47), (92, 49),
            (93, 51),
        ];
        foreach ((int temperature, int code) in envelopeAnchors)
        {
            Equal(code, F7bsdCpuPolicy.ThermalFloorCode(temperature));
        }
        int previousEnvelope = -1;
        for (int temperature = 0;
            temperature < F7bsdCpuPolicy.CriticalTemperatureC;
            temperature++)
        {
            int floor = F7bsdCpuPolicy.ThermalFloorCode(temperature);
            True(floor >= 0 && floor <= F7bsdProfile.MaximumCode);
            True(floor >= previousEnvelope);
            previousEnvelope = floor;
        }

        F7bsdCpuRowState[][] targets = Enumerable
            .Range(0, F7bsdProfile.MaximumCode + 1)
            .Select(code => F7bsdCpuPolicy.CompileTarget((byte)code))
            .ToArray();
        for (byte code = 0; code <= F7bsdProfile.MaximumCode; code++)
        {
            F7bsdCpuRowState[] states = targets[code];
            Equal(F7bsdCpuPolicy.NormalRowCount, states.Length);
            SequenceEqual(
                states,
                F7bsdCpuPolicy.FromMutableBytes(
                    F7bsdCpuPolicy.ToMutableBytes(states)));
            F7bsdCpuPolicyRow[] complete =
                F7bsdCpuPolicy.CompileTargetRows(code);
            Equal(F7bsdCpuPolicy.TotalRowCount, complete.Length);
            Equal(F7bsdCpuPolicy.GetB1Row(7), complete[7]);
            for (int row = 0; row < F7bsdCpuPolicy.NormalRowCount; row++)
            {
                F7bsdCpuPolicyRow b1 = F7bsdCpuPolicy.GetB1Row(row);
                Equal(b1.Upper, complete[row].Upper);
                Equal(b1.Lower, complete[row].Lower);
                Equal(states[row].Base, complete[row].Base);
                Equal(states[row].Slope, complete[row].Slope);
                True(F7bsdCpuPolicy.DominatesThermalEnvelopeAndRequest(
                    row,
                    states[row],
                    code));
                True(F7bsdCpuPolicy.IsTransitionBounded(row, states[row]));
            }

            foreach (bool cooling in new[] { false, true })
            {
                int previousTemperatureTarget = -1;
                for (int temperature = 0;
                    temperature < F7bsdCpuPolicy.CriticalTemperatureC;
                    temperature++)
                {
                    int target = F7bsdCpuPolicy.EvaluateTable(
                        states,
                        temperature,
                        cooling);
                    True(target >= code);
                    True(target >= F7bsdCpuPolicy.ThermalFloorCode(temperature));
                    True(target <= F7bsdProfile.MaximumCode);
                    True(target >= previousTemperatureTarget);
                    if (code > 0)
                    {
                        True(target >= F7bsdCpuPolicy.EvaluateTable(
                            targets[code - 1],
                            temperature,
                            cooling));
                    }
                    previousTemperatureTarget = target;
                }
                Equal(
                    (int)F7bsdProfile.MaximumCode,
                    F7bsdCpuPolicy.EvaluateTable(
                        states,
                        F7bsdCpuPolicy.CriticalTemperatureC,
                        cooling));
            }
        }

        for (int code = 0; code <= F7bsdProfile.MaximumCode; code++)
        {
            for (int row = 0; row <= 3; row++)
            {
                Equal(new F7bsdCpuRowState((byte)code, 0), targets[code][row]);
            }
            Equal(
                code,
                F7bsdCpuPolicy.EvaluateTable(
                    targets[code],
                    30,
                    cooling: false));
        }
        for (int first = 0; first <= F7bsdProfile.MaximumCode; first++)
        {
            for (int second = first + 1;
                second <= F7bsdProfile.MaximumCode;
                second++)
            {
                False(targets[first].SequenceEqual(targets[second]));
            }
        }
        Equal(new F7bsdCpuRowState(10, 50), targets[0][4]);
        Equal(new F7bsdCpuRowState(19, 188), targets[0][5]);
        Equal(new F7bsdCpuRowState(41, 200), targets[0][6]);
        for (int row = 0; row < F7bsdCpuPolicy.NormalRowCount; row++)
        {
            Equal(
                new F7bsdCpuRowState(F7bsdProfile.MaximumCode, 0),
                targets[F7bsdProfile.MaximumCode][row]);
        }

        Throws<ArgumentOutOfRangeException>(() => F7bsdCpuPolicy.CompileTarget(52));
        Throws<ArgumentOutOfRangeException>(() =>
            F7bsdCpuPolicy.ThermalFloorCode(
                F7bsdCpuPolicy.CriticalTemperatureC));
        Throws<ArgumentException>(() => F7bsdCpuPolicy.FromMutableBytes([0, 1]));
    }

    private static void CpuOneRowFirmwareTransitionsExhaustive()
    {
        for (byte code = 0; code <= F7bsdProfile.MaximumCode; code++)
        {
            F7bsdCpuRowState[] states = F7bsdCpuPolicy.CompileTarget(code);
            for (int row = 0; row < F7bsdCpuPolicy.NormalRowCount - 1; row++)
            {
                F7bsdCpuPolicyRow band = F7bsdCpuPolicy.GetB1Row(row);
                (int stayRow, int stayTarget) = FirmwarePolicyInvocation(
                    states,
                    row,
                    band.Upper,
                    cooling: false);
                Equal(row, stayRow);
                AssertFirmwareTarget(code, band.Upper, stayTarget);

                (int nextRow, int nextTarget) = FirmwarePolicyInvocation(
                    states,
                    row,
                    band.Upper + 1,
                    cooling: false);
                Equal(row + 1, nextRow);
                AssertFirmwareTarget(code, band.Upper + 1, nextTarget);
            }
            for (int row = 1; row < F7bsdCpuPolicy.NormalRowCount; row++)
            {
                F7bsdCpuPolicyRow band = F7bsdCpuPolicy.GetB1Row(row);
                (int stayRow, int stayTarget) = FirmwarePolicyInvocation(
                    states,
                    row,
                    band.Lower,
                    cooling: true);
                Equal(row, stayRow);
                AssertFirmwareTarget(code, band.Lower, stayTarget);

                (int priorRow, int priorTarget) = FirmwarePolicyInvocation(
                    states,
                    row,
                    band.Lower - 1,
                    cooling: true);
                Equal(row - 1, priorRow);
                AssertFirmwareTarget(code, band.Lower - 1, priorTarget);
            }
            for (int row = 0; row < F7bsdCpuPolicy.NormalRowCount; row++)
            {
                Equal(
                    51,
                    FirmwarePolicyInvocation(
                        states,
                        row,
                        F7bsdCpuPolicy.CriticalTemperatureC,
                        cooling: false).Target);
            }
        }

        // From the coolest row, a sudden 93 C sample remains at zero for three
        // invocations, then reaches the sustainable restart row, high tail,
        // and full target.
        F7bsdCpuRowState[] zero = F7bsdCpuPolicy.CompileTarget(0);
        int activeRow = 0;
        int[] expectedTargets = [0, 0, 0, 23, 50, 51];
        for (int invocation = 0; invocation < expectedTargets.Length; invocation++)
        {
            (activeRow, int target) = FirmwarePolicyInvocation(
                zero,
                activeRow,
                93,
                cooling: false);
            Equal(expectedTargets[invocation], target);
        }
    }

    private static (int Row, int Target) FirmwarePolicyInvocation(
        ReadOnlySpan<F7bsdCpuRowState> states,
        int currentRow,
        int temperatureC,
        bool cooling)
    {
        if (temperatureC >= F7bsdCpuPolicy.CriticalTemperatureC)
        {
            return (currentRow, F7bsdProfile.MaximumCode);
        }
        F7bsdCpuPolicyRow currentBand = F7bsdCpuPolicy.GetB1Row(currentRow);
        int selectedRow = currentRow;
        if (!cooling &&
            selectedRow < F7bsdCpuPolicy.NormalRowCount - 1 &&
            temperatureC > currentBand.Upper)
        {
            selectedRow++;
        }
        else if (cooling && selectedRow > 0 && temperatureC < currentBand.Lower)
        {
            selectedRow--;
        }
        F7bsdCpuPolicyRow selectedBand = F7bsdCpuPolicy.GetB1Row(selectedRow);
        F7bsdCpuRowState state = states[selectedRow];
        return (
            selectedRow,
            state.Base +
                ((state.Slope * (temperatureC - selectedBand.Lower)) / 100));
    }

    private static void AssertFirmwareTarget(byte code, int temperatureC, int target)
    {
        True(target >= code);
        True(target >= F7bsdCpuPolicy.ThermalFloorCode(temperatureC));
        True(target <= F7bsdProfile.MaximumCode);
    }

    private static void CpuBytewiseTransitionsExhaustive()
    {
        F7bsdCpuRowState[][] targets = Enumerable
            .Range(0, F7bsdProfile.MaximumCode + 1)
            .Select(code => F7bsdCpuPolicy.CompileTarget((byte)code))
            .ToArray();
        bool sawDirect = false;
        bool sawB1Anchor = false;
        HashSet<(int Row, F7bsdCpuRowState State)> verifiedDirectB1 = [];
        using IncrementalHash pathHash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        for (int fromCode = 0;
            fromCode <= F7bsdProfile.MaximumCode;
            fromCode++)
        {
            F7bsdCpuRowState[] from = targets[fromCode];
            for (int toCode = 0;
                toCode <= F7bsdProfile.MaximumCode;
                toCode++)
            {
                F7bsdCpuRowState[] to = targets[toCode];
                for (int row = 0; row < F7bsdCpuPolicy.NormalRowCount; row++)
                {
                    bool hasDirect = F7bsdCpuPolicy.TryPlanDirectRowTransition(
                        row,
                        from[row],
                        to[row],
                        out F7bsdCpuRowState[] direct,
                        out int neighborChecks);
                    True(neighborChecks <=
                        F7bsdCpuPolicy.MaximumDirectPlannerNeighborChecks);
                    F7bsdCpuRowState[] path = F7bsdCpuPolicy.PlanRowTransition(
                        row,
                        from[row],
                        to[row]);
                    pathHash.AppendData(
                        [(byte)row, (byte)fromCode, (byte)toCode, (byte)path.Length]);
                    foreach (F7bsdCpuRowState state in path)
                    {
                        pathHash.AppendData([state.Base, state.Slope]);
                    }

                    if (hasDirect)
                    {
                        sawDirect = true;
                        True(direct.Length <=
                            F7bsdCpuPolicy.DirectMaximumWritesPerRow);
                        SequenceEqual(direct, path);
                    }
                    else
                    {
                        sawB1Anchor = true;
                        True(path.Contains(F7bsdCpuPolicy.GetB1Row(row).State));
                    }
                    True(path.Length <= F7bsdCpuPolicy.MaximumWritesPerRow);

                    F7bsdCpuRowState previous = from[row];
                    foreach (F7bsdCpuRowState state in path)
                    {
                        Equal(1,
                            (state.Base == previous.Base ? 0 : 1) +
                            (state.Slope == previous.Slope ? 0 : 1));
                        True(F7bsdCpuPolicy.IsTransitionBounded(row, state));
                        if (verifiedDirectB1.Add((row, state)))
                        {
                            True(F7bsdCpuPolicy.TryPlanDirectRowTransition(
                                row,
                                state,
                                F7bsdCpuPolicy.GetB1Row(row).State,
                                out F7bsdCpuRowState[] directToB1));
                            True(directToB1.Length <=
                                F7bsdCpuPolicy.DirectMaximumWritesPerRow);
                        }
                        F7bsdCpuPolicyRow band = F7bsdCpuPolicy.GetB1Row(row);
                        for (int temperature = band.Lower;
                            temperature <= band.Upper;
                            temperature++)
                        {
                            int target = F7bsdCpuPolicy.TargetAt(
                                row,
                                state,
                                temperature);
                            True(target >= F7bsdCpuPolicy.TransitionFloorAt(
                                row,
                                temperature));
                            True(target <= F7bsdProfile.MaximumCode);
                        }
                        previous = state;
                    }
                    Equal(to[row], previous);
                }
            }
        }
        True(sawDirect);
        True(sawB1Anchor);
        // Golden digest captured from the original exhaustive BFS after the
        // cool-stop table was finalized. It pins every compiled-code pair's
        // deterministic row path while the search-budget assertion above
        // prevents the former repeated full-frontier scans from returning.
        Equal(
            "76BD815E710CAE8953A1D0AC45ECE992B8C26437FEC38E48B8C5583511FB892C",
            Convert.ToHexString(pathHash.GetHashAndReset()));
    }

    private static void CpuExactB1ResetExhaustive()
    {
        F7bsdCpuRowState[] b1 = F7bsdCpuPolicy.GetB1MutableStates();
        for (byte code = 0; code <= F7bsdProfile.MaximumCode; code++)
        {
            F7bsdCpuRowState[] target = F7bsdCpuPolicy.CompileTarget(code);
            for (int row = 0; row < F7bsdCpuPolicy.NormalRowCount; row++)
            {
                True(F7bsdCpuPolicy.TryPlanDirectRowTransition(
                    row,
                    target[row],
                    F7bsdCpuPolicy.GetB1Row(row).State,
                    out F7bsdCpuRowState[] directToB1));
                True(directToB1.Length <=
                    F7bsdCpuPolicy.DirectMaximumWritesPerRow);
            }
            F7bsdCpuTransitionStep[] engage = F7bsdCpuPolicy.PlanTransition(b1, target);
            True(engage.Length <= F7bsdCpuPolicy.MaximumWritesPerTransition);
            SequenceEqual(target, ApplyCpuTransitionSteps(b1, engage));

            F7bsdCpuTransitionStep[] reset =
                F7bsdCpuPolicy.PlanTransitionToB1(target);
            True(reset.Length <= F7bsdCpuPolicy.MaximumWritesPerTransition);
            SequenceEqual(b1, ApplyCpuTransitionSteps(target, reset));
            False(reset.Any(step =>
                CpuCriticalAddresses.Contains(step.Write.Address)));
        }
    }

    private static void CpuExactPrefixDirectRecovery()
    {
        (F7bsdCpuRowState[] Source, F7bsdCpuRowState[] Destination)[] cases =
        [
            (F7bsdCpuPolicy.GetB1MutableStates(), F7bsdCpuPolicy.CompileTarget(0)),
            (F7bsdCpuPolicy.CompileTarget(0), F7bsdCpuPolicy.CompileTarget(51)),
            (F7bsdCpuPolicy.CompileTarget(51), F7bsdCpuPolicy.CompileTarget(0)),
            (F7bsdCpuPolicy.CompileTarget(20), F7bsdCpuPolicy.CompileTarget(21)),
        ];
        F7bsdCpuRowState[] b1 = F7bsdCpuPolicy.GetB1MutableStates();
        foreach ((F7bsdCpuRowState[] source, F7bsdCpuRowState[] destination) in cases)
        {
            F7bsdCpuTransitionStep[] issued =
                F7bsdCpuPolicy.PlanTransition(source, destination);
            for (int completed = 0; completed <= issued.Length; completed++)
            {
                F7bsdCpuRowState[] observed =
                    F7bsdCpuPolicy.MaterializeTransitionPrefix(
                        source,
                        issued,
                        completed);
                True(F7bsdCpuPolicy.TryMatchTransitionPrefix(
                    source,
                    issued,
                    observed,
                    out int matchedPrefix));
                SequenceEqual(
                    observed,
                    F7bsdCpuPolicy.MaterializeTransitionPrefix(
                        source,
                        issued,
                        matchedPrefix));

                F7bsdCpuTransitionStep[] recovery =
                    F7bsdCpuPolicy.PlanTransitionToB1(observed);
                True(recovery.Length <=
                    F7bsdCpuPolicy.MaximumWritesPerTransition);
                SequenceEqual(b1, ApplyCpuTransitionSteps(observed, recovery));

                // A direct B1 recovery becomes the next issued transaction.
                // If interrupted, its exact remaining suffix must still finish.
                for (int recovered = 0; recovered <= recovery.Length; recovered++)
                {
                    F7bsdCpuRowState[] interrupted =
                        F7bsdCpuPolicy.MaterializeTransitionPrefix(
                            observed,
                            recovery,
                            recovered);
                    True(F7bsdCpuPolicy.TryMatchTransitionPrefix(
                        observed,
                        recovery,
                        interrupted,
                        out int matchedRecoveryPrefix));
                    SequenceEqual(
                        b1,
                        ApplyCpuTransitionSteps(
                            interrupted,
                            recovery[matchedRecoveryPrefix..]));
                }
            }
        }

        F7bsdCpuRowState[] rejectedSource = F7bsdCpuPolicy.CompileTarget(20);
        F7bsdCpuTransitionStep[] rejectedPlan = F7bsdCpuPolicy.PlanTransition(
            rejectedSource,
            F7bsdCpuPolicy.CompileTarget(40));
        F7bsdCpuRowState[]? unissuedTable = null;
        for (byte code = 0; code <= F7bsdProfile.MaximumCode; code++)
        {
            F7bsdCpuRowState[] candidate = F7bsdCpuPolicy.CompileTarget(code);
            if (!candidate.SequenceEqual(b1) &&
                !F7bsdCpuPolicy.TryMatchTransitionPrefix(
                    rejectedSource,
                    rejectedPlan,
                    candidate,
                    out _))
            {
                unissuedTable = candidate;
                break;
            }
        }
        NotNull(unissuedTable);
        False(F7bsdCpuPolicy.TryMatchTransitionPrefix(
            rejectedSource,
            rejectedPlan,
            unissuedTable!,
            out _));

        Throws<ArgumentException>(() =>
            F7bsdCpuPolicy.MaterializeTransitionPrefix(
                rejectedSource,
                new F7bsdCpuTransitionStep[
                    F7bsdCpuPolicy.MaximumWritesPerTransition + 1],
                0));
        Throws<ArgumentException>(() =>
            F7bsdCpuPolicy.TryMatchTransitionPrefix(
                rejectedSource,
                new F7bsdCpuTransitionStep[
                    F7bsdCpuPolicy.MaximumWritesPerTransition + 1],
                rejectedSource,
                out _));
    }

    private static void TachLowHighLowDecoder()
    {
        byte[] values = TelemetryBytes(
            cpuCounter: 1_000,
            systemCounter: 1_250,
            cpuTemperature: 56,
            systemTemperature: 44);
        True(F7bsdTelemetryDecoder.TryDecode(values, out F7bsdTelemetry? telemetry));
        NotNull(telemetry);
        Equal(2_156, telemetry!.CpuFanRpm);
        Equal(1_725, telemetry.SystemFanRpm);
        Equal(56, telemetry.CpuTemperatureC);
        Equal(44, telemetry.SystemTemperatureC);

        True(F7bsdTelemetryDecoder.TryDecode(
            TelemetryBytes(0, 0, 40, 35),
            out F7bsdTelemetry? stopped));
        Equal(0, stopped!.CpuFanRpm);
        Equal(0, stopped.SystemFanRpm);

        byte[] tornCpu = (byte[])values.Clone();
        tornCpu[2]++;
        False(F7bsdTelemetryDecoder.TryDecode(tornCpu, out _));

        byte[] tornSystem = (byte[])values.Clone();
        tornSystem[5]++;
        False(F7bsdTelemetryDecoder.TryDecode(tornSystem, out _));
        Throws<ArgumentException>(() => F7bsdTelemetryDecoder.TryDecode([0, 1], out _));
    }

    private static void PawnIoParksEveryByte()
    {
        List<string> trace = [];
        FakePawnIoExecutor executor = new(trace);
        executor.SetByte(0x0309, 56);
        executor.SetByte(0x0305, 44);
        FakeIsaMutex mutex = new(trace);
        using PawnIoTransport transport = new(executor, mutex);

        SequenceEqual([(byte)56, (byte)44], transport.Read([0x0309, 0x0305]));
        Equal(17, executor.ExecuteCalls);
        SequenceEqual(
        [
            "SELECT:0",
            "SOUT:002E=11", "SOUT:002F=03", "SOUT:002E=10",
            "SOUT:002F=09", "SOUT:002E=12", "SIN:002F",
            "SOUT:002E=10", "POUT:002E=20",
            "SOUT:002E=11", "SOUT:002F=03", "SOUT:002E=10",
            "SOUT:002F=05", "SOUT:002E=12", "SIN:002F",
            "SOUT:002E=10", "POUT:002E=20",
        ],
        executor.Operations);
        Equal("MUTEX:RELEASE", trace.Last());
        Equal((byte)0x10, executor.Depth2Selector);
        Equal((byte)0x20, executor.OuterIndex);

        int before = executor.ExecuteCalls;
        SequenceEqual(F7bsdProfile.ExpectedPnpIdentity, transport.ReadPnpIdentity());
        Equal(7, executor.ExecuteCalls - before);
        SequenceEqual(
        [
            "SELECT:0",
            "SIN:0020", "POUT:002E=20",
            "SIN:0021", "POUT:002E=20",
            "SIN:0022", "POUT:002E=20",
        ],
        executor.Operations.Skip(before));
    }

    private static void PawnIoByteFailuresParkAndPoison()
    {
        // Call 1 selects the slot. Calls 2..9 are the complete byte body and
        // its two park operations. Inject at every position.
        for (int failureCall = 2; failureCall <= 9; failureCall++)
        {
            List<string> trace = [];
            FakePawnIoExecutor executor = new(trace)
            {
                FailExecuteCall = failureCall,
            };
            FakeIsaMutex mutex = new(trace);
            using PawnIoTransport transport = new(executor, mutex);

            Throws<InvalidOperationException>(() => transport.Read([0x0309]));
            if (failureCall <= 8)
            {
                True(executor.Operations.Contains("POUT:002E=20"));
            }
            if (failureCall <= 7)
            {
                True(executor.Operations.Count(item => item == "SOUT:002E=10") >= 1);
            }

            int nativeCalls = executor.ExecuteCalls;
            int mutexWaits = mutex.WaitCalls;
            Throws<InvalidOperationException>(() => transport.Read([0x0309]));
            Equal(nativeCalls, executor.ExecuteCalls);
            Equal(mutexWaits, mutex.WaitCalls);
        }

        List<string> doubleTrace = [];
        FakePawnIoExecutor doubleFailure = new(doubleTrace);
        doubleFailure.FailExecuteCalls.UnionWith([7, 8, 9]);
        using PawnIoTransport doubleTransport = new(
            doubleFailure,
            new FakeIsaMutex(doubleTrace));
        InvalidOperationException aggregate = Capture<InvalidOperationException>(
            () => doubleTransport.Read([0x0309]));
        True(aggregate.ToString().Contains("failure 7", StringComparison.Ordinal));
        True(aggregate.ToString().Contains("failure 8", StringComparison.Ordinal));
        True(aggregate.ToString().Contains("failure 9", StringComparison.Ordinal));
    }

    private static void PawnIoCleanMismatchIsRecoverable()
    {
        List<string> trace = [];
        FakePawnIoExecutor executor = KnownControllerExecutor(trace);
        executor.IgnoreEcWrites = true;
        using PawnIoTransport transport = new(executor, new FakeIsaMutex(trace));

        Throws<IOException>(() => transport.Write([new EcWrite(0x0885, 17)]));
        int callsAfterMismatch = executor.ExecuteCalls;
        executor.IgnoreEcWrites = false;
        executor.SetByte(0x0309, 61);
        SequenceEqual([(byte)61], transport.Read([0x0309]));
        True(executor.ExecuteCalls > callsAfterMismatch);
    }

    private static void PawnIoRejectsBeforeHardwareAccess()
    {
        List<string> trace = [];
        FakePawnIoExecutor executor = new(trace);
        FakeIsaMutex mutex = new(trace);
        using PawnIoTransport transport = new(executor, mutex);

        Throws<InvalidOperationException>(() => transport.Read([0xffff]));
        Throws<InvalidOperationException>(() =>
            transport.Write([new EcWrite(0x0325, 51)]));
        Equal(0, executor.ExecuteCalls);
        Equal(0, mutex.WaitCalls);
    }

    private static void PawnIoMutexTimeoutIsRecoverable()
    {
        List<string> trace = [];
        FakePawnIoExecutor executor = new(trace);
        executor.SetByte(0x0309, 52);
        FakeIsaMutex mutex = new(trace);
        mutex.WaitResults.Enqueue(false);
        mutex.WaitResults.Enqueue(true);
        using PawnIoTransport transport = new(executor, mutex);

        Throws<TimeoutException>(() => transport.Read([0x0309]));
        Equal(0, executor.ExecuteCalls);
        SequenceEqual([(byte)52], transport.Read([0x0309]));
    }

    private static void PawnIoAbandonedMutexPoisons()
    {
        List<string> trace = [];
        FakePawnIoExecutor executor = new(trace);
        FakeIsaMutex mutex = new(trace) { AbandonNextWait = true };
        using PawnIoTransport transport = new(executor, mutex);

        Throws<InvalidOperationException>(() => transport.Read([0x0309]));
        Equal(0, executor.ExecuteCalls);
        Equal(1, mutex.ReleaseCalls);
        int waits = mutex.WaitCalls;
        Throws<InvalidOperationException>(() => transport.Read([0x0309]));
        Equal(waits, mutex.WaitCalls);
        Equal(0, executor.ExecuteCalls);
    }

    private static void PawnIoTransactionBudgets()
    {
        List<string> trace = [];
        FakePawnIoExecutor executor = KnownControllerExecutor(trace);
        using PawnIoTransport transport = new(executor, new FakeIsaMutex(trace));

        transport.ReadPnpIdentity();
        Equal(7, executor.ExecuteCalls);
        int before = executor.ExecuteCalls;
        transport.Read(F7bsdProfile.TelemetryAddresses);
        Equal(1 + (8 * F7bsdProfile.TelemetryAddresses.Length),
            executor.ExecuteCalls - before);
        before = executor.ExecuteCalls;
        transport.Write(
        [
            new EcWrite(F7bsdProfile.SystemTargetAddress, 20),
            new EcWrite(F7bsdProfile.SystemTargetAddress, 21),
        ]);
        Equal(55 + (16 * 2), executor.ExecuteCalls - before);
    }

    private static void ExactHostIdentityGate()
    {
        HostIdentityGate.Assert(ExactHost);
        Throws<ArgumentNullException>(() => HostIdentityGate.Assert(null!));

        HostIdentitySnapshot[] rejected =
        [
            ExactHost with { Product = "Other" },
            ExactHost with { Board = "OTHER" },
            ExactHost with { BoardVersion = "1.0" },
            ExactHost with { BiosVersion = "1.05" },
            ExactHost with { EmbeddedControllerMajorVersion = 1 },
            ExactHost with { EmbeddedControllerMinorVersion = 7 },
        ];
        foreach (HostIdentitySnapshot identity in rejected)
        {
            Throws<PlatformNotSupportedException>(() => HostIdentityGate.Assert(identity));
        }
    }

    private static void InitializationIdentityAndCriticalGates()
    {
        FakeTransport good = new();
        PawnIoF7bsdBackend backend = CreateBackend(good);
        backend.Initialize();
        backend.Initialize();
        Equal(0, good.WriteBatches.Count);
        backend.Dispose();
        True(good.Disposed);
        Equal(0, good.WriteBatches.Count);

        FakeTransport wrongPnp = new()
        {
            PnpIdentity = [0x55, 0x71, 0x03],
        };
        AssertInitializationRejectedWithoutWrites(wrongPnp);

        FakeTransport wrongController = new();
        wrongController.SetByte(0x200d, 0x42);
        AssertInitializationRejectedWithoutWrites(wrongController);

        FakeTransport wrongCritical = new();
        wrongCritical.SetByte(0x0325, 50);
        AssertInitializationRejectedWithoutWrites(wrongCritical);

        int factoryCalls = 0;
        FakeTransport unused = new();
        PawnIoF7bsdBackend wrongHostBackend = new(
            static () => ExactHost with { Board = "Other" },
            () =>
            {
                factoryCalls++;
                return unused;
            });
        Throws<PlatformNotSupportedException>(wrongHostBackend.Initialize);
        Equal(0, factoryCalls);
        Equal(0, unused.WriteBatches.Count);
        False(unused.Disposed);
    }

    private static void InitializationRejectsNonStockPolicyState()
    {
        FakeTransport transport = new();
        byte[] arbitraryCpu =
        [
            7, 52, 50, 12, 0, 255, 23,
            255, 1, 88, 0, 17, 244, 9,
        ];
        transport.SetCpuBytes(arbitraryCpu);
        transport.SetByte(0x032f, 0xa5);
        transport.SetByte(0x0311, 99);
        transport.SetByte(0x0331, 1);
        transport.SetByte(0x0334, 2);
        transport.SetByte(0x0337, 3);
        transport.SetByte(0x0309, 0);
        transport.SetByte(0x0888, 201);
        transport.SetByte(0x0305, 255);
        transport.SetByte(0x0889, 7);
        transport.SetByte(0x0884, 250);
        transport.SetByte(0x0885, 249);
        transport.SetByte(0x088a, 0x7e);

        AssertInitializationRejectedWithoutWrites(transport);
        SequenceEqual(arbitraryCpu, transport.CpuBytes());
        Equal(0, transport.WriteBatches.Count);
        True(transport.Disposed);
    }

    private static void ExternalSystemOwnershipIsRefused()
    {
        FakeTransport transport = new();
        transport.SetByte(0x088b, 0xff);
        transport.SetByte(0x0889, 0xff);
        AssertInitializationRejectedWithoutWrites(transport);
        int beforeSet = transport.WriteBatches.Count;
        Equal(0, beforeSet);
        Equal((byte)0xff, transport.ByteAt(0x088b));
        Equal((byte)0xff, transport.ByteAt(0x0889));
    }

    private static void CpuNativeTargetLifecycle()
    {
        FakeClock clock = new();
        FakeTransport transport = new();
        byte[] baseline = transport.CpuBytes();
        byte[] critical = transport.CriticalBytes();
        PawnIoF7bsdBackend backend = CreateBackend(transport, clock);
        backend.Initialize();

        int beforeFirstSet = transport.WriteBatches.Count;
        Equal((byte)0, backend.Set(F7bsdFan.Cpu, 0));
        AssertCpuPolicy(transport, 0);
        False(transport.CpuBytes().SequenceEqual(baseline));
        SequenceEqual(critical, transport.CriticalBytes());
        AssertOnlyCpuTargetWrites(transport.WritesSince(beforeFirstSet));

        // Every distinct code is now a distinct physical cool-temperature
        // target and is applied synchronously.
        int beforeLowSet = transport.WriteBatches.Count;
        Equal((byte)10, backend.Set(F7bsdFan.Cpu, 10));
        True(transport.WriteBatches.Count > beforeLowSet);
        AssertCpuPolicy(transport, 10);

        int beforeSecondSet = transport.WriteBatches.Count;
        Equal((byte)51, backend.Set(F7bsdFan.Cpu, 51));
        AssertCpuPolicy(transport, 51);
        SequenceEqual(critical, transport.CriticalBytes());
        AssertOnlyCpuTargetWrites(transport.WritesSince(beforeSecondSet));

        int beforeReset = transport.WriteBatches.Count;
        backend.Reset(F7bsdFan.Cpu);
        SequenceEqual(baseline, transport.CpuBytes());
        SequenceEqual(critical, transport.CriticalBytes());
        AssertOnlyCpuTargetWrites(transport.WritesSince(beforeReset));

        int afterReset = transport.WriteBatches.Count;
        backend.Reset(F7bsdFan.Cpu);
        Equal(afterReset, transport.WriteBatches.Count);
        Throws<ArgumentOutOfRangeException>(() => backend.Set(F7bsdFan.Cpu, 52));

        backend.Dispose();
        True(transport.Disposed);
    }

    private static void CpuTargetFailureRestoresB1()
    {
        FakeTransport transport = new();
        byte[] baseline = transport.CpuBytes();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        transport.FailAfterWriteCalls.Add(1);

        ThrowsAny<Exception>(() => backend.Set(F7bsdFan.Cpu, 31));
        SequenceEqual(baseline, transport.CpuBytes());

        backend.Reset(F7bsdFan.Cpu);
        SequenceEqual(baseline, transport.CpuBytes());
        backend.Dispose();
    }

    private static void PartialCpuTransitionsRestoreB1()
    {
        int stepCount = F7bsdCpuPolicy.PlanTransition(
            F7bsdCpuPolicy.GetB1MutableStates(),
            F7bsdCpuPolicy.CompileTarget(31)).Length;
        True(stepCount > 1);
        for (int failedAfter = 1; failedAfter <= stepCount; failedAfter++)
        {
            FakeTransport transport = new()
            {
                FailAfterIndividualWriteInNextBatch = failedAfter,
            };
            byte[] baseline = transport.CpuBytes();
            PawnIoF7bsdBackend backend = CreateBackend(transport);
            backend.Initialize();

            ThrowsAny<Exception>(() => backend.Set(F7bsdFan.Cpu, 31));
            SequenceEqual(baseline, transport.CpuBytes());
            SequenceEqual(
                F7bsdCpuPolicy.GetB1MutableStates(),
                F7bsdCpuPolicy.FromMutableBytes(transport.CpuBytes()));
            Throws<InvalidOperationException>(() => backend.Set(F7bsdFan.Cpu, 31));
            backend.Dispose();
            True(transport.Disposed);
        }
    }

    private static void InterruptedCpuRecoveryResumesCertifiedSuffix()
    {
        F7bsdCpuRowState[] b1 = F7bsdCpuPolicy.GetB1MutableStates();
        F7bsdCpuRowState[] target = F7bsdCpuPolicy.CompileTarget(31);
        F7bsdCpuTransitionStep[] issued =
            F7bsdCpuPolicy.PlanTransition(b1, target);
        True(issued.Length > 1);

        for (int initialFailure = 1;
            initialFailure <= issued.Length;
            initialFailure++)
        {
            F7bsdCpuRowState[] observed =
                F7bsdCpuPolicy.MaterializeTransitionPrefix(
                    b1,
                    issued,
                    initialFailure);
            F7bsdCpuTransitionStep[] recovery =
                F7bsdCpuPolicy.PlanTransitionToB1(observed);
            True(recovery.Length > 0);

            for (int recoveryFailure = 1;
                recoveryFailure <= recovery.Length;
                recoveryFailure++)
            {
                FakeTransport transport = new();
                transport.FailAfterIndividualWriteCalls[1] = initialFailure;
                transport.FailAfterIndividualWriteCalls[2] = recoveryFailure;
                PawnIoF7bsdBackend backend = CreateBackend(transport);
                backend.Initialize();

                ThrowsAny<Exception>(() => backend.Set(F7bsdFan.Cpu, 31));
                backend.Reset(F7bsdFan.Cpu);
                SequenceEqual(b1, F7bsdCpuPolicy.FromMutableBytes(
                    transport.CpuBytes()));
                backend.Dispose();
                True(transport.Disposed);
            }
        }

        // External/non-prefix corruption after an interrupted recovery is not
        // blessed merely because each byte is allowlisted or looks plausible.
        F7bsdCpuRowState[] firstObserved =
            F7bsdCpuPolicy.MaterializeTransitionPrefix(b1, issued, 1);
        F7bsdCpuTransitionStep[] firstRecovery =
            F7bsdCpuPolicy.PlanTransitionToB1(firstObserved);
        FakeTransport corrupted = new();
        corrupted.FailAfterIndividualWriteCalls[1] = 1;
        corrupted.FailAfterIndividualWriteCalls[2] = 1;
        PawnIoF7bsdBackend corruptedBackend = CreateBackend(corrupted);
        corruptedBackend.Initialize();
        ThrowsAny<Exception>(() => corruptedBackend.Set(F7bsdFan.Cpu, 31));

        F7bsdCpuRowState[] nonPrefix = (F7bsdCpuRowState[])b1.Clone();
        nonPrefix[0] = new(F7bsdProfile.MaximumCode, nonPrefix[0].Slope);
        False(F7bsdCpuPolicy.TryMatchTransitionPrefix(
            firstObserved,
            firstRecovery,
            nonPrefix,
            out _));
        corrupted.SetCpuBytes(F7bsdCpuPolicy.ToMutableBytes(nonPrefix));
        int writesBeforeRefusal = corrupted.WriteBatches.Count;
        ThrowsAny<Exception>(() => corruptedBackend.Reset(F7bsdFan.Cpu));
        Equal(writesBeforeRefusal, corrupted.WriteBatches.Count);

        corrupted.SetCpuBytes(F7bsdCpuPolicy.ToMutableBytes(b1));
        corruptedBackend.Reset(F7bsdFan.Cpu);
        corruptedBackend.Dispose();
        True(corrupted.Disposed);
    }

    private static void CpuPreconditionDriftWritesNothing()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        transport.SetByte(CpuBaseAddresses[0], 1);
        int before = transport.WriteBatches.Count;

        Throws<EcWritePreconditionException>(() => backend.Set(F7bsdFan.Cpu, 31));
        Equal(before, transport.WriteBatches.Count);
        Equal((byte)1, transport.ByteAt(CpuBaseAddresses[0]));
        Throws<InvalidOperationException>(() => backend.Set(F7bsdFan.Cpu, 31));
        backend.Dispose();
        True(transport.Disposed);
    }

    private static void CpuRestoreIgnoresMutableConfiguration()
    {
        FakeTransport transport = new();
        byte[] baseline = transport.CpuBytes();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        backend.Set(F7bsdFan.Cpu, 31);
        byte[] originalConfiguration = F7bsdProfile.CpuConfigurationAddresses
            .Select(transport.ByteAt)
            .ToArray();

        // Selector, bands, and the critical row are firmware-owned. Restoration
        // must refuse to combine our mutable rows with a different policy.
        transport.SetByte(0x032f, 0xa5);
        transport.SetByte(0x0311, 99);
        transport.SetByte(0x0312, 98);
        byte[] changedCritical = [4, 97, 91, 13];
        for (int index = 0; index < CpuCriticalAddresses.Length; index++)
        {
            transport.SetByte(CpuCriticalAddresses[index], changedCritical[index]);
        }

        int beforeReset = transport.WriteBatches.Count;
        ThrowsAny<Exception>(() => backend.Reset(F7bsdFan.Cpu));
        Equal(beforeReset, transport.WriteBatches.Count);
        SequenceEqual(changedCritical, transport.CriticalBytes());

        for (int index = 0;
            index < F7bsdProfile.CpuConfigurationAddresses.Length;
            index++)
        {
            transport.SetByte(
                F7bsdProfile.CpuConfigurationAddresses[index],
                originalConfiguration[index]);
        }
        backend.Reset(F7bsdFan.Cpu);
        SequenceEqual(baseline, transport.CpuBytes());
        Throws<InvalidOperationException>(() => backend.Set(F7bsdFan.Cpu, 31));

        backend.Dispose();
        True(transport.Disposed);
    }

    private static void CpuRestoreToleratesFirmwareTargetTransient()
    {
        FakeTransport transport = new();
        byte[] baseline = transport.CpuBytes();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        backend.Set(F7bsdFan.Cpu, 0);

        // The EC can briefly expose an unsigned out-of-band result in its
        // firmware-owned target byte when a stale high row sees a large
        // cooling/resume jump. That output must not strand our certified table.
        transport.SetByte(F7bsdProfile.CpuTargetAddress, 157);
        backend.Reset(F7bsdFan.Cpu);
        SequenceEqual(baseline, transport.CpuBytes());
        Equal((byte)157, transport.ByteAt(F7bsdProfile.CpuTargetAddress));
        backend.Dispose();
        True(transport.Disposed);

        // Normal mutations remain strict: an out-of-range target aborts the
        // request, restores exact B1, and never blesses the transient value.
        FakeTransport strict = new();
        PawnIoF7bsdBackend strictBackend = CreateBackend(strict);
        strictBackend.Initialize();
        strictBackend.Set(F7bsdFan.Cpu, 18);
        strict.SetByte(F7bsdProfile.CpuTargetAddress, 157);
        Throws<IOException>(() => strictBackend.Set(F7bsdFan.Cpu, 20));
        SequenceEqual(baseline, strict.CpuBytes());
        strictBackend.Dispose();
        True(strict.Disposed);

        // Recovery still refuses a broken temperature path or a host/foreign
        // temperature override even when the mutable table is certified.
        foreach ((ushort address, byte invalid) in new[]
        {
            (F7bsdProfile.CpuEffectiveTemperatureAddress, (byte)0),
            (F7bsdProfile.CpuTemperatureOverrideAddress, (byte)1),
        })
        {
            FakeTransport unsafeTransport = new();
            PawnIoF7bsdBackend unsafeBackend = CreateBackend(unsafeTransport);
            unsafeBackend.Initialize();
            unsafeBackend.Set(F7bsdFan.Cpu, 18);
            unsafeTransport.SetByte(F7bsdProfile.CpuTargetAddress, 157);
            unsafeTransport.SetByte(address, invalid);
            int beforeReset = unsafeTransport.WriteBatches.Count;
            ThrowsAny<Exception>(() => unsafeBackend.Reset(F7bsdFan.Cpu));
            Equal(beforeReset, unsafeTransport.WriteBatches.Count);

            unsafeTransport.SetByte(
                F7bsdProfile.CpuEffectiveTemperatureAddress,
                56);
            unsafeTransport.SetByte(
                F7bsdProfile.CpuTemperatureOverrideAddress,
                0);
            unsafeBackend.Reset(F7bsdFan.Cpu);
            SequenceEqual(baseline, unsafeTransport.CpuBytes());
            unsafeBackend.Dispose();
            True(unsafeTransport.Disposed);
        }
    }

    private static void SystemOwnershipLifecycle()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();

        transport.SetByte(0x088a, 0x7e);
        int start = transport.WriteBatches.Count;
        Equal((byte)17, backend.Set(F7bsdFan.System, 17));
        SequenceEqual(
            [new EcWrite(0x088b, 0xff), new EcWrite(0x0885, 17)],
            transport.WritesSince(start));
        AssertSystemOwned(transport, 17);
        AssertOnlySystemControlWrites(transport.WritesSince(start));
        AssertOwnershipWasVerifiedBetweenWrites(transport, 17);

        int readStart = transport.ReadBatches.Count;
        start = transport.WriteBatches.Count;
        Equal((byte)17, backend.Set(F7bsdFan.System, 17));
        Equal(readStart, transport.ReadBatches.Count);
        Equal(start, transport.WriteBatches.Count);

        Equal((byte)23, backend.Set(F7bsdFan.System, 23));
        AssertSystemOwned(transport, 23);

        start = transport.WriteBatches.Count;
        backend.Reset(F7bsdFan.System);
        SequenceEqual(
            [new EcWrite(0x0885, 51), new EcWrite(0x088b, 0)],
            transport.WritesSince(start));
        Equal((byte)0, transport.ByteAt(0x088b));
        Equal(transport.ByteAt(0x0305), transport.ByteAt(0x0889));

        int afterReset = transport.WriteBatches.Count;
        backend.Reset(F7bsdFan.System);
        Equal(afterReset, transport.WriteBatches.Count);
        backend.Dispose();
        True(transport.Disposed);
    }

    private static void SystemOwnershipPollingIsBounded()
    {
        FakeClock delayedClock = new();
        FakeTransport delayed = new() { OwnershipReadsBeforeEffective = 15 };
        PawnIoF7bsdBackend delayedBackend = CreateBackend(delayed, delayedClock);
        delayedBackend.Initialize();
        Equal((byte)22, delayedBackend.Set(F7bsdFan.System, 22));
        Equal(16, delayed.OwnershipPollReads);
        Equal(15, delayedClock.Sleeps.Count);
        True(delayedClock.Sleeps.All(delay => delay == TimeSpan.FromMilliseconds(100)));
        delayed.ReleaseReadsBeforeEffective = 15;
        delayedBackend.Reset(F7bsdFan.System);
        Equal(16, delayed.ReleasePollReads);
        Equal(30, delayedClock.Sleeps.Count);
        True(delayedClock.Sleeps.Skip(15).All(delay =>
            delay == TimeSpan.FromMilliseconds(100)));
        True(delayed.ReadBatches
            .Where(addresses => addresses.Length == 1 &&
                addresses[0] == F7bsdProfile.SystemEffectiveTemperatureAddress)
            .Count() >= 32);
        delayedBackend.Dispose();

        FakeClock timeoutClock = new();
        FakeTransport timeout = new() { AutoSystemOwnership = false };
        PawnIoF7bsdBackend timeoutBackend = CreateBackend(timeout, timeoutClock);
        timeoutBackend.Initialize();
        ThrowsAny<Exception>(() => timeoutBackend.Set(F7bsdFan.System, 20));
        // Sixteen effective-byte polls, one final state read, and one cleanup
        // snapshot occur before the verified release.
        Equal(18, timeout.OwnershipPollReads);
        Equal(15, timeoutClock.Sleeps.Count);
        Equal((byte)51, timeout.ByteAt(0x0885));
        Equal((byte)0, timeout.ByteAt(0x088b));
        int reads = timeout.ReadBatches.Count;
        int writes = timeout.WriteBatches.Count;
        Throws<InvalidOperationException>(() => timeoutBackend.Set(F7bsdFan.System, 51));
        Equal(reads, timeout.ReadBatches.Count);
        Equal(writes, timeout.WriteBatches.Count);
        timeoutBackend.Dispose();
    }

    private static void VerifiedSystemFaultDoesNotPoisonCpu()
    {
        FakeClock clock = new();
        FakeTransport transport = new() { AutoSystemOwnership = false };
        byte[] baseline = transport.CpuBytes();
        PawnIoF7bsdBackend backend = CreateBackend(transport, clock);
        backend.Initialize();

        Equal((byte)10, backend.Set(F7bsdFan.Cpu, 10));
        AssertCpuPolicy(transport, 10);
        ThrowsAny<Exception>(() => backend.Set(F7bsdFan.System, 30));
        Equal((byte)0, transport.ByteAt(0x088b));
        Equal<byte?>((byte)10, backend.ReadTelemetry().CpuAppliedCode);

        clock.Advance(TimeSpan.FromSeconds(1));
        Equal((byte)18, backend.Set(F7bsdFan.Cpu, 18));
        AssertCpuPolicy(transport, 18);
        Throws<InvalidOperationException>(() => backend.Set(F7bsdFan.System, 30));

        backend.Dispose();
        SequenceEqual(baseline, transport.CpuBytes());
    }

    private static void UnsafeInitialSystemTemperatureOnlyAllowsFull()
    {
        foreach (byte raw in new byte[] { 0, 70, 120, 121, 255 })
        {
            FakeTransport transport = new();
            PawnIoF7bsdBackend backend = CreateBackend(transport);
            backend.Initialize();
            transport.SetByte(0x0305, raw);

            int writes = transport.WriteBatches.Count;
            Throws<InvalidOperationException>(() => backend.Set(F7bsdFan.System, 20));
            Equal(writes, transport.WriteBatches.Count);
            Equal((byte)0, transport.ByteAt(0x088b));

            Equal((byte)51, backend.Set(F7bsdFan.System, 51));
            AssertSystemOwned(transport, 51);
            transport.SetByte(0x0305, 44);
            backend.Reset(F7bsdFan.System);
            backend.Dispose();
        }

        FakeTransport safe = new();
        PawnIoF7bsdBackend safeBackend = CreateBackend(safe);
        safeBackend.Initialize();
        safe.SetByte(0x0305, 69);
        Equal((byte)20, safeBackend.Set(F7bsdFan.System, 20));
        AssertSystemOwned(safe, 20);
        safeBackend.Dispose();

        FakeTransport raced = new();
        PawnIoF7bsdBackend racedBackend = CreateBackend(raced);
        racedBackend.Initialize();
        raced.BeforeGuardedWrite = current =>
        {
            if (current.ByteAt(0x088b) == 0xff)
            {
                current.SetByte(0x0305, 70);
            }
        };
        Equal((byte)51, racedBackend.Set(F7bsdFan.System, 20));
        AssertSystemOwned(raced, 51);
        True(raced.AppliedWrites.Any(write =>
            write.Address == 0x0885 && write.Value == 20));
        True(raced.AppliedWrites.Any(write =>
            write.Address == 0x0885 && write.Value == 51));
        raced.SetByte(0x0305, 44);
        racedBackend.Dispose();
    }

    private static void SystemReleaseFailurePaths()
    {
        FakeClock targetClock = new();
        FakeTransport targetFailure = new();
        PawnIoF7bsdBackend targetBackend = CreateBackend(targetFailure, targetClock);
        targetBackend.Initialize();
        targetBackend.Set(F7bsdFan.System, 12);
        targetFailure.FailBeforeWriteCalls.Add(targetFailure.WriteCallCount + 1);
        ThrowsAny<Exception>(() => targetBackend.Reset(F7bsdFan.System));
        Equal((byte)0, targetFailure.ByteAt(0x088b));
        Equal(targetFailure.ByteAt(0x0305), targetFailure.ByteAt(0x0889));
        int reads = targetFailure.ReadBatches.Count;
        int writes = targetFailure.WriteBatches.Count;
        Throws<InvalidOperationException>(() => targetBackend.Set(F7bsdFan.System, 20));
        Equal(reads, targetFailure.ReadBatches.Count);
        Equal(writes, targetFailure.WriteBatches.Count);
        targetBackend.Dispose();

        FakeClock clearClock = new();
        FakeTransport clearFailure = new();
        PawnIoF7bsdBackend clearBackend = CreateBackend(clearFailure, clearClock);
        clearBackend.Initialize();
        clearBackend.Set(F7bsdFan.System, 12);
        clearFailure.FailBeforeWriteCalls.Add(clearFailure.WriteCallCount + 2);
        ThrowsAny<Exception>(() => clearBackend.Reset(F7bsdFan.System));
        Equal((byte)0xff, clearFailure.ByteAt(0x088b));
        reads = clearFailure.ReadBatches.Count;
        writes = clearFailure.WriteBatches.Count;
        Throws<InvalidOperationException>(() => clearBackend.ReadTelemetry());
        Equal(reads, clearFailure.ReadBatches.Count);
        Equal(writes, clearFailure.WriteBatches.Count);

        clearBackend.Reset(F7bsdFan.System);
        Equal((byte)0, clearFailure.ByteAt(0x088b));
        reads = clearFailure.ReadBatches.Count;
        writes = clearFailure.WriteBatches.Count;
        Throws<InvalidOperationException>(() => clearBackend.Set(F7bsdFan.System, 20));
        Equal(reads, clearFailure.ReadBatches.Count);
        Equal(writes, clearFailure.WriteBatches.Count);
        clearBackend.Dispose();

        FakeClock staleClock = new();
        FakeTransport staleEffective = new();
        PawnIoF7bsdBackend staleBackend = CreateBackend(staleEffective, staleClock);
        staleBackend.Initialize();
        staleBackend.Set(F7bsdFan.System, 12);
        staleEffective.AutoSystemRelease = false;
        ThrowsAny<Exception>(() => staleBackend.Reset(F7bsdFan.System));
        Equal((byte)0, staleEffective.ByteAt(0x088b));
        Equal((byte)0xff, staleEffective.ByteAt(0x0889));
        reads = staleEffective.ReadBatches.Count;
        writes = staleEffective.WriteBatches.Count;
        Throws<InvalidOperationException>(() => staleBackend.ReadTelemetry());
        Equal(reads, staleEffective.ReadBatches.Count);
        Equal(writes, staleEffective.WriteBatches.Count);
        Throws<InvalidOperationException>(() => staleBackend.Set(F7bsdFan.Cpu, 18));
        Equal(reads, staleEffective.ReadBatches.Count);
        Equal(writes, staleEffective.WriteBatches.Count);
        Throws<InvalidOperationException>(() => staleBackend.Set(F7bsdFan.System, 51));
        Equal(reads, staleEffective.ReadBatches.Count);
        Equal(writes, staleEffective.WriteBatches.Count);

        staleEffective.SetByte(0x0889, staleEffective.ByteAt(0x0305));
        staleBackend.Reset(F7bsdFan.System);
        reads = staleEffective.ReadBatches.Count;
        writes = staleEffective.WriteBatches.Count;
        Throws<InvalidOperationException>(() => staleBackend.Set(F7bsdFan.System, 51));
        Equal(reads, staleEffective.ReadBatches.Count);
        Equal(writes, staleEffective.WriteBatches.Count);
        staleBackend.Dispose();
    }

    private static void SystemThermalGuardPrecedesTachRetries()
    {
        FakeClock clock = new();
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport, clock);
        backend.Initialize();
        backend.Set(F7bsdFan.System, 20);

        transport.SetByte(0x0305, 69);
        int writes = transport.WriteBatches.Count;
        Equal<byte?>((byte)20, backend.ReadTelemetry().SystemAppliedCode);
        Equal(writes, transport.WriteBatches.Count);

        transport.SetByte(0x0305, 70);
        transport.UnstableTelemetryReadsRemaining = 3;
        int readStart = transport.ReadBatches.Count;
        writes = transport.WriteBatches.Count;
        Throws<IOException>(() => backend.ReadTelemetry());
        SequenceEqual(
            [new EcWrite(0x0885, 51)],
            transport.WritesSince(writes));
        AssertSystemOwned(transport, 51);
        ushort[][] guardReads = transport.ReadBatches.Skip(readStart).ToArray();
        Equal(1, guardReads.Count(addresses =>
            addresses.SequenceEqual(F7bsdProfile.RuntimeTelemetryAddresses)));
        Equal(2, guardReads.Count(addresses =>
            addresses.SequenceEqual(F7bsdProfile.CpuTachAddresses)));

        transport.SetByte(0x0305, 44);
        writes = transport.WriteBatches.Count;
        F7bsdTelemetry cooled = backend.ReadTelemetry();
        Equal<byte?>((byte)51, cooled.SystemAppliedCode);
        Equal(writes, transport.WriteBatches.Count);
        Equal((byte)20, backend.Set(F7bsdFan.System, 20));
        AssertSystemOwned(transport, 20);
        backend.Dispose();
    }

    private static void SystemGuardGapReleasesToFirmware()
    {
        FakeClock clock = new();
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport, clock);
        backend.Initialize();
        backend.Set(F7bsdFan.System, 20);

        clock.Advance(TimeSpan.FromSeconds(4));
        Equal<byte?>((byte)20, backend.ReadTelemetry().SystemAppliedCode);
        AssertSystemOwned(transport, 20);

        clock.Advance(TimeSpan.FromSeconds(4) + TimeSpan.FromTicks(1));
        int writes = transport.WriteBatches.Count;
        Throws<IOException>(() => backend.ReadTelemetry());
        SequenceEqual(
            [new EcWrite(0x0885, 51), new EcWrite(0x088b, 0)],
            transport.WritesSince(writes));
        Equal((byte)0, transport.ByteAt(0x088b));
        int reads = transport.ReadBatches.Count;
        writes = transport.WriteBatches.Count;
        Throws<InvalidOperationException>(() => backend.Set(F7bsdFan.System, 20));
        Equal(reads, transport.ReadBatches.Count);
        Equal(writes, transport.WriteBatches.Count);
        backend.Dispose();

        FakeClock backwardClock = new();
        FakeTransport backward = new();
        PawnIoF7bsdBackend backwardBackend = CreateBackend(backward, backwardClock);
        backwardBackend.Initialize();
        backwardBackend.Set(F7bsdFan.System, 20);
        backwardClock.Set(TimeSpan.FromTicks(-1));
        Throws<IOException>(() => backwardBackend.ReadTelemetry());
        Equal((byte)0, backward.ByteAt(0x088b));
        backwardBackend.Dispose();
    }

    private static void PersistentOwnedTelemetryFailureReleasesSystem()
    {
        FakeClock clock = new();
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport, clock);
        backend.Initialize();
        backend.Set(F7bsdFan.System, 20);

        for (int failure = 0; failure < 2; failure++)
        {
            transport.UnstableTelemetryReadsRemaining = 3;
            Throws<IOException>(() => backend.ReadTelemetry());
            AssertSystemOwned(transport, 20);
        }

        transport.UnstableTelemetryReadsRemaining = 3;
        int writes = transport.WriteBatches.Count;
        Throws<IOException>(() => backend.ReadTelemetry());
        SequenceEqual(
            [new EcWrite(0x0885, 51), new EcWrite(0x088b, 0)],
            transport.WritesSince(writes));
        Equal((byte)0, transport.ByteAt(0x088b));
        int reads = transport.ReadBatches.Count;
        writes = transport.WriteBatches.Count;
        Throws<InvalidOperationException>(() => backend.Set(F7bsdFan.System, 20));
        Equal(reads, transport.ReadBatches.Count);
        Equal(writes, transport.WriteBatches.Count);
        backend.Dispose();
    }

    private static void SystemDriftFaultsWithoutReengaging()
    {
        FakeClock lostClock = new();
        FakeTransport lost = new();
        PawnIoF7bsdBackend lostBackend = CreateBackend(lost, lostClock);
        lostBackend.Initialize();
        lostBackend.Set(F7bsdFan.System, 17);
        lost.SetByte(0x088b, 0);
        lost.SetByte(0x0889, lost.ByteAt(0x0305));
        int writes = lost.WriteBatches.Count;
        Throws<IOException>(() => lostBackend.ReadTelemetry());
        Equal(writes, lost.WriteBatches.Count);
        False(lost.WritesSince(writes).Any(write =>
            write.Address == 0x088b && write.Value == 0xff));
        int reads = lost.ReadBatches.Count;
        Throws<InvalidOperationException>(() => lostBackend.Set(F7bsdFan.System, 17));
        Equal(reads, lost.ReadBatches.Count);
        lostBackend.Dispose();

        FakeClock driftClock = new();
        FakeTransport drift = new();
        PawnIoF7bsdBackend driftBackend = CreateBackend(drift, driftClock);
        driftBackend.Initialize();
        driftBackend.Set(F7bsdFan.System, 17);
        drift.SetByte(0x0885, 18);
        writes = drift.WriteBatches.Count;
        Throws<IOException>(() => driftBackend.ReadTelemetry());
        SequenceEqual(
            [new EcWrite(0x0885, 51), new EcWrite(0x088b, 0)],
            drift.WritesSince(writes));
        False(drift.WritesSince(writes).Any(write =>
            write.Address == 0x088b && write.Value == 0xff));
        driftBackend.Dispose();
    }

    private static void CpuZeroUsesCoolStopThermalTail()
    {
        FakeTransport transport = new();
        byte[] b1 = transport.CpuBytes();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        transport.SetByte(0x0309, 60);
        transport.SetByte(0x0888, 60);
        Equal((byte)0, backend.Set(F7bsdFan.Cpu, 0));
        AssertCpuPolicy(transport, 0);
        False(transport.CpuBytes().SequenceEqual(b1));

        F7bsdCpuRowState[] target = F7bsdCpuPolicy.CompileTarget(0);
        (int Temperature, int Heating, int Cooling)[] targets =
        [
            (30, 0, 0),
            (65, 0, 0),
            (66, 0, 10),
            (67, 10, 10),
            (73, 13, 13),
            (74, 14, 14),
            (76, 15, 19),
            (77, 20, 20),
            (82, 30, 30),
            (88, 41, 41),
            (93, 51, 51),
            (94, 51, 51),
        ];
        foreach ((int temperature, int heating, int cooling) in targets)
        {
            Equal(
                heating,
                F7bsdCpuPolicy.EvaluateTable(target, temperature, cooling: false));
            Equal(
                cooling,
                F7bsdCpuPolicy.EvaluateTable(target, temperature, cooling: true));
        }
        True(F7bsdCpuPolicy.EvaluateTable(target, 30, cooling: false) <
            F7bsdCpuPolicy.EvaluateTable(
                F7bsdCpuPolicy.GetB1MutableStates(),
                30,
                cooling: false));
        backend.Dispose();
        SequenceEqual(b1, transport.CpuBytes());
    }

    private static void CpuMutationsApplyImmediately()
    {
        FakeClock clock = new();
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport, clock);
        backend.Initialize();

        Equal((byte)17, backend.Set(F7bsdFan.Cpu, 17));
        AssertCpuPolicy(transport, 17);
        int reads = transport.ReadBatches.Count;
        int writes = transport.WriteBatches.Count;
        Equal((byte)31, backend.Set(F7bsdFan.Cpu, 31));
        True(transport.ReadBatches.Count > reads);
        True(transport.WriteBatches.Count > writes);
        AssertCpuPolicy(transport, 31);

        foreach (byte code in new byte[] { 41, 20, 0, 10, 51, 0 })
        {
            Equal(code, backend.Set(F7bsdFan.Cpu, code));
            AssertCpuPolicy(transport, code);
        }

        reads = transport.ReadBatches.Count;
        writes = transport.WriteBatches.Count;
        Equal((byte)0, backend.Set(F7bsdFan.Cpu, 0));
        Equal(reads, transport.ReadBatches.Count);
        Equal(writes, transport.WriteBatches.Count);
        Equal<byte?>((byte)0, backend.ReadTelemetry().CpuAppliedCode);
        AssertCpuPolicy(transport, 0);
        backend.Dispose();
    }

    private static void CpuBurstRequestsStayBounded()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        byte[] sequence =
        [
            .. Enumerable.Range(0, 52).Select(value => (byte)value),
            .. Enumerable.Range(0, 52).Reverse().Select(value => (byte)value),
        ];
        int expectedMutations = 0;
        byte? previous = null;
        foreach (byte code in sequence)
        {
            int before = transport.WriteBatches.Count;
            Equal(code, backend.Set(F7bsdFan.Cpu, code));
            AssertCpuPolicy(transport, code);
            if (previous != code)
            {
                expectedMutations++;
                Equal(before + 1, transport.WriteBatches.Count);
                EcWrite[] writes = transport.WriteBatches[^1];
                True(writes.Length <= F7bsdCpuPolicy.MaximumWritesPerTransition);
                AssertOnlyCpuTargetWrites(writes);
            }
            else
            {
                Equal(before, transport.WriteBatches.Count);
            }
            previous = code;
        }
        Equal(expectedMutations, transport.WriteBatches.Count);

        int beforeTelemetry = transport.WriteBatches.Count;
        Equal<byte?>((byte)0, backend.ReadTelemetry().CpuAppliedCode);
        Equal(beforeTelemetry, transport.WriteBatches.Count);
        backend.Dispose();
    }

    private static void CpuWritesIgnoreSystemGuardClock()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = new(
            static () => ExactHost,
            () => transport,
            static () => throw new IOException("Expected timestamp failure."),
            static (_, _) => throw new IOException("Expected elapsed-time failure."),
            static _ => throw new IOException("Unexpected selector sleep."));
        backend.Initialize();
        Equal((byte)17, backend.Set(F7bsdFan.Cpu, 17));
        Equal((byte)31, backend.Set(F7bsdFan.Cpu, 31));
        AssertCpuPolicy(transport, 31);
        backend.Reset(F7bsdFan.Cpu);
        SequenceEqual(
            F7bsdCpuPolicy.GetB1MutableStates(),
            F7bsdCpuPolicy.FromMutableBytes(transport.CpuBytes()));
        backend.Dispose();
    }

    private static void DuplicateControlRequestsAreCached()
    {
        FakeClock clock = new();
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport, clock);
        backend.Initialize();
        backend.Set(F7bsdFan.Cpu, 17);
        backend.Set(F7bsdFan.System, 18);

        int reads = transport.ReadBatches.Count;
        int writes = transport.WriteBatches.Count;
        Equal((byte)17, backend.Set(F7bsdFan.Cpu, 17));
        Equal((byte)18, backend.Set(F7bsdFan.System, 18));
        Equal(reads, transport.ReadBatches.Count);
        Equal(writes, transport.WriteBatches.Count);

        clock.Advance(TimeSpan.FromSeconds(4));
        Equal((byte)18, backend.Set(F7bsdFan.System, 18));
        Equal(reads, transport.ReadBatches.Count);
        Equal(writes, transport.WriteBatches.Count);
        clock.Advance(TimeSpan.FromTicks(1));
        Throws<IOException>(() => backend.Set(F7bsdFan.System, 18));
        Equal((byte)0, transport.ByteAt(0x088b));
        AssertCpuPolicy(transport, 17);
        backend.Dispose();
    }

    private static void TelemetryGuardsSystemControl()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        backend.Set(F7bsdFan.System, 26);

        int start = transport.ReadBatches.Count;
        F7bsdTelemetry telemetry = backend.ReadTelemetry();
        Equal(2_156, telemetry.CpuFanRpm);
        Equal(1_725, telemetry.SystemFanRpm);
        Equal(56, telemetry.CpuTemperatureC);
        Equal(44, telemetry.SystemTemperatureC);
        Equal<byte?>((byte)26, telemetry.SystemAppliedCode);
        True(transport.ReadBatches.Skip(start).Any(addresses =>
            addresses.SequenceEqual(F7bsdProfile.RuntimeTelemetryAddresses)));
        AssertSystemOwned(transport, 26);

        backend.Reset(F7bsdFan.System);
        backend.Dispose();
    }

    private static void CloseRestoresBothControls()
    {
        FakeTransport transport = new();
        byte[] baseline = transport.CpuBytes();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        backend.Set(F7bsdFan.Cpu, 9);
        backend.Set(F7bsdFan.System, 23);

        backend.Dispose();
        True(transport.Disposed);
        SequenceEqual(baseline, transport.CpuBytes());
        Equal((byte)51, transport.ByteAt(0x0885));
        Equal((byte)0, transport.ByteAt(0x088b));

        int afterDispose = transport.WriteBatches.Count;
        backend.Dispose();
        Equal(afterDispose, transport.WriteBatches.Count);
    }

    private static void CloseContinuesAfterRestoreFailure()
    {
        FakeTransport transport = new();
        byte[] baseline = transport.CpuBytes();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        backend.Set(F7bsdFan.Cpu, 9);
        backend.Set(F7bsdFan.System, 23);
        transport.AutoSystemRelease = false;

        ThrowsAny<Exception>(backend.Dispose);
        False(transport.Disposed);
        SequenceEqual(baseline, transport.CpuBytes());

        transport.AutoSystemRelease = true;
        transport.SetByte(0x0889, transport.ByteAt(0x0305));
        backend.Dispose();
        True(transport.Disposed);
    }

    private static void PluginSensorAndRawControlLifecycle()
    {
        FakeBackend backend = new(
            Sample(3_000, 1_900, 62, 41),
            Sample(3_100, 2_000, 63, 42, 51));
        FakeLogger logger = new();
        UM780XTXPlugin plugin = new(() => backend, logger);
        FakeContainer container = new();

        try
        {
            plugin.Initialize();
            plugin.Load(container);
            Equal(1, backend.InitializeCalls);
            Equal(4, container.FanSensors.Count + container.TempSensors.Count);
            Equal(2, container.ControlSensors.Count);
            IPluginControlSensor2 cpu = FindControl(
                container.ControlSensors,
                UM780XTXPlugin.CpuControlId);
            IPluginControlSensor2 system = FindControl(
                container.ControlSensors,
                UM780XTXPlugin.SystemControlId);
            Equal("UM780 XTX CPU Fan Target (Cool-Stop Thermal Tail)", cpu.Name);
            Equal("UM780 XTX System Fan Raw Target", system.Name);
            Equal(
                $"{plugin.Name}/minisforum.um780xtx.f7bsd.fan1",
                cpu.PairedFanSensorId);
            Equal(
                $"{plugin.Name}/minisforum.um780xtx.f7bsd.fan2",
                system.PairedFanSensorId);
            Equal(3_000f, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan1").Value);
            Equal(41f, Find(
                container.TempSensors,
                "minisforum.um780xtx.f7bsd.system-temperature").Value);

            cpu.Set(0f);
            cpu.Set(50f);
            system.Set(50f);
            SequenceEqual(
                [
                    (F7bsdFan.Cpu, (byte)0),
                    (F7bsdFan.Cpu, (byte)26),
                    (F7bsdFan.System, (byte)26),
                ],
                backend.SetCalls);
            Equal<float?>(F7bsdProfile.ToPercentage(26), cpu.Value);
            Equal<float?>(F7bsdProfile.ToPercentage(26), system.Value);
            cpu.Reset();
            SequenceEqual([F7bsdFan.Cpu], backend.ResetCalls);
            Equal<float?>(null, cpu.Value);

            plugin.Update();
            Equal(3_100f, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan1").Value);
            Equal(2_000f, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan2").Value);
            Equal(63f, Find(
                container.TempSensors,
                "minisforum.um780xtx.f7bsd.cpu-temperature").Value);
            Equal<float?>(100f, system.Value);
            system.Reset();
            SequenceEqual([F7bsdFan.Cpu, F7bsdFan.System], backend.ResetCalls);
            Equal<float?>(null, system.Value);

            plugin.Close();
            Equal(1, backend.DisposeCalls);
            Equal<float?>(null, cpu.Value);
            Equal<float?>(null, system.Value);
            Equal<float?>(null, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan1").Value);
            plugin.Close();
            Equal(1, backend.DisposeCalls);
            True(logger.Messages.Any(message => message.Contains("initialized")));
        }
        finally
        {
            plugin.Close();
        }
    }

    private static void PluginReportsImmediateCpuConfirmation()
    {
        FakeClock clock = new();
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport, clock);
        UM780XTXPlugin plugin = new(() => backend);
        FakeContainer container = new();
        try
        {
            plugin.Initialize();
            plugin.Load(container);
            IPluginControlSensor2 cpu = FindControl(
                container.ControlSensors,
                UM780XTXPlugin.CpuControlId);

            cpu.Set(F7bsdProfile.ToPercentage(17));
            Equal<float?>(F7bsdProfile.ToPercentage(17), cpu.Value);
            cpu.Set(F7bsdProfile.ToPercentage(31));
            Equal<float?>(F7bsdProfile.ToPercentage(31), cpu.Value);
            AssertCpuPolicy(transport, 31);

            int writes = transport.WriteBatches.Count;
            plugin.Update();
            Equal<float?>(F7bsdProfile.ToPercentage(31), cpu.Value);
            Equal(writes, transport.WriteBatches.Count);
            AssertCpuPolicy(transport, 31);
        }
        finally
        {
            plugin.Close();
        }
        SequenceEqual(
            F7bsdCpuPolicy.GetB1MutableStates(),
            F7bsdCpuPolicy.FromMutableBytes(transport.CpuBytes()));
        True(transport.Disposed);
    }

    private static void PluginIsolatesSynchronousCpuControlFailure()
    {
        FakeClock clock = new();
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport, clock);
        FakeLogger logger = new();
        UM780XTXPlugin plugin = new(() => backend, logger);
        FakeContainer container = new();
        IPluginControlSensor2? system = null;
        try
        {
            plugin.Initialize();
            plugin.Load(container);
            IPluginControlSensor2 cpu = FindControl(
                container.ControlSensors,
                UM780XTXPlugin.CpuControlId);
            system = FindControl(
                container.ControlSensors,
                UM780XTXPlugin.SystemControlId);

            cpu.Set(F7bsdProfile.ToPercentage(17));
            transport.FailAfterIndividualWriteCalls[2] = 1;
            cpu.Set(F7bsdProfile.ToPercentage(31));

            Equal<float?>(null, cpu.Value);
            Equal<float?>(2_156f, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan1").Value);
            SequenceEqual(
                F7bsdCpuPolicy.GetB1MutableStates(),
                F7bsdCpuPolicy.FromMutableBytes(transport.CpuBytes()));
            True(logger.Messages.Any(message => message.Contains(
                "CPU control failed",
                StringComparison.Ordinal)));
            False(logger.Messages.Any(message => message.Contains(
                "telemetry read failed",
                StringComparison.Ordinal)));

            int writesAfterCpuFault = transport.WriteBatches.Count;
            cpu.Set(F7bsdProfile.ToPercentage(41));
            Equal(writesAfterCpuFault, transport.WriteBatches.Count);

            system.Set(F7bsdProfile.ToPercentage(30));
            Equal<float?>(F7bsdProfile.ToPercentage(30), system.Value);
        }
        finally
        {
            system?.Reset();
            plugin.Close();
        }
        True(transport.Disposed);
    }

    private static void PluginTelemetryErrorClearsStaleValues()
    {
        FakeBackend backend = new(
            Sample(2_800, 1_700, 60, 39),
            Sample(2_900, 1_800, 61, 40));
        backend.FailReadCalls.Add(2);
        FakeLogger logger = new();
        UM780XTXPlugin plugin = new(() => backend, logger);
        FakeContainer container = new();

        try
        {
            plugin.Initialize();
            plugin.Load(container);
            Equal(2, container.ControlSensors.Count);
            IPluginControlSensor2 system = FindControl(
                container.ControlSensors,
                UM780XTXPlugin.SystemControlId);
            system.Set(50f);
            True(system.Value.HasValue);

            plugin.Update();
            Equal<float?>(null, system.Value);
            Equal<float?>(null, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan1").Value);
            Equal<float?>(null, Find(
                container.TempSensors,
                "minisforum.um780xtx.f7bsd.system-temperature").Value);
            Equal(0, backend.DisposeCalls);
            True(logger.Messages.Any(message =>
                message.Contains("telemetry read failed", StringComparison.Ordinal)));

            plugin.Update();
            Equal(2_900f, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan1").Value);
            Equal(1_800f, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan2").Value);
            Equal(61f, Find(
                container.TempSensors,
                "minisforum.um780xtx.f7bsd.cpu-temperature").Value);
        }
        finally
        {
            plugin.Close();
        }
    }

    private static void PluginControlFailuresAreContained()
    {
        FakeBackend backend = new(Sample(2_800, 1_700, 60, 39));
        backend.FailSetCalls.Add(1);
        backend.FailResetCalls.Add(1);
        FakeLogger logger = new();
        UM780XTXPlugin plugin = new(() => backend, logger);
        FakeContainer container = new();
        try
        {
            plugin.Initialize();
            plugin.Load(container);
            IPluginControlSensor2 cpu = FindControl(
                container.ControlSensors,
                UM780XTXPlugin.CpuControlId);
            cpu.Set(50f);
            Equal<float?>(null, cpu.Value);
            Equal(1, backend.SetCalls.Count);
            cpu.Set(60f);
            Equal(1, backend.SetCalls.Count);
            cpu.Reset();
            Equal<float?>(null, cpu.Value);
            Equal(1, backend.ResetCalls.Count);
            cpu.Set(50f);
            Equal(1, backend.SetCalls.Count);
            cpu.Reset();
            Equal(2, backend.ResetCalls.Count);
            cpu.Set(50f);
            Equal(2, backend.SetCalls.Count);
            True(cpu.Value.HasValue);
            True(logger.Messages.Count(message =>
                message.Contains("CPU control failed", StringComparison.Ordinal)) == 2);
        }
        finally
        {
            plugin.Close();
        }
    }

    private static void PluginPersistentTelemetryFailureLatches()
    {
        FakeBackend backend = new(Sample(2_800, 1_700, 60, 39));
        backend.FailReadCalls.UnionWith([2, 3, 4, 5]);
        FakeLogger logger = new();
        UM780XTXPlugin plugin = new(() => backend, logger);
        FakeContainer container = new();
        try
        {
            plugin.Initialize();
            plugin.Load(container);
            IPluginControlSensor2 system = FindControl(
                container.ControlSensors,
                UM780XTXPlugin.SystemControlId);
            for (int failure = 0; failure < 3; failure++)
            {
                plugin.Update();
            }
            Equal(4, backend.ReadCalls);
            plugin.Update();
            Equal(4, backend.ReadCalls);
            system.Set(50f);
            Equal(0, backend.SetCalls.Count);
            system.Reset();
            Equal(1, backend.ResetCalls.Count);
            system.Set(50f);
            Equal(0, backend.SetCalls.Count);
            True(logger.Messages.Any(message => message.Contains(
                "disabled after three consecutive failures",
                StringComparison.Ordinal)));
        }
        finally
        {
            plugin.Close();
        }
    }

    private static void PluginInitializationFailureCleanup()
    {
        FakeBackend initializeFailure = new(Sample(1, 2, 3, 4))
        {
            InitializeException = new IOException("expected initialization failure"),
        };
        UM780XTXPlugin initializePlugin = new(() => initializeFailure);
        Throws<IOException>(initializePlugin.Initialize);
        Equal(1, initializeFailure.DisposeCalls);

        FakeBackend readFailure = new(Sample(1, 2, 3, 4));
        readFailure.FailReadCalls.Add(1);
        UM780XTXPlugin readPlugin = new(() => readFailure);
        Throws<IOException>(readPlugin.Initialize);
        Equal(1, readFailure.DisposeCalls);
    }

    private static PawnIoF7bsdBackend CreateBackend(FakeTransport transport) => new(
        static () => ExactHost,
        () => transport);

    private static PawnIoF7bsdBackend CreateBackend(
        FakeTransport transport,
        FakeClock clock) => new(
            static () => ExactHost,
            () => transport,
            clock.Timestamp,
            static (start, end) => TimeSpan.FromTicks(end - start),
            clock.Sleep);

    private static void AssertInitializationRejectedWithoutWrites(FakeTransport transport)
    {
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        Throws<PlatformNotSupportedException>(backend.Initialize);
        Equal(0, transport.WriteBatches.Count);
        True(transport.Disposed);
    }

    private static F7bsdCpuRowState[] ApplyCpuTransitionSteps(
        ReadOnlySpan<F7bsdCpuRowState> source,
        ReadOnlySpan<F7bsdCpuTransitionStep> steps)
    {
        Equal(F7bsdCpuPolicy.NormalRowCount, source.Length);
        F7bsdCpuRowState[] current = source.ToArray();
        foreach (F7bsdCpuTransitionStep step in steps)
        {
            True(step.RowIndex >= 0 &&
                step.RowIndex < F7bsdCpuPolicy.NormalRowCount);
            F7bsdCpuRowState previous = current[step.RowIndex];
            F7bsdCpuRowState next = step.ResultingState;
            bool baseChanged = previous.Base != next.Base;
            bool slopeChanged = previous.Slope != next.Slope;
            True(baseChanged ^ slopeChanged);
            Equal(
                baseChanged
                    ? new EcWrite(
                        CpuBaseAddresses[step.RowIndex],
                        next.Base)
                    : new EcWrite(
                        CpuSlopeAddresses[step.RowIndex],
                        next.Slope),
                step.Write);
            True(F7bsdCpuPolicy.IsTransitionBounded(step.RowIndex, next));
            current[step.RowIndex] = next;
        }
        return current;
    }

    private static void AssertCpuPolicy(FakeTransport transport, byte code)
    {
        SequenceEqual(
            F7bsdCpuPolicy.ToMutableBytes(F7bsdCpuPolicy.CompileTarget(code)),
            transport.CpuBytes());
    }

    private static void AssertOnlyCpuTargetWrites(IEnumerable<EcWrite> writes)
    {
        HashSet<ushort> allowed = [.. CpuRestoreAddresses];
        EcWrite[] materialized = writes.ToArray();
        True(materialized.Length > 0);
        True(materialized.All(write => allowed.Contains(write.Address)));
        False(materialized.Any(write => CpuCriticalAddresses.Contains(write.Address)));
        AssertNoPolicyOrPwmWrites(materialized);
    }

    private static void AssertNoPolicyOrPwmWrites(IEnumerable<EcWrite> writes)
    {
        ushort[] forbidden = [0x0331, 0x0334, 0x0337, 0x1803, 0x1804];
        False(writes.Any(write => forbidden.Contains(write.Address)));
    }

    private static void AssertOnlySystemControlWrites(IEnumerable<EcWrite> writes)
    {
        EcWrite[] materialized = writes.ToArray();
        True(materialized.Length > 0);
        True(materialized.All(write =>
            (write.Address == F7bsdProfile.SystemTargetAddress &&
                write.Value <= F7bsdProfile.MaximumCode) ||
            (write.Address == F7bsdProfile.SystemTemperatureOverrideAddress &&
                write.Value is 0 or F7bsdProfile.SystemSentinel)));
        AssertNoPolicyOrPwmWrites(materialized);
    }

    private static void AssertSystemOwned(FakeTransport transport, byte code)
    {
        Equal((byte)0xff, transport.ByteAt(0x088b));
        Equal((byte)0xff, transport.ByteAt(0x0889));
        Equal(code, transport.ByteAt(0x0885));
    }

    private static void AssertOwnershipWasVerifiedBetweenWrites(
        FakeTransport transport,
        byte targetCode)
    {
        int sentinelOperation = transport.Operations.FindIndex(item =>
            item == "W:088B=FF");
        int targetOperation = transport.Operations.FindIndex(item =>
            item == $"W:0885={targetCode:X2}");
        True(sentinelOperation >= 0);
        True(targetOperation > sentinelOperation);
        True(transport.Operations
            .Skip(sentinelOperation + 1)
            .Take(targetOperation - sentinelOperation - 1)
            .Any(item => item.StartsWith("R:", StringComparison.Ordinal) &&
                item.Contains("0889", StringComparison.Ordinal)));
    }

    private static byte[] TelemetryBytes(
        ushort cpuCounter,
        ushort systemCounter,
        byte cpuTemperature,
        byte systemTemperature)
    {
        byte cpuLow = (byte)cpuCounter;
        byte systemLow = (byte)systemCounter;
        return
        [
            cpuLow,
            (byte)(cpuCounter >> 8),
            cpuLow,
            systemLow,
            (byte)(systemCounter >> 8),
            systemLow,
            cpuTemperature,
            systemTemperature,
        ];
    }

    private static F7bsdTelemetry Sample(
        int cpuRpm,
        int systemRpm,
        int cpuTemperature,
        int systemTemperature,
        byte? systemAppliedCode = null) => new(
            cpuRpm,
            systemRpm,
            cpuTemperature,
            systemTemperature,
            systemAppliedCode);

    private static FakePawnIoExecutor KnownControllerExecutor(List<string> trace)
    {
        FakePawnIoExecutor executor = new(trace);
        for (int index = 0;
            index < F7bsdProfile.ControllerProfileAddresses.Length;
            index++)
        {
            executor.SetByte(
                F7bsdProfile.ControllerProfileAddresses[index],
                F7bsdProfile.ExpectedControllerProfile[index]);
        }
        return executor;
    }

    private static IPluginSensor Find(List<IPluginSensor> sensors, string id) =>
        sensors.Single(sensor => sensor.Id == id);

    private static IPluginControlSensor2 FindControl(
        List<IPluginControlSensor> sensors,
        string id) =>
        (IPluginControlSensor2)sensors.Single(sensor => sensor.Id == id);

    private static void NotNull<T>(T? value)
        where T : class
    {
        if (value is null)
        {
            throw new InvalidOperationException("Expected a non-null value.");
        }
    }

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static void False(bool condition) => True(!condition);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}; found {actual}.");
        }
    }

    private static void SequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual)
    {
        T[] expectedArray = expected.ToArray();
        T[] actualArray = actual.ToArray();
        if (!expectedArray.SequenceEqual(actualArray))
        {
            throw new InvalidOperationException(
                "Expected [" + string.Join(", ", expectedArray) + "]; found [" +
                string.Join(", ", actualArray) + "].");
        }
    }

    private static void Throws<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void ThrowsAny<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static T Capture<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class FakeContainer : IPluginSensorsContainer
    {
        public List<IPluginControlSensor> ControlSensors { get; } = [];
        public List<IPluginSensor> FanSensors { get; } = [];
        public List<IPluginSensor> TempSensors { get; } = [];
    }

    private sealed class FakeLogger : IPluginLogger
    {
        internal List<string> Messages { get; } = [];

        public void Log(string message) => Messages.Add(message);
    }

    private sealed class FakeIsaMutex(List<string> trace) : IIsaMutex
    {
        internal Queue<bool> WaitResults { get; } = [];

        internal bool AbandonNextWait { get; set; }

        internal int WaitCalls { get; private set; }

        internal int ReleaseCalls { get; private set; }

        internal bool Disposed { get; private set; }

        public bool WaitOne(TimeSpan timeout)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            WaitCalls++;
            trace.Add("MUTEX:WAIT");
            if (AbandonNextWait)
            {
                AbandonNextWait = false;
                throw new AbandonedMutexException();
            }
            return WaitResults.TryDequeue(out bool result) ? result : true;
        }

        public void ReleaseMutex()
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            ReleaseCalls++;
            trace.Add("MUTEX:RELEASE");
        }

        public void Dispose()
        {
            Disposed = true;
            trace.Add("MUTEX:DISPOSE");
        }
    }

    private sealed class FakePawnIoExecutor(List<string> trace) : IPawnIoExecutor
    {
        private readonly Dictionary<ushort, byte> memory = [];
        private ushort currentAddress;

        internal List<string> Operations { get; } = [];

        internal HashSet<int> FailExecuteCalls { get; } = [];

        internal int? FailExecuteCall
        {
            init
            {
                if (value.HasValue)
                {
                    FailExecuteCalls.Add(value.Value);
                }
            }
        }

        internal bool IgnoreEcWrites { get; set; }

        internal int ExecuteCalls { get; private set; }

        internal byte Depth2Selector { get; private set; }

        internal byte OuterIndex { get; private set; }

        internal bool Disposed { get; private set; }

        public ulong[] Execute(string name, ulong[] input, int outputCount)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            ExecuteCalls++;
            string operation = Describe(name, input);
            Operations.Add(operation);
            trace.Add(operation);
            if (FailExecuteCalls.Contains(ExecuteCalls))
            {
                throw new IOException($"Expected native failure {ExecuteCalls}.");
            }

            ulong[] output = name switch
            {
                "ioctl_select_slot" => [],
                "ioctl_pio_outb" => ExecutePioOut(input),
                "ioctl_superio_outb" => ExecuteSuperIoOut(input),
                "ioctl_superio_inb" => ExecuteSuperIoIn(input),
                _ => throw new InvalidOperationException(
                    $"Unexpected fake PawnIO export {name}."),
            };
            Equal(outputCount, output.Length);
            return output;
        }

        public void Dispose()
        {
            Disposed = true;
            trace.Add("NATIVE:DISPOSE");
        }

        internal void SetByte(ushort address, byte value) => memory[address] = value;

        private ulong[] ExecutePioOut(ulong[] input)
        {
            Equal(2, input.Length);
            if (input[0] == 0x2e)
            {
                OuterIndex = checked((byte)input[1]);
            }
            return [];
        }

        private ulong[] ExecuteSuperIoOut(ulong[] input)
        {
            Equal(2, input.Length);
            byte port = checked((byte)input[0]);
            byte value = checked((byte)input[1]);
            if (port == 0x2e)
            {
                Depth2Selector = value;
            }
            else if (port == 0x2f)
            {
                switch (Depth2Selector)
                {
                    case 0x11:
                        currentAddress = (ushort)((currentAddress & 0x00ff) | (value << 8));
                        break;
                    case 0x10:
                        currentAddress = (ushort)((currentAddress & 0xff00) | value);
                        break;
                    case 0x12 when !IgnoreEcWrites:
                        memory[currentAddress] = value;
                        break;
                }
            }
            return [];
        }

        private ulong[] ExecuteSuperIoIn(ulong[] input)
        {
            Equal(1, input.Length);
            ulong register = input[0];
            if (register == 0x2f)
            {
                return [ByteAt(currentAddress)];
            }
            int identityIndex = checked((int)register - 0x20);
            if (identityIndex is >= 0 and < 3)
            {
                return [F7bsdProfile.ExpectedPnpIdentity[identityIndex]];
            }
            throw new InvalidOperationException(
                $"Unexpected fake Super-I/O read 0x{register:X}.");
        }

        private byte ByteAt(ushort address) => memory.TryGetValue(address, out byte value)
            ? value
            : (byte)0;

        private static string Describe(string name, ulong[] input) => name switch
        {
            "ioctl_select_slot" => $"SELECT:{input[0]}",
            "ioctl_pio_outb" => $"POUT:{input[0]:X4}={input[1]:X2}",
            "ioctl_superio_outb" => $"SOUT:{input[0]:X4}={input[1]:X2}",
            "ioctl_superio_inb" => $"SIN:{input[0]:X4}",
            _ => name,
        };
    }

    private sealed class FakeClock
    {
        private long nowTicks;

        internal List<TimeSpan> Sleeps { get; } = [];

        internal long Timestamp() => nowTicks;

        internal void Advance(TimeSpan amount) => nowTicks += amount.Ticks;

        internal void Set(TimeSpan value) => nowTicks = value.Ticks;

        internal void Sleep(TimeSpan delay)
        {
            Sleeps.Add(delay);
            Advance(delay);
        }
    }

    private sealed class FakeTransport : IF7bsdTransport
    {
        private static readonly (byte Base, byte Upper, byte Lower, byte Slope)[] B1 =
        [
            (0, 25, 0, 0),
            (16, 45, 25, 10),
            (18, 54, 45, 33),
            (21, 66, 54, 58),
            (28, 76, 66, 60),
            (32, 88, 76, 16),
            (33, 93, 88, 200),
            (51, 100, 93, 0),
        ];

        private readonly Dictionary<ushort, byte> memory = [];
        private int readCalls;
        private int writeCalls;
        private bool systemOwnershipPending;
        private bool systemReleasePending;

        internal FakeTransport()
        {
            for (int index = 0;
                index < F7bsdProfile.ControllerProfileAddresses.Length;
                index++)
            {
                memory[F7bsdProfile.ControllerProfileAddresses[index]] =
                    F7bsdProfile.ExpectedControllerProfile[index];
            }
            memory[0x032f] = 0xb1;
            for (int row = 0; row < B1.Length; row++)
            {
                ushort baseAddress = (ushort)(0x0310 + (row * 3));
                memory[baseAddress] = B1[row].Base;
                memory[(ushort)(baseAddress + 1)] = B1[row].Upper;
                memory[(ushort)(baseAddress + 2)] = B1[row].Lower;
                memory[(ushort)(0x08b0 + row)] = B1[row].Slope;
            }
            memory[0x0331] = 25;
            memory[0x0334] = 83;
            memory[0x0337] = 100;
            InstallTelemetry(TelemetryBytes(1_000, 1_250, 56, 44));
            memory[0x0888] = 56;
            memory[0x0889] = 44;
            memory[0x088a] = 0;
            memory[0x088b] = 0;
            memory[0x0884] = 0;
            memory[0x0885] = 0;
        }

        internal List<ushort[]> ReadBatches { get; } = [];

        internal List<EcWrite[]> WriteBatches { get; } = [];

        internal List<EcWrite[]> AppliedWriteBatches { get; } = [];

        internal List<string> Operations { get; } = [];

        internal HashSet<int> FailReadCalls { get; } = [];

        internal HashSet<int> FailBeforeWriteCalls { get; } = [];

        internal HashSet<int> FailAfterWriteCalls { get; } = [];

        internal Dictionary<int, int> FailAfterIndividualWriteCalls { get; } = [];

        internal int? FailAfterIndividualWriteInNextBatch { get; set; }

        internal bool AutoSystemOwnership { get; set; } = true;

        internal bool AutoSystemRelease { get; set; } = true;

        internal int OwnershipReadsBeforeEffective { get; set; }

        internal int OwnershipPollReads { get; private set; }

        internal int ReleaseReadsBeforeEffective { get; set; }

        internal int ReleasePollReads { get; private set; }

        internal Action<FakeTransport>? BeforeGuardedWrite { get; set; }

        internal int UnstableTelemetryReadsRemaining { get; set; }

        internal int WriteCallCount => writeCalls;

        internal bool Disposed { get; private set; }

        internal byte[] PnpIdentity { get; set; } =
            (byte[])F7bsdProfile.ExpectedPnpIdentity.Clone();

        internal IEnumerable<EcWrite> AppliedWrites =>
            AppliedWriteBatches.SelectMany(batch => batch);

        public byte[] ReadPnpIdentity()
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Operations.Add("PNP");
            return (byte[])PnpIdentity.Clone();
        }

        public byte[] Read(ushort[] addresses)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            readCalls++;
            ushort[] copy = (ushort[])addresses.Clone();
            ReadBatches.Add(copy);
            Operations.Add("R:" + string.Join(",", copy.Select(address =>
                address.ToString("X4"))));
            if (FailReadCalls.Contains(readCalls))
            {
                throw new IOException($"Expected fake read failure {readCalls}.");
            }

            if (IsSystemSelectorPoll(addresses) &&
                ByteAt(F7bsdProfile.SystemTemperatureOverrideAddress) ==
                    F7bsdProfile.SystemSentinel &&
                ByteAt(F7bsdProfile.SystemEffectiveTemperatureAddress) !=
                    F7bsdProfile.SystemSentinel)
            {
                OwnershipPollReads++;
                if (AutoSystemOwnership && systemOwnershipPending)
                {
                    if (OwnershipReadsBeforeEffective == 0)
                    {
                        memory[F7bsdProfile.SystemEffectiveTemperatureAddress] =
                            F7bsdProfile.SystemSentinel;
                        systemOwnershipPending = false;
                    }
                    else
                    {
                        OwnershipReadsBeforeEffective--;
                    }
                }
            }
            if (IsSystemSelectorPoll(addresses) &&
                ByteAt(F7bsdProfile.SystemTemperatureOverrideAddress) == 0 &&
                ByteAt(F7bsdProfile.SystemEffectiveTemperatureAddress) ==
                    F7bsdProfile.SystemSentinel &&
                systemReleasePending)
            {
                ReleasePollReads++;
                if (AutoSystemRelease)
                {
                    if (ReleaseReadsBeforeEffective == 0)
                    {
                        memory[F7bsdProfile.SystemEffectiveTemperatureAddress] =
                            memory[0x0305];
                        systemReleasePending = false;
                    }
                    else
                    {
                        ReleaseReadsBeforeEffective--;
                    }
                }
            }

            byte[] result = addresses.Select(ByteAt).ToArray();
            if (UnstableTelemetryReadsRemaining > 0 &&
                (addresses.SequenceEqual(F7bsdProfile.RuntimeTelemetryAddresses) ||
                    addresses.SequenceEqual(F7bsdProfile.CpuTachAddresses)))
            {
                UnstableTelemetryReadsRemaining--;
                result[2] = unchecked((byte)(result[0] + 1));
            }
            return result;
        }

        public void Write(EcWrite[] writes)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            F7bsdProfile.AssertWritesAllowed(writes);
            writeCalls++;
            EcWrite[] copy = (EcWrite[])writes.Clone();
            WriteBatches.Add(copy);
            foreach (EcWrite write in copy)
            {
                Operations.Add($"W:{write.Address:X4}={write.Value:X2}");
            }
            if (FailBeforeWriteCalls.Contains(writeCalls))
            {
                throw new IOException($"Expected fake pre-write failure {writeCalls}.");
            }
            int? failAfterIndividual =
                FailAfterIndividualWriteCalls.TryGetValue(
                    writeCalls,
                    out int scheduledIndividualFailure)
                    ? scheduledIndividualFailure
                    : FailAfterIndividualWriteInNextBatch;
            FailAfterIndividualWriteInNextBatch = null;
            List<EcWrite> applied = [];
            foreach (EcWrite write in copy)
            {
                memory[write.Address] = write.Value;
                applied.Add(write);
                if (write.Address == 0x088b && write.Value == 0xff &&
                    AutoSystemOwnership)
                {
                    if (OwnershipReadsBeforeEffective == 0)
                    {
                        memory[0x0889] = 0xff;
                    }
                    else
                    {
                        systemOwnershipPending = true;
                    }
                }
                if (write.Address == 0x088b && write.Value == 0 && AutoSystemRelease)
                {
                    systemOwnershipPending = false;
                    if (ReleaseReadsBeforeEffective == 0)
                    {
                        memory[0x0889] = memory[0x0305];
                    }
                    else
                    {
                        systemReleasePending = true;
                    }
                }
                if (applied.Count == failAfterIndividual)
                {
                    AppliedWriteBatches.Add(applied.ToArray());
                    throw new IOException(
                        $"Expected fake failure after individual write {applied.Count}.");
                }
            }
            AppliedWriteBatches.Add(copy);
            if (FailAfterWriteCalls.Contains(writeCalls))
            {
                throw new IOException($"Expected fake post-write failure {writeCalls}.");
            }
        }

        public byte[] WriteGuarded(
            EcExpectation[] before,
            EcWrite[] writes,
            EcExpectation[] after,
            ushort[] resultAddresses)
        {
            byte[] beforeValues = Read(before.Select(item => item.Address).ToArray());
            for (int index = 0; index < before.Length; index++)
            {
                if (beforeValues[index] != before[index].Value)
                {
                    throw new EcWritePreconditionException(
                        before[index].Address,
                        before[index].Value,
                        beforeValues[index]);
                }
            }
            BeforeGuardedWrite?.Invoke(this);
            Write(writes);
            byte[] afterValues = Read(after.Select(item => item.Address).ToArray());
            for (int index = 0; index < after.Length; index++)
            {
                if (afterValues[index] != after[index].Value)
                {
                    throw new IOException(
                        $"Expected fake postcondition failure at 0x{after[index].Address:X4}.");
                }
            }
            return Read(resultAddresses);
        }

        public void Dispose() => Disposed = true;

        internal byte ByteAt(ushort address) => memory.TryGetValue(address, out byte value)
            ? value
            : (byte)0;

        private static bool IsSystemSelectorPoll(ReadOnlySpan<ushort> addresses) =>
            addresses.SequenceEqual(F7bsdProfile.SystemOwnershipAddresses) ||
            addresses.SequenceEqual(
                F7bsdProfile.SystemEffectiveTemperaturePollAddresses);

        internal void SetByte(ushort address, byte value) => memory[address] = value;

        internal void SetCpuBytes(ReadOnlySpan<byte> values)
        {
            if (values.Length != CpuRestoreAddresses.Length)
            {
                throw new ArgumentException("Unexpected fake CPU byte count.", nameof(values));
            }
            for (int index = 0; index < CpuRestoreAddresses.Length; index++)
            {
                memory[CpuRestoreAddresses[index]] = values[index];
            }
        }

        internal byte[] CpuBytes() => CpuRestoreAddresses.Select(ByteAt).ToArray();

        internal byte[] CriticalBytes() => CpuCriticalAddresses.Select(ByteAt).ToArray();

        internal EcWrite[] WritesSince(int batchIndex) => WriteBatches
            .Skip(batchIndex)
            .SelectMany(batch => batch)
            .ToArray();

        private void InstallTelemetry(byte[] values)
        {
            for (int index = 0; index < F7bsdProfile.TelemetryAddresses.Length; index++)
            {
                memory[F7bsdProfile.TelemetryAddresses[index]] = values[index];
            }
        }
    }

    private sealed class FakeBackend(params F7bsdTelemetry[] samples) : IF7bsdBackend
    {
        private readonly Queue<F7bsdTelemetry> samples = new(samples);
        private F7bsdTelemetry? lastSample;

        internal Exception? InitializeException { get; init; }

        internal HashSet<int> FailReadCalls { get; } = [];

        internal int InitializeCalls { get; private set; }

        internal int ReadCalls { get; private set; }

        internal List<(F7bsdFan Fan, byte Code)> SetCalls { get; } = [];

        internal List<F7bsdFan> ResetCalls { get; } = [];

        internal HashSet<int> FailSetCalls { get; } = [];

        internal HashSet<int> FailResetCalls { get; } = [];

        internal int DisposeCalls { get; private set; }

        public void Initialize()
        {
            InitializeCalls++;
            if (InitializeException is not null)
            {
                throw InitializeException;
            }
        }

        public F7bsdTelemetry ReadTelemetry()
        {
            ReadCalls++;
            if (FailReadCalls.Contains(ReadCalls))
            {
                throw new IOException($"Expected fake read failure {ReadCalls}.");
            }
            if (samples.TryDequeue(out F7bsdTelemetry? sample))
            {
                lastSample = sample;
            }
            return lastSample ?? throw new InvalidOperationException("No fake telemetry.");
        }

        public byte Set(F7bsdFan fan, byte requestedCode)
        {
            SetCalls.Add((fan, requestedCode));
            if (FailSetCalls.Contains(SetCalls.Count))
            {
                throw new IOException($"Expected fake Set failure {SetCalls.Count}.");
            }
            return requestedCode;
        }

        public void Reset(F7bsdFan fan)
        {
            ResetCalls.Add(fan);
            if (FailResetCalls.Contains(ResetCalls.Count))
            {
                throw new IOException($"Expected fake Reset failure {ResetCalls.Count}.");
            }
        }

        public void Dispose() => DisposeCalls++;
    }
}
