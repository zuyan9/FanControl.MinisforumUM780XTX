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
            ("raw profile write generation", RawProfileWriteGeneration),
            ("thin write allowlist", ThinWriteAllowlist),
            ("tach low-high-low decoder", TachLowHighLowDecoder),
            ("exact host identity gate", ExactHostIdentityGate),
            ("initialization identity and critical-row gates", InitializationIdentityAndCriticalGates),
            ("initialization accepts arbitrary controller policy state", InitializationAcceptsArbitraryPolicyState),
            ("external system ownership is refused on Set", ExternalSystemOwnershipIsRefused),
            ("raw CPU target lifecycle", RawCpuTargetLifecycle),
            ("raw CPU failure restores baseline", RawCpuFailureRestoresBaseline),
            ("CPU restore ignores mutable external configuration", CpuRestoreIgnoresMutableConfiguration),
            ("raw system ownership lifecycle", RawSystemOwnershipLifecycle),
            ("raw system ownership failures are recoverable", RawSystemOwnershipFailureRecovery),
            ("raw system release can be retried", RawSystemReleaseRetry),
            ("raw targets are not thermally promoted", RawTargetsAreNotThermallyPromoted),
            ("periodic Set reasserts raw targets", PeriodicSetReassertsRawTargets),
            ("telemetry remains passive during system control", TelemetryWhileSystemOwned),
            ("close restores both controls", CloseRestoresBothControls),
            ("close continues restoration after failure", CloseContinuesAfterRestoreFailure),
            ("plugin sensor and raw control lifecycle", PluginSensorAndRawControlLifecycle),
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
            .Append(new EcWrite(CpuBaseAddresses[0], 0xff))
            .Concat(CpuSlopeAddresses.Select(address => new EcWrite(address, 0)))
            .Append(new EcWrite(0x0885, 0))
            .Append(new EcWrite(0x088b, 0xff))
            .Append(new EcWrite(0x088b, 0))
            .ToArray();
        F7bsdProfile.AssertWritesAllowed(allowed);

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

    private static void RawProfileWriteGeneration()
    {
        foreach (byte code in Enumerable.Range(0, 52).Select(value => (byte)value))
        {
            EcWrite[] expected = CpuBaseAddresses
                .Select(address => new EcWrite(address, code))
                .Concat(CpuSlopeAddresses.Select(address => new EcWrite(address, 0)))
                .ToArray();
            SequenceEqual(expected, F7bsdProfile.CpuManualWrites(code));
            SequenceEqual(
                [.. Enumerable.Repeat(code, 7), .. new byte[7]],
                F7bsdProfile.CpuManualBytes(code));
        }

        byte[] baseline =
        [
            0, 16, 18, 21, 28, 32, 33,
            0, 10, 33, 58, 60, 16, 200,
        ];
        EcWrite[] expectedRestore = CpuSlopeAddresses
            .Select((address, row) => new EcWrite(address, baseline[7 + row]))
            .Concat(CpuBaseAddresses.Select(
                (address, row) => new EcWrite(address, baseline[row])))
            .ToArray();
        SequenceEqual(expectedRestore, F7bsdProfile.CpuRestoreWrites(baseline));
        False(expectedRestore.Any(write => CpuCriticalAddresses.Contains(write.Address)));

        Throws<ArgumentOutOfRangeException>(() => F7bsdProfile.CpuManualBytes(52));
        Throws<ArgumentOutOfRangeException>(() => F7bsdProfile.CpuManualWrites(52));
        Throws<ArgumentException>(() => F7bsdProfile.CpuRestoreWrites([0, 1]));
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

    private static void InitializationAcceptsArbitraryPolicyState()
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

        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        Equal(0, transport.WriteBatches.Count);

        // Initialization is not a policy audit. It captures the normal CPU
        // table and leaves unrelated live firmware state alone.
        SequenceEqual(arbitraryCpu, transport.CpuBytes());
        backend.Dispose();
        Equal(0, transport.WriteBatches.Count);
        True(transport.Disposed);
    }

    private static void ExternalSystemOwnershipIsRefused()
    {
        FakeTransport transport = new();
        transport.SetByte(0x088b, 0xff);
        transport.SetByte(0x0889, 0xff);
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();

        int beforeSet = transport.WriteBatches.Count;
        Throws<InvalidOperationException>(() => backend.Set(F7bsdFan.System, 20));
        Equal(beforeSet, transport.WriteBatches.Count);
        Equal((byte)0xff, transport.ByteAt(0x088b));
        Equal((byte)0xff, transport.ByteAt(0x0889));

        backend.Dispose();
        Equal(beforeSet, transport.WriteBatches.Count);
        True(transport.Disposed);
    }

    private static void RawCpuTargetLifecycle()
    {
        FakeTransport transport = new();
        transport.SetCpuBytes(
        [
            7, 52, 50, 12, 0, 255, 23,
            255, 1, 88, 0, 17, 244, 9,
        ]);
        byte[] baseline = transport.CpuBytes();
        byte[] critical = transport.CriticalBytes();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();

        // Runtime temperatures, their effective values, and CPU override state
        // are telemetry, not prerequisites for writing the normal target rows.
        transport.SetByte(0x0309, 0);
        transport.SetByte(0x0888, 200);
        transport.SetByte(0x088a, 0x7e);
        transport.SetByte(0x032f, 0xa5);
        transport.SetByte(0x0311, 99);
        Equal((byte)0, backend.Set(F7bsdFan.Cpu, 0));
        AssertCpuFlat(transport, 0);
        SequenceEqual(critical, transport.CriticalBytes());
        AssertOnlyCpuTargetWrites(transport.WritesSince(0));

        int beforeSecondSet = transport.WriteBatches.Count;
        Equal((byte)51, backend.Set(F7bsdFan.Cpu, 51));
        AssertCpuFlat(transport, 51);
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

    private static void RawCpuFailureRestoresBaseline()
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

    private static void CpuRestoreIgnoresMutableConfiguration()
    {
        FakeTransport transport = new();
        byte[] baseline = transport.CpuBytes();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        backend.Set(F7bsdFan.Cpu, 31);

        // Selector, band, and critical-row bytes are owned by firmware. Their
        // changing must not prevent restoration of the captured normal rows.
        transport.SetByte(0x032f, 0xa5);
        transport.SetByte(0x0311, 99);
        transport.SetByte(0x0312, 98);
        byte[] changedCritical = [4, 97, 91, 13];
        for (int index = 0; index < CpuCriticalAddresses.Length; index++)
        {
            transport.SetByte(CpuCriticalAddresses[index], changedCritical[index]);
        }

        int beforeReset = transport.WriteBatches.Count;
        backend.Reset(F7bsdFan.Cpu);
        SequenceEqual(baseline, transport.CpuBytes());
        SequenceEqual(changedCritical, transport.CriticalBytes());
        AssertOnlyCpuTargetWrites(transport.WritesSince(beforeReset));

        backend.Dispose();
        True(transport.Disposed);
    }

    private static void RawSystemOwnershipLifecycle()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();

        transport.SetByte(0x088a, 0x7e);
        int start = transport.WriteBatches.Count;
        Equal((byte)0, backend.Set(F7bsdFan.System, 0));
        SequenceEqual(
            [new EcWrite(0x088b, 0xff), new EcWrite(0x0885, 0)],
            transport.WritesSince(start));
        AssertSystemOwned(transport, 0);
        AssertNoPolicyOrPwmWrites(transport.WritesSince(start));
        AssertOwnershipWasVerifiedBetweenWrites(transport, start);

        start = transport.WriteBatches.Count;
        Equal((byte)17, backend.Set(F7bsdFan.System, 17));
        SequenceEqual([new EcWrite(0x0885, 17)], transport.WritesSince(start));
        AssertSystemOwned(transport, 17);

        start = transport.WriteBatches.Count;
        Equal((byte)17, backend.Set(F7bsdFan.System, 17));
        SequenceEqual([new EcWrite(0x0885, 17)], transport.WritesSince(start));
        AssertSystemOwned(transport, 17);

        // Simulate the firmware dropping manual ownership between Fan Control
        // update ticks. The next Set must re-engage even when the code is unchanged.
        transport.SetByte(0x088b, 0);
        transport.SetByte(0x0889, transport.ByteAt(0x0305));
        start = transport.WriteBatches.Count;
        Equal((byte)17, backend.Set(F7bsdFan.System, 17));
        SequenceEqual(
            [new EcWrite(0x088b, 0xff), new EcWrite(0x0885, 17)],
            transport.WritesSince(start));
        AssertSystemOwned(transport, 17);

        start = transport.WriteBatches.Count;
        Equal((byte)51, backend.Set(F7bsdFan.System, 51));
        SequenceEqual([new EcWrite(0x0885, 51)], transport.WritesSince(start));
        AssertSystemOwned(transport, 51);

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

    private static void RawSystemOwnershipFailureRecovery()
    {
        FakeTransport transport = new() { AutoSystemOwnership = false };
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();

        ThrowsAny<Exception>(() => backend.Set(F7bsdFan.System, 20));
        False(transport.AppliedWrites.Any(write =>
            write.Address == 0x0885 && write.Value == 20));

        transport.AutoSystemOwnership = true;
        backend.Reset(F7bsdFan.System);
        Equal((byte)51, transport.ByteAt(0x0885));
        Equal((byte)0, transport.ByteAt(0x088b));
        Equal(transport.ByteAt(0x0305), transport.ByteAt(0x0889));
        backend.Dispose();
    }

    private static void RawSystemReleaseRetry()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        backend.Set(F7bsdFan.System, 12);
        transport.AutoSystemRelease = false;

        ThrowsAny<Exception>(() => backend.Reset(F7bsdFan.System));
        Equal((byte)51, transport.ByteAt(0x0885));

        transport.AutoSystemRelease = true;
        backend.Reset(F7bsdFan.System);
        Equal((byte)0, transport.ByteAt(0x088b));
        Equal(transport.ByteAt(0x0305), transport.ByteAt(0x0889));
        backend.Dispose();
    }

    private static void RawTargetsAreNotThermallyPromoted()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();

        // Once initialization has identified the known controller, target
        // selection stays raw: the plugin does not replace Fan Control's value
        // with an internal thermal policy.
        transport.SetByte(0x0309, 120);
        transport.SetByte(0x0888, 120);
        transport.SetByte(0x0305, 120);
        transport.SetByte(0x0889, 120);
        Equal((byte)0, backend.Set(F7bsdFan.Cpu, 0));
        Equal((byte)0, backend.Set(F7bsdFan.System, 0));
        AssertCpuFlat(transport, 0);
        AssertSystemOwned(transport, 0);

        backend.Dispose();
    }

    private static void TelemetryWhileSystemOwned()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        backend.Set(F7bsdFan.System, 26);

        F7bsdTelemetry telemetry = backend.ReadTelemetry();
        Equal(2_156, telemetry.CpuFanRpm);
        Equal(1_725, telemetry.SystemFanRpm);
        Equal(56, telemetry.CpuTemperatureC);
        Equal(44, telemetry.SystemTemperatureC);
        AssertSystemOwned(transport, 26);

        backend.Reset(F7bsdFan.System);
        backend.Dispose();
    }

    private static void PeriodicSetReassertsRawTargets()
    {
        FakeTransport transport = new();
        PawnIoF7bsdBackend backend = CreateBackend(transport);
        backend.Initialize();
        backend.Set(F7bsdFan.Cpu, 17);
        backend.Set(F7bsdFan.System, 18);

        transport.SetByte(CpuBaseAddresses[0], 3);
        transport.SetByte(0x0885, 4);
        int beforeTelemetry = transport.WriteBatches.Count;
        backend.ReadTelemetry();
        Equal(beforeTelemetry, transport.WriteBatches.Count);
        Equal((byte)3, transport.ByteAt(CpuBaseAddresses[0]));
        Equal((byte)4, transport.ByteAt(0x0885));

        backend.Set(F7bsdFan.Cpu, 17);
        backend.Set(F7bsdFan.System, 18);
        AssertCpuFlat(transport, 17);
        AssertSystemOwned(transport, 18);
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
        True(transport.Disposed);
        SequenceEqual(baseline, transport.CpuBytes());
    }

    private static void PluginSensorAndRawControlLifecycle()
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
            Equal(1, backend.InitializeCalls);
            Equal(4, container.FanSensors.Count + container.TempSensors.Count);
            Equal(2, container.ControlSensors.Count);

            IPluginControlSensor2 cpu = FindControl(
                container.ControlSensors,
                "minisforum.um780xtx.f7bsd.cpu-control");
            IPluginControlSensor2 system = FindControl(
                container.ControlSensors,
                "minisforum.um780xtx.f7bsd.system-control");
            Equal("UM780 XTX CPU Fan Control", cpu.Name);
            Equal("UM780 XTX System Fan Control", system.Name);
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
            system.Set(1f);
            cpu.Set(50f);
            system.Set(100f);
            SequenceEqual(
                [
                    (F7bsdFan.Cpu, (byte)0),
                    (F7bsdFan.System, (byte)1),
                    (F7bsdFan.Cpu, (byte)26),
                    (F7bsdFan.System, (byte)51),
                ],
                backend.SetCalls);
            Equal<float?>(F7bsdProfile.ToPercentage(26), cpu.Value);
            Equal<float?>(100f, system.Value);

            cpu.Reset();
            SequenceEqual([F7bsdFan.Cpu], backend.ResetCalls);
            Equal<float?>(null, cpu.Value);
            Equal<float?>(100f, system.Value);

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
            IPluginControlSensor2 system = FindControl(
                container.ControlSensors,
                "minisforum.um780xtx.f7bsd.system-control");
            system.Set(25f);
            float? confirmedControl = system.Value;

            plugin.Update();
            Equal<float?>(null, Find(
                container.FanSensors,
                "minisforum.um780xtx.f7bsd.fan1").Value);
            Equal<float?>(null, Find(
                container.TempSensors,
                "minisforum.um780xtx.f7bsd.system-temperature").Value);
            Equal(confirmedControl, system.Value);
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
            Equal(confirmedControl, system.Value);
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

    private static void AssertCpuFlat(FakeTransport transport, byte code)
    {
        SequenceEqual(Enumerable.Repeat(code, 7),
            CpuBaseAddresses.Select(transport.ByteAt));
        SequenceEqual(Enumerable.Repeat((byte)0, 7),
            CpuSlopeAddresses.Select(transport.ByteAt));
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

    private static void AssertSystemOwned(FakeTransport transport, byte code)
    {
        Equal((byte)0xff, transport.ByteAt(0x088b));
        Equal((byte)0xff, transport.ByteAt(0x0889));
        Equal(code, transport.ByteAt(0x0885));
    }

    private static void AssertOwnershipWasVerifiedBetweenWrites(
        FakeTransport transport,
        int firstWriteBatch)
    {
        int sentinelOperation = transport.Operations.FindIndex(item =>
            item == "W:088B=FF");
        int targetOperation = transport.Operations.FindIndex(item =>
            item == "W:0885=00");
        True(firstWriteBatch == 0 || sentinelOperation >= 0);
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
        int systemTemperature) => new(
            cpuRpm,
            systemRpm,
            cpuTemperature,
            systemTemperature);

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
        }

        internal List<ushort[]> ReadBatches { get; } = [];

        internal List<EcWrite[]> WriteBatches { get; } = [];

        internal List<EcWrite[]> AppliedWriteBatches { get; } = [];

        internal List<string> Operations { get; } = [];

        internal HashSet<int> FailReadCalls { get; } = [];

        internal HashSet<int> FailBeforeWriteCalls { get; } = [];

        internal HashSet<int> FailAfterWriteCalls { get; } = [];

        internal bool AutoSystemOwnership { get; set; } = true;

        internal bool AutoSystemRelease { get; set; } = true;

        internal int UnstableTelemetryReadsRemaining { get; set; }

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

            byte[] result = addresses.Select(ByteAt).ToArray();
            if (UnstableTelemetryReadsRemaining > 0 &&
                addresses.SequenceEqual(F7bsdProfile.TelemetryAddresses))
            {
                UnstableTelemetryReadsRemaining--;
                result[2] = unchecked((byte)(result[0] + 1));
            }
            return result;
        }

        public void Write(EcWrite[] writes)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
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
            AppliedWriteBatches.Add(copy);
            foreach (EcWrite write in copy)
            {
                memory[write.Address] = write.Value;
                if (write.Address == 0x088b && write.Value == 0xff &&
                    AutoSystemOwnership)
                {
                    memory[0x0889] = 0xff;
                }
                if (write.Address == 0x088b && write.Value == 0 && AutoSystemRelease)
                {
                    memory[0x0889] = memory[0x0305];
                }
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
            return requestedCode;
        }

        public void Reset(F7bsdFan fan) => ResetCalls.Add(fan);

        public void Dispose() => DisposeCalls++;
    }
}
