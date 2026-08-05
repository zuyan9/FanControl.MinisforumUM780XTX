# FanControl.MinisforumUM780XTX

A hardware-specific [Fan Control](https://github.com/Rem0o/FanControl.Releases)
plugin for the Minisforum UM780 XTX. It hash-pins the reviewed PawnIO,
LibreHardwareMonitor, and embedded LPC-module binaries used to access the
machine's IT5571 embedded controller (EC), and exposes:

- CPU and system fan RPM;
- CPU and system raw EC temperatures;
- a native CPU cool-stop target compiled into the EC's B1 temperature bands
  with an autonomous thermal tail; and
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

### CPU Fan Target (Cool-Stop Thermal Tail)

ID: `minisforum.um780xtx.f7bsd.cpu-cool-stop-v4`

This is a native low-temperature request with an EC-resident thermal envelope,
not a flat RPM command and not an OEM B1 floor. Fan Control requests code
`0..51`; the plugin compiles that request into the seven mutable base/slope
fields of the immutable B1 temperature bands. Every code is a distinct physical
policy. Code `0` is a genuine zero target on the heating path through 66 C;
on the cooling path the row hysteresis retains code `10` at 66 C and returns to
zero below it. Repeated live tests reached a physical 0 RPM and restarted
reliably. On heating, the autonomous envelope requests the proven sustainable code 10
beginning at 67 C, rises to approximately 1500 RPM at 76 C, then reaches 3000
RPM at 82 C, 4000 RPM at 88 C, and 5100 RPM at 93 C. A higher Fan Control
request raises that subcritical minimum. Because this v4 policy follows the
requested target plus that thermal envelope, it may be below the OEM B1 target
at some temperatures. Explicit raw codes `1..9` remain available, but this fan
can hunt or stall at those targets; a practical stop/start curve should jump
from code `0` to about code `10`.

The independent B1 critical row `(51,100,93,0)` is never written and takes over
at 94 C and above. Every normal-row transition is planned byte by byte so each
issued prefix satisfies the conservative transition floor. The plugin verifies
the selector, immutable temperature bands, critical row, temperature override,
expected mutable table, and write readback in the same guarded transaction.

The firmware changes at most one temperature row per policy invocation. From a
stale coolest-row state, an abrupt 93 C sample therefore produces targets
`0,0,0,23,50,51` over six invocations; from the realistic 54..66 C idle row it
produces `23,50,51`. The normal 0-RPM-to-load path has been qualified on this
machine, but an instantaneous 93 C jump and sleep/resume remain separate cases.

Every distinct CPU request is applied synchronously and reported only after the
guarded table transaction completes. Exact duplicate requests return without EC
I/O. There is no plugin-side rate limiter or deferred write on the telemetry
path; Fan Control owns output cadence, command stepping, curves, hysteresis,
mixing, and start/stop policy.

For a synchronous partial-write failure, recovery accepts only exact prefixes
of the transition the plugin actually issued, then restores the exact B1
mutable table. `Reset` and orderly `Close` also restore exact B1. Arbitrary
safe-looking EC RAM is not accepted as a new baseline. Restoration still
requires plausible raw/effective temperatures, override zero, the exact B1
immutable profile, and an exact issued-table certificate; it deliberately does
not reject a transient out-of-range firmware-owned target byte caused by stale
row arithmetic after a large cooling/resume jump.

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

The CPU `v4` ID intentionally prevents configurations made for the earlier v2
minimum or v3 1000-RPM-floor contracts from binding silently. The unchanged
system raw contract retains its `system-raw-v2` ID.

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

Version 4.0.0 has passed direct stop/restart, autonomous zero-to-load recovery,
high-rate CPU transaction, real Fan Control dual-control, real curve, and
all-core-load stages on this exact machine. No freeze, black screen, plugin
fault, relevant Windows event, or new live-kernel dump occurred. These tests
reduce uncertainty; they do not prove that generic user-mode EC traffic can
never deadlock firmware or that an unrelated GPU/platform fault cannot recur.
Save work and test attended. Live system code `0`, an actual 70 C system-fan
trip, forced termination, OS crash, sleep/resume, multi-hour operation, and an
instantaneous jump into the high-temperature CPU tail remain untested.

The stabilized table model covers normal heating/cooling and exact adjacent-row
crossings. A large discontinuous cooling event while the firmware row remains
several bands high can exercise unsigned out-of-band subtraction until the EC
steps back through those rows. Reset/Close can now restore exact B1 during that
firmware-owned target transient, but sleep/resume behavior itself still needs
live qualification.

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

The offline suite checks every v4 CPU request over both thermal paths, one-row
firmware heating transients, all `52 x 52 x 7` compiled-policy transitions,
immediate distinct requests, zero-I/O duplicate requests, interruption of both
the original write and its direct exact-prefix recovery, non-prefix rejection,
exact B1 reset, EC address
allowlists, parking and poisoning behavior, bounded system ownership/release,
cross-fan fault isolation, thermal and timing boundaries, drift, cleanup retry,
telemetry ordering, and plugin exception containment.

## Hardware validation status

The exact v4.0.0 DLL was tested directly and inside Fan Control V272 on the
machine above. Direct validation completed 515 synchronous CPU Set calls (510
distinct EC table mutations) in 7.7 seconds; latency across the calls was 13.6
ms mean, 15.6 ms p95, and 45.8 ms maximum. The cold first request completed in
49.8 ms. Three stop/restart cycles reached
0 RPM and restarted, and the autonomous load test changed target `0` to `10`
at 67 C, began reporting RPM about one second later, and sustained four
consecutive samples above 800 RPM while load continued.

Through Fan Control itself, the build completed 18 manual lifecycle stages,
30 rapid combined CPU/system transitions, 120 curve samples with 60 seconds of
all-core load, and a 180-sample migrated-user-curve soak with 120 seconds of
all-core load. The final soak kept CPU at 63..84 C / 1006..3494 RPM and system
at 57..60 C / 925..1891 RPM; Fan Control responded with complete telemetry in
every sample. Two orderly exits and one clean relaunch passed, every cleanup
verified both controls disabled, and the final independent stock audit passed.

No whole-machine freeze, black screen, plugin error, WHEA/display/BugCheck/
Kernel-Power event, relevant application event, or new LiveKernelReport
occurred. The exact results, deployment hash, final state, and residual risks
are recorded in the
[v4 cool-stop hardware campaign](diagnostics/hardware-results/2026-08-04-v4-cool-stop/CAMPAIGN.md).
The earlier system-selector correction and broader display-load matrix remain
documented in the
[v3 real Fan Control campaign](diagnostics/hardware-results/2026-08-04-real-fan-control/CAMPAIGN.md).
Neither campaign can prove that a rare firmware, raw indexed-port, GPU, or
platform failure will never recur. A 24-72 hour representative-use soak,
sleep/resume, forced termination, live system code `0`, and an actual system
thermal-failsafe trip remain untested.

A follow-up reproduction campaign then exercised 24 acknowledged dynamic CPU
profile transitions, including 12 consecutive changes during 96.0..98.2% iGPU
load. Every sequence and exact control readback passed. The longest workload
was stopped after the completed sequence by its conservative 69 C system guard;
there was no freeze, black screen, EC deadlock, relevant event, or new dump.
See the
[A5 dynamic-control reproduction campaign](diagnostics/hardware-results/2026-08-04-reproduction/CAMPAIGN.md)
for the raw-wrapper classification, GPU overlap, final restoration, and
remaining limitations.

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
requires a trusted signing and distribution path and is not part of v4. Such a
module could narrow user-mode death and transaction-race windows; it would not
provide exclusive ownership against software that ignores the ISA mutex or
guarantee cleanup while the kernel, EC firmware, or hardware itself is frozen
or power is lost.
