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

    // Stable low/high/low tachometer reads followed by raw temperatures.
    internal static readonly ushort[] TelemetryAddresses =
    [
        0x181e, 0x181f, 0x181e,
        0x1820, 0x1821, 0x1820,
        0x0309, 0x0305,
    ];
    internal static readonly ushort[] CpuTachAddresses = [0x181e, 0x181f, 0x181e];
    internal static readonly ushort[] SystemTachAddresses = [0x1820, 0x1821, 0x1820];

    internal static readonly ushort[] CpuBaseAddresses = Enumerable.Range(0, 7)
        .Select(row => (ushort)(0x0310 + (row * 3)))
        .ToArray();
    internal static readonly ushort[] CpuSlopeAddresses = Enumerable.Range(0x08b0, 7)
        .Select(address => (ushort)address)
        .ToArray();
    internal static readonly ushort[] CpuOwnedAddresses =
        [.. CpuBaseAddresses, .. CpuSlopeAddresses];

    private static readonly ushort[] CpuCriticalAddresses =
        [0x0325, 0x0326, 0x0327, 0x08b7];
    private static readonly ushort CpuTemperatureOverrideAddress = 0x088a;

    internal static readonly ushort[] CpuSnapshotAddresses =
    [
        CpuTemperatureOverrideAddress,
        .. CpuCriticalAddresses,
        .. CpuOwnedAddresses,
    ];
    internal static readonly ushort[] SystemStateAddresses =
    [
        SystemEffectiveTemperatureAddress,
        SystemTemperatureOverrideAddress,
        SystemTargetAddress,
    ];
    internal static readonly ushort[] SystemEffectiveTemperaturePollAddresses =
        [SystemEffectiveTemperatureAddress];

    private static readonly byte[] ExpectedCpuCriticalRow = [51, 100, 93, 0];
    private static readonly HashSet<ushort> ReadAllowlist =
    [
        .. ControllerProfileAddresses,
        .. TelemetryAddresses,
        .. CpuTachAddresses,
        .. SystemTachAddresses,
        .. CpuSnapshotAddresses,
        .. SystemStateAddresses,
    ];
    private static readonly HashSet<ushort> CpuBases = [.. CpuBaseAddresses];
    private static readonly Dictionary<ushort, int> CpuSlopes = CpuSlopeAddresses
        .Select((address, index) => new KeyValuePair<ushort, int>(address, index))
        .ToDictionary();

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
        AssertCode(code, nameof(code));
        return code * 100f / MaximumCode;
    }

    internal static byte[] CaptureCpuBaseline(ReadOnlySpan<byte> snapshot)
    {
        if (snapshot.Length != CpuSnapshotAddresses.Length)
        {
            throw new ArgumentException("Unexpected CPU snapshot length.", nameof(snapshot));
        }

        int criticalOffset = 1;
        int baselineOffset = criticalOffset + ExpectedCpuCriticalRow.Length;
        if (snapshot[0] != 0)
        {
            throw new PlatformNotSupportedException(
                "The CPU firmware-temperature override is active.");
        }
        if (!snapshot.Slice(criticalOffset, ExpectedCpuCriticalRow.Length)
            .SequenceEqual(ExpectedCpuCriticalRow))
        {
            throw new PlatformNotSupportedException(
                "The CPU critical row is not (51,100,93,0).");
        }

        ReadOnlySpan<byte> baseline = snapshot[baselineOffset..];
        ValidateCapturedCpuBaseline(baseline);
        return baseline.ToArray();
    }

    internal static void ValidateFirmwareSystemState(ReadOnlySpan<byte> state)
    {
        ValidateSystemStateLength(state);
        if (state[1] != 0)
        {
            throw new PlatformNotSupportedException(
                $"System firmware-temperature override is 0x{state[1]:X2}, not zero.");
        }
        if (!PlausibleTemperature(state[0]) || state[2] > MaximumCode)
        {
            throw new PlatformNotSupportedException(
                "The firmware-owned system fan state is not plausible.");
        }
    }

    internal static void ValidateOwnedSystemState(
        ReadOnlySpan<byte> state,
        byte? expectedTarget = null)
    {
        ValidateSystemStateLength(state);
        if (state[0] != SystemSentinel || state[1] != SystemSentinel)
        {
            throw new IOException("System fixed-target ownership is not active.");
        }
        if (state[2] > MaximumCode ||
            (expectedTarget.HasValue && state[2] != expectedTarget.Value))
        {
            throw new IOException("The system fan target did not match its request.");
        }
    }

    internal static bool IsReleasedSystemState(ReadOnlySpan<byte> state) =>
        state.Length == SystemStateAddresses.Length &&
        state[1] == 0 &&
        PlausibleTemperature(state[0]) &&
        state[2] <= MaximumCode;

    internal static bool PlausibleTemperature(byte value) => value is >= 1 and <= 120;

    internal static EcWrite[] CpuTargetWrites(byte code, bool includeSlopes)
    {
        AssertCode(code, nameof(code));
        IEnumerable<EcWrite> writes = CpuBaseAddresses.Select(
            address => new EcWrite(address, code));
        if (includeSlopes)
        {
            writes = CpuSlopeAddresses.Select(address => new EcWrite(address, 0))
                .Concat(writes);
        }
        return writes.ToArray();
    }

    internal static EcWrite[] CpuRestoreWrites(ReadOnlySpan<byte> baseline)
    {
        ValidateCapturedCpuBaseline(baseline);

        byte[] captured = baseline.ToArray();
        return CpuSlopeAddresses.Select(address => new EcWrite(address, 0))
            .Concat(CpuBaseAddresses.Select((address, index) =>
                new EcWrite(address, captured[index])))
            .Concat(CpuSlopeAddresses.Select((address, index) =>
                new EcWrite(address, captured[7 + index])))
            .ToArray();
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

    internal static void AssertCpuWritesAllowed(
        IEnumerable<EcWrite> writes,
        ReadOnlySpan<byte> baseline)
    {
        ValidateCapturedCpuBaseline(baseline);
        foreach (EcWrite write in writes)
        {
            bool allowed =
                (CpuBases.Contains(write.Address) && write.Value <= MaximumCode) ||
                (CpuSlopes.TryGetValue(write.Address, out int index) &&
                    (write.Value == 0 ||
                        write.Value == baseline[CpuBaseAddresses.Length + index]));
            if (!allowed)
            {
                throw new InvalidOperationException(
                    $"EC CPU write 0x{write.Address:X4}=0x{write.Value:X2} is not allowed.");
            }
        }
    }

    private static void AssertCode(byte code, string parameterName)
    {
        if (code > MaximumCode)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateCapturedCpuBaseline(ReadOnlySpan<byte> baseline)
    {
        if (baseline.Length != CpuOwnedAddresses.Length)
        {
            throw new ArgumentException("Unexpected CPU baseline length.", nameof(baseline));
        }
        foreach (byte baseCode in baseline[..CpuBaseAddresses.Length])
        {
            if (baseCode > MaximumCode)
            {
                throw new PlatformNotSupportedException(
                    "A captured CPU base target is outside code 0..51.");
            }
        }
    }

    private static void ValidateSystemStateLength(ReadOnlySpan<byte> state)
    {
        if (state.Length != SystemStateAddresses.Length)
        {
            throw new ArgumentException("Unexpected system state length.", nameof(state));
        }
    }
}
