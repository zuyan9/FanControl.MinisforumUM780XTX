namespace FanControl.MinisforumUM780XTX;

internal sealed class PawnIoF7bsdBackend : IDisposable
{
    private const int SystemHandoffPollAttempts = 16;
    private static readonly TimeSpan SystemHandoffPollDelay =
        TimeSpan.FromMilliseconds(100);

    private PawnIoTransport? transport;
    private byte[]? cpuBaseline;
    private byte? cpuCode;
    private byte? systemCode;
    private bool systemMayBeOwned;
    private bool cpuRestorePending;
    private bool systemRestorePending;

    internal void Initialize()
    {
        if (transport is not null)
        {
            return;
        }

        HostIdentity.AssertSupported();
        PawnIoTransport candidate = new();
        try
        {
            byte[] capturedCpu = F7bsdProfile.CaptureCpuBaseline(
                candidate.Read(F7bsdProfile.CpuSnapshotAddresses));
            F7bsdProfile.ValidateFirmwareSystemState(
                candidate.Read(F7bsdProfile.SystemStateAddresses));

            transport = candidate;
            cpuBaseline = capturedCpu;
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    internal F7bsdTelemetry ReadTelemetry()
    {
        PawnIoTransport active = ActiveTransport();
        byte[] sample = active.Read(F7bsdProfile.TelemetryAddresses);
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
        return new F7bsdTelemetry(cpuRpm, systemRpm, sample[6], sample[7]);
    }

    internal void ResetCpu() => RestoreCpu(ActiveTransport());

    internal void ResetSystem() => ReleaseSystem(ActiveTransport());

    public void Dispose()
    {
        PawnIoTransport? old = transport;
        if (old is null)
        {
            return;
        }

        List<Exception> failures = [];
        TryRestore(
            systemMayBeOwned || systemRestorePending,
            () => ReleaseSystem(old));
        TryRestore(
            cpuCode.HasValue || cpuRestorePending,
            () => RestoreCpu(old));
        if (failures.Count != 0)
        {
            throw new AggregateException("F7BSD restoration failed.", failures);
        }

        old.Dispose();
        transport = null;
        cpuBaseline = null;

        void TryRestore(bool needed, Action restore)
        {
            if (!needed)
            {
                return;
            }
            try
            {
                restore();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
    }

    internal byte SetCpu(byte requestedCode)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            requestedCode,
            F7bsdProfile.MaximumCode);
        PawnIoTransport active = ActiveTransport();
        if (cpuRestorePending)
        {
            throw new InvalidOperationException(
                "CPU restoration is pending; reset the control or refresh the plugin.");
        }
        if (cpuCode == requestedCode)
        {
            return requestedCode;
        }

        try
        {
            active.WriteCpuVerified(
                F7bsdProfile.CpuTargetWrites(
                    requestedCode,
                    includeSlopes: !cpuCode.HasValue),
                ActiveCpuBaseline());
            cpuCode = requestedCode;
            return requestedCode;
        }
        catch (Exception failure)
        {
            try
            {
                RestoreCpuCore(active);
            }
            catch (Exception cleanup)
            {
                cpuRestorePending = true;
                throw new AggregateException(
                    "CPU control failed and captured-state restoration is incomplete.",
                    failure,
                    cleanup);
            }
            throw;
        }
    }

    private void RestoreCpu(PawnIoTransport active)
    {
        if (!cpuCode.HasValue && !cpuRestorePending)
        {
            return;
        }
        try
        {
            RestoreCpuCore(active);
        }
        catch
        {
            cpuRestorePending = true;
            throw;
        }
    }

    private void RestoreCpuCore(PawnIoTransport active)
    {
        byte[] baseline = ActiveCpuBaseline();
        active.WriteCpuVerified(F7bsdProfile.CpuRestoreWrites(baseline), baseline);
        cpuCode = null;
        cpuRestorePending = false;
    }

    internal byte SetSystem(byte requestedCode)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            requestedCode,
            F7bsdProfile.MaximumCode);
        PawnIoTransport active = ActiveTransport();
        if (systemRestorePending)
        {
            throw new InvalidOperationException(
                "System-fan ownership release is pending; reset the control or " +
                "refresh the plugin.");
        }
        if (systemMayBeOwned && systemCode == requestedCode)
        {
            return requestedCode;
        }

        try
        {
            if (!systemMayBeOwned)
            {
                EngageSystem(active);
            }

            active.WriteVerified(
                [
                    new EcExpectation(
                        F7bsdProfile.SystemEffectiveTemperatureAddress,
                        F7bsdProfile.SystemSentinel),
                    new EcExpectation(
                        F7bsdProfile.SystemTemperatureOverrideAddress,
                        F7bsdProfile.SystemSentinel),
                ],
                [new EcWrite(F7bsdProfile.SystemTargetAddress, requestedCode)]);
            F7bsdProfile.ValidateOwnedSystemState(
                active.Read(F7bsdProfile.SystemStateAddresses),
                requestedCode);
            systemCode = requestedCode;
            return requestedCode;
        }
        catch (Exception failure)
        {
            if (!systemMayBeOwned)
            {
                throw;
            }
            try
            {
                ReleaseSystemCore(active);
            }
            catch (Exception cleanup)
            {
                systemRestorePending = true;
                throw new AggregateException(
                    "System control failed and firmware ownership release is incomplete.",
                    failure,
                    cleanup);
            }
            throw;
        }
    }

    private void EngageSystem(PawnIoTransport active)
    {
        F7bsdProfile.ValidateFirmwareSystemState(
            active.Read(F7bsdProfile.SystemStateAddresses));
        active.WriteVerified(
            [new EcExpectation(F7bsdProfile.SystemTemperatureOverrideAddress, 0)],
            [new EcWrite(
                F7bsdProfile.SystemTemperatureOverrideAddress,
                F7bsdProfile.SystemSentinel)],
            () => systemMayBeOwned = true);
        WaitForSystemEffective(active, owned: true);
    }

    private void ReleaseSystem(PawnIoTransport active)
    {
        if (!systemMayBeOwned && !systemRestorePending)
        {
            return;
        }
        try
        {
            ReleaseSystemCore(active);
        }
        catch
        {
            systemRestorePending = true;
            throw;
        }
    }

    private void ReleaseSystemCore(PawnIoTransport active)
    {
        byte[] state = active.Read(F7bsdProfile.SystemStateAddresses);
        if (F7bsdProfile.IsReleasedSystemState(state))
        {
            CompleteSystemRelease();
            return;
        }

        if (state[1] == F7bsdProfile.SystemSentinel)
        {
            Exception? writeFailure = null;
            Attempt(new EcWrite(
                F7bsdProfile.SystemTargetAddress,
                F7bsdProfile.MaximumCode));
            Attempt(new EcWrite(F7bsdProfile.SystemTemperatureOverrideAddress, 0));
            if (writeFailure is not null)
            {
                try
                {
                    VerifySystemReleased(active);
                }
                catch (Exception verificationFailure)
                {
                    throw new AggregateException(writeFailure, verificationFailure);
                }
                throw new IOException(
                    "Firmware resumed system-fan ownership, but the release writes " +
                    "were not both verified.",
                    writeFailure);
            }

            void Attempt(EcWrite write)
            {
                try
                {
                    active.WriteVerified([write]);
                }
                catch (Exception exception)
                {
                    writeFailure = writeFailure is null
                        ? exception
                        : new AggregateException(writeFailure, exception);
                }
            }
        }
        else if (state[1] != 0)
        {
            throw new IOException(
                $"System override 0x{state[1]:X2} is neither firmware nor raw mode.");
        }

        VerifySystemReleased(active);
        CompleteSystemRelease();
    }

    private static void VerifySystemReleased(PawnIoTransport active)
    {
        WaitForSystemEffective(active, owned: false);
        byte[] state = active.Read(F7bsdProfile.SystemStateAddresses);
        if (!F7bsdProfile.IsReleasedSystemState(state))
        {
            throw new IOException("Firmware did not resume system-fan ownership.");
        }
    }

    private static void WaitForSystemEffective(
        PawnIoTransport active,
        bool owned)
    {
        byte last = 0;
        for (int attempt = 0; attempt < SystemHandoffPollAttempts; attempt++)
        {
            last = active.Read(
                F7bsdProfile.SystemEffectiveTemperaturePollAddresses)[0];
            if (owned
                    ? last == F7bsdProfile.SystemSentinel
                    : F7bsdProfile.PlausibleTemperature(last))
            {
                return;
            }
            if (attempt + 1 < SystemHandoffPollAttempts)
            {
                Thread.Sleep(SystemHandoffPollDelay);
            }
        }

        string direction = owned ? "enter raw mode" : "return to firmware mode";
        throw new IOException(
            $"System fan did not {direction}; effective byte ended at 0x{last:X2}.");
    }

    private void CompleteSystemRelease()
    {
        systemMayBeOwned = false;
        systemCode = null;
        systemRestorePending = false;
    }

    private PawnIoTransport ActiveTransport() => transport ??
        throw new InvalidOperationException("The F7BSD backend is not initialized.");

    private byte[] ActiveCpuBaseline() => cpuBaseline ??
        throw new InvalidOperationException("The captured CPU baseline is unavailable.");

    private static int ReadStableCounter(
        PawnIoTransport active,
        ReadOnlySpan<byte> initial,
        ushort[] addresses,
        string name)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ReadOnlySpan<byte> sample = attempt == 0
                ? initial
                : active.Read(addresses);
            if (F7bsdTelemetryDecoder.TryDecodeCounter(sample, out int rpm))
            {
                return rpm;
            }
        }
        throw new IOException(
            $"The EC {name} tachometer did not produce a stable sample.");
    }
}

internal sealed record F7bsdTelemetry(
    int CpuFanRpm,
    int SystemFanRpm,
    int CpuTemperatureC,
    int SystemTemperatureC);

internal static class F7bsdTelemetryDecoder
{
    internal static bool TryDecodeCounter(
        ReadOnlySpan<byte> lowHighLow,
        out int rpm)
    {
        if (lowHighLow.Length != 3)
        {
            throw new ArgumentException(
                "A tachometer sample must contain low/high/low bytes.",
                nameof(lowHighLow));
        }
        if (lowHighLow[0] != lowHighLow[2])
        {
            rpm = 0;
            return false;
        }

        ushort counter = (ushort)(lowHighLow[0] | (lowHighLow[1] << 8));
        rpm = counter == 0 ? 0 : 2_156_250 / counter;
        return true;
    }
}
