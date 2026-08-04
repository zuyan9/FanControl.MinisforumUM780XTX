using FanControl.Plugins;

namespace FanControl.MinisforumUM780XTX;

/// <summary>Exposes verified F7BSD fan telemetry and dual fan controls.</summary>
public sealed class UM780XTXPlugin : IPlugin2
{
    private readonly Func<IF7bsdBackend> backendFactory;
    private readonly IPluginLogger? logger;
    private readonly object lifecycleSync = new();
    private readonly Sensor cpuFan = new(
        "minisforum.um780xtx.f7bsd.fan1",
        "UM780 XTX CPU Fan");
    private readonly Sensor systemFan = new(
        "minisforum.um780xtx.f7bsd.fan2",
        "UM780 XTX System Fan");
    private readonly Sensor cpuTemperature = new(
        "minisforum.um780xtx.f7bsd.cpu-temperature",
        "UM780 XTX EC CPU Temperature");
    private readonly Sensor systemTemperature = new(
        "minisforum.um780xtx.f7bsd.system-temperature",
        "UM780 XTX EC System Temperature");
    private readonly ControlSensor cpuControl;
    private readonly ControlSensor systemControl;
    private IF7bsdBackend? backend;
    private int consecutiveTelemetryFailures;
    private bool telemetryFaulted;

    /// <summary>Creates the plugin with the native PawnIO backend.</summary>
    public UM780XTXPlugin()
        : this(static () => new PawnIoF7bsdBackend(), null)
    {
    }

    /// <summary>Creates the plugin with FanControl logging.</summary>
    public UM780XTXPlugin(IPluginLogger logger)
        : this(static () => new PawnIoF7bsdBackend(), logger)
    {
    }

    internal UM780XTXPlugin(
        Func<IF7bsdBackend> backendFactory,
        IPluginLogger? logger = null)
    {
        this.backendFactory = backendFactory;
        this.logger = logger;
        cpuControl = new ControlSensor(
            "minisforum.um780xtx.f7bsd.cpu-native-v3",
            "UM780 XTX CPU Fan Target (EC Thermal Tail)",
            $"{Name}/{cpuFan.Id}",
            value => Set(F7bsdFan.Cpu, value),
            () => Reset(F7bsdFan.Cpu),
            exception => Log(
                "Minisforum UM780 XTX CPU control failed and was disabled: " +
                exception.Message),
            lifecycleSync);
        systemControl = new ControlSensor(
            "minisforum.um780xtx.f7bsd.system-raw-v2",
            "UM780 XTX System Fan Raw Target",
            $"{Name}/{systemFan.Id}",
            value => Set(F7bsdFan.System, value),
            () => Reset(F7bsdFan.System),
            exception => Log(
                "Minisforum UM780 XTX system control failed and was disabled: " +
                exception.Message),
            lifecycleSync);
    }

    /// <inheritdoc />
    public string Name => "Minisforum UM780 XTX (F7BSD)";

    /// <inheritdoc />
    public void Initialize()
    {
        lock (lifecycleSync)
        {
            Close();
            if (backend is not null)
            {
                throw new InvalidOperationException(
                    "The previous F7BSD backend still requires verified restoration. " +
                    "Restart Windows before reinitializing the plugin.");
            }
            IF7bsdBackend candidate = backendFactory();
            try
            {
                candidate.Initialize();
                Apply(candidate.ReadTelemetry());
                backend = candidate;
                consecutiveTelemetryFailures = 0;
                telemetryFaulted = false;
                Log("Minisforum UM780 XTX F7BSD backend initialized.");
            }
            catch
            {
                candidate.Dispose();
                throw;
            }
        }
    }

    /// <inheritdoc />
    public void Load(IPluginSensorsContainer container)
    {
        lock (lifecycleSync)
        {
            container.FanSensors.AddRange([cpuFan, systemFan]);
            container.TempSensors.AddRange([cpuTemperature, systemTemperature]);
            container.ControlSensors.AddRange([cpuControl, systemControl]);
            Log(
                "Minisforum UM780 XTX loaded native CPU-target and guarded raw " +
                "system-fan controls.");
        }
    }

    /// <inheritdoc />
    public void Update()
    {
        lock (lifecycleSync)
        {
            if (telemetryFaulted)
            {
                return;
            }
            try
            {
                if (backend is not null)
                {
                    Apply(backend.ReadTelemetry());
                    consecutiveTelemetryFailures = 0;
                }
            }
            catch (DeferredCpuControlException exception)
            {
                consecutiveTelemetryFailures = 0;
                ClearTelemetry();
                cpuControl.Fault();
                Log(
                    "Minisforum UM780 XTX deferred CPU control failed and was " +
                    "disabled: " + (exception.InnerException?.Message ??
                        exception.Message));
            }
            catch (Exception exception)
            {
                consecutiveTelemetryFailures++;
                ClearTelemetry();
                systemControl.ClearValue();
                if (consecutiveTelemetryFailures == 1)
                {
                    Log($"Minisforum UM780 XTX telemetry read failed: {exception.Message}");
                }
                if (consecutiveTelemetryFailures >= 3)
                {
                    telemetryFaulted = true;
                    systemControl.Fault();
                    Log(
                        "Minisforum UM780 XTX telemetry disabled after three " +
                        "consecutive failures; refresh the plugin after checking " +
                        "stock state.");
                }
            }
        }
    }

    /// <inheritdoc />
    public void Close()
    {
        lock (lifecycleSync)
        {
            cpuControl.Clear();
            systemControl.Clear();
            consecutiveTelemetryFailures = 0;
            telemetryFaulted = false;
            ClearTelemetry();
            IF7bsdBackend? old = backend;
            if (old is null)
            {
                return;
            }

            try
            {
                old.Dispose();
                backend = null;
            }
            catch (Exception exception)
            {
                Log(
                    "Minisforum UM780 XTX baseline restoration remains pending: " +
                    exception.Message);
            }
        }
    }

    private byte Set(F7bsdFan fan, float percentage)
    {
        lock (lifecycleSync)
        {
            if (fan == F7bsdFan.System && telemetryFaulted)
            {
                throw new InvalidOperationException(
                    "System control is unavailable while guarded telemetry is disabled. " +
                    "Refresh the plugin after checking stock state.");
            }
            byte requestedCode = F7bsdProfile.ToCode(percentage);
            return (backend ??
                throw new InvalidOperationException("The F7BSD backend is unavailable."))
                .Set(fan, requestedCode);
        }
    }

    private void Reset(F7bsdFan fan)
    {
        lock (lifecycleSync)
        {
            backend?.Reset(fan);
        }
    }

    private void Apply(F7bsdTelemetry telemetry)
    {
        cpuFan.Value = telemetry.CpuFanRpm;
        systemFan.Value = telemetry.SystemFanRpm;
        cpuTemperature.Value = telemetry.CpuTemperatureC;
        systemTemperature.Value = telemetry.SystemTemperatureC;
        cpuControl.SetConfirmedCode(telemetry.CpuAppliedCode);
        systemControl.SetConfirmedCode(telemetry.SystemAppliedCode);
    }

    private void ClearTelemetry()
    {
        cpuFan.Value = null;
        systemFan.Value = null;
        cpuTemperature.Value = null;
        systemTemperature.Value = null;
    }

    private void Log(string message)
    {
        try
        {
            logger?.Log(message);
        }
        catch
        {
        }
    }

    private sealed class Sensor(string id, string name) : IPluginSensor
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public float? Value { get; internal set; }

        public void Update()
        {
        }
    }

    private sealed class ControlSensor(
        string id,
        string name,
        string pairedFanSensorId,
        Func<float, byte> set,
        Action reset,
        Action<Exception> reportFailure,
        object sync) : IPluginControlSensor2
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public string PairedFanSensorId { get; } = pairedFanSensorId;

        public float? Value { get; private set; }

        public void Set(float value)
        {
            lock (sync)
            {
                if (faulted)
                {
                    return;
                }
                try
                {
                    Value = F7bsdProfile.ToPercentage(set(value));
                }
                catch (Exception exception)
                {
                    faulted = true;
                    Value = null;
                    reportFailure(exception);
                }
            }
        }

        public void Reset()
        {
            lock (sync)
            {
                try
                {
                    reset();
                    faulted = false;
                    Value = null;
                }
                catch (Exception exception)
                {
                    faulted = true;
                    Value = null;
                    reportFailure(exception);
                }
            }
        }

        public void Update()
        {
        }

        internal void Clear()
        {
            lock (sync)
            {
                faulted = false;
                Value = null;
            }
        }

        internal void ClearValue()
        {
            lock (sync)
            {
                Value = null;
            }
        }

        internal void Fault()
        {
            lock (sync)
            {
                faulted = true;
                Value = null;
            }
        }

        internal void SetConfirmedCode(byte? code)
        {
            lock (sync)
            {
                if (faulted)
                {
                    Value = null;
                    return;
                }
                Value = code.HasValue
                    ? F7bsdProfile.ToPercentage(code.Value)
                    : null;
            }
        }

        private bool faulted;
    }
}
