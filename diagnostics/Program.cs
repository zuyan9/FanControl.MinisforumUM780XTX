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
        parsedCode <= F7bsdProfile.MaximumCode &&
        int.TryParse(seconds, out int parsedSeconds) &&
        parsedSeconds is >= 1 and <= 120 =>
            Run($"cpu-{parsedCode}", () => Cpu(parsedCode, parsedSeconds)),
    ["cpu-step"] => Run("cpu-step-low-v4", CpuStep),
    ["cpu-stop-start"] => Run("cpu-stop-start-v4", CpuStopStart),
    ["cpu-zero-load"] => Run("cpu-zero-load-v4", CpuZeroLoad),
    ["cpu-soak"] => Run("cpu-soak-10-120", () => Cpu(10, 120)),
    ["plugin-cpu"] => Run("plugin-cpu-18", PluginCpu),
    ["plugin-cpu-step"] => Run("plugin-cpu-step-v4", PluginCpuStep),
    ["plugin-cpu-burst"] => Run("plugin-cpu-burst-v4", PluginCpuBurst),
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
            $"applying cool-temperature target code {code} ({code * 100} RPM); " +
            "the EC thermal tail begins above 66 C and reaches code 51 at 93 C");
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
                telemetry.CpuFanRpm > 6_000 ||
                (code >= 10 && index >= 2 && telemetry.CpuFanRpm < 500))
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

static void CpuStopStart()
{
    PawnIoF7bsdBackend backend = new();
    bool initialized = false;
    try
    {
        backend.Initialize();
        initialized = true;
        F7bsdTelemetry before = backend.ReadTelemetry();
        if (before.CpuTemperatureC is < 1 or > 64)
        {
            throw new InvalidOperationException(
                $"CPU is {before.CpuTemperatureC} C; stop/start test requires " +
                "a plausible value at or below 64 C.");
        }

        backend.Set(F7bsdFan.Cpu, 18);
        for (int cycle = 1; cycle <= 3; cycle++)
        {
            Stopwatch stopTransaction = Stopwatch.StartNew();
            backend.Set(F7bsdFan.Cpu, 0);
            stopTransaction.Stop();
            int stoppedSamples = 0;
            bool stopped = false;
            for (int sample = 1; sample <= 30; sample++)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
                F7bsdTelemetry telemetry = backend.ReadTelemetry();
                Console.WriteLine(
                    $"cycle {cycle} stop {sample}: {telemetry.CpuTemperatureC} C / " +
                    $"{telemetry.CpuFanRpm} RPM");
                if (telemetry.CpuTemperatureC >= 67 || telemetry.CpuFanRpm > 6_000)
                {
                    throw new IOException(
                        $"CPU stop abort at {telemetry.CpuTemperatureC} C / " +
                        $"{telemetry.CpuFanRpm} RPM.");
                }
                stoppedSamples = telemetry.CpuFanRpm <= 150
                    ? stoppedSamples + 1
                    : 0;
                if (stoppedSamples >= 3)
                {
                    stopped = true;
                    break;
                }
            }
            if (!stopped)
            {
                throw new IOException("CPU fan did not reach a stable stopped state.");
            }

            Stopwatch startTransaction = Stopwatch.StartNew();
            backend.Set(F7bsdFan.Cpu, 18);
            startTransaction.Stop();
            bool restarted = false;
            for (int sample = 1; sample <= 15; sample++)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
                F7bsdTelemetry telemetry = backend.ReadTelemetry();
                Console.WriteLine(
                    $"cycle {cycle} start {sample}: {telemetry.CpuTemperatureC} C / " +
                    $"{telemetry.CpuFanRpm} RPM");
                if (telemetry.CpuTemperatureC >= 75 || telemetry.CpuFanRpm > 6_000)
                {
                    throw new IOException(
                        $"CPU restart abort at {telemetry.CpuTemperatureC} C / " +
                        $"{telemetry.CpuFanRpm} RPM.");
                }
                if (telemetry.CpuFanRpm >= 800)
                {
                    restarted = true;
                    break;
                }
            }
            if (!restarted)
            {
                throw new IOException("CPU fan did not restart above 800 RPM.");
            }
            Console.WriteLine(
                $"cycle {cycle}: stop transaction " +
                $"{stopTransaction.Elapsed.TotalMilliseconds:F1} ms; start transaction " +
                $"{startTransaction.Elapsed.TotalMilliseconds:F1} ms");
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

static void CpuZeroLoad()
{
    PawnIoTransport? transport = null;
    PawnIoF7bsdBackend? backend = null;
    CancellationTokenSource burnCancellation = new();
    List<Task> burnTasks = [];
    bool initialized = false;
    try
    {
        transport = new PawnIoTransport();
        backend = new PawnIoF7bsdBackend(HostIdentity.Read, () => transport);
        backend.Initialize();
        initialized = true;

        backend.Set(F7bsdFan.Cpu, F7bsdProfile.MaximumCode);
        int coolSamples = 0;
        for (int sample = 1; sample <= 120 && coolSamples < 4; sample++)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            F7bsdTelemetry telemetry = backend.ReadTelemetry();
            Console.WriteLine(
                $"pre-cool {sample}: {telemetry.CpuTemperatureC} C / " +
                $"{telemetry.CpuFanRpm} RPM");
            if (telemetry.CpuTemperatureC >= 85 || telemetry.CpuFanRpm > 6_000)
            {
                throw new IOException("CPU pre-cool telemetry was outside bounds.");
            }
            coolSamples = telemetry.CpuTemperatureC <= 58 ? coolSamples + 1 : 0;
        }
        if (coolSamples < 4)
        {
            throw new IOException("CPU did not cool to 58 C before the zero-load test.");
        }

        Stopwatch stopTransaction = Stopwatch.StartNew();
        backend.Set(F7bsdFan.Cpu, 0);
        stopTransaction.Stop();
        int stoppedSamples = 0;
        for (int sample = 1; sample <= 90 && stoppedSamples < 4; sample++)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            F7bsdTelemetry telemetry = backend.ReadTelemetry();
            byte[] state = ReadCpuProbeState(transport);
            Console.WriteLine(
                $"stop {sample}: {telemetry.CpuTemperatureC} C / " +
                $"{telemetry.CpuFanRpm} RPM / target {state[3]}");
            if (telemetry.CpuTemperatureC >= 67 || state[3] != 0)
            {
                backend.Set(F7bsdFan.Cpu, F7bsdProfile.MaximumCode);
                throw new IOException(
                    "CPU left the <=66 C zero-target band before reaching stop.");
            }
            stoppedSamples = telemetry.CpuFanRpm <= 150
                ? stoppedSamples + 1
                : 0;
        }
        if (stoppedSamples < 4)
        {
            backend.Set(F7bsdFan.Cpu, F7bsdProfile.MaximumCode);
            throw new IOException("CPU fan did not reach a stable physical stop.");
        }

        EnsureCpuBurnWorkers(burnTasks, burnCancellation.Token, 2);
        Stopwatch loadClock = Stopwatch.StartNew();
        double? firstAbove66Ms = null;
        double? firstNonzeroTargetMs = null;
        double? firstSpinningRpmMs = null;
        int sustainedRestartSamples = 0;
        bool completedSustainedRestart = false;
        for (int sample = 1; sample <= 180; sample++)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            if (loadClock.Elapsed >= TimeSpan.FromSeconds(10) && burnTasks.Count < 4)
            {
                EnsureCpuBurnWorkers(burnTasks, burnCancellation.Token, 4);
            }
            if (loadClock.Elapsed >= TimeSpan.FromSeconds(20) && burnTasks.Count < 8)
            {
                EnsureCpuBurnWorkers(burnTasks, burnCancellation.Token, 8);
            }
            if (loadClock.Elapsed >= TimeSpan.FromSeconds(30) &&
                burnTasks.Count < Environment.ProcessorCount)
            {
                EnsureCpuBurnWorkers(
                    burnTasks,
                    burnCancellation.Token,
                    Math.Min(Environment.ProcessorCount, 32));
            }

            F7bsdTelemetry telemetry = backend.ReadTelemetry();
            byte[] state = ReadCpuProbeState(transport);
            double elapsedMs = loadClock.Elapsed.TotalMilliseconds;
            if (state[1] > 66)
            {
                firstAbove66Ms ??= elapsedMs;
            }
            if (state[3] > 0)
            {
                firstNonzeroTargetMs ??= elapsedMs;
            }
            if (telemetry.CpuFanRpm >= 300)
            {
                firstSpinningRpmMs ??= elapsedMs;
            }
            sustainedRestartSamples = state[3] >= 10 &&
                telemetry.CpuFanRpm >= 800
                    ? sustainedRestartSamples + 1
                    : 0;
            Console.WriteLine(
                $"load {sample}: {elapsedMs:F0} ms / workers {burnTasks.Count} / " +
                $"raw {state[0]} C / effective {state[1]} C / target {state[3]} / " +
                $"{telemetry.CpuFanRpm} RPM");

            if (telemetry.CpuTemperatureC >= 85 || state[0] >= 85)
            {
                backend.Set(F7bsdFan.Cpu, F7bsdProfile.MaximumCode);
                throw new IOException("CPU reached the 85 C zero-load abort limit.");
            }
            if (firstAbove66Ms.HasValue &&
                !firstNonzeroTargetMs.HasValue &&
                elapsedMs - firstAbove66Ms.Value > 3_000)
            {
                backend.Set(F7bsdFan.Cpu, F7bsdProfile.MaximumCode);
                throw new IOException(
                    "CPU target remained zero over three seconds above 66 C.");
            }
            if (state[0] >= 75 && telemetry.CpuFanRpm < 300)
            {
                backend.Set(F7bsdFan.Cpu, F7bsdProfile.MaximumCode);
                throw new IOException("CPU fan had not restarted by 75 C.");
            }
            if (sustainedRestartSamples >= 4)
            {
                completedSustainedRestart = true;
                break;
            }
        }
        if (!firstAbove66Ms.HasValue || !firstNonzeroTargetMs.HasValue ||
            !firstSpinningRpmMs.HasValue || !completedSustainedRestart)
        {
            backend.Set(F7bsdFan.Cpu, F7bsdProfile.MaximumCode);
            throw new IOException(
                "CPU zero-load test did not observe four consecutive sustainable " +
                "thermal-tail restart samples.");
        }

        burnCancellation.Cancel();
        if (!Task.WaitAll(burnTasks.ToArray(), TimeSpan.FromSeconds(10)))
        {
            throw new IOException("CPU load workers did not stop within ten seconds.");
        }
        burnTasks.Clear();

        Stopwatch restartTransaction = Stopwatch.StartNew();
        backend.Set(F7bsdFan.Cpu, 18);
        restartTransaction.Stop();
        bool running = false;
        for (int sample = 1; sample <= 30; sample++)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            F7bsdTelemetry telemetry = backend.ReadTelemetry();
            Console.WriteLine(
                $"post-load restart {sample}: {telemetry.CpuTemperatureC} C / " +
                $"{telemetry.CpuFanRpm} RPM");
            if (telemetry.CpuFanRpm >= 800)
            {
                running = true;
                break;
            }
        }
        if (!running)
        {
            throw new IOException("CPU fan did not remain restartable after tail load.");
        }

        Console.WriteLine(
            $"ZERO_LOAD_TIMING stop_set_ms={stopTransaction.Elapsed.TotalMilliseconds:F1} " +
            $"above66_ms={firstAbove66Ms:F1} target_nonzero_ms={firstNonzeroTargetMs:F1} " +
            $"fan_300rpm_ms={firstSpinningRpmMs:F1} " +
            $"restart_set_ms={restartTransaction.Elapsed.TotalMilliseconds:F1}");
    }
    finally
    {
        burnCancellation.Cancel();
        try
        {
            if (burnTasks.Count != 0)
            {
                Task.WaitAll(burnTasks.ToArray(), TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception workerFailure)
        {
            // Hardware restoration takes priority over reporting a diagnostic
            // worker teardown failure. Preserve it in the probe log.
            Console.Error.WriteLine(
                "CPU load-worker cleanup failed: " + workerFailure);
        }
        burnCancellation.Dispose();
        if (backend is not null)
        {
            if (initialized)
            {
                try
                {
                    backend.Set(F7bsdFan.Cpu, F7bsdProfile.MaximumCode);
                }
                finally
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
            }
            else
            {
                backend.Dispose();
            }
        }
        else
        {
            transport?.Dispose();
        }
    }
}

static byte[] ReadCpuProbeState(PawnIoTransport transport)
{
    byte[] state = transport.Read(F7bsdProfile.CpuSafetyStateAddresses);
    if (!F7bsdProfile.PlausibleTemperature(state[0]) ||
        !F7bsdProfile.PlausibleTemperature(state[1]) ||
        state[2] != 0 || state[3] > F7bsdProfile.MaximumCode)
    {
        throw new IOException(
            $"Invalid CPU probe state: {string.Join(' ', state.Select(value => value.ToString("X2")))}");
    }
    return state;
}

static void EnsureCpuBurnWorkers(
    List<Task> workers,
    CancellationToken cancellation,
    int requestedCount)
{
    while (workers.Count < requestedCount)
    {
        int worker = workers.Count;
        workers.Add(Task.Factory.StartNew(
            () => BurnCpu(worker, cancellation),
            cancellation,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default));
    }
}

static void BurnCpu(int worker, CancellationToken cancellation)
{
    ulong state = unchecked(0x9E3779B97F4A7C15UL * (ulong)(worker + 1));
    double accumulator = worker + 1;
    while (!cancellation.IsCancellationRequested)
    {
        for (int index = 0; index < 100_000; index++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            accumulator += Math.Sqrt((state & 0xffff) + 1);
            if (accumulator > 1e100)
            {
                accumulator = worker + 1;
            }
        }
    }
    GC.KeepAlive(accumulator);
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
            control.Id.EndsWith(UM780XTXPlugin.CpuControlId, StringComparison.Ordinal));
        if (!cpu.Id.EndsWith(UM780XTXPlugin.CpuControlId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The staged CPU control ID is not v4.");
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
        try
        {
            cpu?.Reset();
        }
        finally
        {
            plugin.Close();
        }
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
            control.Id.EndsWith(UM780XTXPlugin.CpuControlId, StringComparison.Ordinal));
        if (Value(container.TempSensors, "cpu-temperature") >= 70)
        {
            throw new InvalidOperationException(
                "CPU plugin-step test requires a temperature below 70 C.");
        }

        foreach (byte code in new byte[] { 18, 16, 14, 12, 10, 12, 14, 16, 18 })
        {
            Stopwatch mutation = Stopwatch.StartNew();
            cpu.Set(F7bsdProfile.ToPercentage(code));
            mutation.Stop();
            if (cpu.Value != F7bsdProfile.ToPercentage(code))
            {
                throw new IOException(
                    $"CPU code {code} was not confirmed synchronously.");
            }
            Console.WriteLine(
                $"Code {code} confirmed in {mutation.Elapsed.TotalMilliseconds:F1} ms.");
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

static void PluginCpuBurst()
{
    UM780XTXPlugin plugin = new(new ProbeLogger());
    ProbeContainer container = new();
    IPluginControlSensor2? cpu = null;
    try
    {
        plugin.Initialize();
        plugin.Load(container);
        cpu = (IPluginControlSensor2)container.ControlSensors.Single(control =>
            control.Id.EndsWith(UM780XTXPlugin.CpuControlId, StringComparison.Ordinal));
        if (Value(container.TempSensors, "cpu-temperature") >= 75)
        {
            throw new InvalidOperationException(
                "CPU plugin-burst test requires a temperature below 75 C.");
        }

        byte[] oneSweep =
        [
            .. Enumerable.Range(0, F7bsdProfile.MaximumCode + 1)
                .Select(code => (byte)code),
            .. Enumerable.Range(1, F7bsdProfile.MaximumCode)
                .Reverse()
                .Select(code => (byte)code),
        ];
        List<double> mutationMilliseconds = [];
        int request = 0;
        int hardwareMutations = 0;
        byte? previousCode = null;
        for (int cycle = 1; cycle <= 5; cycle++)
        {
            foreach (byte code in oneSweep)
            {
                Stopwatch mutation = Stopwatch.StartNew();
                cpu.Set(F7bsdProfile.ToPercentage(code));
                mutation.Stop();
                request++;
                if (previousCode != code)
                {
                    hardwareMutations++;
                }
                previousCode = code;
                mutationMilliseconds.Add(mutation.Elapsed.TotalMilliseconds);
                if (cpu.Value != F7bsdProfile.ToPercentage(code))
                {
                    throw new IOException(
                        $"CPU burst code {code} was not confirmed synchronously.");
                }

                if (request % 25 == 0)
                {
                    plugin.Update();
                    float temperature = Value(
                        container.TempSensors,
                        "cpu-temperature");
                    float cpuRpm = Value(container.FanSensors, "fan1");
                    float systemRpm = Value(container.FanSensors, "fan2");
                    if (temperature >= 85 || cpuRpm > 6_000 || systemRpm <= 0)
                    {
                        throw new IOException(
                            $"CPU burst abort at {temperature} C / " +
                            $"CPU {cpuRpm} RPM / system {systemRpm} RPM.");
                    }
                    Console.WriteLine(
                        $"request {request}: cycle {cycle}, code {code}, " +
                        $"{temperature} C / {cpuRpm} RPM; " +
                        $"last {mutation.Elapsed.TotalMilliseconds:F1} ms");
                }
            }
        }

        cpu.Set(F7bsdProfile.ToPercentage(18));
        bool running = false;
        for (int sample = 1; sample <= 10; sample++)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            plugin.Update();
            float temperature = Value(container.TempSensors, "cpu-temperature");
            float rpm = Value(container.FanSensors, "fan1");
            Console.WriteLine(
                $"post-burst {sample}: {temperature} C / {rpm} RPM");
            if (temperature >= 85 || rpm > 6_000)
            {
                throw new IOException(
                    $"CPU post-burst abort at {temperature} C / {rpm} RPM.");
            }
            if (rpm >= 800)
            {
                running = true;
                break;
            }
        }
        if (!running)
        {
            throw new IOException("CPU fan did not remain restartable after the burst.");
        }

        mutationMilliseconds.Sort();
        double mean = mutationMilliseconds.Average();
        double p95 = mutationMilliseconds[
            (int)Math.Ceiling(mutationMilliseconds.Count * 0.95) - 1];
        double maximum = mutationMilliseconds[^1];
        Console.WriteLine(
            $"BURST_TIMING requests={mutationMilliseconds.Count} " +
            $"hardware_mutations={hardwareMutations} " +
            $"mean_ms={mean:F1} p95_ms={p95:F1} max_ms={maximum:F1}");
        if (p95 > 250 || maximum > 1_500)
        {
            throw new IOException(
                $"CPU burst transaction latency was excessive: " +
                $"p95 {p95:F1} ms / max {maximum:F1} ms.");
        }
    }
    finally
    {
        try
        {
            cpu?.Reset();
        }
        finally
        {
            plugin.Close();
        }
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
            control.Id.EndsWith(UM780XTXPlugin.CpuControlId, StringComparison.Ordinal));
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
        "plugin [1..10]|cpu {0..51} {1..120 seconds}|cpu-step|cpu-stop-start|" +
        "cpu-zero-load|cpu-soak|" +
        "plugin-cpu|plugin-cpu-step|plugin-cpu-burst|" +
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
