using FanControl.Plugins;

namespace FanControl.MinisforumUM780XTX.Tests;

internal static class Program
{
    private static readonly HostIdentitySnapshot ExactHost = new(
        "Venus series",
        "F7BSD",
        "1.1",
        "1.06",
        0,
        8);

    private static int Main()
    {
        (string Name, Action Body)[] tests =
        [
            ("percentage validation and system quantization", PercentageAndQuantization),
            ("system policy write order", SystemPolicyWriteOrder),
            ("CPU compiler exhaustive default", () => ExhaustiveCpuCompiler(0)),
            ("CPU compiler exhaustive B1", () => ExhaustiveCpuCompiler(0xb1)),
            ("CPU compiler exhaustive B2", () => ExhaustiveCpuCompiler(0xb2)),
            ("CPU bytewise transitions stay safe", CpuTransitionsStaySafe),
            ("CPU critical addresses excluded", CpuCriticalAddressesExcluded),
            ("tach low-high-low decoder", TachLowHighLowDecoder),
            ("exact host identity gate", ExactHostIdentityGate),
            ("initialization gates without writes", InitializationGatesWithoutWrites),
            ("backend CPU lifecycle", BackendCpuLifecycle),
            ("CPU transition snapshot preconditions", CpuTransitionSnapshotPreconditions),
            ("backend CPU failure restoration", BackendCpuFailureRestoration),
            ("active policy reload recovery", ActivePolicyReloadRecovery),
            ("system policies and thermal seeds", SystemPoliciesAndThermalSeeds),
            ("system failure restoration", SystemFailureRestoration),
            ("backend close restoration continuation", BackendCloseRestorationContinuation),
            ("plugin sensor and control lifecycle", PluginSensorAndControlLifecycle),
            ("plugin telemetry error clears stale values", PluginTelemetryErrorClearsStaleValues),
            ("plugin initialization failure cleanup", PluginInitializationFailureCleanup),
        ];

        int failures = 0;
        foreach ((string name, Action body) in tests)
        {
            try
            {
                body();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception}");
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void PercentageAndQuantization()
    {
        Equal((byte)0, F7bsdProfile.ToCode(0f));
        Equal((byte)1, F7bsdProfile.ToCode(1f));
        Equal((byte)13, F7bsdProfile.ToCode(25f));
        Equal((byte)26, F7bsdProfile.ToCode(50f));
        Equal((byte)38, F7bsdProfile.ToCode(75f));
        Equal((byte)51, F7bsdProfile.ToCode(100f));
        Equal(0f, F7bsdProfile.ToPercentage(0));
        Equal(26 * 100f / 51f, F7bsdProfile.ToPercentage(26));
        Equal(100f, F7bsdProfile.ToPercentage(51));

        Throws<ArgumentOutOfRangeException>(() => F7bsdProfile.ToCode(-0.01f));
        Throws<ArgumentOutOfRangeException>(() => F7bsdProfile.ToCode(100.01f));
        Throws<ArgumentOutOfRangeException>(() => F7bsdProfile.ToCode(float.NaN));
        Throws<ArgumentOutOfRangeException>(() => F7bsdProfile.ToCode(float.PositiveInfinity));
        Throws<ArgumentOutOfRangeException>(() => F7bsdProfile.ToPercentage(52));

        for (byte code = 0; code <= F7bsdProfile.MaximumCode; code++)
        {
            SystemFanMode expected = code switch
            {
                < 10 => SystemFanMode.Off,
                < 36 => SystemFanMode.Quiet,
                _ => SystemFanMode.Full,
            };
            Equal(expected, F7bsdProfile.SystemMode(code));
        }
        Equal((byte)0, F7bsdProfile.SystemModeCode(SystemFanMode.Off));
        Equal((byte)20, F7bsdProfile.SystemModeCode(SystemFanMode.Quiet));
        Equal((byte)51, F7bsdProfile.SystemModeCode(SystemFanMode.Full));
        Throws<ArgumentOutOfRangeException>(() => F7bsdProfile.SystemMode(52));
    }

    private static void SystemPolicyWriteOrder()
    {
        SequenceEqual(
            [
                new EcWrite(0x0334, 70),
                new EcWrite(0x0337, 100),
                new EcWrite(0x0331, 70),
            ],
            F7bsdProfile.SystemWrites(SystemFanMode.Off));
        SequenceEqual(
            [
                new EcWrite(0x0334, 70),
                new EcWrite(0x0337, 100),
                new EcWrite(0x0331, 0),
            ],
            F7bsdProfile.SystemWrites(SystemFanMode.Quiet));
        SequenceEqual(
            [
                new EcWrite(0x0334, 0),
                new EcWrite(0x0337, 100),
                new EcWrite(0x0331, 0),
            ],
            F7bsdProfile.SystemWrites(SystemFanMode.Full));
        SequenceEqual(
            [
                new EcWrite(0x0334, 83),
                new EcWrite(0x0337, 100),
                new EcWrite(0x0331, 25),
            ],
            F7bsdProfile.SystemRestoreWrites([25, 83, 100]));

        foreach (SystemFanMode mode in Enum.GetValues<SystemFanMode>())
        {
            EcWrite[] writes = F7bsdProfile.SystemWrites(mode);
            Equal(3, writes.Length);
            F7bsdProfile.AssertWritesAllowed(writes);
        }
    }

    private static void ExhaustiveCpuCompiler(byte selector)
    {
        FakeTransport transport = new(selector);
        CpuConfiguration configuration = F7bsdProfile.ValidateCpuConfiguration(
            transport.Read(F7bsdProfile.CpuConfigurationAddresses));
        Equal(selector, configuration.Selector);

        for (byte requestedCode = 0;
            requestedCode <= F7bsdProfile.MaximumCode;
            requestedCode++)
        {
            CpuEncoding[] encodings = F7bsdProfile.CompileCpuCurve(
                requestedCode,
                configuration.Bands);
            Equal(7, encodings.Length);

            foreach (bool cooling in new[] { false, true })
            {
                int previous = -1;
                for (int temperature = 0;
                    temperature < F7bsdProfile.CpuCriticalTemperatureC;
                    temperature++)
                {
                    int target = F7bsdProfile.CpuTarget(
                        encodings,
                        configuration.Bands,
                        temperature,
                        cooling);
                    True(target >= requestedCode);
                    True(target >= F7bsdProfile.SafetyCode(temperature));
                    True(target <= F7bsdProfile.MaximumCode);
                    True(target >= previous);
                    previous = target;
                }

                Equal(
                    (int)F7bsdProfile.MaximumCode,
                    F7bsdProfile.CpuTarget(
                        encodings,
                        configuration.Bands,
                        F7bsdProfile.CpuCriticalTemperatureC,
                        cooling));
            }

            EcWrite[] writes = F7bsdProfile.CpuWrites(
                requestedCode,
                configuration.Bands);
            Equal(14, writes.Length);
            Equal(14, writes.Select(write => write.Address).Distinct().Count());
            SequenceEqual(F7bsdProfile.CpuBaseAddresses, writes.Take(7).Select(w => w.Address));
            SequenceEqual(F7bsdProfile.CpuSlopeAddresses, writes.Skip(7).Select(w => w.Address));
            F7bsdProfile.AssertWritesAllowed(writes);
        }
    }

    private static void CpuTransitionsStaySafe()
    {
        foreach (byte selector in new byte[] { 0, 0xb1, 0xb2 })
        {
            FakeTransport transport = new(selector);
            CpuConfiguration configuration = F7bsdProfile.ValidateCpuConfiguration(
                transport.Read(F7bsdProfile.CpuConfigurationAddresses));
            byte[] baseline = F7bsdProfile.CpuRestoreAddresses
                .Select(transport.ByteAt)
                .ToArray();
            byte[] low = F7bsdProfile.CpuBytes(
                F7bsdProfile.CompileCpuCurve(0, configuration.Bands));
            byte[] middle = F7bsdProfile.CpuBytes(
                F7bsdProfile.CompileCpuCurve(28, configuration.Bands));
            byte[] full = F7bsdProfile.CpuBytes(
                F7bsdProfile.CompileCpuCurve(51, configuration.Bands));

            foreach ((string Name, byte[] Start, byte[] End) in new[]
            {
                ("baseline-low", baseline, low),
                ("baseline-middle", baseline, middle),
                ("baseline-full", baseline, full),
                ("low-baseline", low, baseline),
                ("middle-baseline", middle, baseline),
                ("full-baseline", full, baseline),
                ("low-full", low, full),
                ("full-low", full, low),
                ("low-middle", low, middle),
                ("middle-low", middle, low),
            })
            {
                try
                {
                    AssertSafeCpuTransition(Start, End, configuration.Bands);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"Selector 0x{selector:X2}, transition {Name} failed.",
                        exception);
                }
            }
        }
    }

    private static void AssertSafeCpuTransition(
        byte[] start,
        byte[] end,
        CpuBand[] bands)
    {
        byte[] current = (byte[])start.Clone();
        CpuEncoding[] startEncodings = F7bsdProfile.DecodeCpuBytes(start);
        CpuEncoding[] endEncodings = F7bsdProfile.DecodeCpuBytes(end);
        EcWrite[] writes = F7bsdProfile.CpuTransitionWrites(start, end, bands);
        F7bsdProfile.AssertWritesAllowed(writes);
        False(writes.Any(write =>
            F7bsdProfile.CpuCriticalAddresses.Contains(write.Address)));

        foreach (EcWrite write in writes)
        {
            int baseIndex = Array.IndexOf(F7bsdProfile.CpuBaseAddresses, write.Address);
            int slopeIndex = Array.IndexOf(F7bsdProfile.CpuSlopeAddresses, write.Address);
            True(baseIndex >= 0 || slopeIndex >= 0);
            current[baseIndex >= 0 ? baseIndex : 7 + slopeIndex] = write.Value;
            CpuEncoding[] intermediate = F7bsdProfile.DecodeCpuBytes(current);
            for (int row = 0; row < 7; row++)
            {
                for (int temperature = bands[row].Lower;
                    temperature <= bands[row].Upper;
                    temperature++)
                {
                    int delta = temperature - bands[row].Lower;
                    int actual = EncodingTarget(intermediate[row], delta);
                    int minimum = Math.Min(
                        EncodingTarget(startEncodings[row], delta),
                        EncodingTarget(endEncodings[row], delta));
                    True(actual >= minimum);
                    True(actual <= F7bsdProfile.CpuTransitionMaximumCode);
                }
            }
        }
        SequenceEqual(end, current);
    }

    private static int EncodingTarget(CpuEncoding encoding, int delta) =>
        encoding.Base + ((encoding.Slope * delta) / 100);

    private static void CpuCriticalAddressesExcluded()
    {
        foreach (byte selector in new byte[] { 0, 0xb1, 0xb2 })
        {
            FakeTransport transport = new(selector);
            CpuConfiguration configuration = F7bsdProfile.ValidateCpuConfiguration(
                transport.Read(F7bsdProfile.CpuConfigurationAddresses));
            foreach (byte code in new byte[] { 0, 1, 20, 50, 51 })
            {
                ushort[] addresses = F7bsdProfile.CpuWrites(code, configuration.Bands)
                    .Select(write => write.Address)
                    .ToArray();
                False(addresses.Intersect(F7bsdProfile.CpuCriticalAddresses).Any());
            }
        }

        foreach (ushort criticalAddress in F7bsdProfile.CpuCriticalAddresses)
        {
            Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
                [new EcWrite(criticalAddress, 0)]));
        }
        Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
            [new EcWrite(0x1803, 20)]));
        Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
            [new EcWrite(0x088a, 0)]));
    }

    private static void TachLowHighLowDecoder()
    {
        byte[] values = TelemetryBytes(
            cpuCounter: 1_000,
            systemCounter: 1_250,
            cpuTemperature: 56,
            systemTemperature: 44);
        True(F7bsdTelemetryDecoder.TryDecode(values, out F7bsdTelemetry? telemetry));
        NotNull(telemetry);
        Equal(2_156, telemetry!.CpuFanRpm);
        Equal(1_725, telemetry.SystemFanRpm);
        Equal(56, telemetry.CpuTemperatureC);
        Equal(44, telemetry.SystemTemperatureC);

        byte[] stopped = TelemetryBytes(0, 0, 40, 35);
        True(F7bsdTelemetryDecoder.TryDecode(stopped, out F7bsdTelemetry? zero));
        Equal(0, zero!.CpuFanRpm);
        Equal(0, zero.SystemFanRpm);

        byte[] tornCpu = (byte[])values.Clone();
        tornCpu[2]++;
        False(F7bsdTelemetryDecoder.TryDecode(tornCpu, out F7bsdTelemetry? cpuResult));
        Equal<F7bsdTelemetry?>(null, cpuResult);

        byte[] tornSystem = (byte[])values.Clone();
        tornSystem[5]++;
        False(F7bsdTelemetryDecoder.TryDecode(tornSystem, out F7bsdTelemetry? systemResult));
        Equal<F7bsdTelemetry?>(null, systemResult);
        Throws<ArgumentException>(() => F7bsdTelemetryDecoder.TryDecode([0, 1], out _));
    }

    private static void ExactHostIdentityGate()
    {
        HostIdentityGate.Assert(ExactHost);
        Throws<ArgumentNullException>(() => HostIdentityGate.Assert(null!));

        HostIdentitySnapshot[] rejected =
        [
            ExactHost with { Product = "Other" },
            ExactHost with { Board = "OTHER" },
            ExactHost with { BoardVersion = "1.0" },
            ExactHost with { BiosVersion = "1.05" },
            ExactHost with { EmbeddedControllerMajorVersion = 1 },
            ExactHost with { EmbeddedControllerMinorVersion = 7 },
        ];
        foreach (HostIdentitySnapshot identity in rejected)
        {
            Throws<PlatformNotSupportedException>(() => HostIdentityGate.Assert(identity));
        }
    }

    private static void InitializationGatesWithoutWrites()
    {
        FakeTransport good = new();
        PawnIoF7bsdBackend backend = CreateBackend(good);
        backend.Initialize();
        backend.Initialize();
        Equal(5, good.ReadBatches.Count);
        SequenceEqual(F7bsdProfile.ControllerProfileAddresses, good.ReadBatches[0]);
        SequenceEqual(F7bsdProfile.CpuConfigurationAddresses, good.ReadBatches[1]);
        SequenceEqual(F7bsdProfile.CpuRestoreAddresses, good.ReadBatches[2]);
        SequenceEqual(F7bsdProfile.SystemThresholdAddresses, good.ReadBatches[3]);
        SequenceEqual(F7bsdProfile.TelemetryAddresses, good.ReadBatches[4]);
        Equal(0, good.WriteBatches.Count);
        backend.Dispose();
        True(good.Disposed);
        Equal(0, good.WriteBatches.Count);

        FakeTransport b2 = new(0xb2);
        PawnIoF7bsdBackend b2Backend = CreateBackend(b2);
        b2Backend.Initialize();
        Equal(0, b2.WriteBatches.Count);
        b2Backend.Dispose();

        FakeTransport defaultProfile = new(0);
        PawnIoF7bsdBackend defaultBackend = CreateBackend(defaultProfile);
        defaultBackend.Initialize();
        Equal(0, defaultProfile.WriteBatches.Count);
        defaultBackend.Dispose();

        FakeTransport wrongPnp = new();
        wrongPnp.PnpIdentity = [0x55, 0x71, 0x03];
        AssertInitializationRejectedWithoutWrites(wrongPnp);

        FakeTransport wrongController = new();
        wrongController.SetByte(0x200d, 0x42);
        AssertInitializationRejectedWithoutWrites(wrongController);

        FakeTransport wrongSelector = new();
        wrongSelector.SetByte(0x032f, 0xb3);
        AssertInitializationRejectedWithoutWrites(wrongSelector);

        FakeTransport wrongBand = new();
        wrongBand.SetByte(0x0311, 26);
        AssertInitializationRejectedWithoutWrites(wrongBand);

        FakeTransport wrongCritical = new();
        wrongCritical.SetByte(0x0325, 50);
        AssertInitializationRejectedWithoutWrites(wrongCritical);

        FakeTransport wrongCpuBaseline = new();
        wrongCpuBaseline.SetByte(0x0310, 1);
        AssertInitializationRejectedWithoutWrites(wrongCpuBaseline);

        FakeTransport wrongThreshold = new();
        wrongThreshold.SetByte(0x0331, 26);
        AssertInitializationRejectedWithoutWrites(wrongThreshold);

        FakeTransport cpuOverride = new();
        cpuOverride.SetByte(0x088a, 1);
        AssertInitializationRejectedWithoutWrites(cpuOverride);

        FakeTransport systemOverride = new();
        systemOverride.SetByte(0x088b, 0xff);
        AssertInitializationRejectedWithoutWrites(systemOverride);

        FakeTransport effectiveMismatch = new();
        effectiveMismatch.SetByte(0x0889, 43);
        AssertInitializationRejectedWithoutWrites(effectiveMismatch);

        FakeTransport unstable = new() { UnstableTelemetryReadsRemaining = 16 };
        PawnIoF7bsdBackend unstableBackend = CreateBackend(unstable);
        Throws<IOException>(unstableBackend.Initialize);
        Equal(0, unstable.WriteBatches.Count);
        Equal(20, unstable.ReadBatches.Count);
        True(unstable.Disposed);

        int factoryCalls = 0;
        FakeTransport unused = new();
        PawnIoF7bsdBackend wrongHostBackend = new(
            static () => ExactHost with { Board = "Other" },
            () =>
            {
                factoryCalls++;
                return unused;
            });
        Throws<PlatformNotSupportedException>(wrongHostBackend.Initialize);
        Equal(0, factoryCalls);
        Equal(0, unused.WriteBatches.Count);
        False(unused.Disposed);
    }

    private static void BackendCpuLifecycle()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();

        byte returned = backend.Set(F7bsdFan.Cpu, 0);
        Equal((byte)0, returned);
        Equal<byte?>((byte)0, backend.CpuCode);
        Equal(1, transport.WriteBatches.Count);
        F7bsdProfile.AssertWritesAllowed(transport.WriteBatches[0]);
        False(transport.WriteBatches[0].Any(write =>
            F7bsdProfile.CpuCriticalAddresses.Contains(write.Address)));
        AssertCpuCode(transport, 0);

        backend.Set(F7bsdFan.Cpu, 0);
        Equal(1, transport.WriteBatches.Count);

        backend.Reset(F7bsdFan.Cpu);
        Equal<byte?>(null, backend.CpuCode);
        AssertCpuBaseline(transport);
        Equal(2, transport.WriteBatches.Count);
        backend.Reset(F7bsdFan.Cpu);
        Equal(2, transport.WriteBatches.Count);

        backend.Set(F7bsdFan.Cpu, 31);
        Equal<byte?>((byte)31, backend.CpuCode);
        backend.Dispose();
        True(transport.Disposed);
        Equal<byte?>(null, backend.CpuCode);
        AssertCpuBaseline(transport);
        Throws<InvalidOperationException>(() => backend.Set(F7bsdFan.Cpu, 10));

        FakeTransport changedConfiguration = new();
        PawnIoF7bsdBackend changedBackend = CreateBackend(changedConfiguration);
        changedBackend.Initialize();
        changedConfiguration.SetByte(0x0311, 26);
        Throws<PlatformNotSupportedException>(() => changedBackend.Set(F7bsdFan.Cpu, 20));
        Equal(0, changedConfiguration.WriteBatches.Count);
        changedConfiguration.SetByte(0x0311, 25);
        changedBackend.Dispose();

        FakeTransport overrideChanged = new();
        PawnIoF7bsdBackend overrideBackend = CreateBackend(overrideChanged);
        overrideBackend.Initialize();
        overrideChanged.SetByte(0x088a, 1);
        Throws<InvalidOperationException>(() => overrideBackend.Set(F7bsdFan.Cpu, 20));
        Equal(0, overrideChanged.WriteBatches.Count);
        overrideChanged.SetByte(0x088a, 0);
        overrideBackend.Dispose();

        FakeTransport invalidCodeTransport = new();
        PawnIoF7bsdBackend invalidCodeBackend = CreateBackend(invalidCodeTransport);
        invalidCodeBackend.Initialize();
        Throws<ArgumentOutOfRangeException>(() => invalidCodeBackend.Set(F7bsdFan.Cpu, 52));
        Equal(0, invalidCodeTransport.WriteBatches.Count);
        invalidCodeBackend.Dispose();
    }

    private static void BackendCpuFailureRestoration()
    {
        FakeTransport setFailure = new();
        PawnIoF7bsdBackend setBackend = CreateBackend(setFailure);
        setBackend.Initialize();
        setFailure.FailAfterWriteCalls.Add(1);
        Throws<IOException>(() => setBackend.Set(F7bsdFan.Cpu, 30));
        Equal<byte?>(null, setBackend.CpuCode);
        AssertCpuBaseline(setFailure);
        Equal(2, setFailure.WriteBatches.Count);
        setBackend.Dispose();
        True(setFailure.Disposed);

        FakeTransport continuation = new();
        PawnIoF7bsdBackend continuationBackend = CreateBackend(continuation);
        continuationBackend.Initialize();
        continuationBackend.Set(F7bsdFan.Cpu, 28);
        continuation.FailBeforeWriteCalls.UnionWith([2, 3, 4]);
        Throws<AggregateException>(() => continuationBackend.Reset(F7bsdFan.Cpu));
        Equal(4, continuation.WriteBatches.Count);
        AssertCpuCode(continuation, 28);

        continuationBackend.Dispose();
        True(continuation.Disposed);
        AssertCpuBaseline(continuation);
    }

    private static void CpuTransitionSnapshotPreconditions()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        transport.BeforeWrite = call =>
        {
            if (call == 1)
            {
                // Simulate a firmware profile reload after the path was
                // computed but before its first byte could be applied.
                transport.SetByte(0x0311, 26);
            }
        };

        Throws<AggregateException>(() => backend.Set(F7bsdFan.Cpu, 28));
        Equal(1, transport.WriteBatches.Count);
        Equal(0, transport.AppliedWriteBatches.Count);
        AssertCpuBaseline(transport);

        transport.BeforeWrite = null;
        transport.SetByte(0x0311, 25);
        backend.Dispose();
    }

    private static void ActivePolicyReloadRecovery()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        backend.Set(F7bsdFan.Cpu, 28);
        backend.Set(F7bsdFan.System, 0);
        Equal(2, transport.WriteBatches.Count);

        for (int index = 0; index < F7bsdProfile.CpuRestoreAddresses.Length; index++)
        {
            transport.SetByte(
                F7bsdProfile.CpuRestoreAddresses[index],
                transport.InitialCpuBaseline[index]);
        }
        for (int index = 0; index < F7bsdProfile.SystemThresholdAddresses.Length; index++)
        {
            transport.SetByte(
                F7bsdProfile.SystemThresholdAddresses[index],
                transport.InitialSystemBaseline[index]);
        }

        backend.ReadTelemetry();
        Equal(4, transport.WriteBatches.Count);
        AssertCpuCode(transport, 28);
        AssertMemory(transport, F7bsdProfile.SystemWrites(SystemFanMode.Off));

        backend.Dispose();
        AssertCpuBaseline(transport);
        AssertSystemBaseline(transport);

        FakeTransport changedProfile = new();
        PawnIoF7bsdBackend changedBackend = CreateBackend(changedProfile);
        changedBackend.Initialize();
        changedBackend.Set(F7bsdFan.Cpu, 28);
        int writesBeforeReset = changedProfile.WriteBatches.Count;
        changedProfile.SetByte(0x0311, 26);
        Throws<AggregateException>(() => changedBackend.Reset(F7bsdFan.Cpu));
        Equal(writesBeforeReset, changedProfile.WriteBatches.Count);
        changedProfile.SetByte(0x0311, 25);
        changedBackend.Dispose();
    }

    private static void SystemPoliciesAndThermalSeeds()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();

        Equal((byte)0, backend.Set(F7bsdFan.System, 0));
        Equal<SystemFanMode?>(SystemFanMode.Off, backend.SystemMode);
        SequenceEqual(F7bsdProfile.SystemWrites(SystemFanMode.Off), transport.WriteBatches[0]);
        AssertMemory(transport, F7bsdProfile.SystemWrites(SystemFanMode.Off));

        backend.Set(F7bsdFan.System, 9);
        Equal(1, transport.WriteBatches.Count);

        Equal((byte)20, backend.Set(F7bsdFan.System, 10));
        Equal<SystemFanMode?>(SystemFanMode.Quiet, backend.SystemMode);
        SequenceEqual(F7bsdProfile.SystemWrites(SystemFanMode.Quiet), transport.WriteBatches[1]);
        backend.Set(F7bsdFan.System, 35);
        Equal(2, transport.WriteBatches.Count);

        Equal((byte)51, backend.Set(F7bsdFan.System, 36));
        Equal<SystemFanMode?>(SystemFanMode.Full, backend.SystemMode);
        SequenceEqual(F7bsdProfile.SystemWrites(SystemFanMode.Full), transport.WriteBatches[2]);
        backend.Set(F7bsdFan.System, 51);
        Equal(3, transport.WriteBatches.Count);

        backend.Reset(F7bsdFan.System);
        Equal<SystemFanMode?>(null, backend.SystemMode);
        AssertSystemBaseline(transport);
        Equal(6, transport.WriteBatches.Count);
        backend.Reset(F7bsdFan.System);
        Equal(6, transport.WriteBatches.Count);

        backend.Set(F7bsdFan.System, 10);
        backend.Dispose();
        True(transport.Disposed);
        AssertSystemBaseline(transport);

        foreach (byte raw in new byte[] { 0, 100, 121 })
        {
            FakeTransport guarded = new();
            guarded.SetSystemTemperature(raw, raw);
            PawnIoF7bsdBackend guardedBackend = CreateBackend(guarded);
            if (raw is 0 or 121)
            {
                // Initialization rejects an invalid sensor, so inject it only after capture.
                guarded.SetSystemTemperature(44, 44);
                guardedBackend.Initialize();
                guarded.SetSystemTemperature(raw, raw);
            }
            else
            {
                guardedBackend.Initialize();
            }

            guarded.SetByte(0x0885, 20);
            byte applied = guardedBackend.Set(F7bsdFan.System, 10);
            SystemFanMode appliedMode = raw is 0 or 121
                ? SystemFanMode.Full
                : SystemFanMode.Quiet;
            Equal(F7bsdProfile.SystemModeCode(appliedMode), applied);
            SequenceEqual(
                [new EcWrite(0x0885, F7bsdProfile.MaximumCode)],
                guarded.WriteBatches[0]);
            SequenceEqual(
                F7bsdProfile.SystemWrites(appliedMode),
                guarded.WriteBatches[1]);
            Equal(F7bsdProfile.MaximumCode, guarded.ByteAt(0x0885));

            if (raw is 0 or 121)
            {
                Throws<InvalidOperationException>(() =>
                    guardedBackend.Reset(F7bsdFan.System));
                AssertMemory(guarded, F7bsdProfile.SystemWrites(SystemFanMode.Full));
                for (int index = 0;
                    index < F7bsdProfile.SystemThresholdAddresses.Length;
                    index++)
                {
                    guarded.SetByte(
                        F7bsdProfile.SystemThresholdAddresses[index],
                        guarded.InitialSystemBaseline[index]);
                }
                guarded.SetByte(0x0885, 20);
                guardedBackend.ReadTelemetry();
                AssertMemory(guarded, F7bsdProfile.SystemWrites(SystemFanMode.Full));
                Equal(F7bsdProfile.MaximumCode, guarded.ByteAt(0x0885));
                guarded.SetSystemTemperature(44, 44);
            }
            else
            {
                guarded.SetByte(0x0885, 20);
                guardedBackend.Reset(F7bsdFan.System);
                SequenceEqual(
                    [new EcWrite(0x0885, F7bsdProfile.MaximumCode)],
                    guarded.WriteBatches[2]);
                AssertSystemBaseline(guarded);
            }
            guardedBackend.Dispose();
            AssertSystemBaseline(guarded);
        }

        FakeTransport overrideChanged = new();
        PawnIoF7bsdBackend overrideBackend = CreateBackend(overrideChanged);
        overrideBackend.Initialize();
        overrideChanged.SetByte(0x088b, 0xff);
        Throws<InvalidOperationException>(() => overrideBackend.Set(F7bsdFan.System, 20));
        Equal(0, overrideChanged.WriteBatches.Count);
        overrideChanged.SetByte(0x088b, 0);
        overrideBackend.Dispose();
    }

    private static void SystemFailureRestoration()
    {
        FakeTransport setFailure = new();
        PawnIoF7bsdBackend setBackend = CreateBackend(setFailure);
        setBackend.Initialize();
        setFailure.FailAfterWriteCalls.Add(1);
        Throws<IOException>(() => setBackend.Set(F7bsdFan.System, 20));
        Equal<SystemFanMode?>(null, setBackend.SystemMode);
        AssertSystemBaseline(setFailure);
        Equal(4, setFailure.WriteBatches.Count);
        setBackend.Dispose();

        FakeTransport continuation = new();
        PawnIoF7bsdBackend continuationBackend = CreateBackend(continuation);
        continuationBackend.Initialize();
        continuationBackend.Set(F7bsdFan.System, 0);
        continuation.FailBeforeWriteCalls.Add(2);
        Throws<AggregateException>(() => continuationBackend.Reset(F7bsdFan.System));
        Equal(4, continuation.WriteBatches.Count);
        Equal((byte)25, continuation.ByteAt(0x0331));
        False(continuation.ByteAt(0x0334) == 83);
        Equal((byte)100, continuation.ByteAt(0x0337));
        continuationBackend.Dispose();
        True(continuation.Disposed);
        AssertSystemBaseline(continuation);

        FakeTransport hotSeedFailure = new();
        PawnIoF7bsdBackend hotBackend = CreateBackend(hotSeedFailure);
        hotBackend.Initialize();
        hotSeedFailure.SetSystemTemperature(100, 100);
        hotSeedFailure.FailAfterWriteCalls.Add(1);
        Throws<IOException>(() => hotBackend.Set(F7bsdFan.System, 0));
        Equal(F7bsdProfile.MaximumCode, hotSeedFailure.ByteAt(0x0885));
        AssertSystemBaseline(hotSeedFailure);
        hotBackend.Dispose();

        FakeTransport invalidRetry = new();
        PawnIoF7bsdBackend invalidRetryBackend = CreateBackend(invalidRetry);
        invalidRetryBackend.Initialize();
        invalidRetryBackend.Set(F7bsdFan.System, 0);
        invalidRetry.SetSystemTemperature(0, 0);
        invalidRetry.FailBeforeWriteCalls.Add(2);
        Throws<IOException>(() => invalidRetryBackend.Reset(F7bsdFan.System));
        invalidRetry.FailBeforeWriteCalls.Clear();
        Throws<InvalidOperationException>(() =>
            invalidRetryBackend.Reset(F7bsdFan.System));
        AssertMemory(invalidRetry, F7bsdProfile.SystemWrites(SystemFanMode.Full));
        invalidRetry.SetSystemTemperature(44, 44);
        invalidRetryBackend.Dispose();
        AssertSystemBaseline(invalidRetry);

        FakeTransport failedInvalidRecovery = new();
        PawnIoF7bsdBackend failedInvalidBackend = CreateBackend(failedInvalidRecovery);
        failedInvalidBackend.Initialize();
        failedInvalidRecovery.SetSystemTemperature(0, 0);
        failedInvalidRecovery.FailBeforeWriteCalls.UnionWith([1, 2]);
        Throws<AggregateException>(() =>
            failedInvalidBackend.Set(F7bsdFan.System, 0));
        failedInvalidRecovery.FailBeforeWriteCalls.Clear();
        failedInvalidRecovery.SetByte(0x0885, 0);
        failedInvalidBackend.ReadTelemetry();
        Equal(F7bsdProfile.MaximumCode, failedInvalidRecovery.ByteAt(0x0885));
        AssertMemory(
            failedInvalidRecovery,
            F7bsdProfile.SystemWrites(SystemFanMode.Full));
        failedInvalidRecovery.SetSystemTemperature(44, 44);
        failedInvalidBackend.Dispose();
        AssertSystemBaseline(failedInvalidRecovery);

        FakeTransport staleHotTarget = new();
        PawnIoF7bsdBackend staleHotBackend = CreateBackend(staleHotTarget);
        staleHotBackend.Initialize();
        staleHotBackend.Set(F7bsdFan.System, 51);
        staleHotTarget.SetSystemTemperature(121, 121);
        staleHotTarget.SetByte(0x0885, F7bsdProfile.MaximumCode);
        staleHotTarget.AfterRead = (_, addresses) =>
        {
            if (addresses.SequenceEqual(F7bsdProfile.TelemetryAddresses))
            {
                staleHotTarget.SetByte(0x0885, 0);
                staleHotTarget.AfterRead = null;
            }
        };
        staleHotBackend.ReadTelemetry();
        Equal(F7bsdProfile.MaximumCode, staleHotTarget.ByteAt(0x0885));
        staleHotTarget.SetSystemTemperature(44, 44);
        staleHotBackend.Dispose();
        AssertSystemBaseline(staleHotTarget);
    }

    private static void BackendCloseRestorationContinuation()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        backend.Set(F7bsdFan.Cpu, 28);
        backend.Set(F7bsdFan.System, 0);

        // Dispose restores system first. Fail its first threshold write and
        // verify that all later system writes and the complete CPU restore
        // are still attempted before the transport is closed.
        transport.FailBeforeWriteCalls.Add(3);
        Throws<AggregateException>(backend.Dispose);
        True(transport.Disposed);
        Equal<byte?>(null, backend.CpuCode);
        Equal<SystemFanMode?>(null, backend.SystemMode);
        AssertCpuBaseline(transport);
        False(transport.ByteAt(0x0334) == transport.InitialSystemBaseline[1]);
        Equal(transport.InitialSystemBaseline[0], transport.ByteAt(0x0331));
        Equal(transport.InitialSystemBaseline[2], transport.ByteAt(0x0337));
        Equal(6, transport.WriteBatches.Count);
    }

    private static void PluginSensorAndControlLifecycle()
    {
        FakeBackend backend = new(
            Sample(3_000, 1_900, 62, 41),
            Sample(3_100, 2_000, 63, 42));
        FakeLogger logger = new();
        UM780XTXPlugin plugin = new(() => backend, logger);
        FakeContainer container = new();

        try
        {
            plugin.Initialize();
            plugin.Load(container);
            Equal("Minisforum UM780 XTX (F7BSD)", plugin.Name);
            SequenceEqual(
                [
                    "minisforum.um780xtx.f7bsd.fan1",
                    "minisforum.um780xtx.f7bsd.fan2",
                ],
                container.FanSensors.Select(sensor => sensor.Id));
            SequenceEqual(
                [
                    "minisforum.um780xtx.f7bsd.cpu-temperature",
                    "minisforum.um780xtx.f7bsd.system-temperature",
                ],
                container.TempSensors.Select(sensor => sensor.Id));
            SequenceEqual(
                [
                    "minisforum.um780xtx.f7bsd.cpu-control",
                    "minisforum.um780xtx.f7bsd.system-control",
                ],
                container.ControlSensors.Select(sensor => sensor.Id));

            IPluginControlSensor2 cpu = FindControl(
                container.ControlSensors,
                "minisforum.um780xtx.f7bsd.cpu-control");
            IPluginControlSensor2 system = FindControl(
                container.ControlSensors,
                "minisforum.um780xtx.f7bsd.system-control");
            Equal("UM780 XTX CPU Fan Control", cpu.Name);
            Equal("UM780 XTX System Fan Mode", system.Name);
            Equal(
                $"{plugin.Name}/minisforum.um780xtx.f7bsd.fan1",
                cpu.PairedFanSensorId);
            Equal(
                $"{plugin.Name}/minisforum.um780xtx.f7bsd.fan2",
                system.PairedFanSensorId);
            Equal(3_000f, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan1").Value);
            Equal(41f, Find(
                container.TempSensors,
                "minisforum.um780xtx.f7bsd.system-temperature").Value);

            cpu.Set(50f);
            system.Set(50f);
            SequenceEqual(
                [
                    (F7bsdFan.Cpu, (byte)26),
                    (F7bsdFan.System, (byte)26),
                ],
                backend.SetCalls);
            Equal<float?>(F7bsdProfile.ToPercentage(26), cpu.Value);
            Equal<float?>(F7bsdProfile.ToPercentage(20), system.Value);

            cpu.Reset();
            SequenceEqual([F7bsdFan.Cpu], backend.ResetCalls);
            Equal<float?>(null, cpu.Value);
            Equal<float?>(F7bsdProfile.ToPercentage(20), system.Value);

            plugin.Update();
            Equal(3_100f, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan1").Value);
            Equal(2_000f, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan2").Value);
            Equal(63f, Find(
                container.TempSensors,
                "minisforum.um780xtx.f7bsd.cpu-temperature").Value);
            Equal(42f, Find(
                container.TempSensors,
                "minisforum.um780xtx.f7bsd.system-temperature").Value);

            plugin.Close();
            Equal(1, backend.DisposeCalls);
            Equal<float?>(null, cpu.Value);
            Equal<float?>(null, system.Value);
            Equal<float?>(null, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan1").Value);
            Equal<float?>(null, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan2").Value);
            Equal<float?>(null, Find(
                container.TempSensors,
                "minisforum.um780xtx.f7bsd.cpu-temperature").Value);
            Equal<float?>(null, Find(
                container.TempSensors,
                "minisforum.um780xtx.f7bsd.system-temperature").Value);
            plugin.Close();
            Equal(1, backend.DisposeCalls);
            True(logger.Messages.Any(message => message.Contains("initialized")));
        }
        finally
        {
            plugin.Close();
        }
    }

    private static void PluginTelemetryErrorClearsStaleValues()
    {
        FakeBackend backend = new(
            Sample(2_800, 1_700, 60, 39),
            Sample(2_900, 1_800, 61, 40));
        backend.FailReadCalls.Add(2);
        FakeLogger logger = new();
        UM780XTXPlugin plugin = new(() => backend, logger);
        FakeContainer container = new();

        try
        {
            plugin.Initialize();
            plugin.Load(container);
            IPluginControlSensor2 cpu = FindControl(
                container.ControlSensors,
                "minisforum.um780xtx.f7bsd.cpu-control");
            cpu.Set(25f);
            float? confirmedControl = cpu.Value;

            plugin.Update();
            Equal<float?>(null, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan1").Value);
            Equal<float?>(null, Find(
                container.TempSensors,
                "minisforum.um780xtx.f7bsd.system-temperature").Value);
            Equal(confirmedControl, cpu.Value);
            Equal(0, backend.DisposeCalls);
            True(logger.Messages.Any(message =>
                message.Contains("telemetry read failed", StringComparison.Ordinal)));

            plugin.Update();
            Equal(2_900f, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan1").Value);
            Equal(1_800f, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan2").Value);
            Equal(61f, Find(
                container.TempSensors,
                "minisforum.um780xtx.f7bsd.cpu-temperature").Value);
            Equal(confirmedControl, cpu.Value);
        }
        finally
        {
            plugin.Close();
        }
    }

    private static void PluginInitializationFailureCleanup()
    {
        FakeBackend initializeFailure = new(Sample(1, 2, 3, 4))
        {
            InitializeException = new IOException("expected initialization failure"),
        };
        UM780XTXPlugin initializePlugin = new(() => initializeFailure);
        Throws<IOException>(initializePlugin.Initialize);
        Equal(1, initializeFailure.DisposeCalls);

        FakeBackend readFailure = new(Sample(1, 2, 3, 4));
        readFailure.FailReadCalls.Add(1);
        UM780XTXPlugin readPlugin = new(() => readFailure);
        Throws<IOException>(readPlugin.Initialize);
        Equal(1, readFailure.DisposeCalls);
    }

    private static PawnIoF7bsdBackend CreateBackend(FakeTransport transport) => new(
        static () => ExactHost,
        () => transport);

    private static void AssertInitializationRejectedWithoutWrites(FakeTransport transport)
    {
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        Throws<PlatformNotSupportedException>(backend.Initialize);
        Equal(0, transport.WriteBatches.Count);
        True(transport.Disposed);
    }

    private static void AssertCpuBaseline(FakeTransport transport)
    {
        SequenceEqual(
            transport.InitialCpuBaseline,
            F7bsdProfile.CpuRestoreAddresses.Select(transport.ByteAt));
    }

    private static void AssertCpuCode(FakeTransport transport, byte code)
    {
        CpuConfiguration configuration = F7bsdProfile.ValidateCpuConfiguration(
            F7bsdProfile.CpuConfigurationAddresses.Select(transport.ByteAt).ToArray());
        byte[] expected = F7bsdProfile.CpuBytes(
            F7bsdProfile.CompileCpuCurve(code, configuration.Bands));
        SequenceEqual(
            expected,
            F7bsdProfile.CpuRestoreAddresses.Select(transport.ByteAt));
    }

    private static void AssertSystemBaseline(FakeTransport transport)
    {
        SequenceEqual(
            transport.InitialSystemBaseline,
            F7bsdProfile.SystemThresholdAddresses.Select(transport.ByteAt));
    }

    private static void AssertMemory(FakeTransport transport, IEnumerable<EcWrite> writes)
    {
        foreach (EcWrite write in writes)
        {
            Equal(write.Value, transport.ByteAt(write.Address));
        }
    }

    private static byte[] TelemetryBytes(
        ushort cpuCounter,
        ushort systemCounter,
        byte cpuTemperature,
        byte systemTemperature,
        byte cpuTarget = 22,
        byte systemTarget = 20,
        byte cpuOverride = 0,
        byte systemOverride = 0)
    {
        byte cpuLow = (byte)cpuCounter;
        byte systemLow = (byte)systemCounter;
        return
        [
            cpuLow,
            (byte)(cpuCounter >> 8),
            cpuLow,
            systemLow,
            (byte)(systemCounter >> 8),
            systemLow,
            cpuTemperature,
            systemTemperature,
            cpuTarget,
            systemTarget,
            cpuTemperature,
            systemTemperature,
            cpuOverride,
            systemOverride,
        ];
    }

    private static F7bsdTelemetry Sample(
        int cpuRpm,
        int systemRpm,
        int cpuTemperature,
        int systemTemperature) => new(
            cpuRpm,
            systemRpm,
            cpuTemperature,
            systemTemperature,
            22,
            20,
            (byte)cpuTemperature,
            (byte)systemTemperature,
            0,
            0);

    private static IPluginSensor Find(List<IPluginSensor> sensors, string id) =>
        sensors.Single(sensor => sensor.Id == id);

    private static IPluginControlSensor2 FindControl(
        List<IPluginControlSensor> sensors,
        string id) =>
        (IPluginControlSensor2)sensors.Single(sensor => sensor.Id == id);

    private static void NotNull<T>(T? value)
        where T : class
    {
        if (value is null)
        {
            throw new InvalidOperationException("Expected a non-null value.");
        }
    }

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static void False(bool condition) => True(!condition);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}; found {actual}.");
        }
    }

    private static void SequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual)
    {
        T[] expectedArray = expected.ToArray();
        T[] actualArray = actual.ToArray();
        if (!expectedArray.SequenceEqual(actualArray))
        {
            throw new InvalidOperationException(
                "Expected [" + string.Join(", ", expectedArray) + "]; found [" +
                string.Join(", ", actualArray) + "].");
        }
    }

    private static void Throws<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class FakeContainer : IPluginSensorsContainer
    {
        public List<IPluginControlSensor> ControlSensors { get; } = [];
        public List<IPluginSensor> FanSensors { get; } = [];
        public List<IPluginSensor> TempSensors { get; } = [];
    }

    private sealed class FakeLogger : IPluginLogger
    {
        internal List<string> Messages { get; } = [];

        public void Log(string message) => Messages.Add(message);
    }

    private sealed class FakeTransport : IF7bsdTransport
    {
        private static readonly (byte Base, byte Upper, byte Lower, byte Slope)[] Default =
        [
            (0, 25, 0, 0),
            (16, 45, 25, 10),
            (18, 54, 45, 33),
            (21, 66, 54, 58),
            (28, 76, 66, 60),
            (34, 88, 76, 16),
            (36, 93, 88, 200),
            (51, 100, 93, 0),
        ];

        private static readonly (byte Base, byte Upper, byte Lower, byte Slope)[] B1 =
        [
            (0, 25, 0, 0),
            (16, 45, 25, 10),
            (18, 54, 45, 33),
            (21, 66, 54, 58),
            (28, 76, 66, 60),
            (32, 88, 76, 16),
            (33, 93, 88, 200),
            (51, 100, 93, 0),
        ];

        private static readonly (byte Base, byte Upper, byte Lower, byte Slope)[] B2 =
        [
            (0, 25, 0, 0),
            (18, 45, 25, 15),
            (21, 54, 45, 77),
            (28, 66, 54, 66),
            (36, 80, 66, 40),
            (42, 88, 80, 50),
            (46, 93, 88, 100),
            (51, 100, 93, 0),
        ];

        private readonly Dictionary<ushort, byte> memory = [];
        private int readCalls;
        private int writeCalls;

        internal FakeTransport(byte selector = 0xb1)
        {
            for (int index = 0; index < F7bsdProfile.ControllerProfileAddresses.Length; index++)
            {
                memory[F7bsdProfile.ControllerProfileAddresses[index]] =
                    F7bsdProfile.ExpectedControllerProfile[index];
            }
            InstallCpuProfile(selector);
            memory[0x0331] = 25;
            memory[0x0334] = 83;
            memory[0x0337] = 100;
            InstallTelemetry(TelemetryBytes(1_000, 1_250, 56, 44));

            InitialCpuBaseline = F7bsdProfile.CpuRestoreAddresses
                .Select(ByteAt)
                .ToArray();
            InitialSystemBaseline = F7bsdProfile.SystemThresholdAddresses
                .Select(ByteAt)
                .ToArray();
        }

        internal List<ushort[]> ReadBatches { get; } = [];

        internal List<EcWrite[]> WriteBatches { get; } = [];

        internal List<EcWrite[]> AppliedWriteBatches { get; } = [];

        internal HashSet<int> FailReadCalls { get; } = [];

        internal HashSet<int> FailBeforeWriteCalls { get; } = [];

        internal HashSet<int> FailAfterWriteCalls { get; } = [];

        internal byte[] InitialCpuBaseline { get; }

        internal byte[] InitialSystemBaseline { get; }

        internal int UnstableTelemetryReadsRemaining { get; set; }

        internal Action<int>? BeforeWrite { get; set; }

        internal Action<int, ushort[]>? AfterRead { get; set; }

        internal bool Disposed { get; private set; }

        internal byte[] PnpIdentity { get; set; } =
            (byte[])F7bsdProfile.ExpectedPnpIdentity.Clone();

        public byte[] ReadPnpIdentity()
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            return (byte[])PnpIdentity.Clone();
        }

        public byte[] Read(ushort[] addresses)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            readCalls++;
            ReadBatches.Add((ushort[])addresses.Clone());
            if (FailReadCalls.Contains(readCalls))
            {
                throw new IOException($"Expected fake read failure {readCalls}.");
            }

            byte[] result = addresses.Select(ByteAt).ToArray();
            if (UnstableTelemetryReadsRemaining > 0 &&
                addresses.SequenceEqual(F7bsdProfile.TelemetryAddresses))
            {
                UnstableTelemetryReadsRemaining--;
                result[2] = unchecked((byte)(result[0] + 1));
            }
            AfterRead?.Invoke(readCalls, addresses);
            return result;
        }

        public void Write(EcWrite[] writes, EcExpectation[]? expectations = null)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            writeCalls++;
            EcWrite[] copy = (EcWrite[])writes.Clone();
            WriteBatches.Add(copy);
            if (FailBeforeWriteCalls.Contains(writeCalls))
            {
                throw new IOException($"Expected fake pre-write failure {writeCalls}.");
            }
            BeforeWrite?.Invoke(writeCalls);
            if (expectations is not null)
            {
                foreach (EcExpectation expectation in expectations)
                {
                    if (ByteAt(expectation.Address) != expectation.Value)
                    {
                        throw new IOException(
                            $"Expected fake precondition failure at " +
                            $"0x{expectation.Address:X4}.");
                    }
                }
            }
            AppliedWriteBatches.Add(copy);
            foreach (EcWrite write in copy)
            {
                memory[write.Address] = write.Value;
            }
            if (FailAfterWriteCalls.Contains(writeCalls))
            {
                throw new IOException($"Expected fake post-write failure {writeCalls}.");
            }
        }

        public void Dispose() => Disposed = true;

        internal byte ByteAt(ushort address) => memory.TryGetValue(address, out byte value)
            ? value
            : (byte)0;

        internal void SetByte(ushort address, byte value) => memory[address] = value;

        internal void SetSystemTemperature(byte raw, byte effective)
        {
            memory[0x0305] = raw;
            memory[0x0889] = effective;
        }

        private void InstallCpuProfile(byte selector)
        {
            (byte Base, byte Upper, byte Lower, byte Slope)[] rows = selector switch
            {
                0 => Default,
                0xb2 => B2,
                0xb1 => B1,
                _ => B1,
            };
            memory[0x032f] = selector;
            for (int row = 0; row < rows.Length; row++)
            {
                ushort baseAddress = (ushort)(0x0310 + (row * 3));
                memory[baseAddress] = rows[row].Base;
                memory[(ushort)(baseAddress + 1)] = rows[row].Upper;
                memory[(ushort)(baseAddress + 2)] = rows[row].Lower;
                memory[(ushort)(0x08b0 + row)] = rows[row].Slope;
            }
        }

        private void InstallTelemetry(byte[] values)
        {
            for (int index = 0; index < F7bsdProfile.TelemetryAddresses.Length; index++)
            {
                memory[F7bsdProfile.TelemetryAddresses[index]] = values[index];
            }
        }
    }

    private sealed class FakeBackend(params F7bsdTelemetry[] samples) : IF7bsdBackend
    {
        private readonly Queue<F7bsdTelemetry> samples = new(samples);
        private F7bsdTelemetry? lastSample;

        internal Exception? InitializeException { get; init; }

        internal HashSet<int> FailReadCalls { get; } = [];

        internal int InitializeCalls { get; private set; }

        internal int ReadCalls { get; private set; }

        internal List<(F7bsdFan Fan, byte Code)> SetCalls { get; } = [];

        internal List<F7bsdFan> ResetCalls { get; } = [];

        internal int DisposeCalls { get; private set; }

        public void Initialize()
        {
            InitializeCalls++;
            if (InitializeException is not null)
            {
                throw InitializeException;
            }
        }

        public F7bsdTelemetry ReadTelemetry()
        {
            ReadCalls++;
            if (FailReadCalls.Contains(ReadCalls))
            {
                throw new IOException($"Expected fake read failure {ReadCalls}.");
            }
            if (samples.TryDequeue(out F7bsdTelemetry? sample))
            {
                lastSample = sample;
            }
            return lastSample ?? throw new InvalidOperationException("No fake telemetry.");
        }

        public byte Set(F7bsdFan fan, byte requestedCode)
        {
            SetCalls.Add((fan, requestedCode));
            return fan == F7bsdFan.System
                ? F7bsdProfile.SystemModeCode(F7bsdProfile.SystemMode(requestedCode))
                : requestedCode;
        }

        public void Reset(F7bsdFan fan) => ResetCalls.Add(fan);

        public void Dispose() => DisposeCalls++;
    }
}
