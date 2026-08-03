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
    internal const uint PawnIoApiVersion = 0x00020000;
    internal const byte MaximumCode = 51;
    internal const byte SystemSentinel = 0xff;
    internal const ushort SystemTargetAddress = 0x0885;
    internal const ushort SystemEffectiveTemperatureAddress = 0x0889;
    internal const ushort SystemTemperatureOverrideAddress = 0x088b;

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
    internal static readonly ushort[] SystemOwnershipAddresses =
    [
        0x0305,
        SystemEffectiveTemperatureAddress,
        SystemTemperatureOverrideAddress,
        SystemTargetAddress,
    ];
    private static readonly byte[] ExpectedCpuCriticalBytes = [51, 100, 93, 0];
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

    internal static byte[] CpuManualBytes(byte code)
    {
        if (code > MaximumCode)
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }
        return [.. Enumerable.Repeat(code, 7), .. new byte[7]];
    }

    internal static EcWrite[] CpuManualWrites(byte code)
    {
        if (code > MaximumCode)
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }
        return CpuBaseAddresses
            .Select(address => new EcWrite(address, code))
            .Concat(CpuSlopeAddresses.Select(address => new EcWrite(address, 0)))
            .ToArray();
    }

    internal static EcWrite[] CpuRestoreWrites(ReadOnlySpan<byte> baseline)
    {
        if (baseline.Length != CpuRestoreAddresses.Length)
        {
            throw new ArgumentException("Unexpected CPU baseline length.", nameof(baseline));
        }

        List<EcWrite> writes = [];
        for (int row = 0; row < 7; row++)
        {
            writes.Add(new EcWrite(CpuSlopeAddresses[row], baseline[7 + row]));
        }
        for (int row = 0; row < 7; row++)
        {
            writes.Add(new EcWrite(CpuBaseAddresses[row], baseline[row]));
        }
        return writes.ToArray();
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
                CpuBaseAllowlist.Contains(write.Address) ||
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
        .. CpuRestoreAddresses,
        .. CpuCriticalAddresses,
        .. SystemOwnershipAddresses,
    ];
}
