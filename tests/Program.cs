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

    private static readonly ushort[] CpuBaseAddresses =
        Enumerable.Range(0, 7)
            .Select(row => (ushort)(0x0310 + (row * 3)))
            .ToArray();

    private static readonly ushort[] CpuSlopeAddresses =
        Enumerable.Range(0x08b0, 7)
            .Select(address => (ushort)address)
            .ToArray();

    private static readonly ushort[] CpuRestoreAddresses =
        [.. CpuBaseAddresses, .. CpuSlopeAddresses];

    private static readonly ushort[] CpuCriticalAddresses =
        [0x0325, 0x0326, 0x0327, 0x08b7];

    private static int Main()
    {
        (string Name, Action Body)[] tests =
        [
            ("linear percentage conversion", LinearPercentageConversion),
            ("CPU B1 floor compiler", CpuB1FloorCompiler),
            ("CPU B1 transitions exhaustive", CpuB1TransitionsExhaustive),
            ("thin write allowlist", ThinWriteAllowlist),
            ("tach low-high-low decoder", TachLowHighLowDecoder),
            ("PawnIO parks every EC and PNP byte", PawnIoParksEveryByte),
            ("PawnIO byte failures park and poison", PawnIoByteFailuresParkAndPoison),
            ("PawnIO clean mismatch stays recoverable", PawnIoCleanMismatchIsRecoverable),
            ("PawnIO rejects before hardware access", PawnIoRejectsBeforeHardwareAccess),
            ("PawnIO mutex timeout stays recoverable", PawnIoMutexTimeoutIsRecoverable),
            ("PawnIO abandoned mutex poisons", PawnIoAbandonedMutexPoisons),
            ("PawnIO exact transaction budgets", PawnIoTransactionBudgets),
            ("exact host identity gate", ExactHostIdentityGate),
            ("initialization identity and critical-row gates", InitializationIdentityAndCriticalGates),
            ("initialization rejects non-stock controller policy state", InitializationRejectsNonStockPolicyState),
            ("external system ownership is refused on initialize", ExternalSystemOwnershipIsRefused),
            ("OEM-floor CPU target lifecycle", CpuFloorTargetLifecycle),
            ("CPU floor failure restores baseline", CpuFloorFailureRestoresBaseline),
            ("every partial CPU transition restores B1", PartialCpuTransitionsRestoreB1),
            ("CPU precondition drift writes nothing", CpuPreconditionDriftWritesNothing),
            ("CPU restore refuses immutable profile drift", CpuRestoreIgnoresMutableConfiguration),
            ("system ownership lifecycle", SystemOwnershipLifecycle),
            ("system ownership polling is bounded", SystemOwnershipPollingIsBounded),
            ("unsafe initial system temperature only allows full", UnsafeInitialSystemTemperatureOnlyAllowsFull),
            ("system release failures are bounded and recoverable", SystemReleaseFailurePaths),
            ("system thermal guard precedes tach retries", SystemThermalGuardPrecedesTachRetries),
            ("persistent owned telemetry failure releases system", PersistentOwnedTelemetryFailureReleasesSystem),
            ("system guard gap releases to firmware", SystemGuardGapReleasesToFirmware),
            ("system drift faults without reengaging", SystemDriftFaultsWithoutReengaging),
            ("CPU zero preserves OEM policy", CpuZeroPreservesOemPolicy),
            ("duplicate control requests are cached", DuplicateControlRequestsAreCached),
            ("telemetry guards system control", TelemetryGuardsSystemControl),
            ("close restores both controls", CloseRestoresBothControls),
            ("close continues restoration after failure", CloseContinuesAfterRestoreFailure),
            ("plugin sensor and dual-control lifecycle", PluginSensorAndRawControlLifecycle),
            ("plugin control failures are contained", PluginControlFailuresAreContained),
            ("plugin telemetry error clears stale values", PluginTelemetryErrorClearsStaleValues),
            ("plugin persistent telemetry failure latches", PluginPersistentTelemetryFailureLatches),
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

    private static void LinearPercentageConversion()
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

        for (byte code = 0; code <= 51; code++)
        {
            Equal(code, F7bsdProfile.ToCode(F7bsdProfile.ToPercentage(code)));
        }

        Throws<ArgumentOutOfRangeException>(() => F7bsdProfile.ToCode(-0.01f));
        Throws<ArgumentOutOfRangeException>(() => F7bsdProfile.ToCode(100.01f));
        Throws<ArgumentOutOfRangeException>(() => F7bsdProfile.ToCode(float.NaN));
        Throws<ArgumentOutOfRangeException>(() =>
            F7bsdProfile.ToCode(float.PositiveInfinity));
        Throws<ArgumentOutOfRangeException>(() => F7bsdProfile.ToPercentage(52));
    }

    private static void ThinWriteAllowlist()
    {
        EcWrite[] allowed = CpuBaseAddresses
            .Select(address => new EcWrite(address, 51))
            .Concat(CpuSlopeAddresses.Select(address => new EcWrite(address, 0)))
            .Append(new EcWrite(0x0885, 0))
            .Append(new EcWrite(0x088b, 0xff))
            .Append(new EcWrite(0x088b, 0))
            .ToArray();
        F7bsdProfile.AssertWritesAllowed(allowed);

        Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
            [new EcWrite(CpuBaseAddresses[0], 0xff)]));

        Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
            [new EcWrite(0x0325, 51)]));
        Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
            [new EcWrite(0x0331, 0)]));
        Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
            [new EcWrite(0x1803, 20)]));
        Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
            [new EcWrite(0x1804, 20)]));
        Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
            [new EcWrite(0x088a, 0xff)]));
        Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
            [new EcWrite(0x0885, 52)]));
        Throws<InvalidOperationException>(() => F7bsdProfile.AssertWritesAllowed(
            [new EcWrite(0x088b, 1)]));
    }

    private static void CpuB1FloorCompiler()
    {
        byte[] exactB1 =
        {
            0, 16, 18, 21, 28, 32, 33,
            0, 10, 33, 58, 60, 16, 200,
        };
        SequenceEqual(
            exactB1,
            F7bsdCpuPolicy.ToMutableBytes(F7bsdCpuPolicy.CompileFloor(0)));

        for (byte code = 0; code <= F7bsdProfile.MaximumCode; code++)
        {
            F7bsdCpuRowState[] states = F7bsdCpuPolicy.CompileFloor(code);
            Equal(F7bsdCpuPolicy.NormalRowCount, states.Length);
            for (int row = 0; row < states.Length; row++)
            {
                True(F7bsdCpuPolicy.DominatesB1AndFloor(row, states[row], code));
                True(F7bsdCpuPolicy.IsB1Safe(row, states[row]));
            }
            F7bsdCpuPolicyRow[] complete = F7bsdCpuPolicy.CompileFloorRows(code);
            Equal(F7bsdCpuPolicy.GetB1Row(7), complete[7]);
        }

        Throws<ArgumentOutOfRangeException>(() => F7bsdCpuPolicy.CompileFloor(52));
        Throws<ArgumentException>(() => F7bsdCpuPolicy.FromMutableBytes([0, 1]));
    }

    private static void CpuB1TransitionsExhaustive()
    {
        int maximum = 0;
        for (byte fromCode = 0; fromCode <= F7bsdProfile.MaximumCode; fromCode++)
        {
            F7bsdCpuRowState[] from = F7bsdCpuPolicy.CompileFloor(fromCode);
            for (byte toCode = 0; toCode <= F7bsdProfile.MaximumCode; toCode++)
            {
                F7bsdCpuRowState[] to = F7bsdCpuPolicy.CompileFloor(toCode);
                for (int row = 0; row < F7bsdCpuPolicy.NormalRowCount; row++)
                {
                    F7bsdCpuRowState[] path = F7bsdCpuPolicy.PlanRowTransition(
                        row,
                        from[row],
                        to[row]);
                    True(path.Length <= F7bsdCpuPolicy.MaximumWritesPerRow);
                    maximum = Math.Max(maximum, path.Length);
                    F7bsdCpuRowState previous = from[row];
                    foreach (F7bsdCpuRowState state in path)
                    {
                        Equal(1,
                            (state.Base == previous.Base ? 0 : 1) +
                            (state.Slope == previous.Slope ? 0 : 1));
                        F7bsdCpuPolicyRow band = F7bsdCpuPolicy.GetB1Row(row);
                        for (int temperature = band.Lower;
                            temperature <= band.Upper;
                            temperature++)
                        {
                            int target = F7bsdCpuPolicy.TargetAt(row, state, temperature);
                            int lower = Math.Min(
                                F7bsdCpuPolicy.TargetAt(row, from[row], temperature),
                                F7bsdCpuPolicy.TargetAt(row, to[row], temperature));
                            True(target >= lower);
                            True(target <= F7bsdProfile.MaximumCode);
                        }
                        previous = state;
                    }
                    Equal(to[row], previous);
                }
            }
        }
        Equal(F7bsdCpuPolicy.MaximumWritesPerRow, maximum);
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

        True(F7bsdTelemetryDecoder.TryDecode(
            TelemetryBytes(0, 0, 40, 35),
            out F7bsdTelemetry? stopped));
        Equal(0, stopped!.CpuFanRpm);
        Equal(0, stopped.SystemFanRpm);

        byte[] tornCpu = (byte[])values.Clone();
        tornCpu[2]++;
        False(F7bsdTelemetryDecoder.TryDecode(tornCpu, out _));

        byte[] tornSystem = (byte[])values.Clone();
        tornSystem[5]++;
        False(F7bsdTelemetryDecoder.TryDecode(tornSystem, out _));
        Throws<ArgumentException>(() => F7bsdTelemetryDecoder.TryDecode([0, 1], out _));
    }

    private static void PawnIoParksEveryByte()
    {
        List<string> trace = [];
        FakePawnIoExecutor executor = new(trace);
        executor.SetByte(0x0309, 56);
        executor.SetByte(0x0305, 44);
        FakeIsaMutex mutex = new(trace);
        using PawnIoTransport transport = new(executor, mutex);

        SequenceEqual([(byte)56, (byte)44], transport.Read([0x0309, 0x0305]));
        Equal(17, executor.ExecuteCalls);
        SequenceEqual(
        [
            "SELECT:0",
            "SOUT:002E=11", "SOUT:002F=03", "SOUT:002E=10",
            "SOUT:002F=09", "SOUT:002E=12", "SIN:002F",
            "SOUT:002E=10", "POUT:002E=20",
            "SOUT:002E=11", "SOUT:002F=03", "SOUT:002E=10",
            "SOUT:002F=05", "SOUT:002E=12", "SIN:002F",
            "SOUT:002E=10", "POUT:002E=20",
        ],
        executor.Operations);
        Equal("MUTEX:RELEASE", trace.Last());
        Equal((byte)0x10, executor.Depth2Selector);
        Equal((byte)0x20, executor.OuterIndex);

        int before = executor.ExecuteCalls;
        SequenceEqual(F7bsdProfile.ExpectedPnpIdentity, transport.ReadPnpIdentity());
        Equal(7, executor.ExecuteCalls - before);
        SequenceEqual(
        [
            "SELECT:0",
            "SIN:0020", "POUT:002E=20",
            "SIN:0021", "POUT:002E=20",
            "SIN:0022", "POUT:002E=20",
        ],
        executor.Operations.Skip(before));
    }

    private static void PawnIoByteFailuresParkAndPoison()
    {
        // Call 1 selects the slot. Calls 2..9 are the complete byte body and
        // its two park operations. Inject at every position.
        for (int failureCall = 2; failureCall <= 9; failureCall++)
        {
            List<string> trace = [];
            FakePawnIoExecutor executor = new(trace)
            {
                FailExecuteCall = failureCall,
            };
            FakeIsaMutex mutex = new(trace);
            using PawnIoTransport transport = new(executor, mutex);

            Throws<InvalidOperationException>(() => transport.Read([0x0309]));
            if (failureCall <= 8)
            {
                True(executor.Operations.Contains("POUT:002E=20"));
            }
            if (failureCall <= 7)
            {
                True(executor.Operations.Count(item => item == "SOUT:002E=10") >= 1);
            }

            int nativeCalls = executor.ExecuteCalls;
            int mutexWaits = mutex.WaitCalls;
            Throws<InvalidOperationException>(() => transport.Read([0x0309]));
            Equal(nativeCalls, executor.ExecuteCalls);
            Equal(mutexWaits, mutex.WaitCalls);
        }

        List<string> doubleTrace = [];
        FakePawnIoExecutor doubleFailure = new(doubleTrace);
        doubleFailure.FailExecuteCalls.UnionWith([7, 8, 9]);
        using PawnIoTransport doubleTransport = new(
            doubleFailure,
            new FakeIsaMutex(doubleTrace));
        InvalidOperationException aggregate = Capture<InvalidOperationException>(
            () => doubleTransport.Read([0x0309]));
        True(aggregate.ToString().Contains("failure 7", StringComparison.Ordinal));
        True(aggregate.ToString().Contains("failure 8", StringComparison.Ordinal));
        True(aggregate.ToString().Contains("failure 9", StringComparison.Ordinal));
    }

    private static void PawnIoCleanMismatchIsRecoverable()
    {
        List<string> trace = [];
        FakePawnIoExecutor executor = KnownControllerExecutor(trace);
        executor.IgnoreEcWrites = true;
        using PawnIoTransport transport = new(executor, new FakeIsaMutex(trace));

        Throws<IOException>(() => transport.Write([new EcWrite(0x0885, 17)]));
        int callsAfterMismatch = executor.ExecuteCalls;
        executor.IgnoreEcWrites = false;
        executor.SetByte(0x0309, 61);
        SequenceEqual([(byte)61], transport.Read([0x0309]));
        True(executor.ExecuteCalls > callsAfterMismatch);
    }

    private static void PawnIoRejectsBeforeHardwareAccess()
    {
        List<string> trace = [];
        FakePawnIoExecutor executor = new(trace);
        FakeIsaMutex mutex = new(trace);
        using PawnIoTransport transport = new(executor, mutex);

        Throws<InvalidOperationException>(() => transport.Read([0xffff]));
        Throws<InvalidOperationException>(() =>
            transport.Write([new EcWrite(0x0325, 51)]));
        Equal(0, executor.ExecuteCalls);
        Equal(0, mutex.WaitCalls);
    }

    private static void PawnIoMutexTimeoutIsRecoverable()
    {
        List<string> trace = [];
        FakePawnIoExecutor executor = new(trace);
        executor.SetByte(0x0309, 52);
        FakeIsaMutex mutex = new(trace);
        mutex.WaitResults.Enqueue(false);
        mutex.WaitResults.Enqueue(true);
        using PawnIoTransport transport = new(executor, mutex);

        Throws<TimeoutException>(() => transport.Read([0x0309]));
        Equal(0, executor.ExecuteCalls);
        SequenceEqual([(byte)52], transport.Read([0x0309]));
    }

    private static void PawnIoAbandonedMutexPoisons()
    {
        List<string> trace = [];
        FakePawnIoExecutor executor = new(trace);
        FakeIsaMutex mutex = new(trace) { AbandonNextWait = true };
        using PawnIoTransport transport = new(executor, mutex);

        Throws<InvalidOperationException>(() => transport.Read([0x0309]));
        Equal(0, executor.ExecuteCalls);
        Equal(1, mutex.ReleaseCalls);
        int waits = mutex.WaitCalls;
        Throws<InvalidOperationException>(() => transport.Read([0x0309]));
        Equal(waits, mutex.WaitCalls);
        Equal(0, executor.ExecuteCalls);
    }

    private static void PawnIoTransactionBudgets()
    {
        List<string> trace = [];
        FakePawnIoExecutor executor = KnownControllerExecutor(trace);
        using PawnIoTransport transport = new(executor, new FakeIsaMutex(trace));

        transport.ReadPnpIdentity();
        Equal(7, executor.ExecuteCalls);
        int before = executor.ExecuteCalls;
        transport.Read(F7bsdProfile.TelemetryAddresses);
        Equal(1 + (8 * F7bsdProfile.TelemetryAddresses.Length),
            executor.ExecuteCalls - before);
        before = executor.ExecuteCalls;
        transport.Write(
        [
            new EcWrite(F7bsdProfile.SystemTargetAddress, 20),
            new EcWrite(F7bsdProfile.SystemTargetAddress, 21),
        ]);
        Equal(55 + (16 * 2), executor.ExecuteCalls - before);
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

    private static void InitializationIdentityAndCriticalGates()
    {
        FakeTransport good = new();
        PawnIoF7bsdBackend backend = CreateBackend(good);
        backend.Initialize();
        backend.Initialize();
        Equal(0, good.WriteBatches.Count);
        backend.Dispose();
        True(good.Disposed);
        Equal(0, good.WriteBatches.Count);

        FakeTransport wrongPnp = new()
        {
            PnpIdentity = [0x55, 0x71, 0x03],
        };
        AssertInitializationRejectedWithoutWrites(wrongPnp);

        FakeTransport wrongController = new();
        wrongController.SetByte(0x200d, 0x42);
        AssertInitializationRejectedWithoutWrites(wrongController);

        FakeTransport wrongCritical = new();
        wrongCritical.SetByte(0x0325, 50);
        AssertInitializationRejectedWithoutWrites(wrongCritical);

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

    private static void InitializationRejectsNonStockPolicyState()
    {
        FakeTransport transport = new();
        byte[] arbitraryCpu =
        [
            7, 52, 50, 12, 0, 255, 23,
            255, 1, 88, 0, 17, 244, 9,
        ];
        transport.SetCpuBytes(arbitraryCpu);
        transport.SetByte(0x032f, 0xa5);
        transport.SetByte(0x0311, 99);
        transport.SetByte(0x0331, 1);
        transport.SetByte(0x0334, 2);
        transport.SetByte(0x0337, 3);
        transport.SetByte(0x0309, 0);
        transport.SetByte(0x0888, 201);
        transport.SetByte(0x0305, 255);
        transport.SetByte(0x0889, 7);
        transport.SetByte(0x0884, 250);
        transport.SetByte(0x0885, 249);
        transport.SetByte(0x088a, 0x7e);

        AssertInitializationRejectedWithoutWrites(transport);
        SequenceEqual(arbitraryCpu, transport.CpuBytes());
        Equal(0, transport.WriteBatches.Count);
        True(transport.Disposed);
    }

    private static void ExternalSystemOwnershipIsRefused()
    {
        FakeTransport transport = new();
        transport.SetByte(0x088b, 0xff);
        transport.SetByte(0x0889, 0xff);
        AssertInitializationRejectedWithoutWrites(transport);
        int beforeSet = transport.WriteBatches.Count;
        Equal(0, beforeSet);
        Equal((byte)0xff, transport.ByteAt(0x088b));
        Equal((byte)0xff, transport.ByteAt(0x0889));
    }

    private static void CpuFloorTargetLifecycle()
    {
        FakeTransport transport = new();
        byte[] baseline = transport.CpuBytes();
        byte[] critical = transport.CriticalBytes();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();

        // Code zero is exactly the OEM policy and therefore emits no write.
        Equal((byte)0, backend.Set(F7bsdFan.Cpu, 0));
        AssertCpuPolicy(transport, 0);
        SequenceEqual(critical, transport.CriticalBytes());
        Equal(0, transport.WriteBatches.Count);

        int beforeSecondSet = transport.WriteBatches.Count;
        Equal((byte)51, backend.Set(F7bsdFan.Cpu, 51));
        AssertCpuPolicy(transport, 51);
        SequenceEqual(critical, transport.CriticalBytes());
        AssertOnlyCpuTargetWrites(transport.WritesSince(beforeSecondSet));

        int beforeReset = transport.WriteBatches.Count;
        backend.Reset(F7bsdFan.Cpu);
        SequenceEqual(baseline, transport.CpuBytes());
        SequenceEqual(critical, transport.CriticalBytes());
        AssertOnlyCpuTargetWrites(transport.WritesSince(beforeReset));

        int afterReset = transport.WriteBatches.Count;
        backend.Reset(F7bsdFan.Cpu);
        Equal(afterReset, transport.WriteBatches.Count);
        Throws<ArgumentOutOfRangeException>(() => backend.Set(F7bsdFan.Cpu, 52));

        backend.Dispose();
        True(transport.Disposed);
    }

    private static void CpuFloorFailureRestoresBaseline()
    {
        FakeTransport transport = new();
        byte[] baseline = transport.CpuBytes();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        transport.FailAfterWriteCalls.Add(1);

        ThrowsAny<Exception>(() => backend.Set(F7bsdFan.Cpu, 31));
        SequenceEqual(baseline, transport.CpuBytes());

        backend.Reset(F7bsdFan.Cpu);
        SequenceEqual(baseline, transport.CpuBytes());
        backend.Dispose();
    }

    private static void PartialCpuTransitionsRestoreB1()
    {
        int stepCount = F7bsdCpuPolicy.PlanTransition(0, 31).Length;
        True(stepCount > 1);
        for (int failedAfter = 1; failedAfter <= stepCount; failedAfter++)
        {
            FakeTransport transport = new()
            {
                FailAfterIndividualWriteInNextBatch = failedAfter,
            };
            byte[] baseline = transport.CpuBytes();
            PawnIoF7bsdBackend backend = CreateBackend(transport);
            backend.Initialize();

            ThrowsAny<Exception>(() => backend.Set(F7bsdFan.Cpu, 31));
            SequenceEqual(baseline, transport.CpuBytes());
            for (int row = 0; row < F7bsdCpuPolicy.NormalRowCount; row++)
            {
                True(F7bsdCpuPolicy.IsB1Safe(
                    row,
                    F7bsdCpuPolicy.FromMutableBytes(transport.CpuBytes())[row]));
            }
            Throws<InvalidOperationException>(() => backend.Set(F7bsdFan.Cpu, 31));
            backend.Dispose();
            True(transport.Disposed);
        }
    }

    private static void CpuPreconditionDriftWritesNothing()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        transport.SetByte(CpuBaseAddresses[0], 1);
        int before = transport.WriteBatches.Count;

        Throws<EcWritePreconditionException>(() => backend.Set(F7bsdFan.Cpu, 31));
        Equal(before, transport.WriteBatches.Count);
        Equal((byte)1, transport.ByteAt(CpuBaseAddresses[0]));
        Throws<InvalidOperationException>(() => backend.Set(F7bsdFan.Cpu, 31));
        backend.Dispose();
        True(transport.Disposed);
    }

    private static void CpuRestoreIgnoresMutableConfiguration()
    {
        FakeTransport transport = new();
        byte[] baseline = transport.CpuBytes();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        backend.Set(F7bsdFan.Cpu, 31);
        byte[] originalConfiguration = F7bsdProfile.CpuConfigurationAddresses
            .Select(transport.ByteAt)
            .ToArray();

        // Selector, bands, and the critical row are firmware-owned. Restoration
        // must refuse to combine our mutable rows with a different policy.
        transport.SetByte(0x032f, 0xa5);
        transport.SetByte(0x0311, 99);
        transport.SetByte(0x0312, 98);
        byte[] changedCritical = [4, 97, 91, 13];
        for (int index = 0; index < CpuCriticalAddresses.Length; index++)
        {
            transport.SetByte(CpuCriticalAddresses[index], changedCritical[index]);
        }

        int beforeReset = transport.WriteBatches.Count;
        ThrowsAny<Exception>(() => backend.Reset(F7bsdFan.Cpu));
        Equal(beforeReset, transport.WriteBatches.Count);
        SequenceEqual(changedCritical, transport.CriticalBytes());

        for (int index = 0;
            index < F7bsdProfile.CpuConfigurationAddresses.Length;
            index++)
        {
            transport.SetByte(
                F7bsdProfile.CpuConfigurationAddresses[index],
                originalConfiguration[index]);
        }
        backend.Reset(F7bsdFan.Cpu);
        SequenceEqual(baseline, transport.CpuBytes());
        Throws<InvalidOperationException>(() => backend.Set(F7bsdFan.Cpu, 31));

        backend.Dispose();
        True(transport.Disposed);
    }

    private static void SystemOwnershipLifecycle()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();

        transport.SetByte(0x088a, 0x7e);
        int start = transport.WriteBatches.Count;
        Equal((byte)17, backend.Set(F7bsdFan.System, 17));
        SequenceEqual(
            [new EcWrite(0x088b, 0xff), new EcWrite(0x0885, 17)],
            transport.WritesSince(start));
        AssertSystemOwned(transport, 17);
        AssertOnlySystemControlWrites(transport.WritesSince(start));
        AssertOwnershipWasVerifiedBetweenWrites(transport, 17);

        int readStart = transport.ReadBatches.Count;
        start = transport.WriteBatches.Count;
        Equal((byte)17, backend.Set(F7bsdFan.System, 17));
        Equal(readStart, transport.ReadBatches.Count);
        Equal(start, transport.WriteBatches.Count);

        Equal((byte)23, backend.Set(F7bsdFan.System, 23));
        AssertSystemOwned(transport, 23);

        start = transport.WriteBatches.Count;
        backend.Reset(F7bsdFan.System);
        SequenceEqual(
            [new EcWrite(0x0885, 51), new EcWrite(0x088b, 0)],
            transport.WritesSince(start));
        Equal((byte)0, transport.ByteAt(0x088b));
        Equal(transport.ByteAt(0x0305), transport.ByteAt(0x0889));

        int afterReset = transport.WriteBatches.Count;
        backend.Reset(F7bsdFan.System);
        Equal(afterReset, transport.WriteBatches.Count);
        backend.Dispose();
        True(transport.Disposed);
    }

    private static void SystemOwnershipPollingIsBounded()
    {
        FakeClock delayedClock = new();
        FakeTransport delayed = new() { OwnershipReadsBeforeEffective = 5 };
        PawnIoF7bsdBackend delayedBackend = CreateBackend(delayed, delayedClock);
        delayedBackend.Initialize();
        Equal((byte)22, delayedBackend.Set(F7bsdFan.System, 22));
        Equal(6, delayed.OwnershipPollReads);
        Equal(5, delayedClock.Sleeps.Count);
        True(delayedClock.Sleeps.All(delay => delay == TimeSpan.FromMilliseconds(100)));
        delayed.ReleaseReadsBeforeEffective = 5;
        delayedBackend.Reset(F7bsdFan.System);
        Equal(6, delayed.ReleasePollReads);
        Equal(10, delayedClock.Sleeps.Count);
        True(delayedClock.Sleeps.Skip(5).All(delay =>
            delay == TimeSpan.FromMilliseconds(100)));
        delayedBackend.Dispose();

        FakeClock timeoutClock = new();
        FakeTransport timeout = new() { AutoSystemOwnership = false };
        PawnIoF7bsdBackend timeoutBackend = CreateBackend(timeout, timeoutClock);
        timeoutBackend.Initialize();
        ThrowsAny<Exception>(() => timeoutBackend.Set(F7bsdFan.System, 20));
        // Six bounded ownership polls plus one cleanup snapshot after timeout.
        Equal(7, timeout.OwnershipPollReads);
        Equal(5, timeoutClock.Sleeps.Count);
        Equal((byte)51, timeout.ByteAt(0x0885));
        Equal((byte)0, timeout.ByteAt(0x088b));
        int reads = timeout.ReadBatches.Count;
        int writes = timeout.WriteBatches.Count;
        Throws<InvalidOperationException>(() => timeoutBackend.Set(F7bsdFan.System, 51));
        Equal(reads, timeout.ReadBatches.Count);
        Equal(writes, timeout.WriteBatches.Count);
        timeoutBackend.Dispose();
    }

    private static void UnsafeInitialSystemTemperatureOnlyAllowsFull()
    {
        foreach (byte raw in new byte[] { 0, 70, 120, 121, 255 })
        {
            FakeTransport transport = new();
            PawnIoF7bsdBackend backend = CreateBackend(transport);
            backend.Initialize();
            transport.SetByte(0x0305, raw);

            int writes = transport.WriteBatches.Count;
            Throws<InvalidOperationException>(() => backend.Set(F7bsdFan.System, 20));
            Equal(writes, transport.WriteBatches.Count);
            Equal((byte)0, transport.ByteAt(0x088b));

            Equal((byte)51, backend.Set(F7bsdFan.System, 51));
            AssertSystemOwned(transport, 51);
            transport.SetByte(0x0305, 44);
            backend.Reset(F7bsdFan.System);
            backend.Dispose();
        }

        FakeTransport safe = new();
        PawnIoF7bsdBackend safeBackend = CreateBackend(safe);
        safeBackend.Initialize();
        safe.SetByte(0x0305, 69);
        Equal((byte)20, safeBackend.Set(F7bsdFan.System, 20));
        AssertSystemOwned(safe, 20);
        safeBackend.Dispose();

        FakeTransport raced = new();
        PawnIoF7bsdBackend racedBackend = CreateBackend(raced);
        racedBackend.Initialize();
        raced.BeforeGuardedWrite = current =>
        {
            if (current.ByteAt(0x088b) == 0xff)
            {
                current.SetByte(0x0305, 70);
            }
        };
        Equal((byte)51, racedBackend.Set(F7bsdFan.System, 20));
        AssertSystemOwned(raced, 51);
        True(raced.AppliedWrites.Any(write =>
            write.Address == 0x0885 && write.Value == 20));
        True(raced.AppliedWrites.Any(write =>
            write.Address == 0x0885 && write.Value == 51));
        raced.SetByte(0x0305, 44);
        racedBackend.Dispose();
    }

    private static void SystemReleaseFailurePaths()
    {
        FakeClock targetClock = new();
        FakeTransport targetFailure = new();
        PawnIoF7bsdBackend targetBackend = CreateBackend(targetFailure, targetClock);
        targetBackend.Initialize();
        targetBackend.Set(F7bsdFan.System, 12);
        targetFailure.FailBeforeWriteCalls.Add(targetFailure.WriteCallCount + 1);
        ThrowsAny<Exception>(() => targetBackend.Reset(F7bsdFan.System));
        Equal((byte)0, targetFailure.ByteAt(0x088b));
        Equal(targetFailure.ByteAt(0x0305), targetFailure.ByteAt(0x0889));
        int reads = targetFailure.ReadBatches.Count;
        int writes = targetFailure.WriteBatches.Count;
        Throws<InvalidOperationException>(() => targetBackend.Set(F7bsdFan.System, 20));
        Equal(reads, targetFailure.ReadBatches.Count);
        Equal(writes, targetFailure.WriteBatches.Count);
        targetBackend.Dispose();

        FakeClock clearClock = new();
        FakeTransport clearFailure = new();
        PawnIoF7bsdBackend clearBackend = CreateBackend(clearFailure, clearClock);
        clearBackend.Initialize();
        clearBackend.Set(F7bsdFan.System, 12);
        clearFailure.FailBeforeWriteCalls.Add(clearFailure.WriteCallCount + 2);
        ThrowsAny<Exception>(() => clearBackend.Reset(F7bsdFan.System));
        Equal((byte)0xff, clearFailure.ByteAt(0x088b));
        reads = clearFailure.ReadBatches.Count;
        writes = clearFailure.WriteBatches.Count;
        Throws<InvalidOperationException>(() => clearBackend.ReadTelemetry());
        Equal(reads, clearFailure.ReadBatches.Count);
        Equal(writes, clearFailure.WriteBatches.Count);

        clearBackend.Reset(F7bsdFan.System);
        Equal((byte)0, clearFailure.ByteAt(0x088b));
        reads = clearFailure.ReadBatches.Count;
        writes = clearFailure.WriteBatches.Count;
        Throws<InvalidOperationException>(() => clearBackend.Set(F7bsdFan.System, 20));
        Equal(reads, clearFailure.ReadBatches.Count);
        Equal(writes, clearFailure.WriteBatches.Count);
        clearBackend.Dispose();

        FakeClock staleClock = new();
        FakeTransport staleEffective = new();
        PawnIoF7bsdBackend staleBackend = CreateBackend(staleEffective, staleClock);
        staleBackend.Initialize();
        staleBackend.Set(F7bsdFan.System, 12);
        staleEffective.AutoSystemRelease = false;
        ThrowsAny<Exception>(() => staleBackend.Reset(F7bsdFan.System));
        Equal((byte)0, staleEffective.ByteAt(0x088b));
        Equal((byte)0xff, staleEffective.ByteAt(0x0889));
        reads = staleEffective.ReadBatches.Count;
        writes = staleEffective.WriteBatches.Count;
        Throws<InvalidOperationException>(() => staleBackend.ReadTelemetry());
        Equal(reads, staleEffective.ReadBatches.Count);
        Equal(writes, staleEffective.WriteBatches.Count);
        Throws<InvalidOperationException>(() => staleBackend.Set(F7bsdFan.System, 51));
        Equal(reads, staleEffective.ReadBatches.Count);
        Equal(writes, staleEffective.WriteBatches.Count);

        staleEffective.SetByte(0x0889, staleEffective.ByteAt(0x0305));
        staleBackend.Reset(F7bsdFan.System);
        reads = staleEffective.ReadBatches.Count;
        writes = staleEffective.WriteBatches.Count;
        Throws<InvalidOperationException>(() => staleBackend.Set(F7bsdFan.System, 51));
        Equal(reads, staleEffective.ReadBatches.Count);
        Equal(writes, staleEffective.WriteBatches.Count);
        staleBackend.Dispose();
    }

    private static void SystemThermalGuardPrecedesTachRetries()
    {
        FakeClock clock = new();
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport, clock);
        backend.Initialize();
        backend.Set(F7bsdFan.System, 20);

        transport.SetByte(0x0305, 69);
        int writes = transport.WriteBatches.Count;
        Equal<byte?>((byte)20, backend.ReadTelemetry().SystemAppliedCode);
        Equal(writes, transport.WriteBatches.Count);

        transport.SetByte(0x0305, 70);
        transport.UnstableTelemetryReadsRemaining = 3;
        int readStart = transport.ReadBatches.Count;
        writes = transport.WriteBatches.Count;
        Throws<IOException>(() => backend.ReadTelemetry());
        SequenceEqual(
            [new EcWrite(0x0885, 51)],
            transport.WritesSince(writes));
        AssertSystemOwned(transport, 51);
        ushort[][] guardReads = transport.ReadBatches.Skip(readStart).ToArray();
        Equal(1, guardReads.Count(addresses =>
            addresses.SequenceEqual(F7bsdProfile.RuntimeTelemetryAddresses)));
        Equal(2, guardReads.Count(addresses =>
            addresses.SequenceEqual(F7bsdProfile.CpuTachAddresses)));

        transport.SetByte(0x0305, 44);
        writes = transport.WriteBatches.Count;
        F7bsdTelemetry cooled = backend.ReadTelemetry();
        Equal<byte?>((byte)51, cooled.SystemAppliedCode);
        Equal(writes, transport.WriteBatches.Count);
        Equal((byte)20, backend.Set(F7bsdFan.System, 20));
        AssertSystemOwned(transport, 20);
        backend.Dispose();
    }

    private static void SystemGuardGapReleasesToFirmware()
    {
        FakeClock clock = new();
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport, clock);
        backend.Initialize();
        backend.Set(F7bsdFan.System, 20);

        clock.Advance(TimeSpan.FromSeconds(4));
        Equal<byte?>((byte)20, backend.ReadTelemetry().SystemAppliedCode);
        AssertSystemOwned(transport, 20);

        clock.Advance(TimeSpan.FromSeconds(4) + TimeSpan.FromTicks(1));
        int writes = transport.WriteBatches.Count;
        Throws<IOException>(() => backend.ReadTelemetry());
        SequenceEqual(
            [new EcWrite(0x0885, 51), new EcWrite(0x088b, 0)],
            transport.WritesSince(writes));
        Equal((byte)0, transport.ByteAt(0x088b));
        int reads = transport.ReadBatches.Count;
        writes = transport.WriteBatches.Count;
        Throws<InvalidOperationException>(() => backend.Set(F7bsdFan.System, 20));
        Equal(reads, transport.ReadBatches.Count);
        Equal(writes, transport.WriteBatches.Count);
        backend.Dispose();

        FakeClock backwardClock = new();
        FakeTransport backward = new();
        PawnIoF7bsdBackend backwardBackend = CreateBackend(backward, backwardClock);
        backwardBackend.Initialize();
        backwardBackend.Set(F7bsdFan.System, 20);
        backwardClock.Set(TimeSpan.FromTicks(-1));
        Throws<IOException>(() => backwardBackend.ReadTelemetry());
        Equal((byte)0, backward.ByteAt(0x088b));
        backwardBackend.Dispose();
    }

    private static void PersistentOwnedTelemetryFailureReleasesSystem()
    {
        FakeClock clock = new();
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport, clock);
        backend.Initialize();
        backend.Set(F7bsdFan.System, 20);

        for (int failure = 0; failure < 2; failure++)
        {
            transport.UnstableTelemetryReadsRemaining = 3;
            Throws<IOException>(() => backend.ReadTelemetry());
            AssertSystemOwned(transport, 20);
        }

        transport.UnstableTelemetryReadsRemaining = 3;
        int writes = transport.WriteBatches.Count;
        Throws<IOException>(() => backend.ReadTelemetry());
        SequenceEqual(
            [new EcWrite(0x0885, 51), new EcWrite(0x088b, 0)],
            transport.WritesSince(writes));
        Equal((byte)0, transport.ByteAt(0x088b));
        int reads = transport.ReadBatches.Count;
        writes = transport.WriteBatches.Count;
        Throws<InvalidOperationException>(() => backend.Set(F7bsdFan.System, 20));
        Equal(reads, transport.ReadBatches.Count);
        Equal(writes, transport.WriteBatches.Count);
        backend.Dispose();
    }

    private static void SystemDriftFaultsWithoutReengaging()
    {
        FakeClock lostClock = new();
        FakeTransport lost = new();
        PawnIoF7bsdBackend lostBackend = CreateBackend(lost, lostClock);
        lostBackend.Initialize();
        lostBackend.Set(F7bsdFan.System, 17);
        lost.SetByte(0x088b, 0);
        lost.SetByte(0x0889, lost.ByteAt(0x0305));
        int writes = lost.WriteBatches.Count;
        Throws<IOException>(() => lostBackend.ReadTelemetry());
        Equal(writes, lost.WriteBatches.Count);
        False(lost.WritesSince(writes).Any(write =>
            write.Address == 0x088b && write.Value == 0xff));
        int reads = lost.ReadBatches.Count;
        Throws<InvalidOperationException>(() => lostBackend.Set(F7bsdFan.System, 17));
        Equal(reads, lost.ReadBatches.Count);
        lostBackend.Dispose();

        FakeClock driftClock = new();
        FakeTransport drift = new();
        PawnIoF7bsdBackend driftBackend = CreateBackend(drift, driftClock);
        driftBackend.Initialize();
        driftBackend.Set(F7bsdFan.System, 17);
        drift.SetByte(0x0885, 18);
        writes = drift.WriteBatches.Count;
        Throws<IOException>(() => driftBackend.ReadTelemetry());
        SequenceEqual(
            [new EcWrite(0x0885, 51), new EcWrite(0x088b, 0)],
            drift.WritesSince(writes));
        False(drift.WritesSince(writes).Any(write =>
            write.Address == 0x088b && write.Value == 0xff));
        driftBackend.Dispose();
    }

    private static void CpuZeroPreservesOemPolicy()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        transport.SetByte(0x0309, 120);
        transport.SetByte(0x0888, 120);
        Equal((byte)0, backend.Set(F7bsdFan.Cpu, 0));
        AssertCpuPolicy(transport, 0);
        backend.Dispose();
    }

    private static void DuplicateControlRequestsAreCached()
    {
        FakeClock clock = new();
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport, clock);
        backend.Initialize();
        backend.Set(F7bsdFan.Cpu, 17);
        backend.Set(F7bsdFan.System, 18);

        int reads = transport.ReadBatches.Count;
        int writes = transport.WriteBatches.Count;
        Equal((byte)17, backend.Set(F7bsdFan.Cpu, 17));
        Equal((byte)18, backend.Set(F7bsdFan.System, 18));
        Equal(reads, transport.ReadBatches.Count);
        Equal(writes, transport.WriteBatches.Count);

        clock.Advance(TimeSpan.FromSeconds(4));
        Equal((byte)18, backend.Set(F7bsdFan.System, 18));
        Equal(reads, transport.ReadBatches.Count);
        Equal(writes, transport.WriteBatches.Count);
        clock.Advance(TimeSpan.FromTicks(1));
        Throws<IOException>(() => backend.Set(F7bsdFan.System, 18));
        Equal((byte)0, transport.ByteAt(0x088b));
        AssertCpuPolicy(transport, 17);
        backend.Dispose();
    }

    private static void TelemetryGuardsSystemControl()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        backend.Set(F7bsdFan.System, 26);

        int start = transport.ReadBatches.Count;
        F7bsdTelemetry telemetry = backend.ReadTelemetry();
        Equal(2_156, telemetry.CpuFanRpm);
        Equal(1_725, telemetry.SystemFanRpm);
        Equal(56, telemetry.CpuTemperatureC);
        Equal(44, telemetry.SystemTemperatureC);
        Equal<byte?>((byte)26, telemetry.SystemAppliedCode);
        True(transport.ReadBatches.Skip(start).Any(addresses =>
            addresses.SequenceEqual(F7bsdProfile.RuntimeTelemetryAddresses)));
        AssertSystemOwned(transport, 26);

        backend.Reset(F7bsdFan.System);
        backend.Dispose();
    }

    private static void CloseRestoresBothControls()
    {
        FakeTransport transport = new();
        byte[] baseline = transport.CpuBytes();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        backend.Set(F7bsdFan.Cpu, 9);
        backend.Set(F7bsdFan.System, 23);

        backend.Dispose();
        True(transport.Disposed);
        SequenceEqual(baseline, transport.CpuBytes());
        Equal((byte)51, transport.ByteAt(0x0885));
        Equal((byte)0, transport.ByteAt(0x088b));

        int afterDispose = transport.WriteBatches.Count;
        backend.Dispose();
        Equal(afterDispose, transport.WriteBatches.Count);
    }

    private static void CloseContinuesAfterRestoreFailure()
    {
        FakeTransport transport = new();
        byte[] baseline = transport.CpuBytes();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        backend.Set(F7bsdFan.Cpu, 9);
        backend.Set(F7bsdFan.System, 23);
        transport.AutoSystemRelease = false;

        ThrowsAny<Exception>(backend.Dispose);
        False(transport.Disposed);
        SequenceEqual(baseline, transport.CpuBytes());

        transport.AutoSystemRelease = true;
        transport.SetByte(0x0889, transport.ByteAt(0x0305));
        backend.Dispose();
        True(transport.Disposed);
    }

    private static void PluginSensorAndRawControlLifecycle()
    {
        FakeBackend backend = new(
            Sample(3_000, 1_900, 62, 41),
            Sample(3_100, 2_000, 63, 42, 51));
        FakeLogger logger = new();
        UM780XTXPlugin plugin = new(() => backend, logger);
        FakeContainer container = new();

        try
        {
            plugin.Initialize();
            plugin.Load(container);
            Equal(1, backend.InitializeCalls);
            Equal(4, container.FanSensors.Count + container.TempSensors.Count);
            Equal(2, container.ControlSensors.Count);
            IPluginControlSensor2 cpu = FindControl(
                container.ControlSensors,
                "minisforum.um780xtx.f7bsd.cpu-minimum-v2");
            IPluginControlSensor2 system = FindControl(
                container.ControlSensors,
                "minisforum.um780xtx.f7bsd.system-raw-v2");
            Equal("UM780 XTX CPU Fan Minimum (OEM Floor)", cpu.Name);
            Equal("UM780 XTX System Fan Raw Target", system.Name);
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

            cpu.Set(0f);
            cpu.Set(50f);
            system.Set(50f);
            SequenceEqual(
                [
                    (F7bsdFan.Cpu, (byte)0),
                    (F7bsdFan.Cpu, (byte)26),
                    (F7bsdFan.System, (byte)26),
                ],
                backend.SetCalls);
            Equal<float?>(F7bsdProfile.ToPercentage(26), cpu.Value);
            Equal<float?>(F7bsdProfile.ToPercentage(26), system.Value);
            cpu.Reset();
            SequenceEqual([F7bsdFan.Cpu], backend.ResetCalls);
            Equal<float?>(null, cpu.Value);

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
            Equal<float?>(100f, system.Value);
            system.Reset();
            SequenceEqual([F7bsdFan.Cpu, F7bsdFan.System], backend.ResetCalls);
            Equal<float?>(null, system.Value);

            plugin.Close();
            Equal(1, backend.DisposeCalls);
            Equal<float?>(null, cpu.Value);
            Equal<float?>(null, system.Value);
            Equal<float?>(null, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan1").Value);
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
            Equal(2, container.ControlSensors.Count);
            IPluginControlSensor2 system = FindControl(
                container.ControlSensors,
                "minisforum.um780xtx.f7bsd.system-raw-v2");
            system.Set(50f);
            True(system.Value.HasValue);

            plugin.Update();
            Equal<float?>(null, system.Value);
            Equal<float?>(null, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan1").Value);
            Equal<float?>(null, Find(
                container.TempSensors,
                "minisforum.um780xtx.f7bsd.system-temperature").Value);
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
        }
        finally
        {
            plugin.Close();
        }
    }

    private static void PluginControlFailuresAreContained()
    {
        FakeBackend backend = new(Sample(2_800, 1_700, 60, 39));
        backend.FailSetCalls.Add(1);
        backend.FailResetCalls.Add(1);
        FakeLogger logger = new();
        UM780XTXPlugin plugin = new(() => backend, logger);
        FakeContainer container = new();
        try
        {
            plugin.Initialize();
            plugin.Load(container);
            IPluginControlSensor2 cpu = FindControl(
                container.ControlSensors,
                "minisforum.um780xtx.f7bsd.cpu-minimum-v2");
            cpu.Set(50f);
            Equal<float?>(null, cpu.Value);
            Equal(1, backend.SetCalls.Count);
            cpu.Set(60f);
            Equal(1, backend.SetCalls.Count);
            cpu.Reset();
            Equal<float?>(null, cpu.Value);
            Equal(1, backend.ResetCalls.Count);
            cpu.Set(50f);
            Equal(1, backend.SetCalls.Count);
            cpu.Reset();
            Equal(2, backend.ResetCalls.Count);
            cpu.Set(50f);
            Equal(2, backend.SetCalls.Count);
            True(cpu.Value.HasValue);
            True(logger.Messages.Count(message =>
                message.Contains("CPU control failed", StringComparison.Ordinal)) == 2);
        }
        finally
        {
            plugin.Close();
        }
    }

    private static void PluginPersistentTelemetryFailureLatches()
    {
        FakeBackend backend = new(Sample(2_800, 1_700, 60, 39));
        backend.FailReadCalls.UnionWith([2, 3, 4, 5]);
        FakeLogger logger = new();
        UM780XTXPlugin plugin = new(() => backend, logger);
        FakeContainer container = new();
        try
        {
            plugin.Initialize();
            plugin.Load(container);
            IPluginControlSensor2 system = FindControl(
                container.ControlSensors,
                "minisforum.um780xtx.f7bsd.system-raw-v2");
            for (int failure = 0; failure < 3; failure++)
            {
                plugin.Update();
            }
            Equal(4, backend.ReadCalls);
            plugin.Update();
            Equal(4, backend.ReadCalls);
            system.Set(50f);
            Equal(0, backend.SetCalls.Count);
            system.Reset();
            Equal(1, backend.ResetCalls.Count);
            system.Set(50f);
            Equal(0, backend.SetCalls.Count);
            True(logger.Messages.Any(message => message.Contains(
                "disabled after three consecutive failures",
                StringComparison.Ordinal)));
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

    private static PawnIoF7bsdBackend CreateBackend(
        FakeTransport transport,
        FakeClock clock) => new(
            static () => ExactHost,
            () => transport,
            clock.Timestamp,
            static (start, end) => TimeSpan.FromTicks(end - start),
            clock.Sleep);

    private static void AssertInitializationRejectedWithoutWrites(FakeTransport transport)
    {
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        Throws<PlatformNotSupportedException>(backend.Initialize);
        Equal(0, transport.WriteBatches.Count);
        True(transport.Disposed);
    }

    private static void AssertCpuPolicy(FakeTransport transport, byte code)
    {
        SequenceEqual(
            F7bsdCpuPolicy.ToMutableBytes(F7bsdCpuPolicy.CompileFloor(code)),
            transport.CpuBytes());
    }

    private static void AssertOnlyCpuTargetWrites(IEnumerable<EcWrite> writes)
    {
        HashSet<ushort> allowed = [.. CpuRestoreAddresses];
        EcWrite[] materialized = writes.ToArray();
        True(materialized.Length > 0);
        True(materialized.All(write => allowed.Contains(write.Address)));
        False(materialized.Any(write => CpuCriticalAddresses.Contains(write.Address)));
        AssertNoPolicyOrPwmWrites(materialized);
    }

    private static void AssertNoPolicyOrPwmWrites(IEnumerable<EcWrite> writes)
    {
        ushort[] forbidden = [0x0331, 0x0334, 0x0337, 0x1803, 0x1804];
        False(writes.Any(write => forbidden.Contains(write.Address)));
    }

    private static void AssertOnlySystemControlWrites(IEnumerable<EcWrite> writes)
    {
        EcWrite[] materialized = writes.ToArray();
        True(materialized.Length > 0);
        True(materialized.All(write =>
            (write.Address == F7bsdProfile.SystemTargetAddress &&
                write.Value <= F7bsdProfile.MaximumCode) ||
            (write.Address == F7bsdProfile.SystemTemperatureOverrideAddress &&
                write.Value is 0 or F7bsdProfile.SystemSentinel)));
        AssertNoPolicyOrPwmWrites(materialized);
    }

    private static void AssertSystemOwned(FakeTransport transport, byte code)
    {
        Equal((byte)0xff, transport.ByteAt(0x088b));
        Equal((byte)0xff, transport.ByteAt(0x0889));
        Equal(code, transport.ByteAt(0x0885));
    }

    private static void AssertOwnershipWasVerifiedBetweenWrites(
        FakeTransport transport,
        byte targetCode)
    {
        int sentinelOperation = transport.Operations.FindIndex(item =>
            item == "W:088B=FF");
        int targetOperation = transport.Operations.FindIndex(item =>
            item == $"W:0885={targetCode:X2}");
        True(sentinelOperation >= 0);
        True(targetOperation > sentinelOperation);
        True(transport.Operations
            .Skip(sentinelOperation + 1)
            .Take(targetOperation - sentinelOperation - 1)
            .Any(item => item.StartsWith("R:", StringComparison.Ordinal) &&
                item.Contains("0889", StringComparison.Ordinal)));
    }

    private static byte[] TelemetryBytes(
        ushort cpuCounter,
        ushort systemCounter,
        byte cpuTemperature,
        byte systemTemperature)
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
        ];
    }

    private static F7bsdTelemetry Sample(
        int cpuRpm,
        int systemRpm,
        int cpuTemperature,
        int systemTemperature,
        byte? systemAppliedCode = null) => new(
            cpuRpm,
            systemRpm,
            cpuTemperature,
            systemTemperature,
            systemAppliedCode);

    private static FakePawnIoExecutor KnownControllerExecutor(List<string> trace)
    {
        FakePawnIoExecutor executor = new(trace);
        for (int index = 0;
            index < F7bsdProfile.ControllerProfileAddresses.Length;
            index++)
        {
            executor.SetByte(
                F7bsdProfile.ControllerProfileAddresses[index],
                F7bsdProfile.ExpectedControllerProfile[index]);
        }
        return executor;
    }

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

    private static void ThrowsAny<T>(Action action)
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

    private static T Capture<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            return exception;
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

    private sealed class FakeIsaMutex(List<string> trace) : IIsaMutex
    {
        internal Queue<bool> WaitResults { get; } = [];

        internal bool AbandonNextWait { get; set; }

        internal int WaitCalls { get; private set; }

        internal int ReleaseCalls { get; private set; }

        internal bool Disposed { get; private set; }

        public bool WaitOne(TimeSpan timeout)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            WaitCalls++;
            trace.Add("MUTEX:WAIT");
            if (AbandonNextWait)
            {
                AbandonNextWait = false;
                throw new AbandonedMutexException();
            }
            return WaitResults.TryDequeue(out bool result) ? result : true;
        }

        public void ReleaseMutex()
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            ReleaseCalls++;
            trace.Add("MUTEX:RELEASE");
        }

        public void Dispose()
        {
            Disposed = true;
            trace.Add("MUTEX:DISPOSE");
        }
    }

    private sealed class FakePawnIoExecutor(List<string> trace) : IPawnIoExecutor
    {
        private readonly Dictionary<ushort, byte> memory = [];
        private ushort currentAddress;

        internal List<string> Operations { get; } = [];

        internal HashSet<int> FailExecuteCalls { get; } = [];

        internal int? FailExecuteCall
        {
            init
            {
                if (value.HasValue)
                {
                    FailExecuteCalls.Add(value.Value);
                }
            }
        }

        internal bool IgnoreEcWrites { get; set; }

        internal int ExecuteCalls { get; private set; }

        internal byte Depth2Selector { get; private set; }

        internal byte OuterIndex { get; private set; }

        internal bool Disposed { get; private set; }

        public ulong[] Execute(string name, ulong[] input, int outputCount)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            ExecuteCalls++;
            string operation = Describe(name, input);
            Operations.Add(operation);
            trace.Add(operation);
            if (FailExecuteCalls.Contains(ExecuteCalls))
            {
                throw new IOException($"Expected native failure {ExecuteCalls}.");
            }

            ulong[] output = name switch
            {
                "ioctl_select_slot" => [],
                "ioctl_pio_outb" => ExecutePioOut(input),
                "ioctl_superio_outb" => ExecuteSuperIoOut(input),
                "ioctl_superio_inb" => ExecuteSuperIoIn(input),
                _ => throw new InvalidOperationException(
                    $"Unexpected fake PawnIO export {name}."),
            };
            Equal(outputCount, output.Length);
            return output;
        }

        public void Dispose()
        {
            Disposed = true;
            trace.Add("NATIVE:DISPOSE");
        }

        internal void SetByte(ushort address, byte value) => memory[address] = value;

        private ulong[] ExecutePioOut(ulong[] input)
        {
            Equal(2, input.Length);
            if (input[0] == 0x2e)
            {
                OuterIndex = checked((byte)input[1]);
            }
            return [];
        }

        private ulong[] ExecuteSuperIoOut(ulong[] input)
        {
            Equal(2, input.Length);
            byte port = checked((byte)input[0]);
            byte value = checked((byte)input[1]);
            if (port == 0x2e)
            {
                Depth2Selector = value;
            }
            else if (port == 0x2f)
            {
                switch (Depth2Selector)
                {
                    case 0x11:
                        currentAddress = (ushort)((currentAddress & 0x00ff) | (value << 8));
                        break;
                    case 0x10:
                        currentAddress = (ushort)((currentAddress & 0xff00) | value);
                        break;
                    case 0x12 when !IgnoreEcWrites:
                        memory[currentAddress] = value;
                        break;
                }
            }
            return [];
        }

        private ulong[] ExecuteSuperIoIn(ulong[] input)
        {
            Equal(1, input.Length);
            ulong register = input[0];
            if (register == 0x2f)
            {
                return [ByteAt(currentAddress)];
            }
            int identityIndex = checked((int)register - 0x20);
            if (identityIndex is >= 0 and < 3)
            {
                return [F7bsdProfile.ExpectedPnpIdentity[identityIndex]];
            }
            throw new InvalidOperationException(
                $"Unexpected fake Super-I/O read 0x{register:X}.");
        }

        private byte ByteAt(ushort address) => memory.TryGetValue(address, out byte value)
            ? value
            : (byte)0;

        private static string Describe(string name, ulong[] input) => name switch
        {
            "ioctl_select_slot" => $"SELECT:{input[0]}",
            "ioctl_pio_outb" => $"POUT:{input[0]:X4}={input[1]:X2}",
            "ioctl_superio_outb" => $"SOUT:{input[0]:X4}={input[1]:X2}",
            "ioctl_superio_inb" => $"SIN:{input[0]:X4}",
            _ => name,
        };
    }

    private sealed class FakeClock
    {
        private long nowTicks;

        internal List<TimeSpan> Sleeps { get; } = [];

        internal long Timestamp() => nowTicks;

        internal void Advance(TimeSpan amount) => nowTicks += amount.Ticks;

        internal void Set(TimeSpan value) => nowTicks = value.Ticks;

        internal void Sleep(TimeSpan delay)
        {
            Sleeps.Add(delay);
            Advance(delay);
        }
    }

    private sealed class FakeTransport : IF7bsdTransport
    {
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

        private readonly Dictionary<ushort, byte> memory = [];
        private int readCalls;
        private int writeCalls;
        private bool systemOwnershipPending;
        private bool systemReleasePending;

        internal FakeTransport()
        {
            for (int index = 0;
                index < F7bsdProfile.ControllerProfileAddresses.Length;
                index++)
            {
                memory[F7bsdProfile.ControllerProfileAddresses[index]] =
                    F7bsdProfile.ExpectedControllerProfile[index];
            }
            memory[0x032f] = 0xb1;
            for (int row = 0; row < B1.Length; row++)
            {
                ushort baseAddress = (ushort)(0x0310 + (row * 3));
                memory[baseAddress] = B1[row].Base;
                memory[(ushort)(baseAddress + 1)] = B1[row].Upper;
                memory[(ushort)(baseAddress + 2)] = B1[row].Lower;
                memory[(ushort)(0x08b0 + row)] = B1[row].Slope;
            }
            memory[0x0331] = 25;
            memory[0x0334] = 83;
            memory[0x0337] = 100;
            InstallTelemetry(TelemetryBytes(1_000, 1_250, 56, 44));
            memory[0x0888] = 56;
            memory[0x0889] = 44;
            memory[0x088a] = 0;
            memory[0x088b] = 0;
            memory[0x0884] = 0;
            memory[0x0885] = 0;
        }

        internal List<ushort[]> ReadBatches { get; } = [];

        internal List<EcWrite[]> WriteBatches { get; } = [];

        internal List<EcWrite[]> AppliedWriteBatches { get; } = [];

        internal List<string> Operations { get; } = [];

        internal HashSet<int> FailReadCalls { get; } = [];

        internal HashSet<int> FailBeforeWriteCalls { get; } = [];

        internal HashSet<int> FailAfterWriteCalls { get; } = [];

        internal int? FailAfterIndividualWriteInNextBatch { get; set; }

        internal bool AutoSystemOwnership { get; set; } = true;

        internal bool AutoSystemRelease { get; set; } = true;

        internal int OwnershipReadsBeforeEffective { get; set; }

        internal int OwnershipPollReads { get; private set; }

        internal int ReleaseReadsBeforeEffective { get; set; }

        internal int ReleasePollReads { get; private set; }

        internal Action<FakeTransport>? BeforeGuardedWrite { get; set; }

        internal int UnstableTelemetryReadsRemaining { get; set; }

        internal int WriteCallCount => writeCalls;

        internal bool Disposed { get; private set; }

        internal byte[] PnpIdentity { get; set; } =
            (byte[])F7bsdProfile.ExpectedPnpIdentity.Clone();

        internal IEnumerable<EcWrite> AppliedWrites =>
            AppliedWriteBatches.SelectMany(batch => batch);

        public byte[] ReadPnpIdentity()
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Operations.Add("PNP");
            return (byte[])PnpIdentity.Clone();
        }

        public byte[] Read(ushort[] addresses)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            readCalls++;
            ushort[] copy = (ushort[])addresses.Clone();
            ReadBatches.Add(copy);
            Operations.Add("R:" + string.Join(",", copy.Select(address =>
                address.ToString("X4"))));
            if (FailReadCalls.Contains(readCalls))
            {
                throw new IOException($"Expected fake read failure {readCalls}.");
            }

            if (addresses.SequenceEqual(F7bsdProfile.SystemOwnershipAddresses) &&
                ByteAt(F7bsdProfile.SystemTemperatureOverrideAddress) ==
                    F7bsdProfile.SystemSentinel &&
                ByteAt(F7bsdProfile.SystemEffectiveTemperatureAddress) !=
                    F7bsdProfile.SystemSentinel)
            {
                OwnershipPollReads++;
                if (AutoSystemOwnership && systemOwnershipPending)
                {
                    if (OwnershipReadsBeforeEffective == 0)
                    {
                        memory[F7bsdProfile.SystemEffectiveTemperatureAddress] =
                            F7bsdProfile.SystemSentinel;
                        systemOwnershipPending = false;
                    }
                    else
                    {
                        OwnershipReadsBeforeEffective--;
                    }
                }
            }
            if (addresses.SequenceEqual(F7bsdProfile.SystemOwnershipAddresses) &&
                ByteAt(F7bsdProfile.SystemTemperatureOverrideAddress) == 0 &&
                ByteAt(F7bsdProfile.SystemEffectiveTemperatureAddress) ==
                    F7bsdProfile.SystemSentinel &&
                systemReleasePending)
            {
                ReleasePollReads++;
                if (AutoSystemRelease)
                {
                    if (ReleaseReadsBeforeEffective == 0)
                    {
                        memory[F7bsdProfile.SystemEffectiveTemperatureAddress] =
                            memory[0x0305];
                        systemReleasePending = false;
                    }
                    else
                    {
                        ReleaseReadsBeforeEffective--;
                    }
                }
            }

            byte[] result = addresses.Select(ByteAt).ToArray();
            if (UnstableTelemetryReadsRemaining > 0 &&
                (addresses.SequenceEqual(F7bsdProfile.RuntimeTelemetryAddresses) ||
                    addresses.SequenceEqual(F7bsdProfile.CpuTachAddresses)))
            {
                UnstableTelemetryReadsRemaining--;
                result[2] = unchecked((byte)(result[0] + 1));
            }
            return result;
        }

        public void Write(EcWrite[] writes)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            F7bsdProfile.AssertWritesAllowed(writes);
            writeCalls++;
            EcWrite[] copy = (EcWrite[])writes.Clone();
            WriteBatches.Add(copy);
            foreach (EcWrite write in copy)
            {
                Operations.Add($"W:{write.Address:X4}={write.Value:X2}");
            }
            if (FailBeforeWriteCalls.Contains(writeCalls))
            {
                throw new IOException($"Expected fake pre-write failure {writeCalls}.");
            }
            int? failAfterIndividual = FailAfterIndividualWriteInNextBatch;
            FailAfterIndividualWriteInNextBatch = null;
            List<EcWrite> applied = [];
            foreach (EcWrite write in copy)
            {
                memory[write.Address] = write.Value;
                applied.Add(write);
                if (write.Address == 0x088b && write.Value == 0xff &&
                    AutoSystemOwnership)
                {
                    if (OwnershipReadsBeforeEffective == 0)
                    {
                        memory[0x0889] = 0xff;
                    }
                    else
                    {
                        systemOwnershipPending = true;
                    }
                }
                if (write.Address == 0x088b && write.Value == 0 && AutoSystemRelease)
                {
                    systemOwnershipPending = false;
                    if (ReleaseReadsBeforeEffective == 0)
                    {
                        memory[0x0889] = memory[0x0305];
                    }
                    else
                    {
                        systemReleasePending = true;
                    }
                }
                if (applied.Count == failAfterIndividual)
                {
                    AppliedWriteBatches.Add(applied.ToArray());
                    throw new IOException(
                        $"Expected fake failure after individual write {applied.Count}.");
                }
            }
            AppliedWriteBatches.Add(copy);
            if (FailAfterWriteCalls.Contains(writeCalls))
            {
                throw new IOException($"Expected fake post-write failure {writeCalls}.");
            }
        }

        public byte[] WriteGuarded(
            EcExpectation[] before,
            EcWrite[] writes,
            EcExpectation[] after,
            ushort[] resultAddresses)
        {
            byte[] beforeValues = Read(before.Select(item => item.Address).ToArray());
            for (int index = 0; index < before.Length; index++)
            {
                if (beforeValues[index] != before[index].Value)
                {
                    throw new EcWritePreconditionException(
                        before[index].Address,
                        before[index].Value,
                        beforeValues[index]);
                }
            }
            BeforeGuardedWrite?.Invoke(this);
            Write(writes);
            byte[] afterValues = Read(after.Select(item => item.Address).ToArray());
            for (int index = 0; index < after.Length; index++)
            {
                if (afterValues[index] != after[index].Value)
                {
                    throw new IOException(
                        $"Expected fake postcondition failure at 0x{after[index].Address:X4}.");
                }
            }
            return Read(resultAddresses);
        }

        public void Dispose() => Disposed = true;

        internal byte ByteAt(ushort address) => memory.TryGetValue(address, out byte value)
            ? value
            : (byte)0;

        internal void SetByte(ushort address, byte value) => memory[address] = value;

        internal void SetCpuBytes(ReadOnlySpan<byte> values)
        {
            if (values.Length != CpuRestoreAddresses.Length)
            {
                throw new ArgumentException("Unexpected fake CPU byte count.", nameof(values));
            }
            for (int index = 0; index < CpuRestoreAddresses.Length; index++)
            {
                memory[CpuRestoreAddresses[index]] = values[index];
            }
        }

        internal byte[] CpuBytes() => CpuRestoreAddresses.Select(ByteAt).ToArray();

        internal byte[] CriticalBytes() => CpuCriticalAddresses.Select(ByteAt).ToArray();

        internal EcWrite[] WritesSince(int batchIndex) => WriteBatches
            .Skip(batchIndex)
            .SelectMany(batch => batch)
            .ToArray();

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

        internal HashSet<int> FailSetCalls { get; } = [];

        internal HashSet<int> FailResetCalls { get; } = [];

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
            if (FailSetCalls.Contains(SetCalls.Count))
            {
                throw new IOException($"Expected fake Set failure {SetCalls.Count}.");
            }
            return requestedCode;
        }

        public void Reset(F7bsdFan fan)
        {
            ResetCalls.Add(fan);
            if (FailResetCalls.Contains(ResetCalls.Count))
            {
                throw new IOException($"Expected fake Reset failure {ResetCalls.Count}.");
            }
        }

        public void Dispose() => DisposeCalls++;
    }
}
