using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace FanControl.MinisforumUM780XTX;

internal readonly record struct EcWrite(ushort Address, byte Value);

internal readonly record struct EcExpectation(ushort Address, byte Value);

internal sealed class PawnIoTransport : IDisposable
{
    private static readonly TimeSpan IsaTimeout = TimeSpan.FromSeconds(1);
    private readonly Mutex isaMutex;
    private readonly PawnIoNative native;
    private bool poisoned;
    private bool disposed;
    private bool identityVerified;
    private Exception? poisonCause;

    internal PawnIoTransport()
    {
        Mutex mutex = new(false, F7bsdProfile.IsaMutexName);
        PawnIoNative? candidate = null;
        try
        {
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

    internal void AssertIdentity()
    {
        F7bsdProfile.AssertReadsAllowed(F7bsdProfile.ControllerProfileAddresses);
        RunIsa(() =>
        {
            SelectSlot();
            byte[] pnp = Enumerable.Range(0x20, 3)
                .Select(ReadPnpRegister)
                .ToArray();
            if (!pnp.SequenceEqual(F7bsdProfile.ExpectedPnpIdentity))
            {
                throw new PlatformNotSupportedException(
                    "The physical Super-I/O is not the UM780 XTX IT5571 profile.");
            }

            byte[] controller = F7bsdProfile.ControllerProfileAddresses
                .Select(ReadByte)
                .ToArray();
            if (!controller.SequenceEqual(F7bsdProfile.ExpectedControllerProfile))
            {
                throw new PlatformNotSupportedException(
                    "The live controller is not the UM780 XTX F7BSD profile.");
            }
            return 0;
        });
        identityVerified = true;
    }

    internal byte[] Read(ushort[] addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        F7bsdProfile.AssertReadsAllowed(addresses);
        EnsureIdentityVerified();
        return RunIsa(() =>
        {
            SelectSlot();
            return addresses.Select(ReadByte).ToArray();
        });
    }

    internal void WriteVerified(EcWrite[] writes) => WriteVerified([], writes);

    internal void WriteVerified(
        EcExpectation[] before,
        EcWrite[] writes,
        Action? beforeWrites = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(writes);
        F7bsdProfile.AssertReadsAllowed(before.Select(item => item.Address));
        F7bsdProfile.AssertWritesAllowed(writes);
        EnsureIdentityVerified();
        RunIsa(() =>
        {
            SelectSlot();
            foreach (EcExpectation expectation in before)
            {
                byte actual = ReadByte(expectation.Address);
                if (actual != expectation.Value)
                {
                    throw new IOException(
                        $"EC precondition failed at 0x{expectation.Address:X4}: " +
                        $"expected 0x{expectation.Value:X2}, read 0x{actual:X2}.");
                }
            }
            beforeWrites?.Invoke();
            foreach (EcWrite write in writes)
            {
                WriteByte(write.Address, write.Value);
                byte actual = ReadByte(write.Address);
                if (actual != write.Value)
                {
                    throw new IOException(
                        $"EC verification failed at 0x{write.Address:X4}: wrote " +
                        $"0x{write.Value:X2}, read 0x{actual:X2}.");
                }
            }
            return 0;
        });
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        try
        {
            native.Dispose();
        }
        finally
        {
            isaMutex.Dispose();
        }
    }

    private T RunIsa<T>(Func<T> action)
    {
        EnsureUsable();
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
                failure = AmbiguousState(
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
                catch (Exception exception)
                {
                    Poison(exception);
                    failure = Combine(
                        failure,
                        AmbiguousState("Releasing the ISA mutex failed.", exception));
                }
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return result;
    }

    private byte ReadPnpRegister(int register) => RunParked(
        () => checked((byte)Execute(
            "ioctl_superio_inb",
            [(ulong)register],
            1)[0]),
        ParkPnp,
        $"PNP register 0x{register:X2}");

    private byte ReadByte(ushort address) => RunParked(
        () =>
        {
            SetAddress(address);
            Out(0x2e, 0x12);
            return checked((byte)In(0x2f));
        },
        Park,
        $"EC read 0x{address:X4}");

    private void WriteByte(ushort address, byte value) => RunParked(
        () =>
        {
            SetAddress(address);
            Out(0x2e, 0x12);
            Out(0x2f, value);
            return 0;
        },
        Park,
        $"EC write 0x{address:X4}");

    private void SelectSlot() => Execute("ioctl_select_slot", [0], 0);

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

    private ulong In(ulong port) => Execute("ioctl_superio_inb", [port], 1)[0];

    private void Out(ulong port, ulong value) =>
        Execute("ioctl_superio_outb", [port, value], 0);

    private ulong[] Execute(string name, ulong[] input, int outputCount) =>
        native.Execute(name, input, outputCount);

    private T RunParked<T>(Func<T> body, Action park, string operation)
    {
        T result = default!;
        Exception? bodyFailure = null;
        try
        {
            result = body();
        }
        catch (Exception exception)
        {
            bodyFailure = exception;
        }

        try
        {
            park();
        }
        catch (Exception parkFailure)
        {
            Exception combined = Combine(bodyFailure, parkFailure);
            Poison(combined);
            throw AmbiguousState(
                $"{operation} did not complete with verified parking.",
                combined);
        }

        if (bodyFailure is not null)
        {
            ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        }
        return result;
    }

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (poisoned)
        {
            throw AmbiguousState(
                "PawnIO transport was poisoned by an earlier selector failure.",
                poisonCause);
        }
    }

    private void EnsureIdentityVerified()
    {
        if (!identityVerified)
        {
            throw new InvalidOperationException(
                "The UM780 XTX controller identity has not been verified.");
        }
    }

    private void Poison(Exception exception)
    {
        if (!poisoned)
        {
            poisoned = true;
            poisonCause = exception;
        }
    }

    private static InvalidOperationException AmbiguousState(
        string detail,
        Exception? inner) => new(
            detail + " EC selector state is ambiguous; restart Windows before " +
            "accessing the controller again.",
            inner);

    private static Exception Combine(Exception? first, Exception second) =>
        first is null ? second : new AggregateException(first, second);

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
        using Stream stream = assembly.GetManifestResourceStream(
            F7bsdProfile.LpcResourceName) ?? throw new InvalidOperationException(
                $"PawnIO resource was not found: {F7bsdProfile.LpcResourceName}");
        byte[] module = new byte[checked((int)stream.Length)];
        stream.ReadExactly(module);
        return module;
    }
}

internal sealed class PawnIoNative : IDisposable
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
    private readonly OpenDelegate open;
    private readonly LoadDelegate load;
    private readonly ExecuteDelegate execute;
    private readonly CloseDelegate close;

    internal PawnIoNative(string libraryPath)
    {
        library = NativeLibrary.Load(libraryPath);
        try
        {
            VersionDelegate version = Export<VersionDelegate>("pawnio_version");
            open = Export<OpenDelegate>("pawnio_open");
            load = Export<LoadDelegate>("pawnio_load");
            execute = Export<ExecuteDelegate>("pawnio_execute");
            close = Export<CloseDelegate>("pawnio_close");
            Check(version(out uint apiVersion), "pawnio_version");
            if (apiVersion != F7bsdProfile.PawnIoApiVersion)
            {
                throw new InvalidOperationException(
                    $"PawnIO API 0x{apiVersion:X8} is not supported.");
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
        catch (Exception loadFailure)
        {
            try
            {
                Check(close(handle), "pawnio_close");
            }
            catch (Exception closeFailure)
            {
                handle = IntPtr.Zero;
                throw new AggregateException(loadFailure, closeFailure);
            }
            handle = IntPtr.Zero;
            throw;
        }
    }

    internal ulong[] Execute(string name, ulong[] input, int outputCount)
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
        Exception? failure = null;
        if (handle != IntPtr.Zero)
        {
            try
            {
                Check(close(handle), "pawnio_close");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            handle = IntPtr.Zero;
        }
        if (library != IntPtr.Zero)
        {
            try
            {
                NativeLibrary.Free(library);
            }
            catch (Exception exception)
            {
                failure = Combine(failure, exception);
            }
            library = IntPtr.Zero;
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private T Export<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static void Check(int result, string operation)
    {
        if (result != 0)
        {
            throw new ExternalException(
                $"{operation} failed with HRESULT 0x{unchecked((uint)result):X8}.",
                result);
        }
    }

    private static Exception Combine(Exception? first, Exception second) =>
        first is null ? second : new AggregateException(first, second);
}
