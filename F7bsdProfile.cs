namespace FanControl.MinisforumUM780XTX;

internal enum F7bsdFan
{
    Cpu,
    System,
}

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
    internal const byte MaximumPlausibleTemperatureC = 120;
    internal const byte SystemFailsafeTemperatureC = 70;
    internal const byte SystemSentinel = 0xff;
    internal const ushort CpuTargetAddress = 0x0884;
    internal const ushort CpuEffectiveTemperatureAddress = 0x0888;
    internal const ushort CpuTemperatureOverrideAddress = 0x088a;
    internal const ushort SystemTargetAddress = 0x0885;
    internal const ushort SystemEffectiveTemperatureAddress = 0x0889;
    internal const ushort SystemTemperatureOverrideAddress = 0x088b;
    internal const ushort CpuProfileSelectorAddress = 0x032f;

    internal static readonly byte[] ExpectedPnpIdentity = [0x55, 0x71, 0x02];
    internal static readonly ushort[] ControllerProfileAddresses =
        [0x2000, 0x2001, 0x2002, 0x200d, 0x180c, 0x1841];
    internal static readonly byte[] ExpectedControllerProfile =
        [0x55, 0x71, 0x02, 0x43, 0x14, 0x7f];

    // Stable low/high/low tach reads followed by raw temperatures.
    internal static readonly ushort[] TelemetryAddresses =
    [
        0x181e, 0x181f, 0x181e,
        0x1820, 0x1821, 0x1820,
        0x0309, 0x0305,
    ];
    internal static readonly ushort[] RuntimeTelemetryAddresses =
    [
        .. TelemetryAddresses,
        SystemEffectiveTemperatureAddress,
        SystemTemperatureOverrideAddress,
        SystemTargetAddress,
    ];
    internal static readonly ushort[] CpuTachAddresses = [0x181e, 0x181f, 0x181e];
    internal static readonly ushort[] SystemTachAddresses = [0x1820, 0x1821, 0x1820];

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
    internal static readonly ushort[] CpuConfigurationAddresses =
    [
        CpuProfileSelectorAddress,
        .. CpuBaseAddresses.SelectMany(address => new ushort[]
        {
            (ushort)(address + 1),
            (ushort)(address + 2),
        }),
        .. CpuCriticalAddresses,
    ];
    internal static readonly ushort[] CpuSafetyStateAddresses =
    [
        0x0309,
        CpuEffectiveTemperatureAddress,
        CpuTemperatureOverrideAddress,
        CpuTargetAddress,
    ];
    internal static readonly ushort[] CpuControlSnapshotAddresses =
    [
        CpuProfileSelectorAddress,
        CpuTemperatureOverrideAddress,
        0x0309,
        CpuEffectiveTemperatureAddress,
        .. CpuConfigurationAddresses,
        .. CpuRestoreAddresses,
        CpuTemperatureOverrideAddress,
        CpuProfileSelectorAddress,
    ];
    internal static readonly ushort[] CpuRuntimeSnapshotAddresses =
    [
        .. CpuConfigurationAddresses,
        .. CpuSafetyStateAddresses,
        .. CpuRestoreAddresses,
    ];
    internal static readonly ushort[] SystemThresholdAddresses =
        [0x0331, 0x0334, 0x0337];
    internal static readonly ushort[] SystemOwnershipAddresses =
    [
        0x0305,
        SystemEffectiveTemperatureAddress,
        SystemTemperatureOverrideAddress,
        SystemTargetAddress,
    ];
    internal static readonly ushort[] StartupStateAddresses =
    [
        .. CpuSafetyStateAddresses,
        .. SystemOwnershipAddresses,
    ];
    private static readonly byte[] ExpectedCpuCriticalBytes = [51, 100, 93, 0];
    private static readonly byte[] ExpectedStandardBands =
    [
        25, 0,
        45, 25,
        54, 45,
        66, 54,
        76, 66,
        88, 76,
        93, 88,
    ];
    private static readonly byte[] ExpectedPerformanceBands =
    [
        25, 0,
        45, 25,
        54, 45,
        66, 54,
        80, 66,
        88, 80,
        93, 88,
    ];
    private static readonly byte[] ExpectedDefaultCpuBaseline =
    [
        0, 16, 18, 21, 28, 34, 36,
        0, 10, 33, 58, 60, 16, 200,
    ];
    private static readonly byte[] ExpectedBalancedCpuBaseline =
    [
        0, 16, 18, 21, 28, 32, 33,
        0, 10, 33, 58, 60, 16, 200,
    ];
    private static readonly byte[] ExpectedPerformanceCpuBaseline =
    [
        0, 18, 21, 28, 36, 42, 46,
        0, 15, 77, 66, 40, 50, 100,
    ];
    private static readonly byte[] ExpectedSystemThresholds = [25, 83, 100];
    private static readonly HashSet<ushort> ReadAllowlist = BuildReadAllowlist();
    private static readonly HashSet<ushort> CpuBaseAllowlist = [.. CpuBaseAddresses];
    private static readonly HashSet<ushort> CpuSlopeAllowlist = [.. CpuSlopeAddresses];

    internal static byte ToCode(float percentage)
    {
        if (!float.IsFinite(percentage) || percentage < 0 || percentage > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(percentage),
                "Fan Control output must be a finite value from 0 through 100.");
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

    internal static void ValidateCpuCriticalRow(ReadOnlySpan<byte> values)
    {
        if (values.Length != CpuCriticalAddresses.Length)
        {
            throw new ArgumentException(
                "Unexpected CPU critical-row length.",
                nameof(values));
        }
        if (!values.SequenceEqual(ExpectedCpuCriticalBytes))
        {
            throw new PlatformNotSupportedException(
                "The CPU critical row is not (51,100,93,0).");
        }
    }

    internal static byte ValidateCpuConfiguration(ReadOnlySpan<byte> values)
    {
        if (values.Length != CpuConfigurationAddresses.Length)
        {
            throw new ArgumentException(
                "Unexpected CPU configuration length.",
                nameof(values));
        }

        byte selector = values[0];
        ReadOnlySpan<byte> expectedBands = selector switch
        {
            0 or 0xb1 => ExpectedStandardBands,
            0xb2 => ExpectedPerformanceBands,
            _ => throw new PlatformNotSupportedException(
                $"Unknown CPU profile selector 0x{selector:X2}."),
        };
        if (!values[1..15].SequenceEqual(expectedBands))
        {
            throw new PlatformNotSupportedException(
                "The CPU temperature bands do not match the selected F7BSD profile.");
        }
        ValidateCpuCriticalRow(values[15..19]);
        return selector;
    }

    internal static void ValidateCpuBaseline(byte selector, ReadOnlySpan<byte> values)
    {
        if (values.Length != CpuRestoreAddresses.Length)
        {
            throw new ArgumentException("Unexpected CPU baseline length.", nameof(values));
        }

        ReadOnlySpan<byte> expected = selector switch
        {
            0 => ExpectedDefaultCpuBaseline,
            0xb1 => ExpectedBalancedCpuBaseline,
            0xb2 => ExpectedPerformanceCpuBaseline,
            _ => throw new PlatformNotSupportedException(
                $"Unknown CPU profile selector 0x{selector:X2}."),
        };
        if (!values.SequenceEqual(expected))
        {
            throw new PlatformNotSupportedException(
                "The CPU table is not a known firmware baseline. Restart Windows " +
                "before loading the plugin after any uncontrolled termination.");
        }
    }

    internal static void ValidateSystemThresholds(ReadOnlySpan<byte> values)
    {
        if (values.Length != SystemThresholdAddresses.Length)
        {
            throw new ArgumentException(
                "Unexpected system-threshold length.",
                nameof(values));
        }
        if (!values.SequenceEqual(ExpectedSystemThresholds))
        {
            throw new PlatformNotSupportedException(
                "The system thresholds are not the stock (25,83,100) policy.");
        }
    }

    internal static void ValidateStartupState(ReadOnlySpan<byte> values)
    {
        if (values.Length != StartupStateAddresses.Length)
        {
            throw new ArgumentException("Unexpected startup-state length.", nameof(values));
        }

        byte cpuRaw = values[0];
        byte cpuEffective = values[1];
        byte cpuOverride = values[2];
        byte cpuTarget = values[3];
        byte systemRaw = values[4];
        byte systemEffective = values[5];
        byte systemOverride = values[6];
        byte systemTarget = values[7];

        if (cpuOverride != 0 || systemOverride != 0)
        {
            throw new PlatformNotSupportedException(
                "A firmware temperature override is active. Restart Windows before " +
                "loading the plugin.");
        }
        if (!PlausibleTemperature(cpuRaw) || !PlausibleTemperature(cpuEffective) ||
            !PlausibleTemperature(systemRaw) || !PlausibleTemperature(systemEffective))
        {
            throw new PlatformNotSupportedException(
                "The EC temperature path is not reporting plausible live values.");
        }
        if (cpuTarget > MaximumCode || systemTarget > MaximumCode)
        {
            throw new PlatformNotSupportedException(
                "The EC reported a fan target outside code 0..51.");
        }
    }

    internal static bool PlausibleTemperature(byte value) =>
        value is >= 1 and <= MaximumPlausibleTemperatureC;

    internal static byte[] ValidateCpuControlSnapshot(ReadOnlySpan<byte> values)
    {
        if (values.Length != CpuControlSnapshotAddresses.Length)
        {
            throw new ArgumentException(
                "Unexpected CPU control-snapshot length.",
                nameof(values));
        }

        int configurationOffset = 4;
        int mutableOffset = configurationOffset + CpuConfigurationAddresses.Length;
        int endingOverrideOffset = mutableOffset + CpuRestoreAddresses.Length;
        int endingSelectorOffset = endingOverrideOffset + 1;
        byte startingSelector = values[0];
        byte startingOverride = values[1];
        byte rawTemperature = values[2];
        byte effectiveTemperature = values[3];
        byte selector = ValidateCpuConfiguration(values.Slice(
            configurationOffset,
            CpuConfigurationAddresses.Length));
        ReadOnlySpan<byte> mutable = values.Slice(
            mutableOffset,
            CpuRestoreAddresses.Length);

        if (startingSelector != F7bsdCpuPolicy.Selector ||
            selector != F7bsdCpuPolicy.Selector ||
            values[endingSelectorOffset] != F7bsdCpuPolicy.Selector)
        {
            throw new PlatformNotSupportedException(
                "CPU control currently supports only the exact B1 firmware profile.");
        }
        if (startingOverride != 0 || values[endingOverrideOffset] != 0)
        {
            throw new PlatformNotSupportedException(
                "The CPU firmware-temperature override is active.");
        }
        if (!PlausibleTemperature(rawTemperature) ||
            !PlausibleTemperature(effectiveTemperature))
        {
            throw new PlatformNotSupportedException(
                "The CPU temperature path is not reporting plausible live values.");
        }
        ValidateCpuBaseline(selector, mutable);
        return mutable.ToArray();
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
                (CpuBaseAllowlist.Contains(write.Address) &&
                    write.Value <= MaximumCode) ||
                CpuSlopeAllowlist.Contains(write.Address) ||
                (write.Address == SystemTargetAddress && write.Value <= MaximumCode) ||
                (write.Address == SystemTemperatureOverrideAddress &&
                    write.Value is 0 or SystemSentinel);
            if (!allowed)
            {
                throw new InvalidOperationException(
                    $"EC write 0x{write.Address:X4}=0x{write.Value:X2} is not allowed.");
            }
        }
    }

    private static HashSet<ushort> BuildReadAllowlist() =>
    [
        .. ControllerProfileAddresses,
        .. TelemetryAddresses,
        .. RuntimeTelemetryAddresses,
        .. CpuTachAddresses,
        .. SystemTachAddresses,
        .. CpuRestoreAddresses,
        .. CpuCriticalAddresses,
        .. CpuConfigurationAddresses,
        .. CpuSafetyStateAddresses,
        .. CpuControlSnapshotAddresses,
        .. CpuRuntimeSnapshotAddresses,
        .. SystemThresholdAddresses,
        .. SystemOwnershipAddresses,
        .. StartupStateAddresses,
    ];
}
