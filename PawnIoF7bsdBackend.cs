namespace FanControl.MinisforumUM780XTX;

internal sealed class PawnIoF7bsdBackend : IF7bsdBackend
{
    private readonly object sync = new();
    private readonly Func<HostIdentitySnapshot> hostReader;
    private readonly Func<IF7bsdTransport> transportFactory;
    private IF7bsdTransport? transport;
    private CpuConfiguration? cpuConfiguration;
    private byte[]? cpuBaseline;
    private byte[]? systemBaseline;
    private byte? cpuCode;
    private SystemFanMode? systemMode;
    private bool cpuMayBeModified;
    private bool systemMayBeModified;

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

    internal byte? CpuCode => cpuCode;

    internal SystemFanMode? SystemMode => systemMode;

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
                byte[] liveProfile = candidate.Read(F7bsdProfile.ControllerProfileAddresses);
                if (!liveProfile.SequenceEqual(F7bsdProfile.ExpectedControllerProfile))
                {
                    throw new PlatformNotSupportedException(
                        "The live controller is not the UM780 XTX F7BSD IT5571 profile.");
                }

                CpuConfiguration capturedConfiguration =
                    F7bsdProfile.ValidateCpuConfiguration(
                        candidate.Read(F7bsdProfile.CpuConfigurationAddresses));
                byte[] capturedCpu = candidate.Read(F7bsdProfile.CpuRestoreAddresses);
                F7bsdProfile.ValidateCpuBaseline(
                    capturedConfiguration.Selector,
                    capturedCpu);
                byte[] capturedSystem = candidate.Read(
                    F7bsdProfile.SystemThresholdAddresses);
                F7bsdProfile.ValidateSystemBaseline(capturedSystem);
                F7bsdTelemetry telemetry = ReadStableTelemetry(candidate);
                F7bsdProfile.ValidateStartupTelemetry(telemetry);

                transport = candidate;
                cpuConfiguration = capturedConfiguration;
                cpuBaseline = capturedCpu;
                systemBaseline = capturedSystem;
                cpuCode = null;
                systemMode = null;
                cpuMayBeModified = false;
                systemMayBeModified = false;
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
            IF7bsdTransport active = ActiveTransport();
            F7bsdTelemetry telemetry = ReadStableTelemetry(active);
            if (cpuMayBeModified)
            {
                MaintainCpuPolicy(active, telemetry);
            }
            if (systemMayBeModified)
            {
                MaintainSystemPolicy(active, telemetry);
            }
            return telemetry;
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
                    if (systemMayBeModified)
                    {
                        RestoreSystem(active);
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
                if (systemMayBeModified)
                {
                    try
                    {
                        RestoreSystem(old);
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
                cpuConfiguration = null;
                cpuBaseline = null;
                systemBaseline = null;
                cpuCode = null;
                systemMode = null;
                old.Dispose();
            }

            if (errors.Count != 0)
            {
                throw new AggregateException("F7BSD baseline restoration failed.", errors);
            }
        }
    }

    private byte SetCpu(IF7bsdTransport active, byte requestedCode)
    {
        AssertCpuState(active);
        CpuConfiguration configuration = AssertCpuConfiguration(active);
        byte[] target = F7bsdProfile.CpuBytes(
            F7bsdProfile.CompileCpuCurve(requestedCode, configuration.Bands));
        byte[] current = active.Read(F7bsdProfile.CpuRestoreAddresses);
        if (cpuCode == requestedCode && current.SequenceEqual(target))
        {
            return requestedCode;
        }

        EcWrite[] writes = F7bsdProfile.CpuTransitionWrites(
            current,
            target,
            configuration.Bands);
        cpuMayBeModified = true;
        try
        {
            if (writes.Length != 0)
            {
                AssertCpuState(active);
                active.Write(
                    writes,
                    F7bsdProfile.CpuTransitionExpectations(current, configuration));
            }
            AssertCpuBytes(active, target);
            AssertCpuState(active);
            AssertCpuConfiguration(active);
            cpuCode = requestedCode;
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
        SystemFanMode wanted = F7bsdProfile.SystemMode(requestedCode);
        byte rawTemperature = AssertSystemState(active);
        if (rawTemperature is < 1 or > 120)
        {
            wanted = SystemFanMode.Full;
        }
        EcWrite[] writes = F7bsdProfile.SystemWrites(wanted);
        if (systemMode == wanted && SystemThresholdsMatch(active, writes))
        {
            if (rawTemperature is < 1 or > 120 || rawTemperature >= 100)
            {
                active.Write([new EcWrite(0x0885, F7bsdProfile.MaximumCode)]);
            }
            return F7bsdProfile.SystemModeCode(wanted);
        }

        systemMayBeModified = true;
        if (rawTemperature is < 1 or > 120)
        {
            // Preserve the fail-high intent even if the first I/O attempt and
            // its cleanup both fail. A later Update can then retry Full.
            systemMode = SystemFanMode.Full;
        }
        try
        {
            if (rawTemperature is < 1 or > 120 || rawTemperature >= 100)
            {
                active.Write([new EcWrite(0x0885, F7bsdProfile.MaximumCode)]);
            }
            active.Write(writes);
            AssertSystemThresholds(active, writes);
            systemMode = wanted;
            return F7bsdProfile.SystemModeCode(wanted);
        }
        catch (Exception failure)
        {
            ThrowAfterSystemRestore(active, failure);
            throw;
        }
    }

    private CpuConfiguration AssertCpuConfiguration(IF7bsdTransport active)
    {
        CpuConfiguration expected = ActiveCpuConfiguration();
        CpuConfiguration actual = F7bsdProfile.ValidateCpuConfiguration(
            active.Read(F7bsdProfile.CpuConfigurationAddresses));
        if (actual.Selector != expected.Selector ||
            !actual.Bands.SequenceEqual(expected.Bands))
        {
            throw new InvalidOperationException(
                "The CPU profile changed while the plugin was active.");
        }
        return actual;
    }

    private static void AssertCpuState(IF7bsdTransport active)
    {
        byte[] state = active.Read(F7bsdProfile.CpuStateAddresses);
        if (state[2] != 0 || state[3] != 0)
        {
            throw new InvalidOperationException(
                "A firmware temperature override became active; refusing CPU curve writes.");
        }
        if (state[0] is < 1 or > 120 || state[1] != state[0])
        {
            throw new InvalidOperationException(
                "The CPU temperature is not a plausible live sensor value.");
        }
    }

    private static byte AssertSystemState(IF7bsdTransport active)
    {
        byte[] state = active.Read(F7bsdProfile.SystemStateAddresses);
        byte raw = state[0];
        byte effective = state[1];
        if (state[2] != 0 || state[3] != 0)
        {
            throw new InvalidOperationException(
                "A firmware temperature override became active; refusing system policy writes.");
        }
        if (raw is >= 1 and <= 120 && effective != raw)
        {
            throw new InvalidOperationException(
                "The system effective temperature no longer matches its raw sensor.");
        }
        return raw;
    }

    private static void AssertSystemThresholds(
        IF7bsdTransport active,
        IReadOnlyList<EcWrite> expectedWrites)
    {
        if (!SystemThresholdsMatch(active, expectedWrites))
        {
            throw new IOException("The complete system threshold policy did not verify.");
        }
    }

    private static bool SystemThresholdsMatch(
        IF7bsdTransport active,
        IReadOnlyList<EcWrite> expectedWrites)
    {
        byte[] actual = active.Read(F7bsdProfile.SystemThresholdAddresses);
        for (int index = 0; index < F7bsdProfile.SystemThresholdAddresses.Length; index++)
        {
            ushort address = F7bsdProfile.SystemThresholdAddresses[index];
            byte expected = expectedWrites.Single(write => write.Address == address).Value;
            if (actual[index] != expected)
            {
                return false;
            }
        }
        return true;
    }

    private void RestoreCpu(IF7bsdTransport active)
    {
        byte[] baseline = ActiveCpuBaseline();
        List<Exception> errors = [];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                AssertCpuState(active);
                CpuConfiguration configuration = AssertCpuConfiguration(active);
                byte[] current = active.Read(F7bsdProfile.CpuRestoreAddresses);
                if (current.SequenceEqual(baseline))
                {
                    cpuMayBeModified = false;
                    cpuCode = null;
                    return;
                }

                EcWrite[] writes = F7bsdProfile.CpuTransitionWrites(
                    current,
                    baseline,
                    configuration.Bands);
                AssertCpuState(active);
                active.Write(
                    writes,
                    F7bsdProfile.CpuTransitionExpectations(current, configuration));
                AssertCpuBytes(active, baseline);
                AssertCpuState(active);
                AssertCpuConfiguration(active);
                cpuMayBeModified = false;
                cpuCode = null;
                return;
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }
        throw new AggregateException("CPU baseline restoration was incomplete.", errors);
    }

    private void RestoreSystem(IF7bsdTransport active)
    {
        byte[] baseline = ActiveSystemBaseline();
        List<Exception> errors = [];
        byte rawTemperature = AssertSystemState(active);
        if (rawTemperature is < 1 or > 120)
        {
            active.Write([new EcWrite(0x0885, F7bsdProfile.MaximumCode)]);
            EcWrite[] full = F7bsdProfile.SystemWrites(SystemFanMode.Full);
            active.Write(full);
            AssertSystemThresholds(active, full);
            // Control cannot be relinquished safely while the sensor is
            // invalid. Keep ownership so Update can reapply Full if the EC
            // reloads its stock policy before the sensor recovers.
            systemMayBeModified = true;
            systemMode = SystemFanMode.Full;
            throw new InvalidOperationException(
                "The system sensor is invalid; the full-speed policy was retained instead of stock.");
        }

        try
        {
            if (rawTemperature >= 100)
            {
                active.Write([new EcWrite(0x0885, F7bsdProfile.MaximumCode)]);
            }
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        EcWrite[] restoreWrites = F7bsdProfile.SystemRestoreWrites(baseline);
        foreach (EcWrite write in restoreWrites)
        {
            try
            {
                active.Write([write]);
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }
        try
        {
            AssertSystemThresholds(active, restoreWrites);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        if (errors.Count != 0)
        {
            throw new AggregateException("System baseline restoration was incomplete.", errors);
        }
        systemMayBeModified = false;
        systemMode = null;
    }

    private void MaintainCpuPolicy(
        IF7bsdTransport active,
        F7bsdTelemetry telemetry)
    {
        AssertCpuTelemetryState(telemetry);
        CpuConfiguration configuration = AssertCpuConfiguration(active);
        byte requestedCode = cpuCode ?? throw new InvalidOperationException(
            "The active CPU control code is unavailable.");
        byte[] target = F7bsdProfile.CpuBytes(
            F7bsdProfile.CompileCpuCurve(requestedCode, configuration.Bands));
        byte[] current = active.Read(F7bsdProfile.CpuRestoreAddresses);
        if (!current.SequenceEqual(target))
        {
            EcWrite[] writes = F7bsdProfile.CpuTransitionWrites(
                current,
                target,
                configuration.Bands);
            AssertCpuState(active);
            active.Write(
                writes,
                F7bsdProfile.CpuTransitionExpectations(current, configuration));
            AssertCpuBytes(active, target);
            AssertCpuState(active);
            AssertCpuConfiguration(active);
        }
    }

    private void MaintainSystemPolicy(
        IF7bsdTransport active,
        F7bsdTelemetry telemetry)
    {
        AssertSystemTelemetryState(telemetry);
        bool invalid = telemetry.SystemTemperatureC is < 1 or > 120;
        SystemFanMode wanted = invalid
            ? SystemFanMode.Full
            : systemMode ?? throw new InvalidOperationException(
                "The active system fan mode is unavailable.");
        if (invalid || telemetry.SystemTemperatureC >= 100)
        {
            // The telemetry target is a prior snapshot. Enforce Full without
            // relying on it so a concurrent EC reload cannot retain code 0.
            active.Write([new EcWrite(0x0885, F7bsdProfile.MaximumCode)]);
        }

        EcWrite[] writes = F7bsdProfile.SystemWrites(wanted);
        if (!SystemThresholdsMatch(active, writes))
        {
            active.Write(writes);
            AssertSystemThresholds(active, writes);
        }
        systemMode = wanted;
    }

    private static void AssertCpuTelemetryState(F7bsdTelemetry telemetry)
    {
        if (telemetry.CpuTemperatureOverride != 0 ||
            telemetry.SystemTemperatureOverride != 0)
        {
            throw new InvalidOperationException(
                "A firmware temperature override became active.");
        }
        if (telemetry.CpuTemperatureC is < 1 or > 120 ||
            telemetry.CpuEffectiveTemperatureC != telemetry.CpuTemperatureC)
        {
            throw new InvalidOperationException(
                "The CPU temperature is not a plausible live sensor value.");
        }
    }

    private static void AssertSystemTelemetryState(F7bsdTelemetry telemetry)
    {
        if (telemetry.CpuTemperatureOverride != 0 ||
            telemetry.SystemTemperatureOverride != 0)
        {
            throw new InvalidOperationException(
                "A firmware temperature override became active.");
        }
        if (telemetry.SystemTemperatureC is >= 1 and <= 120 &&
            telemetry.SystemEffectiveTemperatureC != telemetry.SystemTemperatureC)
        {
            throw new InvalidOperationException(
                "The system effective temperature no longer matches its raw sensor.");
        }
    }

    private static void AssertCpuBytes(IF7bsdTransport active, byte[] expected)
    {
        if (!active.Read(F7bsdProfile.CpuRestoreAddresses).SequenceEqual(expected))
        {
            throw new IOException("The complete CPU curve did not verify.");
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

    private void ThrowAfterSystemRestore(IF7bsdTransport active, Exception failure)
    {
        try
        {
            RestoreSystem(active);
        }
        catch (Exception cleanup)
        {
            throw new AggregateException(
                "System control failed and baseline restoration was incomplete.",
                failure,
                cleanup);
        }
    }

    private IF7bsdTransport ActiveTransport() => transport ??
        throw new InvalidOperationException("The F7BSD backend is not initialized.");

    private CpuConfiguration ActiveCpuConfiguration() => cpuConfiguration ??
        throw new InvalidOperationException("The CPU configuration is unavailable.");

    private byte[] ActiveCpuBaseline() => cpuBaseline ??
        throw new InvalidOperationException("The CPU baseline is unavailable.");

    private byte[] ActiveSystemBaseline() => systemBaseline ??
        throw new InvalidOperationException("The system baseline is unavailable.");

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
