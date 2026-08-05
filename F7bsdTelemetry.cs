namespace FanControl.MinisforumUM780XTX;

internal sealed record F7bsdTelemetry(
    int CpuFanRpm,
    int SystemFanRpm,
    int CpuTemperatureC,
    int SystemTemperatureC);

internal static class F7bsdTelemetryDecoder
{
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
        rpm = counter == 0 ? 0 : 2_156_250 / counter;
        return true;
    }
}
