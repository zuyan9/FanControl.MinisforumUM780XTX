namespace FanControl.MinisforumUM780XTX;

internal sealed class DeferredCpuControlException(Exception innerException) :
    IOException("A deferred CPU target transaction failed.", innerException)
{
}

internal interface IF7bsdBackend : IDisposable
{
    void Initialize();

    F7bsdTelemetry ReadTelemetry();

    byte Set(F7bsdFan fan, byte requestedCode);

    void Reset(F7bsdFan fan);
}
