namespace FanControl.MinisforumUM780XTX;

internal sealed class PawnIoF7bsdBackend : IF7bsdBackend
{
    private static readonly TimeSpan OwnershipTimeout = TimeSpan.FromSeconds(1.5);
    private readonly object sync = new();
    private readonly Func<HostIdentitySnapshot> hostReader;
    private readonly Func<IF7bsdTransport> transportFactory;
    private IF7bsdTransport? transport;
    private byte[]? cpuBaseline;
    private bool cpuMayBeModified;
    private bool systemMayBeOwned;

    internal PawnIoF7bsdBackend()
        : this(HostIdentity.Read, static () => new PawnIoTransport())
    {
    }

    internal PawnIoF7bsdBackend(
        Func<HostIdentitySnapshot> hostReader,
        Func<IF7bsdTransport> transportFactory)
    {
        this.hostReader = hostReader;
        this.transportFactory = transportFactory;
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

                F7bsdProfile.ValidateCpuCriticalRow(
                    candidate.Read(F7bsdProfile.CpuCriticalAddresses));
                byte[] capturedCpu = candidate.Read(F7bsdProfile.CpuRestoreAddresses);

                transport = candidate;
                cpuBaseline = capturedCpu;
                cpuMayBeModified = false;
                systemMayBeOwned = false;
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
            return ReadStableTelemetry(ActiveTransport());
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
                    if (cpuMayBeModified)
                    {
                        RestoreCpu(active);
                    }
                    break;
                case F7bsdFan.System:
                    if (systemMayBeOwned)
                    {
                        ReleaseSystem(active);
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
            try
            {
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
                if (cpuMayBeModified)
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
            }
            finally
            {
                transport = null;
                cpuBaseline = null;
                cpuMayBeModified = false;
                systemMayBeOwned = false;
                old.Dispose();
            }

            if (errors.Count != 0)
            {
                throw new AggregateException("F7BSD control restoration failed.", errors);
            }
        }
    }

    private byte SetCpu(IF7bsdTransport active, byte requestedCode)
    {
        byte[] target = F7bsdProfile.CpuManualBytes(requestedCode);
        if (active.Read(F7bsdProfile.CpuRestoreAddresses).SequenceEqual(target))
        {
            return requestedCode;
        }

        cpuMayBeModified = true;
        try
        {
            active.Write(F7bsdProfile.CpuManualWrites(requestedCode));
            AssertCpuBytes(active, target);
            return requestedCode;
        }
        catch (Exception failure)
        {
            ThrowAfterCpuRestore(active, failure);
            throw;
        }
    }

    private byte SetSystem(IF7bsdTransport active, byte requestedCode)
    {
        try
        {
            EnsureSystemOwnership(active);
            active.Write(
                [new EcWrite(F7bsdProfile.SystemTargetAddress, requestedCode)]);
            AssertSystemOwnership(active, requestedCode);
            return requestedCode;
        }
        catch (Exception failure)
        {
            if (systemMayBeOwned)
            {
                ThrowAfterSystemRelease(active, failure);
            }
            throw;
        }
    }

    private void EnsureSystemOwnership(IF7bsdTransport active)
    {
        byte[] state = active.Read(F7bsdProfile.SystemOwnershipAddresses);
        if (systemMayBeOwned &&
            state[2] == F7bsdProfile.SystemSentinel &&
            state[1] == F7bsdProfile.SystemSentinel)
        {
            return;
        }
        if (!systemMayBeOwned && state[2] != 0)
        {
            throw new InvalidOperationException(
                "System fixed-target ownership is already active.");
        }
        if (state[2] is not 0 and not F7bsdProfile.SystemSentinel)
        {
            throw new InvalidOperationException(
                $"Unexpected system override 0x{state[2]:X2}.");
        }

        systemMayBeOwned = true;
        active.Write(
            [new EcWrite(
                F7bsdProfile.SystemTemperatureOverrideAddress,
                F7bsdProfile.SystemSentinel)]);
        WaitForSystemOwnership(active);
    }

    private void RestoreCpu(IF7bsdTransport active)
    {
        byte[] baseline = ActiveCpuBaseline();
        if (!active.Read(F7bsdProfile.CpuRestoreAddresses).SequenceEqual(baseline))
        {
            active.Write(F7bsdProfile.CpuRestoreWrites(baseline));
            AssertCpuBytes(active, baseline);
        }
        cpuMayBeModified = false;
    }

    private void ReleaseSystem(IF7bsdTransport active)
    {
        List<Exception> errors = [];
        bool alreadyReleased = false;
        try
        {
            byte[] state = active.Read(F7bsdProfile.SystemOwnershipAddresses);
            alreadyReleased = state[2] == 0 && state[1] == state[0];
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        if (!alreadyReleased)
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
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        try
        {
            WaitForSystemRelease(active);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        if (errors.Count != 0)
        {
            throw new AggregateException(
                "System fixed-target ownership did not release cleanly.",
                errors);
        }
        systemMayBeOwned = false;
    }

    private void WaitForSystemOwnership(IF7bsdTransport active)
    {
        DateTime deadline = DateTime.UtcNow + OwnershipTimeout;
        do
        {
            byte[] state = active.Read(F7bsdProfile.SystemOwnershipAddresses);
            if (state[2] == F7bsdProfile.SystemSentinel &&
                state[1] == F7bsdProfile.SystemSentinel)
            {
                return;
            }
            Thread.Sleep(20);
        }
        while (DateTime.UtcNow < deadline);

        throw new IOException("Firmware did not enter system fixed-target ownership.");
    }

    private static void WaitForSystemRelease(IF7bsdTransport active)
    {
        DateTime deadline = DateTime.UtcNow + OwnershipTimeout;
        do
        {
            byte[] state = active.Read(F7bsdProfile.SystemOwnershipAddresses);
            if (state[2] == 0 && state[1] == state[0])
            {
                return;
            }
            Thread.Sleep(20);
        }
        while (DateTime.UtcNow < deadline);

        throw new IOException("Firmware did not resume live system-temperature ownership.");
    }

    private static void AssertSystemOwnership(IF7bsdTransport active, byte expectedCode)
    {
        byte[] state = active.Read(F7bsdProfile.SystemOwnershipAddresses);
        if (state[2] != F7bsdProfile.SystemSentinel ||
            state[1] != F7bsdProfile.SystemSentinel ||
            state[3] != expectedCode)
        {
            throw new IOException("The system fixed-RPM target did not remain owned.");
        }
    }

    private static void AssertCpuBytes(IF7bsdTransport active, byte[] expected)
    {
        if (!active.Read(F7bsdProfile.CpuRestoreAddresses).SequenceEqual(expected))
        {
            throw new IOException("The complete CPU target table did not verify.");
        }
    }

    private void ThrowAfterCpuRestore(IF7bsdTransport active, Exception failure)
    {
        try
        {
            RestoreCpu(active);
        }
        catch (Exception cleanup)
        {
            throw new AggregateException(
                "CPU control failed and baseline restoration was incomplete.",
                failure,
                cleanup);
        }
    }

    private void ThrowAfterSystemRelease(IF7bsdTransport active, Exception failure)
    {
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
    }

    private IF7bsdTransport ActiveTransport() => transport ??
        throw new InvalidOperationException("The F7BSD backend is not initialized.");

    private byte[] ActiveCpuBaseline() => cpuBaseline ??
        throw new InvalidOperationException("The CPU baseline is unavailable.");

    private static F7bsdTelemetry ReadStableTelemetry(IF7bsdTransport active)
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            if (F7bsdTelemetryDecoder.TryDecode(
                active.Read(F7bsdProfile.TelemetryAddresses),
                out F7bsdTelemetry? telemetry))
            {
                return telemetry!;
            }
        }
        throw new IOException("The EC tachometer counters did not produce a stable sample.");
    }
}
