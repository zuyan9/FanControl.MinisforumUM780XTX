using System.Diagnostics;
using FanControl.MinisforumUM780XTX;
using FanControl.Plugins;

return args switch
{
    ["identity"] => Run("identity", Identity),
    ["profile"] => Run("profile", Profile),
    ["telemetry"] => Run("telemetry", () => Telemetry(1)),
    ["telemetry", string count] when int.TryParse(count, out int parsed) &&
        parsed is >= 1 and <= 10 => Run("telemetry", () => Telemetry(parsed)),
    ["stock"] => Run("stock", Stock),
    ["plugin"] => Run("plugin", () => Plugin(1)),
    ["plugin", string count] when int.TryParse(count, out int parsed) &&
        parsed is >= 1 and <= 10 => Run("plugin", () => Plugin(parsed)),
    ["cpu", string code, string seconds]
        when byte.TryParse(code, out byte parsedCode) &&
        parsedCode is 10 or 12 or 14 or 16 or 18 &&
        int.TryParse(seconds, out int parsedSeconds) &&
        parsedSeconds is >= 1 and <= 30 =>
            Run($"cpu-{parsedCode}", () => Cpu(parsedCode, parsedSeconds)),
    ["cpu-step"] => Run("cpu-step-low-v3", CpuStep),
    ["cpu-soak"] => Run("cpu-soak-10-120", () => Cpu(10, 120)),
    ["plugin-cpu"] => Run("plugin-cpu-18", PluginCpu),
    ["plugin-cpu-step"] => Run("plugin-cpu-step-v3", PluginCpuStep),
    ["plugin-system"] => Run("plugin-system-30", PluginSystem),
    ["plugin-combined"] => Run("plugin-combined-28-30", PluginCombined),
    ["system", string code, string seconds]
        when byte.TryParse(code, out byte parsedCode) &&
        parsedCode is 30 or 51 &&
        int.TryParse(seconds, out int parsedSeconds) &&
        parsedSeconds is >= 1 and <= 30 =>
            Run($"system-{parsedCode}", () => SystemFan(parsedCode, parsedSeconds)),
    _ => Usage(),
};

static int Run(string stage, Action action)
{
    Console.WriteLine($"UM780 XTX staged hardware probe: {stage}");
    Console.WriteLine($"Started: {DateTimeOffset.Now:O}");
    Stopwatch stopwatch = Stopwatch.StartNew();
    try
    {
        HostIdentityGate.Assert(HostIdentity.Read());
        action();
        Console.WriteLine($"PASS {stage} ({stopwatch.Elapsed.TotalMilliseconds:F1} ms)");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL {stage} ({stopwatch.Elapsed.TotalMilliseconds:F1} ms)");
        Console.Error.WriteLine(exception);
        return 1;
    }
}

static void Identity()
{
    using PawnIoTransport transport = new();
    byte[] identity = transport.ReadPnpIdentity();
    RequireEqual("PNP identity", F7bsdProfile.ExpectedPnpIdentity, identity);
    Console.WriteLine("PNP: " + Hex(identity));
}

static void Profile()
{
    using PawnIoTransport transport = new();
    byte[] identity = transport.ReadPnpIdentity();
    RequireEqual("PNP identity", F7bsdProfile.ExpectedPnpIdentity, identity);
    byte[] controller = transport.Read(F7bsdProfile.ControllerProfileAddresses);
    RequireEqual("controller profile", F7bsdProfile.ExpectedControllerProfile, controller);
    byte[] critical = transport.Read(F7bsdProfile.CpuCriticalAddresses);
    F7bsdProfile.ValidateCpuCriticalRow(critical);
    Console.WriteLine("PNP: " + Hex(identity));
    Console.WriteLine("Controller: " + Hex(controller));
    Console.WriteLine("CPU critical row: " + Hex(critical));
}

static void Telemetry(int count)
{
    using PawnIoTransport transport = new();
    RequireEqual(
        "PNP identity",
        F7bsdProfile.ExpectedPnpIdentity,
        transport.ReadPnpIdentity());
    RequireEqual(
        "controller profile",
        F7bsdProfile.ExpectedControllerProfile,
        transport.Read(F7bsdProfile.ControllerProfileAddresses));

    for (int index = 0; index < count; index++)
    {
        byte[] sample = transport.Read(F7bsdProfile.TelemetryAddresses);
        if (!F7bsdTelemetryDecoder.TryDecode(sample, out F7bsdTelemetry? telemetry))
        {
            throw new IOException("Torn tachometer sample; this diagnostic does not retry.");
        }
        Console.WriteLine(
            $"{index + 1}: CPU {telemetry!.CpuTemperatureC} C / " +
            $"{telemetry.CpuFanRpm} RPM; system {telemetry.SystemTemperatureC} C / " +
            $"{telemetry.SystemFanRpm} RPM");
        if (index + 1 < count)
        {
            Thread.Sleep(TimeSpan.FromSeconds(1));
        }
    }
}

static void Stock()
{
    using PawnIoF7bsdBackend backend = new();
    backend.Initialize();
    F7bsdTelemetry telemetry = backend.ReadTelemetry();
    Console.WriteLine(
        $"Stock state validated; CPU {telemetry.CpuTemperatureC} C / " +
        $"{telemetry.CpuFanRpm} RPM; system {telemetry.SystemTemperatureC} C / " +
        $"{telemetry.SystemFanRpm} RPM");
}

static void Plugin(int count)
{
    UM780XTXPlugin plugin = new(new ProbeLogger());
    ProbeContainer container = new();
    try
    {
        plugin.Initialize();
        plugin.Load(container);
        if (container.FanSensors.Count != 2 ||
            container.TempSensors.Count != 2 ||
            container.ControlSensors.Count != 2)
        {
            throw new InvalidOperationException(
                "Plugin surface was not exactly 2 fans, 2 temperatures, 2 controls.");
        }

        for (int index = 0; index < count; index++)
        {
            if (index != 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
                plugin.Update();
            }
            Console.WriteLine(
                $"{index + 1}: CPU {Value(container.TempSensors, "cpu-temperature")} C / " +
                $"{Value(container.FanSensors, "fan1")} RPM; system " +
                $"{Value(container.TempSensors, "system-temperature")} C / " +
                $"{Value(container.FanSensors, "fan2")} RPM; controls 2");
        }
    }
    finally
    {
        plugin.Close();
    }
}

static void Cpu(byte code, int seconds)
{
    PawnIoF7bsdBackend backend = new();
    bool initialized = false;
    try
    {
        backend.Initialize();
        initialized = true;
        F7bsdTelemetry before = backend.ReadTelemetry();
        if (before.CpuTemperatureC is < 1 or >= 70)
        {
            throw new InvalidOperationException(
                $"CPU is {before.CpuTemperatureC} C; live test requires a " +
                "plausible value below 70 C.");
        }

        F7bsdCpuRowState[] baseline = F7bsdCpuPolicy.GetB1MutableStates();
        F7bsdCpuRowState[] target = F7bsdCpuPolicy.CompileTarget(code);
        F7bsdCpuTransitionStep[] plan =
            F7bsdCpuPolicy.PlanTransition(baseline, target);
        Console.WriteLine(
            $"Before: CPU {before.CpuTemperatureC} C / {before.CpuFanRpm} RPM; " +
            $"applying native target code {code} ({code * 100} RPM) through " +
            "74 C; the EC thermal tail reaches code 51 at 93 C");
        Console.WriteLine(
            $"Planned CPU policy writes from exact B1: {plan.Length}");
        Stopwatch mutation = Stopwatch.StartNew();
        byte applied = backend.Set(F7bsdFan.Cpu, code);
        mutation.Stop();
        if (applied != code)
        {
            throw new IOException($"Backend returned code {applied}; expected {code}.");
        }
        Console.WriteLine(
            $"CPU transaction completed in {mutation.Elapsed.TotalMilliseconds:F1} ms.");
        Console.WriteLine(
            "Expected mutable CPU table: " +
            Hex(F7bsdCpuPolicy.ToMutableBytes(target)));

        for (int index = 0; index < seconds; index++)
        {
            if (index != 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
            F7bsdTelemetry telemetry = backend.ReadTelemetry();
            if (telemetry.CpuTemperatureC >= 75 ||
                telemetry.CpuFanRpm == 0 ||
                telemetry.CpuFanRpm > 6_000 ||
                (index >= 2 && telemetry.CpuFanRpm < 500))
            {
                throw new IOException(
                    $"CPU live-test abort at {telemetry.CpuTemperatureC} C / " +
                    $"{telemetry.CpuFanRpm} RPM.");
            }
            Console.WriteLine(
                $"{index + 1}: CPU {telemetry.CpuTemperatureC} C / " +
                $"{telemetry.CpuFanRpm} RPM; system {telemetry.SystemTemperatureC} C / " +
                $"{telemetry.SystemFanRpm} RPM");
        }
    }
    finally
    {
        if (initialized)
        {
            try
            {
                backend.Reset(F7bsdFan.Cpu);
                Console.WriteLine("CPU reset to exact OEM B1 completed.");
            }
            finally
            {
                backend.Dispose();
            }
        }
        else
        {
            backend.Dispose();
        }
    }
}

static void CpuStep()
{
    PawnIoF7bsdBackend backend = new();
    bool initialized = false;
    try
    {
        backend.Initialize();
        initialized = true;
        F7bsdTelemetry before = backend.ReadTelemetry();
        if (before.CpuTemperatureC >= 70)
        {
            throw new InvalidOperationException(
                $"CPU is {before.CpuTemperatureC} C; step test requires <70 C.");
        }

        F7bsdCpuRowState[] current = F7bsdCpuPolicy.GetB1MutableStates();
        foreach (byte code in new byte[] { 18, 16, 14, 12, 10, 12, 18 })
        {
            F7bsdCpuRowState[] target = F7bsdCpuPolicy.CompileTarget(code);
            Console.WriteLine(
                $"Set code {code}; planned writes from the current table: " +
                $"{F7bsdCpuPolicy.PlanTransition(current, target).Length}.");
            backend.Set(F7bsdFan.Cpu, code);
            current = target;
            for (int sample = 0; sample < 5; sample++)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
                F7bsdTelemetry telemetry = backend.ReadTelemetry();
                if (telemetry.CpuTemperatureC >= 75 ||
                    telemetry.CpuFanRpm == 0 ||
                    telemetry.CpuFanRpm > 6_000 ||
                    (sample >= 2 && telemetry.CpuFanRpm < 500))
                {
                    throw new IOException(
                        $"CPU step-test abort at {telemetry.CpuTemperatureC} C / " +
                        $"{telemetry.CpuFanRpm} RPM.");
                }
                Console.WriteLine(
                    $"code {code}, sample {sample + 1}: " +
                    $"{telemetry.CpuTemperatureC} C / {telemetry.CpuFanRpm} RPM");
            }
        }
    }
    finally
    {
        if (initialized)
        {
            try
            {
                backend.Reset(F7bsdFan.Cpu);
                Console.WriteLine("CPU reset to exact OEM B1 completed.");
            }
            finally
            {
                backend.Dispose();
            }
        }
        else
        {
            backend.Dispose();
        }
    }
}

static void SystemFan(byte code, int seconds)
{
    PawnIoF7bsdBackend backend = new();
    bool initialized = false;
    try
    {
        backend.Initialize();
        initialized = true;
        F7bsdTelemetry before = backend.ReadTelemetry();
        if (!F7bsdProfile.PlausibleTemperature((byte)before.SystemTemperatureC) ||
            (code != F7bsdProfile.MaximumCode &&
                before.SystemTemperatureC >= F7bsdProfile.SystemFailsafeTemperatureC))
        {
            throw new InvalidOperationException(
                $"System raw temperature is {before.SystemTemperatureC} C; " +
                "the attended code-30 test requires a plausible value below 70 C.");
        }

        Console.WriteLine(
            $"Before: system {before.SystemTemperatureC} C / " +
            $"{before.SystemFanRpm} RPM; applying raw code {code} " +
            $"({code * 100} RPM nominal target)");
        byte applied = backend.Set(F7bsdFan.System, code);
        if (applied != code)
        {
            throw new IOException(
                $"Backend applied failsafe code {applied}; expected attended code {code}.");
        }

        for (int index = 0; index < seconds; index++)
        {
            if (index != 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
            F7bsdTelemetry telemetry = backend.ReadTelemetry();
            if (telemetry.SystemAppliedCode != code ||
                telemetry.SystemTemperatureC >=
                    F7bsdProfile.SystemFailsafeTemperatureC ||
                telemetry.SystemFanRpm == 0)
            {
                throw new IOException(
                    $"System live-test abort at {telemetry.SystemTemperatureC} C / " +
                    $"{telemetry.SystemFanRpm} RPM / applied code " +
                    $"{telemetry.SystemAppliedCode?.ToString() ?? "none"}.");
            }
            Console.WriteLine(
                $"{index + 1}: system {telemetry.SystemTemperatureC} C / " +
                $"{telemetry.SystemFanRpm} RPM; applied code " +
                $"{telemetry.SystemAppliedCode}");
        }
    }
    finally
    {
        if (initialized)
        {
            try
            {
                backend.Reset(F7bsdFan.System);
                Console.WriteLine(
                    "System target seeded full and firmware-temperature ownership restored.");
            }
            finally
            {
                backend.Dispose();
            }
        }
        else
        {
            backend.Dispose();
        }
    }
}

static void PluginCpu()
{
    UM780XTXPlugin plugin = new(new ProbeLogger());
    ProbeContainer container = new();
    IPluginControlSensor2? cpu = null;
    try
    {
        plugin.Initialize();
        plugin.Load(container);
        cpu = (IPluginControlSensor2)container.ControlSensors.Single(control =>
            control.Id.EndsWith("cpu-native-v3", StringComparison.Ordinal));
        if (!cpu.Id.EndsWith("cpu-native-v3", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The staged CPU control ID is not v3.");
        }
        float temperature = Value(container.TempSensors, "cpu-temperature");
        if (temperature >= 70)
        {
            throw new InvalidOperationException(
                $"CPU is {temperature} C; plugin control test requires <70 C.");
        }

        float requested = F7bsdProfile.ToPercentage(18);
        cpu.Set(requested);
        if (cpu.Value != requested)
        {
            throw new IOException("Plugin did not report the verified code-18 target.");
        }
        for (int index = 0; index < 10; index++)
        {
            if (index != 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
                plugin.Update();
            }
            Console.WriteLine(
                $"{index + 1}: CPU {Value(container.TempSensors, "cpu-temperature")} C / " +
                $"{Value(container.FanSensors, "fan1")} RPM; " +
                $"control {cpu.Value:F2}% (code 18)");
        }
    }
    finally
    {
        cpu?.Reset();
        plugin.Close();
    }
}

static void PluginCpuStep()
{
    UM780XTXPlugin plugin = new(new ProbeLogger());
    ProbeContainer container = new();
    IPluginControlSensor2? cpu = null;
    try
    {
        plugin.Initialize();
        plugin.Load(container);
        cpu = (IPluginControlSensor2)container.ControlSensors.Single(control =>
            control.Id.EndsWith("cpu-native-v3", StringComparison.Ordinal));
        if (Value(container.TempSensors, "cpu-temperature") >= 70)
        {
            throw new InvalidOperationException(
                "CPU plugin-step test requires a temperature below 70 C.");
        }

        cpu.Set(F7bsdProfile.ToPercentage(18));
        foreach (byte code in new byte[] { 16, 14, 12, 10 })
        {
            cpu.Set(F7bsdProfile.ToPercentage(code));
        }
        if (cpu.Value != F7bsdProfile.ToPercentage(18))
        {
            throw new IOException(
                "A suppressed CPU request was reported before hardware confirmation.");
        }

        Thread.Sleep(TimeSpan.FromMilliseconds(1_100));
        plugin.Update();
        if (cpu.Value != F7bsdProfile.ToPercentage(10))
        {
            throw new IOException("The coalesced code-10 request was not confirmed.");
        }
        float downTemperature = Value(container.TempSensors, "cpu-temperature");
        float downRpm = Value(container.FanSensors, "fan1");
        if (downTemperature >= 75 || downRpm == 0 || downRpm > 6_000)
        {
            throw new IOException(
                $"Plugin coalesced-down abort at {downTemperature} C / " +
                $"{downRpm} RPM.");
        }
        Console.WriteLine(
            $"Coalesced down: CPU {downTemperature} C / {downRpm} RPM / " +
            "code 10 confirmed.");

        foreach (byte code in new byte[] { 12, 14, 16, 18 })
        {
            cpu.Set(F7bsdProfile.ToPercentage(code));
        }
        if (cpu.Value != F7bsdProfile.ToPercentage(10))
        {
            throw new IOException(
                "A suppressed CPU request was reported before hardware confirmation.");
        }

        Thread.Sleep(TimeSpan.FromMilliseconds(1_100));
        plugin.Update();
        if (cpu.Value != F7bsdProfile.ToPercentage(18))
        {
            throw new IOException("The coalesced code-18 request was not confirmed.");
        }

        for (int sample = 0; sample < 5; sample++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(1));
            plugin.Update();
            float temperature = Value(container.TempSensors, "cpu-temperature");
            float rpm = Value(container.FanSensors, "fan1");
            if (temperature >= 75 || rpm < 500 || rpm > 6_000)
            {
                throw new IOException(
                    $"Plugin CPU step abort at {temperature} C / {rpm} RPM.");
            }
            Console.WriteLine(
                $"{sample + 1}: CPU {temperature} C / {rpm} RPM / code 18 confirmed.");
        }
    }
    finally
    {
        cpu?.Reset();
        plugin.Close();
    }
}

static void PluginSystem()
{
    UM780XTXPlugin plugin = new(new ProbeLogger());
    ProbeContainer container = new();
    IPluginControlSensor2? system = null;
    try
    {
        plugin.Initialize();
        plugin.Load(container);
        system = (IPluginControlSensor2)container.ControlSensors.Single(control =>
            control.Id.EndsWith("system-raw-v2", StringComparison.Ordinal));
        float temperature = Value(container.TempSensors, "system-temperature");
        if (temperature >= F7bsdProfile.SystemFailsafeTemperatureC)
        {
            throw new InvalidOperationException(
                $"System is {temperature} C; plugin control test requires <70 C.");
        }

        float requested = F7bsdProfile.ToPercentage(30);
        system.Set(requested);
        if (system.Value != requested)
        {
            throw new IOException("Plugin did not report verified system code 30.");
        }
        for (int index = 0; index < 10; index++)
        {
            if (index != 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
                plugin.Update();
            }
            float raw = Value(container.TempSensors, "system-temperature");
            float rpm = Value(container.FanSensors, "fan2");
            if (raw >= F7bsdProfile.SystemFailsafeTemperatureC || rpm == 0 ||
                system.Value != requested)
            {
                throw new IOException(
                    $"Plugin system test abort at {raw} C / {rpm} RPM / " +
                    $"control {system.Value?.ToString("F2") ?? "none"}%.");
            }
            Console.WriteLine(
                $"{index + 1}: system {raw} C / {rpm} RPM; " +
                $"control {system.Value:F2}% (code 30)");
        }
    }
    finally
    {
        system?.Reset();
        plugin.Close();
    }
}

static void PluginCombined()
{
    UM780XTXPlugin plugin = new(new ProbeLogger());
    ProbeContainer container = new();
    IPluginControlSensor2? cpu = null;
    IPluginControlSensor2? system = null;
    try
    {
        plugin.Initialize();
        plugin.Load(container);
        cpu = (IPluginControlSensor2)container.ControlSensors.Single(control =>
            control.Id.EndsWith("cpu-native-v3", StringComparison.Ordinal));
        system = (IPluginControlSensor2)container.ControlSensors.Single(control =>
            control.Id.EndsWith("system-raw-v2", StringComparison.Ordinal));
        if (Value(container.TempSensors, "cpu-temperature") >= 70 ||
            Value(container.TempSensors, "system-temperature") >=
                F7bsdProfile.SystemFailsafeTemperatureC)
        {
            throw new InvalidOperationException(
                "Combined plugin test requires both raw temperatures below 70 C.");
        }

        float cpuRequested = F7bsdProfile.ToPercentage(28);
        float systemRequested = F7bsdProfile.ToPercentage(30);
        cpu.Set(cpuRequested);
        system.Set(systemRequested);
        if (cpu.Value != cpuRequested || system.Value != systemRequested)
        {
            throw new IOException("Plugin did not confirm both requested native codes.");
        }

        for (int index = 0; index < 10; index++)
        {
            if (index != 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
                plugin.Update();
            }
            float cpuTemperature = Value(container.TempSensors, "cpu-temperature");
            float systemTemperature = Value(container.TempSensors, "system-temperature");
            float cpuRpm = Value(container.FanSensors, "fan1");
            float systemRpm = Value(container.FanSensors, "fan2");
            if (cpuTemperature >= 80 ||
                systemTemperature >= F7bsdProfile.SystemFailsafeTemperatureC ||
                cpuRpm == 0 || systemRpm == 0 ||
                cpu.Value != cpuRequested || system.Value != systemRequested)
            {
                throw new IOException(
                    $"Combined test abort: CPU {cpuTemperature} C/{cpuRpm} RPM, " +
                    $"system {systemTemperature} C/{systemRpm} RPM.");
            }
            Console.WriteLine(
                $"{index + 1}: CPU {cpuTemperature} C / {cpuRpm} RPM (code 28); " +
                $"system {systemTemperature} C / {systemRpm} RPM (code 30)");
        }
    }
    finally
    {
        system?.Reset();
        cpu?.Reset();
        plugin.Close();
    }
}

static float Value(IEnumerable<IPluginSensor> sensors, string idSuffix) =>
    sensors.Single(sensor => sensor.Id.EndsWith(idSuffix, StringComparison.Ordinal)).Value ??
    throw new IOException($"Plugin sensor {idSuffix} has no value.");

static void RequireEqual(string name, byte[] expected, byte[] actual)
{
    if (!actual.SequenceEqual(expected))
    {
        throw new PlatformNotSupportedException(
            $"Unexpected {name}: {Hex(actual)} (expected {Hex(expected)}).");
    }
}

static string Hex(IEnumerable<byte> bytes) =>
    string.Join(" ", bytes.Select(value => value.ToString("X2")));

static int Usage()
{
    Console.Error.WriteLine(
        "Usage: diagnostics identity|profile|telemetry [1..10]|stock|" +
        "plugin [1..10]|cpu {10|12|14|16|18} {1..30 seconds}|cpu-step|cpu-soak|" +
        "plugin-cpu|plugin-cpu-step|" +
        "system {30|51} {1..30 seconds}|plugin-system|plugin-combined");
    return 2;
}

file sealed class ProbeContainer : IPluginSensorsContainer
{
    public List<IPluginControlSensor> ControlSensors { get; } = [];

    public List<IPluginSensor> FanSensors { get; } = [];

    public List<IPluginSensor> TempSensors { get; } = [];
}

file sealed class ProbeLogger : IPluginLogger
{
    public void Log(string message) => Console.WriteLine("PLUGIN: " + message);
}
