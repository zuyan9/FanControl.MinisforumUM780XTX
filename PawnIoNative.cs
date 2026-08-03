using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace FanControl.MinisforumUM780XTX;

internal readonly record struct EcWrite(ushort Address, byte Value);

internal readonly record struct EcExpectation(ushort Address, byte Value);

internal interface IF7bsdTransport : IDisposable
{
    byte[] ReadPnpIdentity();

    byte[] Read(ushort[] addresses);

    void Write(EcWrite[] writes, EcExpectation[]? expectations = null);
}

internal sealed class PawnIoTransport : IF7bsdTransport
{
    private readonly Mutex isaMutex = new(false, F7bsdProfile.IsaMutexName);
    private readonly PawnIoNative native;
    private volatile bool abandoned;

    internal PawnIoTransport()
    {
        VerifyPawnIoDriver();
        string pawnIoPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PawnIO",
            "PawnIOLib.dll");
        native = new PawnIoNative(pawnIoPath);
        try
        {
            native.OpenAndLoad(LoadLpcModule());
        }
        catch
        {
            native.Dispose();
            isaMutex.Dispose();
            throw;
        }
    }

    public byte[] ReadPnpIdentity() => RunIsa(() =>
    {
        SelectSlot();
        try
        {
            return ReadPnpIdentityUnlocked();
        }
        finally
        {
            ParkPnp();
        }
    });

    public byte[] Read(ushort[] addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        F7bsdProfile.AssertReadsAllowed(addresses);
        return RunIsa(() =>
        {
            SelectSlot();
            try
            {
                return addresses.Select(ReadByte).ToArray();
            }
            finally
            {
                Park();
            }
        });
    }

    public void Write(EcWrite[] writes, EcExpectation[]? expectations = null)
    {
        ArgumentNullException.ThrowIfNull(writes);
        expectations ??= [];
        F7bsdProfile.AssertWritesAllowed(writes);
        F7bsdProfile.AssertReadsAllowed(expectations.Select(item => item.Address));
        RunIsa(() =>
        {
            SelectSlot();
            try
            {
                AssertPnpIdentity();
                AssertControllerProfile();
                foreach (EcExpectation expectation in expectations)
                {
                    byte actual = ReadByte(expectation.Address);
                    if (actual != expectation.Value)
                    {
                        throw new IOException(
                            $"EC write precondition failed at 0x{expectation.Address:X4}: " +
                            $"expected 0x{expectation.Value:X2}, read 0x{actual:X2}.");
                    }
                }
                foreach (EcWrite write in writes)
                {
                    WriteByte(write.Address, write.Value);
                    Verify(write, "immediate");
                }
                foreach (IGrouping<ushort, EcWrite> group in writes.GroupBy(
                    write => write.Address))
                {
                    Verify(group.Last(), "final aggregate");
                }
            }
            finally
            {
                Park();
            }
            return 0;
        });
    }

    public void Dispose()
    {
        native.Dispose();
        isaMutex.Dispose();
    }

    private T RunIsa<T>(Func<T> action)
    {
        if (abandoned)
        {
            throw new InvalidOperationException(
                "The ISA mutex was abandoned. Restart Windows before accessing the EC again.");
        }

        bool held = false;
        try
        {
            try
            {
                held = isaMutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                held = true;
                abandoned = true;
                throw new InvalidOperationException(
                    "The ISA mutex was abandoned. Restart Windows before accessing the EC again.");
            }

            if (!held)
            {
                throw new TimeoutException("Timed out acquiring the ISA mutex.");
            }
            if (abandoned)
            {
                throw new InvalidOperationException(
                    "The ISA mutex was abandoned. Restart Windows before accessing the EC again.");
            }
            return action();
        }
        finally
        {
            if (held)
            {
                isaMutex.ReleaseMutex();
            }
        }
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
        .Select(register => checked((byte)native.Execute(
            "ioctl_superio_inb",
            [(ulong)register],
            1)[0]))
        .ToArray();

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

    private void SelectSlot() => native.Execute("ioctl_select_slot", [0], 0);

    private byte ReadByte(ushort address)
    {
        SetAddress(address);
        Out(0x2e, 0x12);
        return checked((byte)In(0x2f));
    }

    private void WriteByte(ushort address, byte value)
    {
        SetAddress(address);
        Out(0x2e, 0x12);
        Out(0x2f, value);
    }

    private void SetAddress(ushort address)
    {
        Out(0x2e, 0x11);
        Out(0x2f, (ulong)(address >> 8));
        Out(0x2e, 0x10);
        Out(0x2f, (ulong)(address & 0xff));
    }

    private void Park()
    {
        try
        {
            Out(0x2e, 0x10);
        }
        finally
        {
            ParkPnp();
        }
    }

    private void ParkPnp() => native.Execute("ioctl_pio_outb", [0x2e, 0x20], 0);

    private ulong In(ulong port) =>
        native.Execute("ioctl_superio_inb", [port], 1)[0];

    private void Out(ulong port, ulong value) =>
        native.Execute("ioctl_superio_outb", [port, value], 0);

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
        string resolved = imagePath.StartsWith(systemRootPrefix, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                imagePath[systemRootPrefix.Length..])
            : imagePath.StartsWith(@"\??\", StringComparison.Ordinal)
                ? imagePath[4..]
                : imagePath;
        VerifySha256(resolved, F7bsdProfile.PawnIoDriverSha256, "PawnIO driver");
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
