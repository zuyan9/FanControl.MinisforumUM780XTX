using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length < 2 || !File.Exists(args[0]))
{
    Console.Error.WriteLine(
        "Usage: FanControlIpc <FanControl.IPC.dll> " +
        "<info|configs|sensors|plugin-sensors|refresh|load|exit> [config-path] " +
        "or sample <seconds> <jsonl-path> " +
        "or guard <seconds> <jsonl-path> <abort-path> " +
        "<cpu-max-c> <system-max-c> " +
        "[<expected-cpu-control|null|active> " +
        "<expected-system-control|null|active> " +
        "<minimum-system-rpm> [<gpu-max-c> <dimm-max-c>]] " +
        "or inspect <name-pattern> " +
        "[--output <path>]");
    return 2;
}

string assemblyPath = Path.GetFullPath(args[0]);
string assemblyDirectory = Path.GetDirectoryName(assemblyPath) ??
    throw new InvalidOperationException("The assembly has no directory.");
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string candidate = Path.Combine(assemblyDirectory, $"{name.Name}.dll");
    return File.Exists(candidate)
        ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate)
        : null;
};

Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
string command = args[1].ToLowerInvariant();
List<string> commandArguments = args.Skip(2).ToList();
string? outputPath = null;
int outputOption = commandArguments.FindIndex(
    argument => argument.Equals("--output", StringComparison.OrdinalIgnoreCase));
if (outputOption >= 0)
{
    if (outputOption + 1 >= commandArguments.Count)
    {
        Console.Error.WriteLine("--output requires a path.");
        return 2;
    }

    outputPath = Path.GetFullPath(commandArguments[outputOption + 1]);
    commandArguments.RemoveRange(outputOption, 2);
}

const string CpuRpmSensorId =
    "Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.fan1";
const string SystemRpmSensorId =
    "Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.fan2";
const string CpuTemperatureSensorId =
    "Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.cpu-temperature";
const string SystemTemperatureSensorId =
    "Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.system-temperature";
const string CpuControlSensorId =
    "Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.cpu-cool-stop-v4";
const string SystemControlSensorId =
    "Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.system-raw-v2";
const string CpuPackageTemperatureSensorId = "/amdcpu/0/temperature/2";
const string GpuTemperatureSensorId = "/gpu-amd/0/temperature/4";
const string Dimm0TemperatureSensorId = "/memory/dimm/0/temperature/0";
const string Dimm1TemperatureSensorId = "/memory/dimm/1/temperature/0";
string[] monitoredSensorIds =
[
    CpuRpmSensorId,
    SystemRpmSensorId,
    CpuTemperatureSensorId,
    SystemTemperatureSensorId,
    CpuControlSensorId,
    SystemControlSensorId,
    CpuPackageTemperatureSensorId,
    GpuTemperatureSensorId,
    Dimm0TemperatureSensorId,
    Dimm1TemperatureSensorId,
];
HashSet<string> monitoredSensorIdSet = new(
    monitoredSensorIds,
    StringComparer.Ordinal);
JsonSerializerOptions ledgerJsonOptions = new()
{
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
};

try
{
    object reply = command switch
    {
        "info" => CallFanControl("GetProcessInfo", "ProcessInfoRequest"),
        "configs" => CallFanControl(
            "ListAvailableConfigs", "ListAvailableConfigsRequest"),
        "sensors" => CallSensors("GetAllSensors", "GetAllSensorsRequest"),
        "plugin-sensors" => GetPluginSensorSnapshot(),
        "refresh" => CallFanControl("Refresh", "RefreshRequest"),
        "load" when commandArguments.Count == 1 => CallFanControl(
            "LoadConfig", "LoadConfigRequest",
            request => SetProperty(
                request, "Path", Path.GetFullPath(commandArguments[0]))),
        "sample" when commandArguments.Count == 2 &&
            int.TryParse(commandArguments[0], out int seconds) &&
            seconds is >= 1 and <= 86400 => SampleSensors(
                seconds, Path.GetFullPath(commandArguments[1])),
        "guard" => RunGuardCommand(commandArguments, outputPath),
        "inspect" when commandArguments.Count == 1 =>
            InspectAssembly(commandArguments[0]),
        "exit" => CallFanControl("Exit", "ExitRequest"),
        "load" => throw new ArgumentException("load requires a config path."),
        "sample" => throw new ArgumentException(
            "sample requires 1..86400 seconds and a JSONL path."),
        "inspect" => throw new ArgumentException(
            "inspect requires one name pattern."),
        _ => throw new ArgumentException($"Unknown command: {command}"),
    };

    WriteResult(reply.ToString() ?? string.Empty, isError: false);
    return 0;
}
catch (GuardViolationException exception)
{
    WriteResult(exception.SummaryJson, isError: true);
    return 1;
}
catch (TargetInvocationException exception)
{
    WriteResult((exception.InnerException ?? exception).ToString(), isError: true);
    return 1;
}
catch (Exception exception)
{
    WriteResult(exception.ToString(), isError: true);
    return 1;
}

object CallFanControl(
    string methodName,
    string requestTypeName,
    Action<object>? configure = null)
{
    object client = InvokeFactory("GetFanControlClient");
    return InvokeRpc(client, methodName, requestTypeName, configure);
}

object CallSensors(
    string methodName,
    string requestTypeName,
    Action<object>? configure = null)
{
    object client = InvokeFactory("GetSensorClient");
    return InvokeRpc(client, methodName, requestTypeName, configure);
}

object InvokeFactory(string methodName)
{
    Type factoryType = RequireType("FanControl.IPC.IPCFactory");
    MethodInfo method = factoryType.GetMethod(
        methodName,
        BindingFlags.Public | BindingFlags.Static) ??
        throw new MissingMethodException(factoryType.FullName, methodName);
    return method.Invoke(null, null) ??
        throw new InvalidOperationException($"{methodName} returned null.");
}

object InvokeRpc(
    object client,
    string methodName,
    string requestTypeName,
    Action<object>? configure)
{
    Type requestType = RequireType(requestTypeName);
    object request = Activator.CreateInstance(requestType) ??
        throw new InvalidOperationException(
            $"Could not create {requestType.FullName}.");
    configure?.Invoke(request);

    MethodInfo method = GetRpcMethod(client, methodName, requestType);

    return method.Invoke(
        client,
        [request, null, DateTime.UtcNow.AddSeconds(10), CancellationToken.None]) ??
        throw new InvalidOperationException($"{methodName} returned null.");
}

MethodInfo GetRpcMethod(object client, string methodName, Type requestType) =>
    client.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Single(candidate =>
            candidate.Name == methodName &&
            candidate.GetParameters().Length == 4 &&
            candidate.GetParameters()[0].ParameterType == requestType);

(object Client, object Request, MethodInfo Rpc) CreateSensorReader()
{
    object client = InvokeFactory("GetSensorClient");
    Type requestType = RequireType("GetAllSensorsRequest");
    object request = Activator.CreateInstance(requestType) ??
        throw new InvalidOperationException("Could not create sensor request.");
    return (client, request, GetRpcMethod(client, "GetAllSensors", requestType));
}

Dictionary<string, float?> ReadMonitoredSensors(
    (object Client, object Request, MethodInfo Rpc) reader,
    DateTime deadline)
{
    Dictionary<string, float?> values = new(StringComparer.Ordinal);
    object reply = reader.Rpc.Invoke(
        reader.Client,
        [reader.Request, null, deadline, CancellationToken.None]) ??
        throw new InvalidOperationException("GetAllSensors returned null.");
    object sensors = reply.GetType().GetProperty("Sensors")?.GetValue(reply) ??
        throw new MissingMemberException(reply.GetType().FullName, "Sensors");
    foreach (object sensor in (IEnumerable)sensors)
    {
        Type sensorType = sensor.GetType();
        string identifier = (string)(sensorType
            .GetProperty("Identifier")?.GetValue(sensor) ??
            throw new MissingMemberException(sensorType.FullName, "Identifier"));
        if (!monitoredSensorIdSet.Contains(identifier))
        {
            continue;
        }

        PropertyInfo hasValueProperty =
            sensorType.GetProperty("HasValue") ??
            throw new MissingMemberException(sensorType.FullName, "HasValue");
        PropertyInfo valueProperty =
            sensorType.GetProperty("Value") ??
            throw new MissingMemberException(sensorType.FullName, "Value");
        object hasValueObject = hasValueProperty.GetValue(sensor) ??
            throw new InvalidDataException(
                $"Sensor {identifier} returned null HasValue.");
        bool hasValue = Convert.ToBoolean(hasValueObject);
        float? value = hasValue
            ? Convert.ToSingle(valueProperty.GetValue(sensor) ??
                throw new InvalidDataException(
                    $"Sensor {identifier} reported HasValue with null Value."))
            : null;
        if (!values.TryAdd(identifier, value))
        {
            throw new InvalidDataException(
                $"GetAllSensors returned duplicate monitored identifier: " +
                identifier);
        }
    }
    return values;
}

Dictionary<string, float?> EmptyMonitoredSensors() =>
    new(StringComparer.Ordinal);

static string ReportException(Exception exception)
{
    Exception report = exception is TargetInvocationException invocation &&
        invocation.InnerException is not null
            ? invocation.InnerException
            : exception;
    return $"{report.GetType().FullName}: {report.Message}";
}

void WriteDurableJsonLine(StreamWriter writer, FileStream stream, object value)
{
    writer.WriteLine(JsonSerializer.Serialize(value, ledgerJsonOptions));
    writer.Flush();
    stream.Flush(flushToDisk: true);
}

void CreateDurableAbort(string path, object value)
{
    Directory.CreateDirectory(
        Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory());
    using FileStream stream = new(
        path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    using StreamWriter writer = new(
        stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        bufferSize: 4096, leaveOpen: true);
    writer.WriteLine(JsonSerializer.Serialize(value, ledgerJsonOptions));
    writer.Flush();
    stream.Flush(flushToDisk: true);
}

static bool ValidGuardMaximum(double value) =>
    double.IsFinite(value) && value is >= 1 and <= 120;

static bool TryParseExpectedControl(
    string text,
    out ControlExpectation expectation)
{
    if (text.Equals("null", StringComparison.OrdinalIgnoreCase))
    {
        expectation = new(ControlExpectationMode.Disabled, null);
        return true;
    }

    if (text.Equals("active", StringComparison.OrdinalIgnoreCase))
    {
        expectation = new(ControlExpectationMode.Active, null);
        return true;
    }

    if (double.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double parsed) &&
        double.IsFinite(parsed) &&
        parsed is >= 0 and <= 100)
    {
        expectation = new(ControlExpectationMode.Exact, parsed);
        return true;
    }

    expectation = default;
    return false;
}

static string FormatControlExpectationMode(ControlExpectation expectation) =>
    expectation.Mode switch
    {
        ControlExpectationMode.Disabled => "disabled",
        ControlExpectationMode.Exact => "exact",
        ControlExpectationMode.Active => "active",
        _ => throw new ArgumentOutOfRangeException(nameof(expectation)),
    };

static bool SamePath(string first, string second) =>
    string.Equals(
        Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);

string RunGuardCommand(IReadOnlyList<string> arguments, string? summaryPath)
{
    if (arguments.Count is not (5 or 8 or 10) ||
        !int.TryParse(
            arguments[0],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int seconds) ||
        seconds is < 1 or > 86400 ||
        !double.TryParse(
            arguments[3],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double cpuMaximumC) ||
        !ValidGuardMaximum(cpuMaximumC) ||
        !double.TryParse(
            arguments[4],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double systemMaximumC) ||
        !ValidGuardMaximum(systemMaximumC))
    {
        throw new ArgumentException(
            "guard requires 1..86400 seconds, JSONL and new abort paths, " +
            "and invariant-culture CPU/system maxima in 1..120 C.");
    }

    ControlExpectation expectedCpuControl =
        new(ControlExpectationMode.Disabled, null);
    ControlExpectation expectedSystemControl =
        new(ControlExpectationMode.Disabled, null);
    int minimumSystemRpm = 0;
    if (arguments.Count >= 8 &&
        (!TryParseExpectedControl(
            arguments[5], out expectedCpuControl) ||
         !TryParseExpectedControl(
            arguments[6], out expectedSystemControl) ||
         !int.TryParse(
            arguments[7],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out minimumSystemRpm) ||
         minimumSystemRpm is < 0 or > 6500))
    {
        throw new ArgumentException(
            "guard expected controls must be null, active, or " +
            "invariant-culture percentages in 0..100, and minimum system " +
            "RPM must be 0..6500.");
    }

    double? gpuMaximumC = null;
    double? dimmMaximumC = null;
    double parsedGpuMaximumC = 0;
    double parsedDimmMaximumC = 0;
    if (arguments.Count == 10 &&
        (!double.TryParse(
            arguments[8],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out parsedGpuMaximumC) ||
         !ValidGuardMaximum(parsedGpuMaximumC) ||
         !double.TryParse(
            arguments[9],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out parsedDimmMaximumC) ||
         !ValidGuardMaximum(parsedDimmMaximumC)))
    {
        throw new ArgumentException(
            "guard GPU/DIMM maxima must be invariant-culture values in " +
            "1..120 C.");
    }
    else if (arguments.Count == 10)
    {
        gpuMaximumC = parsedGpuMaximumC;
        dimmMaximumC = parsedDimmMaximumC;
    }

    return GuardSensors(
        seconds,
        Path.GetFullPath(arguments[1]),
        Path.GetFullPath(arguments[2]),
        cpuMaximumC,
        systemMaximumC,
        expectedCpuControl,
        expectedSystemControl,
        minimumSystemRpm,
        gpuMaximumC,
        dimmMaximumC,
        summaryPath);
}

string SampleSensors(int seconds, string samplePath)
{
    var reader = CreateSensorReader();
    Directory.CreateDirectory(
        Path.GetDirectoryName(samplePath) ?? Directory.GetCurrentDirectory());
    using FileStream stream = new(
        samplePath, FileMode.Create, FileAccess.Write, FileShare.Read);
    using StreamWriter writer = new(
        stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        bufferSize: 4096, leaveOpen: true);

    Stopwatch campaign = Stopwatch.StartNew();
    long sequence = 0;
    long nextScheduledMilliseconds = 0;
    long errorCount = 0;
    long successfulSamples = 0;
    while (campaign.Elapsed < TimeSpan.FromSeconds(seconds))
    {
        Stopwatch call = Stopwatch.StartNew();
        Dictionary<string, float?> sampleValues;
        string? error = null;
        try
        {
            sampleValues = ReadMonitoredSensors(
                reader, DateTime.UtcNow.AddSeconds(10));
        }
        catch (Exception exception)
        {
            sampleValues = EmptyMonitoredSensors();
            error = ReportException(exception);
        }
        if (error is null)
        {
            successfulSamples++;
        }
        else
        {
            errorCount++;
        }
        call.Stop();

        WriteDurableJsonLine(writer, stream, new
        {
            Sequence = sequence,
            Utc = DateTimeOffset.UtcNow,
            MonotonicMilliseconds = campaign.Elapsed.TotalMilliseconds,
            RpcMilliseconds = call.Elapsed.TotalMilliseconds,
            Values = sampleValues,
            Error = error,
        });

        sequence++;
        nextScheduledMilliseconds += 1000;
        if (nextScheduledMilliseconds <= campaign.ElapsedMilliseconds)
        {
            nextScheduledMilliseconds =
                (campaign.ElapsedMilliseconds / 1000 + 1) * 1000;
        }
        long delay = nextScheduledMilliseconds - campaign.ElapsedMilliseconds;
        if (delay > 0)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(delay));
        }
    }

    if (successfulSamples == 0)
    {
        throw new IOException(
            $"All {errorCount} sensor samples failed; ledger: {samplePath}");
    }

    return JsonSerializer.Serialize(new
    {
        Status = errorCount == 0 ? "OK" : "DEGRADED",
        Samples = sequence,
        SuccessfulSamples = successfulSamples,
        Errors = errorCount,
        DurationSeconds = campaign.Elapsed.TotalSeconds,
        Path = samplePath,
    });
}

string GuardSensors(
    int seconds,
    string samplePath,
    string abortPath,
    double cpuMaximumC,
    double systemMaximumC,
    ControlExpectation expectedCpuControl,
    ControlExpectation expectedSystemControl,
    int minimumSystemRpm,
    double? gpuMaximumC,
    double? dimmMaximumC,
    string? summaryPath)
{
    if (SamePath(samplePath, abortPath) ||
        (summaryPath is not null &&
            (SamePath(summaryPath, samplePath) || SamePath(summaryPath, abortPath))))
    {
        throw new ArgumentException(
            "Guard JSONL, abort, and summary paths must be distinct.");
    }
    if (File.Exists(abortPath) || Directory.Exists(abortPath))
    {
        throw new IOException($"Guard abort path already exists: {abortPath}");
    }
    if (File.Exists(samplePath) || Directory.Exists(samplePath))
    {
        throw new IOException($"Guard JSONL path already exists: {samplePath}");
    }

    Directory.CreateDirectory(
        Path.GetDirectoryName(samplePath) ?? Directory.GetCurrentDirectory());
    string abortDirectory =
        Path.GetDirectoryName(abortPath) ?? Directory.GetCurrentDirectory();
    Directory.CreateDirectory(abortDirectory);
    if (File.Exists(abortPath) || Directory.Exists(abortPath))
    {
        throw new IOException($"Guard abort path appeared during setup: {abortPath}");
    }

    var reader = CreateSensorReader();
    using FileStream stream = new(
        samplePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    using StreamWriter writer = new(
        stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        bufferSize: 4096, leaveOpen: true);

    Stopwatch campaign = Stopwatch.StartNew();
    long sequence = 0;
    long successfulRpcSamples = 0;
    long nextScheduledMilliseconds = 0;
    double? previousSampleStartMilliseconds = null;
    while (campaign.Elapsed < TimeSpan.FromSeconds(seconds))
    {
        double sampleStartMilliseconds = campaign.Elapsed.TotalMilliseconds;
        double? samplingGapMilliseconds = previousSampleStartMilliseconds.HasValue
            ? sampleStartMilliseconds - previousSampleStartMilliseconds.Value
            : null;
        previousSampleStartMilliseconds = sampleStartMilliseconds;
        List<string> violations = [];
        Dictionary<string, float?> values = EmptyMonitoredSensors();
        string? error = null;
        double? rpcMilliseconds = null;

        if (samplingGapMilliseconds is > 3000.0)
        {
            violations.Add(
                $"Sampling gap was {samplingGapMilliseconds.Value:F3} ms.");
        }
        else
        {
            Stopwatch call = Stopwatch.StartNew();
            try
            {
                values = ReadMonitoredSensors(
                    reader, DateTime.UtcNow.AddSeconds(3));
                successfulRpcSamples++;
            }
            catch (Exception exception)
            {
                error = ReportException(exception);
                violations.Add($"Sensor RPC failed: {error}");
            }
            call.Stop();
            rpcMilliseconds = call.Elapsed.TotalMilliseconds;
            if (rpcMilliseconds > 3000.0)
            {
                violations.Add($"Sensor RPC took {rpcMilliseconds:F3} ms.");
            }

            if (error is null)
            {
                ValidateControl(
                    values,
                    CpuControlSensorId,
                    "CPU",
                    expectedCpuControl,
                    violations);
                ValidateControl(
                    values,
                    SystemControlSensorId,
                    "system",
                    expectedSystemControl,
                    violations);

                double sampleEndMilliseconds = campaign.Elapsed.TotalMilliseconds;
                bool startupGraceActive = sampleEndMilliseconds < 5000.0;
                List<string> telemetryIds =
                [
                    CpuRpmSensorId,
                    SystemRpmSensorId,
                    CpuTemperatureSensorId,
                    SystemTemperatureSensorId,
                ];
                if (gpuMaximumC.HasValue && dimmMaximumC.HasValue)
                {
                    telemetryIds.Add(CpuPackageTemperatureSensorId);
                    telemetryIds.Add(GpuTemperatureSensorId);
                    telemetryIds.Add(Dimm0TemperatureSensorId);
                    telemetryIds.Add(Dimm1TemperatureSensorId);
                }
                if (!startupGraceActive)
                {
                    string[] missing = telemetryIds
                        .Where(identifier =>
                            !values.TryGetValue(identifier, out float? value) ||
                            !value.HasValue)
                        .ToArray();
                    if (missing.Length != 0)
                    {
                        violations.Add(
                            "Plugin telemetry is missing after startup grace: " +
                            string.Join(", ", missing));
                    }
                }

                values.TryGetValue(CpuRpmSensorId, out float? cpuRpm);
                values.TryGetValue(SystemRpmSensorId, out float? systemRpm);
                ValidateRpm(cpuRpm, "CPU", violations);
                ValidateRpm(systemRpm, "system", violations);
                if (minimumSystemRpm > 0 &&
                    (!values.ContainsKey(SystemRpmSensorId) ||
                     !systemRpm.HasValue ||
                     !float.IsFinite(systemRpm.Value) ||
                     systemRpm.Value < minimumSystemRpm))
                {
                    violations.Add(
                        $"system fan RPM {systemRpm?.ToString() ?? "missing/null"} " +
                        $"was below required {minimumSystemRpm}.");
                }
                values.TryGetValue(
                    CpuTemperatureSensorId, out float? cpuTemperature);
                values.TryGetValue(
                    SystemTemperatureSensorId, out float? systemTemperature);
                ValidateTemperature(
                    cpuTemperature,
                    "CPU",
                    cpuMaximumC,
                    violations);
                ValidateTemperature(
                    systemTemperature,
                    "system",
                    systemMaximumC,
                    violations);
                if (gpuMaximumC.HasValue && dimmMaximumC.HasValue)
                {
                    values.TryGetValue(
                        CpuPackageTemperatureSensorId,
                        out float? cpuPackageTemperature);
                    values.TryGetValue(
                        GpuTemperatureSensorId,
                        out float? gpuTemperature);
                    values.TryGetValue(
                        Dimm0TemperatureSensorId,
                        out float? dimm0Temperature);
                    values.TryGetValue(
                        Dimm1TemperatureSensorId,
                        out float? dimm1Temperature);
                    ValidateTemperature(
                        cpuPackageTemperature,
                        "CPU package",
                        cpuMaximumC,
                        violations);
                    ValidateTemperature(
                        gpuTemperature,
                        "GPU VR SoC",
                        gpuMaximumC.Value,
                        violations);
                    ValidateTemperature(
                        dimm0Temperature,
                        "DIMM 0",
                        dimmMaximumC.Value,
                        violations);
                    ValidateTemperature(
                        dimm1Temperature,
                        "DIMM 1",
                        dimmMaximumC.Value,
                        violations);
                }
            }
        }

        double recordMilliseconds = campaign.Elapsed.TotalMilliseconds;
        bool graceActive = recordMilliseconds < 5000.0;
        string[] violationSnapshot = violations.ToArray();
        object record = new
        {
            Sequence = sequence,
            Utc = DateTimeOffset.UtcNow,
            MonotonicMilliseconds = recordMilliseconds,
            SampleStartMilliseconds = sampleStartMilliseconds,
            SamplingGapMilliseconds = samplingGapMilliseconds,
            RpcMilliseconds = rpcMilliseconds,
            StartupGraceActive = graceActive,
            Values = values,
            Error = error,
            Violations = violationSnapshot,
        };
        WriteDurableJsonLine(writer, stream, record);

        if (violationSnapshot.Length != 0)
        {
            object abort = new
            {
                Status = "ABORT",
                Utc = DateTimeOffset.UtcNow,
                Sequence = sequence,
                MonotonicMilliseconds = recordMilliseconds,
                Violations = violationSnapshot,
                LedgerPath = samplePath,
            };
            CreateDurableAbort(abortPath, abort);
            string summary = JsonSerializer.Serialize(new
            {
                Status = "ABORT",
                Samples = sequence + 1,
                SuccessfulRpcSamples = successfulRpcSamples,
                DurationSeconds = campaign.Elapsed.TotalSeconds,
                CpuMaximumC = cpuMaximumC,
                SystemMaximumC = systemMaximumC,
                ExpectedCpuControlMode =
                    FormatControlExpectationMode(expectedCpuControl),
                ExpectedCpuControlPercent = expectedCpuControl.Percent,
                ExpectedSystemControlMode =
                    FormatControlExpectationMode(expectedSystemControl),
                ExpectedSystemControlPercent = expectedSystemControl.Percent,
                MinimumSystemRpm = minimumSystemRpm,
                GpuMaximumC = gpuMaximumC,
                DimmMaximumC = dimmMaximumC,
                Violations = violationSnapshot,
                Path = samplePath,
                AbortPath = abortPath,
            });
            throw new GuardViolationException(summary);
        }

        sequence++;
        nextScheduledMilliseconds += 1000;
        if (nextScheduledMilliseconds <= campaign.ElapsedMilliseconds)
        {
            nextScheduledMilliseconds =
                (campaign.ElapsedMilliseconds / 1000 + 1) * 1000;
        }
        long delay = nextScheduledMilliseconds - campaign.ElapsedMilliseconds;
        if (delay > 0)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(delay));
        }
    }

    return JsonSerializer.Serialize(new
    {
        Status = "OK",
        Samples = sequence,
        SuccessfulRpcSamples = successfulRpcSamples,
        DurationSeconds = campaign.Elapsed.TotalSeconds,
        CpuMaximumC = cpuMaximumC,
        SystemMaximumC = systemMaximumC,
        ExpectedCpuControlMode =
            FormatControlExpectationMode(expectedCpuControl),
        ExpectedCpuControlPercent = expectedCpuControl.Percent,
        ExpectedSystemControlMode =
            FormatControlExpectationMode(expectedSystemControl),
        ExpectedSystemControlPercent = expectedSystemControl.Percent,
        MinimumSystemRpm = minimumSystemRpm,
        GpuMaximumC = gpuMaximumC,
        DimmMaximumC = dimmMaximumC,
        Path = samplePath,
        AbortPath = abortPath,
        AbortCreated = false,
    });
}

static void ValidateRpm(
    float? value,
    string name,
    ICollection<string> violations)
{
    if (value.HasValue &&
        (!float.IsFinite(value.Value) || value.Value is < 0 or > 6500))
    {
        violations.Add($"{name} fan RPM is invalid: {value.Value}.");
    }
}

static void ValidateControl(
    IReadOnlyDictionary<string, float?> values,
    string identifier,
    string name,
    ControlExpectation expected,
    ICollection<string> violations)
{
    const double tolerancePercent = 0.1;
    if (!values.TryGetValue(identifier, out float? actual))
    {
        violations.Add($"{name} control sensor was missing.");
        return;
    }
    if (actual.HasValue &&
        (!float.IsFinite(actual.Value) || actual.Value is < 0 or > 100))
    {
        violations.Add(
            $"{name} control value was invalid: " +
            actual.Value.ToString(CultureInfo.InvariantCulture));
        return;
    }
    if (expected.Mode == ControlExpectationMode.Disabled)
    {
        if (actual.HasValue)
        {
            violations.Add(
                $"{name} control was active at {actual.Value} percent; " +
                "expected disabled.");
        }
        return;
    }

    if (expected.Mode == ControlExpectationMode.Active)
    {
        if (!actual.HasValue)
        {
            violations.Add($"{name} control was disabled; expected active.");
        }
        return;
    }

    if (!actual.HasValue ||
        !float.IsFinite(actual.Value) ||
        !expected.Percent.HasValue ||
        Math.Abs(actual.Value - expected.Percent.Value) > tolerancePercent)
    {
        violations.Add(
            $"{name} control was " +
            $"{(actual.HasValue ? actual.Value.ToString(CultureInfo.InvariantCulture) : "null")} " +
            $"percent; expected " +
            $"{expected.Percent?.ToString(CultureInfo.InvariantCulture) ?? "invalid"} " +
            $"+/- {tolerancePercent}.");
    }
}

static void ValidateTemperature(
    float? value,
    string name,
    double configuredMaximum,
    ICollection<string> violations)
{
    if (!value.HasValue)
    {
        return;
    }
    if (!float.IsFinite(value.Value) || value.Value is < 1 or > 120)
    {
        violations.Add($"{name} temperature is implausible: {value.Value} C.");
    }
    else if (value.Value > configuredMaximum)
    {
        violations.Add(
            $"{name} temperature {value.Value} C exceeded " +
            $"{configuredMaximum} C.");
    }
}

string GetPluginSensorSnapshot()
{
    object reply = CallSensors("GetAllSensors", "GetAllSensorsRequest");
    object sensors = reply.GetType().GetProperty("Sensors")?.GetValue(reply) ??
        throw new MissingMemberException(reply.GetType().FullName, "Sensors");
    List<object> result = [];
    foreach (object sensor in (IEnumerable)sensors)
    {
        Type sensorType = sensor.GetType();
        string origin = (string)(sensorType.GetProperty("Origin")?
            .GetValue(sensor) ?? string.Empty);
        if (origin != "Minisforum UM780 XTX (F7BSD)")
        {
            continue;
        }

        bool hasValue = Convert.ToBoolean(
            sensorType.GetProperty("HasValue")?.GetValue(sensor));
        result.Add(new
        {
            Identifier = sensorType.GetProperty("Identifier")?.GetValue(sensor),
            Type = sensorType.GetProperty("Type")?.GetValue(sensor)?.ToString(),
            Name = sensorType.GetProperty("Name")?.GetValue(sensor),
            Value = hasValue
                ? Convert.ToSingle(
                    sensorType.GetProperty("Value")?.GetValue(sensor))
                : (float?)null,
        });
    }

    return JsonSerializer.Serialize(new
    {
        Utc = DateTimeOffset.UtcNow,
        Sensors = result,
    });
}

string InspectAssembly(string pattern)
{
    StringBuilder result = new();
    BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    foreach (Type type in assembly.GetTypes().OrderBy(type => type.FullName))
    {
        MemberInfo[] matchingMembers = type.GetMembers(flags)
            .Where(member => member.Name.Contains(
                pattern, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (!(type.FullName?.Contains(
                pattern, StringComparison.OrdinalIgnoreCase) ?? false) &&
            matchingMembers.Length == 0)
        {
            continue;
        }

        result.AppendLine($"TYPE {type.FullName}");
        foreach (ConstructorInfo constructor in type.GetConstructors(flags))
        {
            result.AppendLine($"  CTOR {FormatParameters(constructor)}");
        }
        foreach (MemberInfo member in matchingMembers)
        {
            switch (member)
            {
                case MethodInfo method when !method.IsSpecialName:
                    result.AppendLine(
                        $"  METHOD {method.ReturnType.FullName} {method.Name}" +
                        $"{FormatParameters(method)}");
                    break;
                case PropertyInfo property:
                    result.AppendLine(
                        $"  PROPERTY {property.PropertyType.FullName} " +
                        property.Name);
                    break;
                case FieldInfo field:
                    result.AppendLine(
                        $"  FIELD {field.FieldType.FullName} {field.Name}");
                    break;
                case EventInfo eventInfo:
                    result.AppendLine(
                        $"  EVENT {eventInfo.EventHandlerType?.FullName} " +
                        eventInfo.Name);
                    break;
            }
        }
    }

    return result.Length == 0
        ? $"No matching types or members for '{pattern}'."
        : result.ToString();
}

static string FormatParameters(MethodBase method) =>
    "(" + string.Join(", ", method.GetParameters().Select(parameter =>
        $"{parameter.ParameterType.FullName} {parameter.Name}")) + ")";

Type RequireType(string name) => assembly.GetType(name) ??
    throw new TypeLoadException($"Could not load {name}.");

static void SetProperty(object instance, string name, object value)
{
    PropertyInfo property = instance.GetType().GetProperty(name) ??
        throw new MissingMemberException(instance.GetType().FullName, name);
    property.SetValue(instance, value);
}

void WriteResult(string value, bool isError)
{
    if (outputPath is not null)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
        File.WriteAllText(outputPath, value + Environment.NewLine);
        return;
    }

    if (isError)
    {
        Console.Error.WriteLine(value);
    }
    else
    {
        Console.WriteLine(value);
    }
}

sealed class GuardViolationException(string summaryJson) : Exception
{
    internal string SummaryJson { get; } = summaryJson;
}

enum ControlExpectationMode
{
    Disabled,
    Exact,
    Active,
}

readonly record struct ControlExpectation(
    ControlExpectationMode Mode,
    double? Percent);
