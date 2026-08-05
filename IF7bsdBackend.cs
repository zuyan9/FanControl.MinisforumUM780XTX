namespace FanControl.MinisforumUM780XTX;

internal interface IF7bsdBackend : IDisposable
{
    void Initialize();

    F7bsdTelemetry ReadTelemetry();

    byte Set(F7bsdFan fan, byte requestedCode);

    void Reset(F7bsdFan fan);
}
