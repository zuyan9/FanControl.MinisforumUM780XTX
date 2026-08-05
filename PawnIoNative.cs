using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace FanControl.MinisforumUM780XTX;

internal readonly record struct EcWrite(ushort Address, byte Value);

internal readonly record struct EcExpectation(ushort Address, byte Value);

internal sealed class EcWritePreconditionException(
    ushort address,
    byte expected,
    byte actual) : IOException(
        $"EC write precondition failed at 0x{address:X4}: expected " +
        $"0x{expected:X2}, read 0x{actual:X2}.")
{
    internal ushort Address { get; } = address;

    internal byte Expected { get; } = expected;

    internal byte Actual { get; } = actual;
}

internal interface IF7bsdTransport : IDisposable
{
    byte[] ReadPnpIdentity();

    byte[] Read(ushort[] addresses);

    void Write(EcWrite[] writes);

    byte[] WriteGuarded(
        EcExpectation[] before,
        EcWrite[] writes,
        EcExpectation[] after,
        ushort[] resultAddresses);
}

internal interface IPawnIoExecutor : IDisposable
{
    ulong[] Execute(string name, ulong[] input, int outputCount);
}

internal interface IIsaMutex : IDisposable
{
    bool WaitOne(TimeSpan timeout);

    void ReleaseMutex();
}

internal sealed class NamedIsaMutex : IIsaMutex
{
    private readonly Mutex mutex = new(false, F7bsdProfile.IsaMutexName);

    public bool WaitOne(TimeSpan timeout) => mutex.WaitOne(timeout);

    public void ReleaseMutex() => mutex.ReleaseMutex();

    public void Dispose() => mutex.Dispose();
}

internal sealed class PawnIoTransport : IF7bsdTransport
{
    private enum TransportState
    {
        Healthy,
        Poisoned,
        Disposed,
    }

    private static readonly TimeSpan IsaTimeout = TimeSpan.FromSeconds(1);
    private readonly IIsaMutex isaMutex;
    private readonly IPawnIoExecutor native;
    private TransportState state;
    private Exception? poisonCause;

    internal PawnIoTransport()
    {
        NamedIsaMutex mutex = new();
        PawnIoNative? candidate = null;
        try
        {
            VerifyPawnIoDriver();
            string pawnIoPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PawnIO",
                "PawnIOLib.dll");
            candidate = new PawnIoNative(pawnIoPath);
            candidate.OpenAndLoad(LoadLpcModule());
            isaMutex = mutex;
            native = candidate;
        }
        catch
        {
            candidate?.Dispose();
            mutex.Dispose();
            throw;
        }
    }

    internal PawnIoTransport(IPawnIoExecutor native, IIsaMutex isaMutex)
    {
        this.native = native ?? throw new ArgumentNullException(nameof(native));
        this.isaMutex = isaMutex ?? throw new ArgumentNullException(nameof(isaMutex));
    }

    public byte[] ReadPnpIdentity() => RunIsa(() =>
    {
        SelectSlot();
        return ReadPnpIdentityUnlocked();
    });

    public byte[] Read(ushort[] addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        F7bsdProfile.AssertReadsAllowed(addresses);
        return RunIsa(() =>
        {
            SelectSlot();
            return addresses.Select(ReadByte).ToArray();
        });
    }

    public void Write(EcWrite[] writes)
    {
        WriteGuarded([], writes, [], []);
    }

    public byte[] WriteGuarded(
        EcExpectation[] before,
        EcWrite[] writes,
        EcExpectation[] after,
        ushort[] resultAddresses)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(writes);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(resultAddresses);
        F7bsdProfile.AssertWritesAllowed(writes);
        F7bsdProfile.AssertReadsAllowed(before.Select(item => item.Address));
        F7bsdProfile.AssertReadsAllowed(after.Select(item => item.Address));
        F7bsdProfile.AssertReadsAllowed(resultAddresses);
        return RunIsa(() =>
        {
            SelectSlot();
            AssertPnpIdentity();
            AssertControllerProfile();
            foreach (EcExpectation expectation in before)
            {
                AssertExpectation(expectation, "precondition");
            }
            foreach (EcWrite write in writes)
            {
                WriteByte(write.Address, write.Value);
                Verify(write, "immediate");
            }
            foreach (EcExpectation expectation in after)
            {
                AssertExpectation(expectation, "postcondition");
            }
            return resultAddresses.Select(ReadByte).ToArray();
        });
    }

    public void Dispose()
    {
        if (state == TransportState.Disposed)
        {
            return;
        }
        state = TransportState.Disposed;

        List<Exception> failures = [];
        try
        {
            native.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        try
        {
            isaMutex.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        if (failures.Count != 0)
        {
            throw new AggregateException("PawnIO transport disposal failed.", failures);
        }
    }

    private T RunIsa<T>(Func<T> action)
    {
        EnsureHealthy();

        bool held = false;
        T result = default!;
        Exception? failure = null;
        try
        {
            try
            {
                held = isaMutex.WaitOne(IsaTimeout);
            }
            catch (AbandonedMutexException exception)
            {
                held = true;
                Poison(exception);
                failure = AmbiguousStateException(
                    "The ISA mutex was abandoned.",
                    exception);
            }

            if (failure is null && !held)
            {
                throw new TimeoutException("Timed out acquiring the ISA mutex.");
            }
            if (failure is null)
            {
                result = action();
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (held)
            {
                try
                {
                    isaMutex.ReleaseMutex();
                }
                catch (Exception releaseFailure)
                {
                    Poison(releaseFailure);
                    failure = Combine(
                        failure,
                        AmbiguousStateException(
                            "Releasing the ISA mutex failed.",
                            releaseFailure));
                }
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return result;
    }

    private void AssertControllerProfile()
    {
        byte[] actual = F7bsdProfile.ControllerProfileAddresses
            .Select(ReadByte)
            .ToArray();
        if (!actual.SequenceEqual(F7bsdProfile.ExpectedControllerProfile))
        {
            throw new PlatformNotSupportedException(
                "The live controller is not the UM780 XTX F7BSD profile.");
        }
    }

    private void AssertPnpIdentity()
    {
        if (!ReadPnpIdentityUnlocked().SequenceEqual(F7bsdProfile.ExpectedPnpIdentity))
        {
            throw new PlatformNotSupportedException(
                "The physical Super-I/O identity is not the UM780 XTX IT5571 profile.");
        }
    }

    private byte[] ReadPnpIdentityUnlocked() => Enumerable.Range(0x20, 3)
        .Select(ReadPnpRegister)
        .ToArray();

    private byte ReadPnpRegister(int register) => RunParked(
        () => checked((byte)Execute(
            "ioctl_superio_inb",
            [(ulong)register],
            1)[0]),
        ParkPnp,
        $"PNP register 0x{register:X2}");

    private void Verify(EcWrite write, string phase)
    {
        byte actual = ReadByte(write.Address);
        if (actual != write.Value)
        {
            throw new IOException(
                $"EC {phase} verification failed at 0x{write.Address:X4}: " +
                $"wrote 0x{write.Value:X2}, read 0x{actual:X2}.");
        }
    }

    private void AssertExpectation(EcExpectation expectation, string phase)
    {
        byte actual = ReadByte(expectation.Address);
        if (actual != expectation.Value)
        {
            if (phase == "precondition")
            {
                throw new EcWritePreconditionException(
                    expectation.Address,
                    expectation.Value,
                    actual);
            }
            throw new IOException(
                $"EC write {phase} failed at 0x{expectation.Address:X4}: " +
                $"expected 0x{expectation.Value:X2}, read 0x{actual:X2}.");
        }
    }

    private void SelectSlot() => Execute("ioctl_select_slot", [0], 0);

    private byte ReadByte(ushort address) => RunParked(() =>
        {
            SetAddress(address);
            Out(0x2e, 0x12);
            return checked((byte)In(0x2f));
        },
        Park,
        $"EC read 0x{address:X4}");

    private void WriteByte(ushort address, byte value) => RunParked(() =>
        {
            SetAddress(address);
            Out(0x2e, 0x12);
            Out(0x2f, value);
            return 0;
        },
        Park,
        $"EC write 0x{address:X4}");

    private void SetAddress(ushort address)
    {
        Out(0x2e, 0x11);
        Out(0x2f, (ulong)(address >> 8));
        Out(0x2e, 0x10);
        Out(0x2f, (ulong)(address & 0xff));
    }

    private void Park()
    {
        Exception? failure = null;
        try
        {
            Out(0x2e, 0x10);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        try
        {
            ParkPnp();
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private void ParkPnp() => Execute("ioctl_pio_outb", [0x2e, 0x20], 0);

    private ulong In(ulong port) =>
        Execute("ioctl_superio_inb", [port], 1)[0];

    private void Out(ulong port, ulong value) =>
        Execute("ioctl_superio_outb", [port, value], 0);

    private ulong[] Execute(string name, ulong[] input, int outputCount)
    {
        try
        {
            return native.Execute(name, input, outputCount);
        }
        catch (Exception exception)
        {
            Poison(exception);
            throw;
        }
    }

    private T RunParked<T>(Func<T> body, Action park, string operation)
    {
        T result = default!;
        Exception? failure = null;
        try
        {
            result = body();
        }
        catch (Exception exception)
        {
            Poison(exception);
            failure = exception;
        }
        try
        {
            park();
        }
        catch (Exception exception)
        {
            Poison(exception);
            failure = Combine(failure, exception);
        }
        if (failure is not null)
        {
            throw AmbiguousStateException(
                $"{operation} did not complete with verified parking.",
                failure);
        }
        return result;
    }

    private void EnsureHealthy()
    {
        if (state == TransportState.Disposed)
        {
            throw new ObjectDisposedException(nameof(PawnIoTransport));
        }
        if (state == TransportState.Poisoned)
        {
            throw AmbiguousStateException(
                "PawnIO transport was poisoned by an earlier native or parking failure.",
                poisonCause);
        }
    }

    private void Poison(Exception exception)
    {
        if (state == TransportState.Healthy)
        {
            state = TransportState.Poisoned;
            poisonCause = exception;
        }
    }

    private static InvalidOperationException AmbiguousStateException(
        string detail,
        Exception? inner) => new(
            detail + " EC selector state is ambiguous; restart Windows before " +
            "accessing the controller again.",
            inner);

    private static Exception Combine(Exception? first, Exception second) =>
        first is null
            ? second
            : new AggregateException(first, second);

    private static byte[] LoadLpcModule()
    {
        string directory = File.Exists(
            Path.Combine(AppContext.BaseDirectory, "LibreHardwareMonitorLib.dll"))
                ? AppContext.BaseDirectory
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "FanControl");
        string path = Path.Combine(directory, "LibreHardwareMonitorLib.dll");
        Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(item => item.GetName().Name == "LibreHardwareMonitorLib") ??
            Assembly.LoadFrom(path);
        VerifySha256(
            assembly.Location,
            F7bsdProfile.LibreHardwareMonitorSha256,
            "LibreHardwareMonitor assembly");
        using Stream stream = assembly.GetManifestResourceStream(
            F7bsdProfile.LpcResourceName) ?? throw new InvalidOperationException(
                $"PawnIO resource was not found: {F7bsdProfile.LpcResourceName}");
        byte[] module = new byte[checked((int)stream.Length)];
        stream.ReadExactly(module);
        string moduleHash = Convert.ToHexString(SHA256.HashData(module));
        if (!string.Equals(
            moduleHash,
            F7bsdProfile.LpcModuleSha256,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The embedded PawnIO LPC module does not match the reviewed build.");
        }
        return module;
    }

    private static void VerifySha256(string path, string expected, string description)
    {
        using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The {description} does not match the reviewed build.");
        }
    }

    private static void VerifyPawnIoDriver()
    {
        const string serviceKey =
            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\PawnIO";
        string imagePath = Convert.ToString(
            Registry.GetValue(serviceKey, "ImagePath", null))?.Trim().Trim('"') ??
            throw new InvalidOperationException("The PawnIO driver service is not installed.");
        const string systemRootPrefix = @"\SystemRoot\";
        string resolved = imagePath.StartsWith(
            systemRootPrefix,
            StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    imagePath[systemRootPrefix.Length..])
                : imagePath.StartsWith(@"\??\", StringComparison.Ordinal)
                    ? imagePath[4..]
                    : imagePath;
        VerifySha256(
            resolved,
            F7bsdProfile.PawnIoDriverSha256,
            "PawnIO driver");
    }
}

internal sealed class PawnIoNative : IPawnIoExecutor
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VersionDelegate(out uint version);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenDelegate(out IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LoadDelegate(IntPtr handle, [In] byte[] blob, nuint size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int ExecuteDelegate(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        [In] ulong[] input,
        nuint inputCount,
        [Out] ulong[] output,
        nuint outputCount,
        out nuint returnedCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CloseDelegate(IntPtr handle);

    private IntPtr library;
    private IntPtr handle;
    private readonly VersionDelegate version;
    private readonly OpenDelegate open;
    private readonly LoadDelegate load;
    private readonly ExecuteDelegate execute;
    private readonly CloseDelegate close;

    internal PawnIoNative(string libraryPath)
    {
        VerifyLibrary(libraryPath);
        library = NativeLibrary.Load(libraryPath);
        try
        {
            version = Export<VersionDelegate>("pawnio_version");
            open = Export<OpenDelegate>("pawnio_open");
            load = Export<LoadDelegate>("pawnio_load");
            execute = Export<ExecuteDelegate>("pawnio_execute");
            close = Export<CloseDelegate>("pawnio_close");
            Check(version(out uint apiVersion), "pawnio_version");
            if (apiVersion != F7bsdProfile.PawnIoApiVersion)
            {
                throw new InvalidOperationException(
                    $"PawnIO API 0x{apiVersion:X8} is not the reviewed " +
                    $"0x{F7bsdProfile.PawnIoApiVersion:X8} build.");
            }
        }
        catch
        {
            NativeLibrary.Free(library);
            library = IntPtr.Zero;
            throw;
        }
    }

    internal void OpenAndLoad(byte[] module)
    {
        Check(open(out handle), "pawnio_open");
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("pawnio_open returned a null handle.");
        }
        try
        {
            Check(load(handle, module, (nuint)module.Length), "pawnio_load");
        }
        catch
        {
            close(handle);
            handle = IntPtr.Zero;
            throw;
        }
    }

    public ulong[] Execute(string name, ulong[] input, int outputCount)
    {
        ulong[] output = new ulong[outputCount];
        Check(
            execute(
                handle,
                name,
                input,
                (nuint)input.Length,
                output,
                (nuint)output.Length,
                out nuint returned),
            name);
        if (returned != (nuint)outputCount)
        {
            throw new InvalidOperationException(
                $"PawnIO {name} returned {returned} values; expected {outputCount}.");
        }
        return output;
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            close(handle);
            handle = IntPtr.Zero;
        }
        if (library != IntPtr.Zero)
        {
            NativeLibrary.Free(library);
            library = IntPtr.Zero;
        }
    }

    private T Export<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static void VerifyLibrary(string path)
    {
        using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(
            actual,
            F7bsdProfile.PawnIoLibrarySha256,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "PawnIOLib.dll does not match the reviewed 2.2.0 build.");
        }
    }

    private static void Check(int result, string operation)
    {
        if (result != 0)
        {
            throw new ExternalException(
                $"{operation} failed with HRESULT 0x{unchecked((uint)result):X8}.",
                result);
        }
    }
}
