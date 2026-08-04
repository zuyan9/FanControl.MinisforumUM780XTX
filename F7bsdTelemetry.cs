namespace FanControl.MinisforumUM780XTX;

internal sealed record F7bsdTelemetry(
    int CpuFanRpm,
    int SystemFanRpm,
    int CpuTemperatureC,
    int SystemTemperatureC,
    byte? SystemAppliedCode = null,
    byte? CpuAppliedCode = null);

internal static class F7bsdTelemetryDecoder
{
    internal static bool TryDecode(ReadOnlySpan<byte> values, out F7bsdTelemetry? telemetry)
    {
        if (values.Length != F7bsdProfile.TelemetryAddresses.Length)
        {
            throw new ArgumentException(
                "Unexpected F7BSD telemetry length.",
                nameof(values));
        }

        if (!TryDecodeCounter(values[..3], out int cpuRpm) ||
            !TryDecodeCounter(values[3..6], out int systemRpm))
        {
            telemetry = null;
            return false;
        }

        telemetry = new F7bsdTelemetry(
            cpuRpm,
            systemRpm,
            values[6],
            values[7]);
        return true;
    }

    internal static bool TryDecodeCounter(
        ReadOnlySpan<byte> lowHighLow,
        out int rpm)
    {
        if (lowHighLow.Length != 3)
        {
            throw new ArgumentException(
                "A tachometer sample must contain low/high/low bytes.",
                nameof(lowHighLow));
        }
        if (lowHighLow[0] != lowHighLow[2])
        {
            rpm = 0;
            return false;
        }

        ushort counter = (ushort)(lowHighLow[0] | (lowHighLow[1] << 8));
        rpm = CounterToRpm(counter);
        return true;
    }

    private static int CounterToRpm(ushort counter) =>
        counter == 0 ? 0 : 2_156_250 / counter;
}
