using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

if (args.Length < 2 || !File.Exists(args[0]))
{
    Console.Error.WriteLine(
        "Usage: FanControlIpc <FanControl.IPC.dll> " +
        "<info|configs|sensors|plugin-sensors|refresh|load|exit> [config-path] " +
        "or sample <seconds> <jsonl-path> " +
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

string SampleSensors(int seconds, string samplePath)
{
    string[] sensorIds =
    [
        "Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.fan1",
        "Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.fan2",
        "Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.cpu-temperature",
        "Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.system-temperature",
        "Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.cpu-cool-stop-v4",
        "Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.system-raw-v2",
    ];

    object client = InvokeFactory("GetSensorClient");
    Type requestType = RequireType("GetAllSensorsRequest");
    object request = Activator.CreateInstance(requestType) ??
        throw new InvalidOperationException("Could not create sensor request.");
    MethodInfo rpc = GetRpcMethod(client, "GetAllSensors", requestType);
    Directory.CreateDirectory(
        Path.GetDirectoryName(samplePath) ?? Directory.GetCurrentDirectory());
    using FileStream stream = new(
        samplePath, FileMode.Create, FileAccess.Write, FileShare.Read);
    using StreamWriter writer = new(stream) { AutoFlush = true };

    Stopwatch campaign = Stopwatch.StartNew();
    long sequence = 0;
    long nextScheduledMilliseconds = 0;
    long errorCount = 0;
    long successfulSamples = 0;
    while (campaign.Elapsed < TimeSpan.FromSeconds(seconds))
    {
        Stopwatch call = Stopwatch.StartNew();
        Dictionary<string, float?> sampleValues = sensorIds.ToDictionary(
            sensorId => sensorId,
            _ => (float?)null,
            StringComparer.Ordinal);
        string? error = null;
        try
        {
            object reply = rpc.Invoke(
                client,
                [request, null, DateTime.UtcNow.AddSeconds(10),
                    CancellationToken.None]) ??
                throw new InvalidOperationException(
                    "GetAllSensors returned null.");
            object sensors = reply.GetType().GetProperty("Sensors")?
                .GetValue(reply) ??
                throw new MissingMemberException(
                    reply.GetType().FullName, "Sensors");
            foreach (object sensor in (IEnumerable)sensors)
            {
                Type sensorType = sensor.GetType();
                string identifier = (string)(sensorType
                    .GetProperty("Identifier")?.GetValue(sensor) ??
                    throw new MissingMemberException(
                        sensorType.FullName, "Identifier"));
                if (!sampleValues.ContainsKey(identifier))
                {
                    continue;
                }

                bool hasValue = Convert.ToBoolean(
                    sensorType.GetProperty("HasValue")?.GetValue(sensor));
                if (hasValue)
                {
                    sampleValues[identifier] = Convert.ToSingle(
                        sensorType.GetProperty("Value")?.GetValue(sensor));
                }
            }
        }
        catch (Exception exception)
        {
            Exception report = exception is TargetInvocationException invocation &&
                invocation.InnerException is not null
                    ? invocation.InnerException
                    : exception;
            error = $"{report.GetType().FullName}: {report.Message}";
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

        writer.WriteLine(JsonSerializer.Serialize(new
        {
            Sequence = sequence,
            Utc = DateTimeOffset.UtcNow,
            MonotonicMilliseconds = campaign.Elapsed.TotalMilliseconds,
            RpcMilliseconds = call.Elapsed.TotalMilliseconds,
            Values = sampleValues,
            Error = error,
        }));

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
