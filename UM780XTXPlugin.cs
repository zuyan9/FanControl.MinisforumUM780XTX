using FanControl.Plugins;

namespace FanControl.MinisforumUM780XTX;

/// <summary>Exposes the verified F7BSD fan telemetry and safe native policies.</summary>
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
            "minisforum.um780xtx.f7bsd.cpu-control",
            "UM780 XTX CPU Fan Control",
            $"{Name}/{cpuFan.Id}",
            value => Set(F7bsdFan.Cpu, value),
            () => Reset(F7bsdFan.Cpu),
            lifecycleSync);
        systemControl = new ControlSensor(
            "minisforum.um780xtx.f7bsd.system-control",
            "UM780 XTX System Fan Mode",
            $"{Name}/{systemFan.Id}",
            value => Set(F7bsdFan.System, value),
            () => Reset(F7bsdFan.System),
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
            IF7bsdBackend candidate = backendFactory();
            try
            {
                candidate.Initialize();
                Apply(candidate.ReadTelemetry());
                backend = candidate;
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
            Log("Minisforum UM780 XTX loaded CPU curve and system mode controls.");
        }
    }

    /// <inheritdoc />
    public void Update()
    {
        lock (lifecycleSync)
        {
            try
            {
                if (backend is not null)
                {
                    Apply(backend.ReadTelemetry());
                }
            }
            catch (Exception exception)
            {
                ClearTelemetry();
                Log($"Minisforum UM780 XTX telemetry read failed: {exception.Message}");
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
            ClearTelemetry();
            IF7bsdBackend? old = backend;
            backend = null;
            if (old is null)
            {
                return;
            }

            try
            {
                old.Dispose();
            }
            catch (Exception exception)
            {
                Log($"Minisforum UM780 XTX baseline restoration failed: {exception.Message}");
            }
        }
    }

    private byte Set(F7bsdFan fan, float percentage)
    {
        lock (lifecycleSync)
        {
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
                Value = F7bsdProfile.ToPercentage(set(value));
            }
        }

        public void Reset()
        {
            lock (sync)
            {
                reset();
                Value = null;
            }
        }

        public void Update()
        {
        }

        internal void Clear()
        {
            lock (sync)
            {
                Value = null;
            }
        }
    }
}
