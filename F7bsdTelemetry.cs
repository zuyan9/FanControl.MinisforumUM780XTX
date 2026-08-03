namespace FanControl.MinisforumUM780XTX;

internal sealed record F7bsdTelemetry(
    int CpuFanRpm,
    int SystemFanRpm,
    int CpuTemperatureC,
    int SystemTemperatureC,
    byte CpuTargetCode,
    byte SystemTargetCode,
    byte CpuEffectiveTemperatureC,
    byte SystemEffectiveTemperatureC,
    byte CpuTemperatureOverride,
    byte SystemTemperatureOverride);

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

        if (values[0] != values[2] || values[3] != values[5])
        {
            telemetry = null;
            return false;
        }

        ushort cpuCounter = (ushort)(values[0] | (values[1] << 8));
        ushort systemCounter = (ushort)(values[3] | (values[4] << 8));
        telemetry = new F7bsdTelemetry(
            CounterToRpm(cpuCounter),
            CounterToRpm(systemCounter),
            values[6],
            values[7],
            values[8],
            values[9],
            values[10],
            values[11],
            values[12],
            values[13]);
        return true;
    }

    private static int CounterToRpm(ushort counter) =>
        counter == 0 ? 0 : 2_156_250 / counter;
}
