using FanControl.Plugins;

namespace FanControl.MinisforumUM780XTX;

public sealed class UM780XTXPlugin : IPlugin2
{
    private readonly object lifecycleSync = new();
    private readonly IPluginLogger? logger;
    private readonly Sensor cpuFan = new("cpu-rpm", "CPU Fan");
    private readonly Sensor systemFan = new("system-rpm", "System Fan");
    private readonly Sensor cpuTemperature = new("cpu-temperature", "CPU Temperature");
    private readonly Sensor systemTemperature = new(
        "system-temperature",
        "System Temperature");
    private readonly ControlSensor cpuControl;
    private readonly ControlSensor systemControl;
    private PawnIoF7bsdBackend? backend;

    public UM780XTXPlugin()
        : this(null)
    {
    }

    public UM780XTXPlugin(IPluginLogger? logger)
    {
        this.logger = logger;
        cpuControl = new ControlSensor(
            "cpu-control",
            "CPU Fan Control",
            $"{Name}/{cpuFan.Id}",
            SetCpu,
            ResetCpu);
        systemControl = new ControlSensor(
            "system-control",
            "System Fan Control",
            $"{Name}/{systemFan.Id}",
            SetSystem,
            ResetSystem);
    }

    public string Name => "Minisforum UM780 XTX";

    public void Initialize()
    {
        lock (lifecycleSync)
        {
            Close();
            if (backend is not null)
            {
                throw new InvalidOperationException(
                    "The previous backend still requires verified restoration. " +
                    "Restart Windows before reinitializing the plugin.");
            }

            PawnIoF7bsdBackend candidate = new();
            try
            {
                candidate.Initialize();
                Apply(candidate.ReadTelemetry());
                backend = candidate;
                Log("Minisforum UM780 XTX initialized.");
            }
            catch
            {
                candidate.Dispose();
                throw;
            }
        }
    }

    public void Load(IPluginSensorsContainer container)
    {
        container.FanSensors.AddRange([cpuFan, systemFan]);
        container.TempSensors.AddRange([cpuTemperature, systemTemperature]);
        container.ControlSensors.AddRange([cpuControl, systemControl]);
    }

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
                Log($"UM780 XTX telemetry read failed: {exception.Message}");
            }
        }
    }

    public void Close()
    {
        lock (lifecycleSync)
        {
            if (backend is not null)
            {
                try
                {
                    backend.Dispose();
                    backend = null;
                }
                catch (Exception exception)
                {
                    Log($"UM780 XTX restoration remains pending: {exception.Message}");
                    return;
                }
            }

            cpuControl.Clear();
            systemControl.Clear();
            ClearTelemetry();
        }
    }

    private byte SetCpu(float percentage) => Set(
        percentage,
        static (active, code) => active.SetCpu(code));

    private byte SetSystem(float percentage) => Set(
        percentage,
        static (active, code) => active.SetSystem(code));

    private byte Set(
        float percentage,
        Func<PawnIoF7bsdBackend, byte, byte> set)
    {
        lock (lifecycleSync)
        {
            byte code = F7bsdProfile.ToCode(percentage);
            return set(ActiveBackend(), code);
        }
    }

    private void ResetCpu() => Reset(static active => active.ResetCpu());

    private void ResetSystem() => Reset(static active => active.ResetSystem());

    private void Reset(Action<PawnIoF7bsdBackend> reset)
    {
        lock (lifecycleSync)
        {
            if (backend is not null)
            {
                reset(backend);
            }
        }
    }

    private PawnIoF7bsdBackend ActiveBackend() => backend ??
        throw new InvalidOperationException("The F7BSD backend is unavailable.");

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

        public void Update() { }
    }

    private sealed class ControlSensor(
        string id,
        string name,
        string pairedFanSensorId,
        Func<float, byte> set,
        Action reset) : IPluginControlSensor2
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public string PairedFanSensorId { get; } = pairedFanSensorId;

        public float? Value { get; private set; }

        public void Set(float value)
        {
            Value = F7bsdProfile.ToPercentage(set(value));
        }

        public void Reset()
        {
            reset();
            Value = null;
        }

        public void Update() { }

        internal void Clear() => Value = null;
    }
}
