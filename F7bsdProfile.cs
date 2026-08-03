namespace FanControl.MinisforumUM780XTX;

internal enum F7bsdFan
{
    Cpu,
    System,
}

internal enum SystemFanMode
{
    Off,
    Quiet,
    Full,
}

internal readonly record struct CpuBand(byte Upper, byte Lower);

internal readonly record struct CpuEncoding(byte Base, byte Slope);

internal sealed record CpuConfiguration(byte Selector, CpuBand[] Bands);

internal static class F7bsdProfile
{
    internal const string Product = "Venus series";
    internal const string Board = "F7BSD";
    internal const string BoardVersion = "1.1";
    internal const string BiosVersion = "1.06";
    internal const int EmbeddedControllerMajorVersion = 0;
    internal const int EmbeddedControllerMinorVersion = 8;
    internal const string IsaMutexName = "Global\\Access_ISABUS.HTP.Method";
    internal const string LpcResourceName =
        "LibreHardwareMonitor.Resources.PawnIo.LpcIO.bin";
    internal const string PawnIoLibrarySha256 =
        "D71F62627D66983BB9F5B1C269F27BCD8C1B8A46E794377A6330F84C198F4443";
    internal const string PawnIoDriverSha256 =
        "FCA6E7D58B0CF38DBB913A2B9E532F48629145D395F454B16A9F58E97B8D3940";
    internal const string LibreHardwareMonitorSha256 =
        "A849E062BB9681B6E5407E0593FAED27B78F5D8B6A6E91F28D27BC386D7A3BA2";
    internal const string LpcModuleSha256 =
        "3DCF8B2BC80FF642D97C4608511A818642B5BF315FF53DF3DF393D043E71D101";
    internal const uint PawnIoApiVersion = 0x00020000;
    internal const byte MaximumCode = 51;
    internal const byte CpuTransitionMaximumCode = 52;
    internal const byte CpuCriticalTemperatureC = 94;

    internal static readonly byte[] ExpectedPnpIdentity = [0x55, 0x71, 0x02];
    internal static readonly ushort[] ControllerProfileAddresses =
        [0x2000, 0x2001, 0x2002, 0x200d, 0x180c, 0x1841];
    internal static readonly byte[] ExpectedControllerProfile =
        [0x55, 0x71, 0x02, 0x43, 0x14, 0x7f];

    // Stable low/high/low tach reads, followed by raw temperatures, targets,
    // effective temperatures, and override state.
    internal static readonly ushort[] TelemetryAddresses =
    [
        0x181e, 0x181f, 0x181e,
        0x1820, 0x1821, 0x1820,
        0x0309, 0x0305,
        0x0884, 0x0885,
        0x0888, 0x0889,
        0x088a, 0x088b,
    ];

    internal static readonly ushort[] CpuBaseAddresses =
        Enumerable.Range(0, 7)
            .Select(row => (ushort)(0x0310 + (row * 3)))
            .ToArray();
    internal static readonly ushort[] CpuSlopeAddresses =
        Enumerable.Range(0x08b0, 7)
            .Select(address => (ushort)address)
            .ToArray();
    internal static readonly ushort[] CpuRestoreAddresses =
        [.. CpuBaseAddresses, .. CpuSlopeAddresses];
    internal static readonly ushort[] CpuCriticalAddresses =
        [0x0325, 0x0326, 0x0327, 0x08b7];
    internal static readonly ushort[] SystemThresholdAddresses =
        [0x0331, 0x0334, 0x0337];
    internal static readonly ushort[] SystemStateAddresses =
        [0x0305, 0x0889, 0x088a, 0x088b];
    internal static readonly ushort[] CpuStateAddresses =
        [0x0309, 0x0888, 0x088a, 0x088b];

    internal static readonly ushort[] CpuConfigurationAddresses =
    [
        0x032f,
        .. CpuBaseAddresses.SelectMany(address => new ushort[]
        {
            (ushort)(address + 1),
            (ushort)(address + 2),
        }),
        .. CpuCriticalAddresses,
    ];

    private static readonly CpuBand[] StandardBands =
    [
        new(25, 0),
        new(45, 25),
        new(54, 45),
        new(66, 54),
        new(76, 66),
        new(88, 76),
        new(93, 88),
    ];

    private static readonly CpuBand[] PerformanceBands =
    [
        new(25, 0),
        new(45, 25),
        new(54, 45),
        new(66, 54),
        new(80, 66),
        new(88, 80),
        new(93, 88),
    ];

    private static readonly byte[] DefaultCpuBaseline =
    [
        0, 16, 18, 21, 28, 34, 36,
        0, 10, 33, 58, 60, 16, 200,
    ];

    private static readonly byte[] BalancedCpuBaseline =
    [
        0, 16, 18, 21, 28, 32, 33,
        0, 10, 33, 58, 60, 16, 200,
    ];

    private static readonly byte[] PerformanceCpuBaseline =
    [
        0, 18, 21, 28, 36, 42, 46,
        0, 15, 77, 66, 40, 50, 100,
    ];

    private static readonly byte[] StockSystemThresholds = [25, 83, 100];
    private static readonly HashSet<ushort> ReadAllowlist = BuildReadAllowlist();
    private static readonly HashSet<ushort> CpuBaseAllowlist = [.. CpuBaseAddresses];
    private static readonly HashSet<ushort> CpuSlopeAllowlist = [.. CpuSlopeAddresses];

    internal static byte ToCode(float percentage)
    {
        if (!float.IsFinite(percentage) || percentage < 0 || percentage > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(percentage),
                "FanControl output must be a finite value from 0 through 100.");
        }

        return (byte)Math.Round(
            percentage * MaximumCode / 100d,
            MidpointRounding.AwayFromZero);
    }

    internal static float ToPercentage(byte code)
    {
        if (code > MaximumCode)
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }
        return code * 100f / MaximumCode;
    }

    internal static CpuConfiguration ValidateCpuConfiguration(ReadOnlySpan<byte> values)
    {
        if (values.Length != CpuConfigurationAddresses.Length)
        {
            throw new ArgumentException(
                "Unexpected CPU configuration length.",
                nameof(values));
        }

        byte selector = values[0];
        CpuBand[] expectedBands = selector switch
        {
            0 or 0xb1 => StandardBands,
            0xb2 => PerformanceBands,
            _ => throw new PlatformNotSupportedException(
                $"Unknown CPU profile selector 0x{selector:X2}."),
        };

        CpuBand[] actualBands = new CpuBand[7];
        for (int row = 0; row < actualBands.Length; row++)
        {
            actualBands[row] = new CpuBand(
                values[1 + (row * 2)],
                values[2 + (row * 2)]);
        }
        if (!actualBands.SequenceEqual(expectedBands))
        {
            throw new PlatformNotSupportedException(
                "The live CPU temperature bands do not match the selected F7BSD profile.");
        }

        ReadOnlySpan<byte> critical = values[15..19];
        if (!critical.SequenceEqual(new byte[] { 51, 100, 93, 0 }))
        {
            throw new PlatformNotSupportedException(
                "The CPU critical row is not (51,100,93,0).");
        }

        return new CpuConfiguration(selector, actualBands);
    }

    internal static void ValidateCpuBaseline(byte selector, ReadOnlySpan<byte> values)
    {
        if (values.Length != CpuRestoreAddresses.Length)
        {
            throw new ArgumentException("Unexpected CPU baseline length.", nameof(values));
        }

        ReadOnlySpan<byte> expected = selector switch
        {
            0 => DefaultCpuBaseline,
            0xb1 => BalancedCpuBaseline,
            0xb2 => PerformanceCpuBaseline,
            _ => throw new PlatformNotSupportedException(
                $"Unknown CPU profile selector 0x{selector:X2}."),
        };
        if (!values.SequenceEqual(expected))
        {
            throw new PlatformNotSupportedException(
                "The live CPU table is not the selected firmware baseline. " +
                "Restart Windows if FanControl was previously terminated forcibly.");
        }
    }

    internal static void ValidateSystemBaseline(ReadOnlySpan<byte> values)
    {
        if (!values.SequenceEqual(StockSystemThresholds))
        {
            throw new PlatformNotSupportedException(
                "The system thresholds are not the stock (25,83,100) policy. " +
                "Restart Windows if FanControl was previously terminated forcibly.");
        }
    }

    internal static void ValidateStartupTelemetry(F7bsdTelemetry telemetry)
    {
        if (telemetry.CpuTemperatureOverride != 0 ||
            telemetry.SystemTemperatureOverride != 0)
        {
            throw new PlatformNotSupportedException(
                "A firmware temperature override is active. Restart Windows before loading the plugin.");
        }
        if (telemetry.CpuTemperatureC is < 1 or > 120 ||
            telemetry.SystemTemperatureC is < 1 or > 120 ||
            telemetry.CpuEffectiveTemperatureC != telemetry.CpuTemperatureC ||
            telemetry.SystemEffectiveTemperatureC != telemetry.SystemTemperatureC)
        {
            throw new PlatformNotSupportedException(
                "The EC temperatures are not plausible live sensor values.");
        }
        if (telemetry.CpuTargetCode > MaximumCode ||
            telemetry.SystemTargetCode > MaximumCode)
        {
            throw new PlatformNotSupportedException(
                "The EC reported a fan target outside 0..5100 RPM.");
        }
    }

    internal static EcWrite[] CpuWrites(byte requestedCode, CpuBand[] bands)
    {
        CpuEncoding[] encodings = CompileCpuCurve(requestedCode, bands);
        return CpuBaseAddresses
            .Select((address, row) => new EcWrite(address, encodings[row].Base))
            .Concat(CpuSlopeAddresses.Select(
                (address, row) => new EcWrite(address, encodings[row].Slope)))
            .ToArray();
    }

    internal static byte[] CpuBytes(IReadOnlyList<CpuEncoding> encodings)
    {
        if (encodings.Count != 7)
        {
            throw new ArgumentException(
                "Exactly seven CPU encodings are required.",
                nameof(encodings));
        }
        return
        [
            .. encodings.Select(encoding => encoding.Base),
            .. encodings.Select(encoding => encoding.Slope),
        ];
    }

    internal static CpuEncoding[] DecodeCpuBytes(ReadOnlySpan<byte> values)
    {
        if (values.Length != CpuRestoreAddresses.Length)
        {
            throw new ArgumentException("Unexpected CPU byte count.", nameof(values));
        }
        CpuEncoding[] encodings = new CpuEncoding[7];
        for (int row = 0; row < encodings.Length; row++)
        {
            encodings[row] = new CpuEncoding(values[row], values[7 + row]);
        }
        return encodings;
    }

    internal static EcWrite[] CpuTransitionWrites(
        ReadOnlySpan<byte> currentValues,
        ReadOnlySpan<byte> targetValues,
        IReadOnlyList<CpuBand> bands)
    {
        if (bands.Count != 7)
        {
            throw new ArgumentException(
                "Exactly seven CPU bands are required.",
                nameof(bands));
        }
        CpuEncoding[] current = DecodeCpuBytes(currentValues);
        CpuEncoding[] target = DecodeCpuBytes(targetValues);
        List<EcWrite> writes = [];
        for (int row = 0; row < 7; row++)
        {
            writes.AddRange(RowTransitionWrites(
                current[row],
                target[row],
                bands[row],
                CpuBaseAddresses[row],
                CpuSlopeAddresses[row]));
        }
        return writes.ToArray();
    }

    internal static EcExpectation[] CpuTransitionExpectations(
        ReadOnlySpan<byte> currentValues,
        CpuConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (currentValues.Length != CpuRestoreAddresses.Length)
        {
            throw new ArgumentException(
                "Unexpected CPU byte count.",
                nameof(currentValues));
        }
        if (configuration.Bands.Length != 7)
        {
            throw new ArgumentException(
                "Exactly seven CPU bands are required.",
                nameof(configuration));
        }

        List<EcExpectation> expectations =
        [
            new(0x032f, configuration.Selector),
        ];
        for (int row = 0; row < configuration.Bands.Length; row++)
        {
            ushort baseAddress = CpuBaseAddresses[row];
            expectations.Add(new EcExpectation(
                (ushort)(baseAddress + 1),
                configuration.Bands[row].Upper));
            expectations.Add(new EcExpectation(
                (ushort)(baseAddress + 2),
                configuration.Bands[row].Lower));
        }
        expectations.AddRange(new[]
        {
            new EcExpectation(CpuCriticalAddresses[0], MaximumCode),
            new EcExpectation(CpuCriticalAddresses[1], 100),
            new EcExpectation(CpuCriticalAddresses[2], 93),
            new EcExpectation(CpuCriticalAddresses[3], 0),
        });
        for (int index = 0; index < CpuRestoreAddresses.Length; index++)
        {
            expectations.Add(new EcExpectation(
                CpuRestoreAddresses[index],
                currentValues[index]));
        }
        expectations.Add(new EcExpectation(0x088a, 0));
        expectations.Add(new EcExpectation(0x088b, 0));
        return expectations.ToArray();
    }

    internal static SystemFanMode SystemMode(byte requestedCode)
    {
        if (requestedCode > MaximumCode)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedCode));
        }
        return requestedCode switch
        {
            < 10 => SystemFanMode.Off,
            < 36 => SystemFanMode.Quiet,
            _ => SystemFanMode.Full,
        };
    }

    internal static byte SystemModeCode(SystemFanMode mode) => mode switch
    {
        SystemFanMode.Off => 0,
        SystemFanMode.Quiet => 20,
        SystemFanMode.Full => 51,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal static EcWrite[] SystemWrites(SystemFanMode mode)
    {
        byte[] thresholds = mode switch
        {
            SystemFanMode.Off => [70, 70, 100],
            SystemFanMode.Quiet => [0, 70, 100],
            SystemFanMode.Full => [0, 0, 100],
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        return OrderedSystemWrites(thresholds);
    }

    internal static EcWrite[] SystemRestoreWrites(byte[] baseline)
    {
        if (baseline.Length != SystemThresholdAddresses.Length)
        {
            throw new ArgumentException("Unexpected system baseline length.", nameof(baseline));
        }
        return OrderedSystemWrites(baseline);
    }

    internal static CpuEncoding[] CompileCpuCurve(
        byte requestedCode,
        IReadOnlyList<CpuBand> bands)
    {
        if (requestedCode > MaximumCode)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedCode));
        }
        if (bands.Count != 7)
        {
            throw new ArgumentException("Exactly seven normal CPU bands are required.", nameof(bands));
        }

        CpuEncoding[] encodings = bands
            .Select(band => FitBand(requestedCode, band))
            .ToArray();

        foreach (bool cooling in new[] { false, true })
        {
            int previous = -1;
            for (int temperature = 0; temperature < CpuCriticalTemperatureC; temperature++)
            {
                int target = CpuTarget(encodings, bands, temperature, cooling);
                if (target < Math.Max(requestedCode, SafetyCode(temperature)) ||
                    target > MaximumCode || target < previous)
                {
                    throw new InvalidOperationException(
                        "The compiled CPU curve failed its thermal safety validation.");
                }
                previous = target;
            }
        }

        return encodings;
    }

    internal static int CpuTarget(
        IReadOnlyList<CpuEncoding> encodings,
        IReadOnlyList<CpuBand> bands,
        int temperature,
        bool cooling)
    {
        if (temperature >= CpuCriticalTemperatureC)
        {
            return MaximumCode;
        }

        int row;
        if (cooling)
        {
            row = 6;
            while (row > 0 && temperature < bands[row].Lower)
            {
                row--;
            }
        }
        else
        {
            row = 0;
            while (row < 6 && temperature > bands[row].Upper)
            {
                row++;
            }
        }

        CpuEncoding encoding = encodings[row];
        int delta = Math.Max(0, temperature - bands[row].Lower);
        return encoding.Base + ((encoding.Slope * delta) / 100);
    }

    internal static int SafetyCode(int temperature)
    {
        if (temperature <= 74)
        {
            return 10;
        }
        if (temperature <= 82)
        {
            int rpm = 1_000 + ((temperature - 74) * 250);
            return (rpm + 99) / 100;
        }
        if (temperature <= 88)
        {
            int sixthsOfRpm = (3_000 * 6) + ((temperature - 82) * 1_000);
            return (sixthsOfRpm + 599) / 600;
        }

        int highRpm = Math.Min(5_100, 4_000 + ((temperature - 88) * 220));
        return (highRpm + 99) / 100;
    }

    internal static void AssertReadsAllowed(IEnumerable<ushort> addresses)
    {
        foreach (ushort address in addresses)
        {
            if (!ReadAllowlist.Contains(address))
            {
                throw new InvalidOperationException(
                    $"EC read address 0x{address:X4} is not allowed.");
            }
        }
    }

    internal static void AssertWritesAllowed(IEnumerable<EcWrite> writes)
    {
        foreach (EcWrite write in writes)
        {
            bool allowed =
                (CpuBaseAllowlist.Contains(write.Address) && write.Value <= MaximumCode) ||
                CpuSlopeAllowlist.Contains(write.Address) ||
                (write.Address == 0x0331 && write.Value is 0 or 25 or 70) ||
                (write.Address == 0x0334 && write.Value is 0 or 70 or 83) ||
                (write.Address == 0x0337 && write.Value == 100) ||
                (write.Address == 0x0885 && write.Value == MaximumCode);
            if (!allowed)
            {
                throw new InvalidOperationException(
                    $"EC write 0x{write.Address:X4}=0x{write.Value:X2} is not allowed.");
            }
        }
    }

    private static CpuEncoding FitBand(byte requestedCode, CpuBand band)
    {
        (int Score, byte Base, byte Slope)? best = null;
        for (int baseCode = 0; baseCode <= MaximumCode; baseCode++)
        {
            for (int slope = 0; slope <= byte.MaxValue; slope++)
            {
                int score = 0;
                bool valid = true;
                for (int temperature = band.Lower; temperature <= band.Upper; temperature++)
                {
                    int predicted = baseCode + ((slope * (temperature - band.Lower)) / 100);
                    int required = Math.Max(requestedCode, SafetyCode(temperature));
                    if (predicted < required || predicted > MaximumCode)
                    {
                        valid = false;
                        break;
                    }
                    score += predicted - required;
                }
                if (!valid)
                {
                    continue;
                }

                var candidate = (score, (byte)baseCode, (byte)slope);
                if (best is null || candidate.CompareTo(best.Value) < 0)
                {
                    best = candidate;
                }
            }
        }

        if (best is null)
        {
            throw new InvalidOperationException(
                $"No safe CPU encoding exists for band {band.Lower}..{band.Upper} C.");
        }
        return new CpuEncoding(best.Value.Base, best.Value.Slope);
    }

    private static IEnumerable<EcWrite> RowTransitionWrites(
        CpuEncoding start,
        CpuEncoding end,
        CpuBand band,
        ushort baseAddress,
        ushort slopeAddress)
    {
        if (start == end)
        {
            return [];
        }

        int count = (MaximumCode + 1) * (byte.MaxValue + 1);
        bool[] valid = new bool[count];
        for (int baseCode = 0; baseCode <= MaximumCode; baseCode++)
        {
            for (int slope = 0; slope <= byte.MaxValue; slope++)
            {
                valid[EncodingId(baseCode, slope)] = TransitionEncodingIsSafe(
                    new CpuEncoding((byte)baseCode, (byte)slope),
                    start,
                    end,
                    band);
            }
        }

        int startId = EncodingId(start.Base, start.Slope);
        int endId = EncodingId(end.Base, end.Slope);
        if (!valid[startId] || !valid[endId])
        {
            throw new InvalidOperationException(
                "A CPU transition endpoint is outside its safe target range.");
        }

        int[] previous = Enumerable.Repeat(-2, count).ToArray();
        Queue<int> pending = new();
        previous[startId] = -1;
        pending.Enqueue(startId);
        while (pending.Count != 0 && previous[endId] == -2)
        {
            int currentId = pending.Dequeue();
            int currentBase = currentId / 256;
            int currentSlope = currentId % 256;
            for (int baseCode = 0; baseCode <= MaximumCode; baseCode++)
            {
                Visit(EncodingId(baseCode, currentSlope), currentId);
            }
            for (int slope = 0; slope <= byte.MaxValue; slope++)
            {
                Visit(EncodingId(currentBase, slope), currentId);
            }
        }

        if (previous[endId] == -2)
        {
            throw new InvalidOperationException(
                $"No safe bytewise CPU transition exists for band {band.Lower}..{band.Upper} C.");
        }

        Stack<int> path = new();
        for (int node = endId; node != startId; node = previous[node])
        {
            path.Push(node);
        }

        List<EcWrite> writes = [];
        CpuEncoding prior = start;
        while (path.TryPop(out int node))
        {
            CpuEncoding next = new((byte)(node / 256), (byte)(node % 256));
            if (next.Base != prior.Base)
            {
                writes.Add(new EcWrite(baseAddress, next.Base));
            }
            else if (next.Slope != prior.Slope)
            {
                writes.Add(new EcWrite(slopeAddress, next.Slope));
            }
            else
            {
                throw new InvalidOperationException("CPU transition contained an empty step.");
            }
            prior = next;
        }
        return writes;

        void Visit(int candidate, int predecessor)
        {
            if (valid[candidate] && previous[candidate] == -2)
            {
                previous[candidate] = predecessor;
                pending.Enqueue(candidate);
            }
        }
    }

    private static bool TransitionEncodingIsSafe(
        CpuEncoding candidate,
        CpuEncoding start,
        CpuEncoding end,
        CpuBand band)
    {
        for (int temperature = band.Lower; temperature <= band.Upper; temperature++)
        {
            int delta = temperature - band.Lower;
            int candidateTarget = EncodingTarget(candidate, delta);
            int lowerBound = Math.Min(
                EncodingTarget(start, delta),
                EncodingTarget(end, delta));
            if (candidateTarget < lowerBound ||
                candidateTarget > CpuTransitionMaximumCode)
            {
                return false;
            }
        }
        return true;
    }

    private static int EncodingTarget(CpuEncoding encoding, int delta) =>
        encoding.Base + ((encoding.Slope * delta) / 100);

    private static int EncodingId(int baseCode, int slope) => (baseCode * 256) + slope;

    private static EcWrite[] OrderedSystemWrites(IReadOnlyList<byte> thresholds) =>
    [
        new EcWrite(0x0334, thresholds[1]),
        new EcWrite(0x0337, thresholds[2]),
        new EcWrite(0x0331, thresholds[0]),
    ];

    private static HashSet<ushort> BuildReadAllowlist() =>
    [
        .. ControllerProfileAddresses,
        .. TelemetryAddresses,
        .. CpuRestoreAddresses,
        .. CpuConfigurationAddresses,
        .. SystemThresholdAddresses,
        .. SystemStateAddresses,
        .. CpuStateAddresses,
        0x0885,
    ];
}
