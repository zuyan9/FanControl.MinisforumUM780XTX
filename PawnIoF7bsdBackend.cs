using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace FanControl.MinisforumUM780XTX;

internal sealed class PawnIoF7bsdBackend : IF7bsdBackend
{
    private enum CpuControlState
    {
        Ready,
        Active,
        FaultedRestored,
        FaultedMayBeModified,
    }

    private enum SystemControlState
    {
        Firmware,
        Engaging,
        Owned,
        Failsafe,
        Releasing,
        Faulted,
    }

    // Fifteen 100 ms sleeps cover the firmware selector's observed service
    // interval while keeping each wait finite. Only the effective-temperature
    // byte is polled; one full state read verifies the final handoff.
    private const int SystemSelectorPollAttempts = 16;
    private static readonly TimeSpan SystemSelectorPollDelay =
        TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SystemGuardMaximumGap = TimeSpan.FromSeconds(4);
    private readonly object sync = new();
    private readonly Func<HostIdentitySnapshot> hostReader;
    private readonly Func<IF7bsdTransport> transportFactory;
    private readonly Func<long> timestamp;
    private readonly Func<long, long, TimeSpan> elapsedTime;
    private readonly Action<TimeSpan> sleeper;
    private IF7bsdTransport? transport;
    private F7bsdCpuRowState[]? cpuExpected;
    private F7bsdCpuRowState[]? cpuRecoverySource;
    private F7bsdCpuTransitionStep[]? cpuRecoveryPlan;
    private bool cpuRecoveryPlanEndsAtB1;
    private CpuControlState cpuState;
    private byte? cpuAppliedCode;
    // Recovery latch: set before the sentinel write and retained until a
    // plausible firmware-owned temperature path is verified after release.
    private bool systemMayBeOwned;
    private SystemControlState systemState;
    private byte? systemRequestedCode;
    private byte? systemAppliedCode;
    private long? lastSystemGuardTick;
    private int consecutiveOwnedTelemetryFailures;

    internal PawnIoF7bsdBackend()
        : this(HostIdentity.Read, static () => new PawnIoTransport())
    {
    }

    internal PawnIoF7bsdBackend(
        Func<HostIdentitySnapshot> hostReader,
        Func<IF7bsdTransport> transportFactory)
        : this(
            hostReader,
            transportFactory,
            Stopwatch.GetTimestamp,
            Stopwatch.GetElapsedTime,
            static delay => Thread.Sleep(delay))
    {
    }

    internal PawnIoF7bsdBackend(
        Func<HostIdentitySnapshot> hostReader,
        Func<IF7bsdTransport> transportFactory,
        Func<long> timestamp,
        Func<long, long, TimeSpan> elapsedTime,
        Action<TimeSpan> sleeper)
    {
        this.hostReader = hostReader;
        this.transportFactory = transportFactory;
        this.timestamp = timestamp;
        this.elapsedTime = elapsedTime;
        this.sleeper = sleeper;
    }

    public void Initialize()
    {
        lock (sync)
        {
            if (transport is not null)
            {
                return;
            }

            HostIdentityGate.Assert(hostReader());
            IF7bsdTransport candidate = transportFactory();
            try
            {
                if (!candidate.ReadPnpIdentity().SequenceEqual(
                    F7bsdProfile.ExpectedPnpIdentity))
                {
                    throw new PlatformNotSupportedException(
                        "The physical Super-I/O is not the UM780 XTX IT5571 profile.");
                }
                if (!candidate.Read(F7bsdProfile.ControllerProfileAddresses)
                    .SequenceEqual(F7bsdProfile.ExpectedControllerProfile))
                {
                    throw new PlatformNotSupportedException(
                        "The live controller is not the UM780 XTX F7BSD IT5571 profile.");
                }

                byte[] firstCpuSnapshot = candidate.Read(
                    F7bsdProfile.CpuControlSnapshotAddresses);
                Thread.Sleep(20);
                byte[] secondCpuSnapshot = candidate.Read(
                    F7bsdProfile.CpuControlSnapshotAddresses);
                byte[] firstCpu = F7bsdProfile.ValidateCpuControlSnapshot(
                    firstCpuSnapshot);
                byte[] capturedCpu = F7bsdProfile.ValidateCpuControlSnapshot(
                    secondCpuSnapshot);
                if (!firstCpu.SequenceEqual(capturedCpu))
                {
                    throw new PlatformNotSupportedException(
                        "The CPU policy changed during bounded startup capture.");
                }
                F7bsdProfile.ValidateSystemThresholds(
                    candidate.Read(F7bsdProfile.SystemThresholdAddresses));
                F7bsdProfile.ValidateStartupState(
                    candidate.Read(F7bsdProfile.StartupStateAddresses));

                transport = candidate;
                cpuExpected = F7bsdCpuPolicy.FromMutableBytes(capturedCpu);
                ClearCpuRecoveryCertificate();
                cpuState = CpuControlState.Ready;
                cpuAppliedCode = null;
                systemMayBeOwned = false;
                systemState = SystemControlState.Firmware;
                systemRequestedCode = null;
                systemAppliedCode = null;
                lastSystemGuardTick = null;
                consecutiveOwnedTelemetryFailures = 0;
            }
            catch
            {
                candidate.Dispose();
                throw;
            }
        }
    }

    public F7bsdTelemetry ReadTelemetry()
    {
        lock (sync)
        {
            if (systemState == SystemControlState.Faulted && systemMayBeOwned)
            {
                throw new InvalidOperationException(
                    "System ownership cleanup is pending. Only Reset or Close may " +
                    "retry the bounded release transaction.");
            }
            IF7bsdTransport active = ActiveTransport();
            F7bsdTelemetry telemetry;
            try
            {
                telemetry = ReadStableTelemetry(active);
                consecutiveOwnedTelemetryFailures = 0;
            }
            catch (Exception failure)
            {
                if (systemMayBeOwned && systemState is
                    SystemControlState.Owned or SystemControlState.Failsafe)
                {
                    consecutiveOwnedTelemetryFailures++;
                    if (consecutiveOwnedTelemetryFailures >= 3)
                    {
                        systemState = SystemControlState.Faulted;
                        ThrowAfterSystemRelease(
                            active,
                            new IOException(
                                "Three consecutive guarded telemetry samples failed.",
                                failure));
                    }
                }
                throw;
            }

            return telemetry with
            {
                CpuAppliedCode = cpuState == CpuControlState.Active
                    ? cpuAppliedCode
                    : null,
            };
        }
    }

    public byte Set(F7bsdFan fan, byte requestedCode)
    {
        if (requestedCode > F7bsdProfile.MaximumCode)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedCode));
        }

        lock (sync)
        {
            IF7bsdTransport active = ActiveTransport();
            if (systemState == SystemControlState.Faulted && systemMayBeOwned)
            {
                throw new InvalidOperationException(
                    "System ownership cleanup is pending. Only Reset or Close may " +
                    "retry the bounded release transaction.");
            }
            return fan switch
            {
                F7bsdFan.Cpu => SetCpu(active, requestedCode),
                F7bsdFan.System => SetSystem(active, requestedCode),
                _ => throw new ArgumentOutOfRangeException(nameof(fan)),
            };
        }
    }

    public void Reset(F7bsdFan fan)
    {
        lock (sync)
        {
            IF7bsdTransport active = ActiveTransport();
            switch (fan)
            {
                case F7bsdFan.Cpu:
                    RestoreCpu(active);
                    break;
                case F7bsdFan.System:
                    bool wasFaulted = systemState == SystemControlState.Faulted;
                    if (systemMayBeOwned)
                    {
                        ReleaseSystem(active);
                    }
                    if (wasFaulted)
                    {
                        systemState = SystemControlState.Faulted;
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(fan));
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            IF7bsdTransport? old = transport;
            if (old is null)
            {
                return;
            }

            List<Exception> errors = [];
            if (systemMayBeOwned)
            {
                try
                {
                    ReleaseSystem(old);
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }
            if (cpuState is CpuControlState.Active or
                CpuControlState.FaultedMayBeModified)
            {
                try
                {
                    RestoreCpu(old);
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }

            if (errors.Count != 0)
            {
                throw new AggregateException("F7BSD control restoration failed.", errors);
            }

            old.Dispose();
            transport = null;
            cpuExpected = null;
            ClearCpuRecoveryCertificate();
            cpuState = CpuControlState.Ready;
            cpuAppliedCode = null;
            systemMayBeOwned = false;
            systemState = SystemControlState.Firmware;
            systemRequestedCode = null;
            systemAppliedCode = null;
            lastSystemGuardTick = null;
            consecutiveOwnedTelemetryFailures = 0;
        }
    }

    private byte SetCpu(IF7bsdTransport active, byte requestedCode)
    {
        if (cpuState is CpuControlState.FaultedRestored or
            CpuControlState.FaultedMayBeModified)
        {
            throw new InvalidOperationException(
                "CPU control faulted during an earlier transaction. Refresh the " +
                "plugin after verifying stock state or restart Windows.");
        }

        F7bsdCpuRowState[] current = ActiveCpuExpected();
        F7bsdCpuRowState[] target = F7bsdCpuPolicy.CompileTarget(requestedCode);
        if (current.SequenceEqual(target))
        {
            if (cpuState == CpuControlState.Active)
            {
                // Preserve the confirmed request without turning a duplicate
                // target into EC traffic.
                cpuAppliedCode = requestedCode;
            }
            return requestedCode;
        }

        return ApplyCpuTarget(active, requestedCode, current, target);
    }

    private byte ApplyCpuTarget(
        IF7bsdTransport active,
        byte requestedCode,
        F7bsdCpuRowState[] current,
        F7bsdCpuRowState[] target)
    {
        F7bsdCpuTransitionStep[] steps = F7bsdCpuPolicy.PlanTransition(
            current,
            target);
        bool wasActive = cpuState == CpuControlState.Active;
        SetCpuRecoveryCertificate(current, steps, false, target);
        cpuState = CpuControlState.Active;
        bool writeAttempted = false;
        try
        {
            ValidateCpuSafetyState(active.Read(F7bsdProfile.CpuSafetyStateAddresses));
            writeAttempted = true;
            active.WriteGuarded(
                BuildCpuExpectations(current),
                steps.Select(step => step.Write).ToArray(),
                BuildCpuExpectations(target),
                []);
            cpuExpected = target;
            ClearCpuRecoveryCertificate();
            cpuAppliedCode = requestedCode;
            return requestedCode;
        }
        catch (Exception failure)
        {
            if (!wasActive &&
                (!writeAttempted || failure is EcWritePreconditionException))
            {
                // The first command failed before any possible write, so the
                // exact startup B1 table is still authoritative.
                ClearCpuRecoveryCertificate();
                cpuAppliedCode = null;
                cpuState = CpuControlState.FaultedRestored;
                throw;
            }

            cpuState = CpuControlState.FaultedMayBeModified;
            try
            {
                RecoverCpuToB1(active);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(
                    "CPU control failed and OEM B1 restoration is incomplete.",
                    failure,
                    cleanup);
            }
            throw;
        }
    }

    private byte SetSystem(IF7bsdTransport active, byte requestedCode)
    {
        if (systemState == SystemControlState.Faulted)
        {
            throw new InvalidOperationException(
                "System control faulted during an earlier transaction. Refresh " +
                "the plugin after verified release or restart Windows.");
        }

        if (CanReturnCachedSystemRequest(requestedCode))
        {
            return requestedCode;
        }

        if (!systemMayBeOwned)
        {
            byte[] firmwareState = active.Read(F7bsdProfile.SystemOwnershipAddresses);
            ValidateFirmwareSystemState(firmwareState);
            if (UnsafeSystemTemperature(firmwareState[0]) &&
                requestedCode != F7bsdProfile.MaximumCode)
            {
                throw new InvalidOperationException(
                    $"System raw temperature {firmwareState[0]} C is unsafe; only " +
                    "the full target is allowed.");
            }

            try
            {
                byte[] ownedState = EngageSystemOwnership(active, firmwareState);
                return ApplySystemRequest(active, ownedState, requestedCode);
            }
            catch (Exception failure)
            {
                systemState = SystemControlState.Faulted;
                if (systemMayBeOwned)
                {
                    ThrowAfterSystemRelease(active, failure);
                }
                throw;
            }
        }

        try
        {
            byte[] ownedState = active.Read(F7bsdProfile.SystemOwnershipAddresses);
            ownedState = GuardSystem(active, ownedState);
            return ApplySystemRequest(active, ownedState, requestedCode);
        }
        catch (Exception failure) when (systemState != SystemControlState.Faulted)
        {
            systemState = SystemControlState.Faulted;
            if (systemMayBeOwned)
            {
                ThrowAfterSystemRelease(active, failure);
            }
            throw;
        }
    }

    private bool CanReturnCachedSystemRequest(byte requestedCode)
    {
        if (!systemMayBeOwned || systemState != SystemControlState.Owned ||
            systemRequestedCode != requestedCode || systemAppliedCode != requestedCode ||
            lastSystemGuardTick is not long previous)
        {
            return false;
        }

        long current;
        try
        {
            current = timestamp();
        }
        catch
        {
            return false;
        }
        if (current < previous)
        {
            return false;
        }
        TimeSpan elapsed;
        try
        {
            elapsed = elapsedTime(previous, current);
        }
        catch
        {
            return false;
        }
        return elapsed >= TimeSpan.Zero && elapsed <= SystemGuardMaximumGap;
    }

    private byte ApplySystemRequest(
        IF7bsdTransport active,
        byte[] state,
        byte requestedCode)
    {
        byte appliedCode = UnsafeSystemTemperature(state[0]) &&
            requestedCode != F7bsdProfile.MaximumCode
                ? F7bsdProfile.MaximumCode
                : requestedCode;
        if (state[3] != appliedCode)
        {
            state = WriteSystemTarget(active, state[3], appliedCode);
        }
        if (UnsafeSystemTemperature(state[0]) &&
            appliedCode != F7bsdProfile.MaximumCode)
        {
            appliedCode = F7bsdProfile.MaximumCode;
            state = WriteSystemTarget(active, state[3], appliedCode);
        }
        ValidateOwnedSystemState(state, appliedCode);

        systemRequestedCode = requestedCode;
        systemAppliedCode = appliedCode;
        systemState = appliedCode == F7bsdProfile.MaximumCode &&
            requestedCode != F7bsdProfile.MaximumCode
                ? SystemControlState.Failsafe
                : SystemControlState.Owned;
        lastSystemGuardTick = timestamp();
        return appliedCode;
    }

    private byte[] EngageSystemOwnership(IF7bsdTransport active, byte[] state)
    {
        ValidateFirmwareSystemState(state);
        systemMayBeOwned = true;
        systemState = SystemControlState.Engaging;
        try
        {
            active.WriteGuarded(
                [new EcExpectation(F7bsdProfile.SystemTemperatureOverrideAddress, 0)],
                [new EcWrite(
                    F7bsdProfile.SystemTemperatureOverrideAddress,
                    F7bsdProfile.SystemSentinel)],
                [new EcExpectation(
                    F7bsdProfile.SystemTemperatureOverrideAddress,
                    F7bsdProfile.SystemSentinel)],
                []);
        }
        catch (EcWritePreconditionException)
        {
            // No write occurs until every guarded precondition has passed.
            systemMayBeOwned = false;
            systemState = SystemControlState.Faulted;
            throw;
        }
        return WaitForSystemOwnership(active);
    }

    private static void ValidateFirmwareSystemState(ReadOnlySpan<byte> state)
    {
        if (state.Length != F7bsdProfile.SystemOwnershipAddresses.Length)
        {
            throw new ArgumentException("Unexpected system state length.", nameof(state));
        }
        if (state[2] != 0)
        {
            throw new InvalidOperationException(
                $"System firmware-temperature override is 0x{state[2]:X2}, not zero.");
        }
        if (!F7bsdProfile.PlausibleTemperature(state[1]))
        {
            throw new IOException(
                "The firmware-owned system temperature path is not plausible.");
        }
        if (state[3] > F7bsdProfile.MaximumCode)
        {
            throw new IOException("The system target is outside code 0..51.");
        }
    }

    private void RestoreCpu(IF7bsdTransport active)
    {
        switch (cpuState)
        {
            case CpuControlState.Ready:
            case CpuControlState.FaultedRestored:
                return;
            case CpuControlState.FaultedMayBeModified:
                RecoverCpuToB1(active);
                return;
            case CpuControlState.Active:
                break;
            default:
                throw new InvalidOperationException("Unknown CPU control state.");
        }

        F7bsdCpuRowState[] current = ActiveCpuExpected();
        F7bsdCpuRowState[] baseline = F7bsdCpuPolicy.GetB1MutableStates();
        F7bsdCpuTransitionStep[] steps =
            F7bsdCpuPolicy.PlanTransitionToB1(current);
        SetCpuRecoveryCertificate(current, steps, true, baseline);
        try
        {
            ValidateCpuRecoverySafetyState(
                active.Read(F7bsdProfile.CpuSafetyStateAddresses));
            active.WriteGuarded(
                BuildCpuExpectations(current),
                steps.Select(step => step.Write).ToArray(),
                BuildCpuExpectations(baseline),
                []);
            cpuExpected = baseline;
            ClearCpuRecoveryCertificate();
            cpuState = CpuControlState.Ready;
            cpuAppliedCode = null;
        }
        catch (Exception failure)
        {
            cpuState = CpuControlState.FaultedMayBeModified;
            try
            {
                RecoverCpuToB1(active);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(
                    "CPU reset failed and OEM B1 restoration is incomplete.",
                    failure,
                    cleanup);
            }
            throw;
        }
    }

    private void ReleaseSystem(IF7bsdTransport active)
    {
        if (!systemMayBeOwned)
        {
            return;
        }

        systemState = SystemControlState.Releasing;
        List<Exception> errors = [];
        bool overrideCleared = false;
        bool healthyFirmwareState = false;
        try
        {
            byte[] state = active.Read(F7bsdProfile.SystemOwnershipAddresses);
            overrideCleared = state[2] == 0;
            healthyFirmwareState = HealthyFirmwareSystemState(state);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        if (!overrideCleared)
        {
            try
            {
                active.Write(
                    [new EcWrite(
                        F7bsdProfile.SystemTargetAddress,
                        F7bsdProfile.MaximumCode)]);
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
            try
            {
                active.Write(
                    [new EcWrite(F7bsdProfile.SystemTemperatureOverrideAddress, 0)]);
                overrideCleared = true;
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        if (!healthyFirmwareState)
        {
            try
            {
                byte[] releasedState = WaitForSystemRelease(active);
                overrideCleared |= releasedState[2] == 0;
                healthyFirmwareState = HealthyFirmwareSystemState(releasedState);
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        if (healthyFirmwareState)
        {
            systemMayBeOwned = false;
            systemRequestedCode = null;
            systemAppliedCode = null;
            lastSystemGuardTick = null;
            consecutiveOwnedTelemetryFailures = 0;
        }

        if (errors.Count != 0 || !healthyFirmwareState)
        {
            systemState = SystemControlState.Faulted;
            if (!healthyFirmwareState && errors.Count == 0)
            {
                errors.Add(new IOException(
                    "Firmware did not resume a plausible live system-temperature path."));
            }
            throw new AggregateException(
                "System fixed-target ownership did not release cleanly.",
                errors);
        }
        systemState = SystemControlState.Firmware;
    }

    private byte[] WaitForSystemOwnership(IF7bsdTransport active)
    {
        byte lastEffective = 0;
        for (int attempt = 0; attempt < SystemSelectorPollAttempts; attempt++)
        {
            lastEffective = active.Read(
                F7bsdProfile.SystemEffectiveTemperaturePollAddresses)[0];
            if (lastEffective == F7bsdProfile.SystemSentinel)
            {
                break;
            }
            if (attempt + 1 < SystemSelectorPollAttempts)
            {
                sleeper(SystemSelectorPollDelay);
            }
        }

        byte[] state = active.Read(F7bsdProfile.SystemOwnershipAddresses);
        if (state[2] == F7bsdProfile.SystemSentinel &&
            state[1] == F7bsdProfile.SystemSentinel)
        {
            return state;
        }
        throw new IOException(
            "Firmware did not enter system fixed-target ownership after the " +
            $"bounded selector wait; last effective byte was 0x{lastEffective:X2}, " +
            $"final state was {DescribeSystemState(state)}.");
    }

    private byte[] WaitForSystemRelease(IF7bsdTransport active)
    {
        byte lastEffective = F7bsdProfile.SystemSentinel;
        for (int attempt = 0; attempt < SystemSelectorPollAttempts; attempt++)
        {
            lastEffective = active.Read(
                F7bsdProfile.SystemEffectiveTemperaturePollAddresses)[0];
            if (F7bsdProfile.PlausibleTemperature(lastEffective))
            {
                break;
            }
            if (attempt + 1 < SystemSelectorPollAttempts)
            {
                sleeper(SystemSelectorPollDelay);
            }
        }

        byte[] state = active.Read(F7bsdProfile.SystemOwnershipAddresses);
        if (HealthyFirmwareSystemState(state))
        {
            return state;
        }
        throw new IOException(
            "Firmware did not resume live system-temperature ownership after the " +
            $"bounded selector wait; last effective byte was 0x{lastEffective:X2}, " +
            $"final state was {DescribeSystemState(state)}.");
    }

    private static string DescribeSystemState(ReadOnlySpan<byte> state) =>
        state.Length == F7bsdProfile.SystemOwnershipAddresses.Length
            ? $"raw=0x{state[0]:X2}, effective=0x{state[1]:X2}, " +
                $"override=0x{state[2]:X2}, target=0x{state[3]:X2}"
            : $"an unexpected {state.Length}-byte snapshot";

    private static bool HealthyFirmwareSystemState(ReadOnlySpan<byte> state) =>
        state.Length == F7bsdProfile.SystemOwnershipAddresses.Length &&
        state[2] == 0 &&
        F7bsdProfile.PlausibleTemperature(state[0]) &&
        F7bsdProfile.PlausibleTemperature(state[1]) &&
        state[3] <= F7bsdProfile.MaximumCode;

    private static void ValidateOwnedSystemState(
        ReadOnlySpan<byte> state,
        byte expectedTarget)
    {
        if (state.Length != F7bsdProfile.SystemOwnershipAddresses.Length)
        {
            throw new ArgumentException("Unexpected system state length.", nameof(state));
        }
        if (state[2] != F7bsdProfile.SystemSentinel ||
            state[1] != F7bsdProfile.SystemSentinel)
        {
            throw new IOException("System fixed-target ownership was lost.");
        }
        if (state[3] != expectedTarget)
        {
            throw new IOException(
                $"System target drifted from code {expectedTarget} to {state[3]}.");
        }
    }

    private static bool UnsafeSystemTemperature(byte temperature) =>
        !F7bsdProfile.PlausibleTemperature(temperature) ||
        temperature >= F7bsdProfile.SystemFailsafeTemperatureC;

    private static byte[] WriteSystemTarget(
        IF7bsdTransport active,
        byte currentCode,
        byte targetCode)
    {
        return active.WriteGuarded(
            [
                new EcExpectation(
                    F7bsdProfile.SystemTemperatureOverrideAddress,
                    F7bsdProfile.SystemSentinel),
                new EcExpectation(
                    F7bsdProfile.SystemEffectiveTemperatureAddress,
                    F7bsdProfile.SystemSentinel),
                new EcExpectation(F7bsdProfile.SystemTargetAddress, currentCode),
            ],
            [new EcWrite(F7bsdProfile.SystemTargetAddress, targetCode)],
            [
                new EcExpectation(
                    F7bsdProfile.SystemTemperatureOverrideAddress,
                    F7bsdProfile.SystemSentinel),
                new EcExpectation(
                    F7bsdProfile.SystemEffectiveTemperatureAddress,
                    F7bsdProfile.SystemSentinel),
                new EcExpectation(F7bsdProfile.SystemTargetAddress, targetCode),
            ],
            F7bsdProfile.SystemOwnershipAddresses);
    }

    private void RecoverCpuToB1(IF7bsdTransport active)
    {
        byte[] snapshot = active.Read(F7bsdProfile.CpuRuntimeSnapshotAddresses);
        int configurationLength = F7bsdProfile.CpuConfigurationAddresses.Length;
        int safetyLength = F7bsdProfile.CpuSafetyStateAddresses.Length;
        byte selector = F7bsdProfile.ValidateCpuConfiguration(
            snapshot.AsSpan(0, configurationLength));
        if (selector != F7bsdCpuPolicy.Selector)
        {
            throw new IOException(
                "CPU recovery refused because the firmware profile is no longer B1.");
        }
        ValidateCpuRecoverySafetyState(
            snapshot.AsSpan(configurationLength, safetyLength));
        F7bsdCpuRowState[] current = F7bsdCpuPolicy.FromMutableBytes(
            snapshot.AsSpan(configurationLength + safetyLength));
        F7bsdCpuRowState[] baseline = F7bsdCpuPolicy.GetB1MutableStates();
        if (current.SequenceEqual(baseline))
        {
            CompleteCpuRecovery(baseline);
            return;
        }

        F7bsdCpuRowState[] source = cpuRecoverySource ?? throw new IOException(
            "CPU recovery has no issued-transaction source certificate.");
        F7bsdCpuTransitionStep[] issuedPlan = cpuRecoveryPlan ?? throw new IOException(
            "CPU recovery has no issued-transaction plan certificate.");
        if (!F7bsdCpuPolicy.TryMatchTransitionPrefix(
            source,
            issuedPlan,
            current,
            out int completedStepCount))
        {
            throw new IOException(
                "CPU recovery refused because the mutable table is not an exact " +
                "prefix of the issued transaction.");
        }

        // Resume an interrupted B1 recovery by its exact suffix. For an
        // interrupted control mutation, the observed table was certified above;
        // plan directly from that prefix to B1 instead of reversing old writes.
        F7bsdCpuTransitionStep[] recovery = cpuRecoveryPlanEndsAtB1
            ? issuedPlan[completedStepCount..]
            : F7bsdCpuPolicy.PlanTransitionToB1(current);
        if (recovery.Length == 0)
        {
            throw new IOException(
                "A non-B1 CPU table produced an empty certified recovery plan.");
        }

        // Replace the original mutation certificate before the first recovery
        // write. A second interruption can then resume the exact remaining
        // suffix instead of re-planning from an arbitrary intermediate table.
        SetCpuRecoveryCertificate(current, recovery, true, baseline);
        active.WriteGuarded(
            BuildCpuExpectations(current),
            recovery.Select(step => step.Write).ToArray(),
            BuildCpuExpectations(baseline),
            []);
        CompleteCpuRecovery(baseline);
    }

    private void SetCpuRecoveryCertificate(
        ReadOnlySpan<F7bsdCpuRowState> source,
        ReadOnlySpan<F7bsdCpuTransitionStep> plan,
        bool endsAtB1,
        ReadOnlySpan<F7bsdCpuRowState> expectedDestination)
    {
        F7bsdCpuRowState[] materialized =
            F7bsdCpuPolicy.MaterializeTransitionPrefix(
                source,
                plan,
                plan.Length);
        if (!materialized.AsSpan().SequenceEqual(expectedDestination))
        {
            throw new InvalidOperationException(
                "The issued CPU transaction does not materialize its destination.");
        }
        if (endsAtB1 &&
            !materialized.AsSpan().SequenceEqual(
                F7bsdCpuPolicy.GetB1MutableStates()))
        {
            throw new InvalidOperationException(
                "A CPU recovery certificate does not terminate at exact B1.");
        }

        cpuRecoverySource = source.ToArray();
        cpuRecoveryPlan = plan.ToArray();
        cpuRecoveryPlanEndsAtB1 = endsAtB1;
    }

    private void ClearCpuRecoveryCertificate()
    {
        cpuRecoverySource = null;
        cpuRecoveryPlan = null;
        cpuRecoveryPlanEndsAtB1 = false;
    }

    private void CompleteCpuRecovery(F7bsdCpuRowState[] baseline)
    {
        cpuExpected = baseline;
        ClearCpuRecoveryCertificate();
        cpuAppliedCode = null;
        cpuState = CpuControlState.FaultedRestored;
    }

    private void ThrowAfterSystemRelease(IF7bsdTransport active, Exception failure)
    {
        systemState = SystemControlState.Faulted;
        try
        {
            ReleaseSystem(active);
        }
        catch (Exception cleanup)
        {
            throw new AggregateException(
                "System control failed and ownership release was incomplete.",
                failure,
                cleanup);
        }
        systemState = SystemControlState.Faulted;
        ExceptionDispatchInfo.Capture(failure).Throw();
        throw new UnreachableException();
    }

    private IF7bsdTransport ActiveTransport() => transport ??
        throw new InvalidOperationException("The F7BSD backend is not initialized.");

    private F7bsdCpuRowState[] ActiveCpuExpected() => cpuExpected ??
        throw new InvalidOperationException("The expected CPU table is unavailable.");

    private static EcExpectation[] BuildCpuExpectations(
        ReadOnlySpan<F7bsdCpuRowState> states)
    {
        if (states.Length != F7bsdCpuPolicy.NormalRowCount)
        {
            throw new ArgumentException("Unexpected CPU table length.", nameof(states));
        }

        List<EcExpectation> expectations =
        [
            new(F7bsdProfile.CpuProfileSelectorAddress, F7bsdCpuPolicy.Selector),
            new(F7bsdProfile.CpuTemperatureOverrideAddress, 0),
        ];
        for (int row = 0; row < F7bsdCpuPolicy.NormalRowCount; row++)
        {
            F7bsdCpuPolicyRow stock = F7bsdCpuPolicy.GetB1Row(row);
            ushort baseAddress = F7bsdProfile.CpuBaseAddresses[row];
            expectations.Add(new((ushort)(baseAddress + 1), stock.Upper));
            expectations.Add(new((ushort)(baseAddress + 2), stock.Lower));
        }
        F7bsdCpuPolicyRow critical = F7bsdCpuPolicy.GetB1Row(7);
        byte[] criticalValues =
            [critical.Base, critical.Upper, critical.Lower, critical.Slope];
        for (int index = 0; index < F7bsdProfile.CpuCriticalAddresses.Length; index++)
        {
            expectations.Add(new(
                F7bsdProfile.CpuCriticalAddresses[index],
                criticalValues[index]));
        }
        byte[] mutable = F7bsdCpuPolicy.ToMutableBytes(states);
        for (int index = 0; index < F7bsdProfile.CpuRestoreAddresses.Length; index++)
        {
            expectations.Add(new(
                F7bsdProfile.CpuRestoreAddresses[index],
                mutable[index]));
        }
        return expectations.ToArray();
    }

    private static void ValidateCpuSafetyState(ReadOnlySpan<byte> values)
    {
        ValidateCpuRecoverySafetyState(values);
        if (values[3] > F7bsdProfile.MaximumCode)
        {
            throw new IOException("The CPU target is outside code 0..51.");
        }
    }

    private static void ValidateCpuRecoverySafetyState(ReadOnlySpan<byte> values)
    {
        if (values.Length != F7bsdProfile.CpuSafetyStateAddresses.Length)
        {
            throw new ArgumentException("Unexpected CPU safety-state length.", nameof(values));
        }
        if (!F7bsdProfile.PlausibleTemperature(values[0]) ||
            !F7bsdProfile.PlausibleTemperature(values[1]))
        {
            throw new IOException("The CPU temperature path is not plausible.");
        }
        if (values[2] != 0)
        {
            throw new IOException("The CPU firmware-temperature override is active.");
        }

        // 0x0884 is a firmware output, not a host-owned setting. When a stale
        // selector row is evaluated immediately after a large cooling/resume
        // jump, the EC's unsigned slope arithmetic can transiently exceed 51.
        // Restoration must still be able to replace an exactly certified
        // mutable table with exact B1. Normal target mutations retain the
        // stricter 0..51 check above.
    }

    private F7bsdTelemetry ReadStableTelemetry(IF7bsdTransport active)
    {
        bool superviseSystem = systemMayBeOwned;
        ushort[] addresses = superviseSystem
            ? F7bsdProfile.RuntimeTelemetryAddresses
            : F7bsdProfile.TelemetryAddresses;
        byte[] sample;
        try
        {
            sample = active.Read(addresses);
        }
        catch (Exception failure)
        {
            if (systemMayBeOwned)
            {
                systemState = SystemControlState.Faulted;
                ThrowAfterSystemRelease(active, failure);
            }
            throw;
        }

        if (superviseSystem)
        {
            sample = ReplaceSystemState(sample, GuardSystem(
                active,
                sample.AsSpan(7, 4).ToArray()));
        }
        int cpuRpm = ReadStableCounter(
            active,
            sample.AsSpan(0, 3),
            F7bsdProfile.CpuTachAddresses,
            "CPU");
        int systemRpm = ReadStableCounter(
            active,
            sample.AsSpan(3, 3),
            F7bsdProfile.SystemTachAddresses,
            "system");
        return new F7bsdTelemetry(
            cpuRpm,
            systemRpm,
            sample[6],
            sample[7],
            superviseSystem ? systemAppliedCode : null);
    }

    private byte[] GuardSystem(IF7bsdTransport active, byte[] state)
    {
        try
        {
            byte expectedTarget = systemAppliedCode ?? throw new IOException(
                "System ownership is latched without a verified applied target.");
            long current = timestamp();
            if (lastSystemGuardTick is not long previous || current < previous)
            {
                throw new IOException("System control supervision timing is invalid.");
            }

            TimeSpan elapsed = elapsedTime(previous, current);
            if (elapsed < TimeSpan.Zero || elapsed > SystemGuardMaximumGap)
            {
                throw new IOException(
                    $"System control supervision gap was {elapsed.TotalSeconds:F3} seconds.");
            }

            ValidateOwnedSystemState(state, expectedTarget);
            if (UnsafeSystemTemperature(state[0]) &&
                expectedTarget != F7bsdProfile.MaximumCode)
            {
                state = WriteSystemTarget(
                    active,
                    state[3],
                    F7bsdProfile.MaximumCode);
                ValidateOwnedSystemState(state, F7bsdProfile.MaximumCode);
                systemAppliedCode = F7bsdProfile.MaximumCode;
                systemState = SystemControlState.Failsafe;
            }
            lastSystemGuardTick = current;
            return state;
        }
        catch (Exception failure)
        {
            systemState = SystemControlState.Faulted;
            ThrowAfterSystemRelease(active, failure);
            throw new UnreachableException();
        }
    }

    private static byte[] ReplaceSystemState(byte[] telemetry, byte[] state)
    {
        if (telemetry.Length != F7bsdProfile.RuntimeTelemetryAddresses.Length ||
            state.Length != F7bsdProfile.SystemOwnershipAddresses.Length)
        {
            throw new ArgumentException("Unexpected runtime telemetry state length.");
        }
        state.CopyTo(telemetry, 7);
        return telemetry;
    }

    private static int ReadStableCounter(
        IF7bsdTransport active,
        ReadOnlySpan<byte> initial,
        ushort[] addresses,
        string name)
    {
        if (F7bsdTelemetryDecoder.TryDecodeCounter(initial, out int rpm))
        {
            return rpm;
        }

        // Three attempts total: the initial combined sample and at most two
        // retries of only the counter which crossed a low-byte rollover.
        for (int retry = 0; retry < 2; retry++)
        {
            if (F7bsdTelemetryDecoder.TryDecodeCounter(
                active.Read(addresses),
                out rpm))
            {
                return rpm;
            }
        }
        throw new IOException(
            $"The EC {name} tachometer did not produce a stable sample.");
    }
}
