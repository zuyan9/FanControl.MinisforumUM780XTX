# FanControl.MinisforumUM780XTX

A hardware-specific [Fan Control](https://github.com/Rem0o/FanControl.Releases)
plugin for the Minisforum UM780 XTX. It hash-pins the reviewed PawnIO,
LibreHardwareMonitor, and embedded LPC-module binaries used to access the
machine's IT5571 embedded controller (EC), and exposes:

- CPU and system fan RPM;
- CPU and system raw EC temperatures;
- a native CPU target compiled into the EC's B1 temperature bands with a
  thermal tail; and
- a guarded raw system-fan target control.

The plugin does not scan EC memory or attempt generic Minisforum support.

## Compatibility

Initialization succeeds only when all of these gates match:

| Property | Required value |
|---|---|
| Product | `Venus series` |
| Baseboard | `F7BSD` revision `1.1` |
| BIOS / SMBIOS EC | `1.06` / `0.8` |
| PNP identity | `55 71 02` |
| Live controller profile | `55 71 02 43 14 7f` |
| CPU profile | exact stock `B1` table |
| CPU critical row | `(51,100,93,0)` |
| System thresholds | `(25,83,100)` |

The reviewed build targets Fan Control V272 on Windows 11 x64 and .NET 10.
Other models, board revisions, BIOS versions, controller profiles, active
temperature overrides, and non-stock startup policy state are refused before a
fan-policy write.

## Controls

Fan Control percentages map linearly to native EC codes `0..51`. Code `n` is a
nominal `n * 100 RPM` value, but the two controls intentionally have different
semantics.

### CPU Fan Target (EC Thermal Tail)

ID: `minisforum.um780xtx.f7bsd.cpu-native-v3`

This is a native low-temperature request with an EC-resident thermal envelope,
not a flat RPM command and not an OEM B1 floor. Fan Control requests code
`0..51`; the plugin compiles that request into the seven mutable base/slope
fields of the immutable B1 temperature bands. Codes `0..10` intentionally
compile to the same physical policy: a nominal 1000-RPM minimum through 74 C,
then a thermal envelope of approximately 3000 RPM at 82 C, 4000 RPM at 88 C,
and 5100 RPM at 93 C. Higher requests raise the subcritical minimum. Because
this v3 policy follows the requested target plus that thermal envelope, it may
be below the OEM B1 target at some temperatures.

The independent B1 critical row `(51,100,93,0)` is never written and takes over
at 94 C and above. Every normal-row transition is planned byte by byte so each
issued prefix satisfies the conservative transition floor. The plugin verifies
the selector, immutable temperature bands, critical row, temperature override,
expected mutable table, and write readback in the same guarded transaction.

CPU table mutations are separated by at least one quiet second after the prior
transaction completes. Duplicate requests, including equivalent requests among
codes `0..10`, return without any EC I/O. A different request arriving inside
that interval is retained without EC I/O; the normal telemetry tick applies only
the latest request after the interval elapses. Reset and recovery are never
delayed by this limiter.

For a synchronous partial-write failure, recovery accepts only exact prefixes
of the transition the plugin actually issued, then restores the exact B1
mutable table. `Reset` and orderly `Close` also restore exact B1. Arbitrary
safe-looking EC RAM is not accepted as a new baseline.

### System Fan Raw Target

ID: `minisforum.um780xtx.f7bsd.system-raw-v2`

This is the verified raw fixed-target interface. The plugin engages the EC's
`0xff` temperature-selector sentinel and writes code `0..51` directly to the
system-fan target. Code `0` is exposed and may stop the fan. Fan Control owns
curves, hysteresis, mixing, minimum-running-speed, and start/stop policy.

The narrow user-mode guard does not replace Fan Control's policy:

- engagement and release allow the firmware selector up to 1.5 seconds, poll
  only its effective-temperature byte, and finish with one full state read;
- raw system temperature comes from `0x0305`, never from the sentinel-backed
  effective byte;
- firmware-owned telemetry omits the three system-ownership-only bytes; they
  are added to each sample only while raw system control is active;
- a low initial target is refused when raw temperature is invalid or at least
  70 C; code `51` remains available;
- while owned, each telemetry update verifies both sentinel bytes and the
  applied target before tachometer retry work;
- an invalid or at-least-70 C raw temperature promotes the target to code `51`
  once and does not automatically lower it after cooling;
- a later explicit Fan Control `Set` may lower it after temperature is valid and
  below 70 C; and
- ownership loss, target drift, invalid monotonic timing, or an update gap over
  four seconds triggers one bounded full-target/release attempt and faults the
  control session instead of reasserting forever; and
- three consecutive guarded telemetry failures release system ownership and
  stop periodic plugin telemetry until refresh.

Release seeds code `51`, clears the sentinel, and verifies that firmware has
resumed a plausible live temperature path. Firmware may immediately replace
the target after ownership returns, so release does not assert equality between
non-atomic live temperature samples or require the target to remain `51`.

The plugin never writes PWM/DCR actuator output, system temperature thresholds,
the CPU temperature-override byte, the CPU critical row, or firmware.

ISA access is serialized with Fan Control's global mutex, but that mutex is
cooperative. Software that ignores it can still race this plugin.

## Install

1. Install Fan Control V272 with PawnIO enabled.
2. Build the plugin or extract `FanControl.MinisforumUM780XTX.dll` from the
   release ZIP.
3. In Fan Control, use **Settings > Plugins > Install plugin...** and select the
   DLL.
4. Refresh sensors and configure the two paired controls.

Do not run another EC fan-control utility at the same time. Start with an
attended, known-running system-fan target before experimenting with lower codes.

The CPU `v3` ID intentionally prevents configurations made for the earlier
`cpu-minimum-v2` contract from binding silently. The unchanged system raw
contract retains its `system-raw-v2` ID.

A control transaction failure latches that individual Fan Control sensor until
plugin refresh, preventing one rejected curve output from becoming repeated EC
traffic. Reset and Close can still retry restoration while the control is
latched.

## Experimental status

The earlier experimental raw-control build coincided with several complete
freezes, including one black-screen incident while curves were being changed.
The pre-v2 incident evidence also included a LiveKernelEvent 141 report, which
is consistent with a GPU/display timeout but does not identify the initiating
cause. The available evidence neither proved that the plugin caused the freezes
nor excluded raw EC traffic provoking an EC/firmware deadlock.

Version 3.0.1 has now passed attended CPU-only, system-only, combined, real
curve, CPU-load, and display-load stages on this exact machine. The campaign
also exposed and corrected the false system-selector timeout described below.
These tests reduce uncertainty; they do not prove that generic user-mode EC
traffic can never deadlock firmware or that an unrelated GPU/platform fault
cannot recur. Save work and test attended. Live system code `0`, an actual 70 C
system-fan trip, forced termination, OS crash, sleep/resume, multi-hour
operation, and sustained high-temperature CPU-tail operation have not been
exercised on Windows.

The extracted BIOS 1.06 EC image and the firmware routines behind both controls
are documented in [UM780 XTX EC firmware analysis](docs/EC_FIRMWARE_ANALYSIS.md).
That static analysis validates the CPU row arithmetic and system sentinel
semantics, while separating them from the remaining raw indexed-port transport
risk.

## Build and test

Install the .NET 10 SDK and Fan Control, then run:

```powershell
dotnet build -c Release
dotnet run --project .\tests\FanControl.MinisforumUM780XTX.Tests.csproj -c Release
```

If Fan Control is not installed in `C:\Program Files (x86)\FanControl`, pass its
directory explicitly:

```powershell
dotnet build -c Release `
  "-p:FanControlDir=C:\path\to\FanControl_272_net_10_0"
```

The offline suite checks every v3 CPU request over both thermal paths, all
`52 x 52 x 7` compiled-policy transitions, one-second mutation coalescing,
zero-I/O equivalent requests, interruption of both the original write and its
direct exact-prefix recovery, non-prefix rejection, exact B1 reset, EC address
allowlists, parking and poisoning behavior, bounded system ownership/release,
cross-fan fault isolation, thermal and timing boundaries, drift, cleanup retry,
telemetry ordering, and plugin exception containment.

## Hardware validation status

Version 3.0.1 was tested inside the actual Fan Control V272 executable on the
exact UM780 XTX profile above. The campaign discovered a genuine defect in the
previous build: a 500 ms system-selector wait could report a false timeout
while the EC remained responsive, and the resulting shared fault gate could
also disable CPU control after a verified system release.

Version 3.0.1 allows the selector up to 1.5 seconds, polls only its
effective-temperature byte, performs one final full-state verification, and
keeps CPU control independent unless system cleanup is genuinely pending. A
direct live-hardware A/B produced one false release timeout in 12 cycles before
the fix and none in 12 cycles afterward; all 24 following stock audits passed.

The post-fix build then completed, through Fan Control itself:

- 10 CPU-first and five system-first fresh combined engagements and releases;
- 180 seconds of `CPU 10 / system 30` all-core load, with both controls exact
  in all 200 monitored hold samples;
- 40 rapid combined-transition stages, including 120 seconds of overlapping
  all-core load;
- manual CPU codes 18, 14, and 10 and system codes 51, 30, 25, 20, 15, and 10;
- 60-second CPU-only and system-code-10 load stages;
- a completed 360-sample real graph-curve run with 240 seconds of all-core
  load; and
- valid 30-second windowed and 60-second fullscreen WinSAT DWM stages while
  real curves and CPU load were active.

The curve-only run exercised 13 confirmed CPU control levels and two system
levels. Two mixed stages stopped exactly at configured 65 C and 69 C system
limits; Fan Control stayed responsive and both controls were verified null
afterward. A five-second cooperative ISA-mutex hold with safe controls active
also produced the expected bounded fault, then recovered through disable and
refresh.

No whole-machine freeze or black-screen incident occurred. All 41 independent
stock audits passed. The final event/dump query found no new WHEA, display
reset, BugCheck, Kernel-Power, relevant application error, LiveKernelEvent, or
dump in the curve/display/fault-test window.

The complete matrix, expected thermal stops, excluded pilot runs, final state,
and residual risks are recorded in the
[real Fan Control hardware campaign](diagnostics/hardware-results/2026-08-04-real-fan-control/CAMPAIGN.md).
These tests materially reduce uncertainty but do not prove that a rare
firmware, raw indexed-port, GPU, or platform failure cannot recur. A 24-72 hour
representative-use soak, sleep/resume, forced termination, live system code 0,
and an actual system thermal-failsafe trip remain untested.

## Recovery and user-mode limit

When Fan Control invokes `Reset` or `Close` (normally on disable, refresh, or an
orderly exit), the plugin attempts restoration. The backend keeps CPU and system
cleanup independent, retains uncertain recovery latches, and allows bounded
cleanup retry through Reset or Close. It does not silently recapture a modified
CPU table as a new baseline.

The source verifies exact SHA-256 hashes for `PawnIO.sys`, `PawnIOLib.dll`,
`LibreHardwareMonitorLib.dll`, and the embedded generic `LpcIO` blob. It does not
itself validate Authenticode signer provenance; Windows driver-signing
enforcement is separate. The generic PawnIO path performs port I/O in the
driver, but transaction policy, supervision, and recovery remain in this
user-mode plugin. It cannot execute its guard or cleanup while Fan Control is
hung, forcibly killed, Windows is frozen, the machine is suspended, or the OS
crashes. A forced stop can therefore leave the compiled CPU table or system
fixed target active in volatile EC RAM, including a zero system target. If Fan
Control terminates abnormally or restoration cannot be confirmed, reboot before
accessing the EC again.

A future, separately reviewed and properly signed F7BSD-specific PawnIO module
could collapse a complete parked EC transaction into one driver call and make a
best-effort sentinel release when its client handle closes. The installed
PawnIO loader does not accept an ad-hoc unsigned board module, so that work also
requires a trusted signing and distribution path and is not part of v3. Such a
module could narrow user-mode death and transaction-race windows; it would not
provide exclusive ownership against software that ignores the ISA mutex or
guarantee cleanup while the kernel, EC firmware, or hardware itself is frozen
or power is lost.
