# FanControl.MinisforumUM780XTX

A hardware-specific [Fan Control](https://github.com/Rem0o/FanControl.Releases)
plugin for the Minisforum UM780 XTX. It hash-pins the reviewed PawnIO,
LibreHardwareMonitor, and embedded LPC-module binaries used to access the
machine's IT5571 embedded controller (EC), and exposes:

- CPU and system fan RPM;
- CPU and system raw EC temperatures;
- a native fixed CPU-fan target using the EC's stock closed-loop RPM control;
  and
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

### CPU Fan Raw Target

ID: `minisforum.um780xtx.f7bsd.cpu-raw-v1`

Fan Control requests native code `0..51`. The plugin writes that code as the
base of all seven normal B1 temperature rows and sets all seven normal slopes to
zero. The EC therefore retains one fixed closed-loop target at every
subcritical temperature; the plugin does not add a temperature floor or hidden
curve. Code `0` is a genuine stop request. Codes `1..9` are also exposed
unchanged, although the physical fan can stall or hunt below its sustainable
running speed. Fan Control owns curves, hysteresis, mixing, minimum-running
speed, start/stop behavior, and command cadence.

The independent B1 critical row `(51,100,93,0)` is never written and takes over
at 94 C and above. That firmware takeover is the only temperature-based CPU
override retained by the plugin. Preventing it would require writing the
critical row and is intentionally unsupported.

Every normal-table update is a short deterministic sequence: zero a differing
slope, write the requested base, then write a nonzero destination slope only
when restoring exact B1. The plugin verifies the selector, immutable
temperature bands, critical row, temperature override, expected mutable table,
and final readback in the same guarded transaction. It never writes the
firmware-owned live target byte.

Every distinct CPU request is applied synchronously and reported only after the
guarded table transaction completes. Exact duplicate requests return without EC
I/O. There is no plugin-side rate limiter or deferred write on the telemetry
path; Fan Control owns output cadence, command stepping, curves, hysteresis,
mixing, and start/stop policy.

Duplicate suppression deliberately trusts the plugin's exclusive-ownership
session instead of rereading the fourteen mutable bytes on every Fan Control
tick. Do not use another EC writer concurrently. After sleep/resume or any
suspected firmware reinitialization, refresh the plugin before trusting an
unchanged displayed request; sleep/resume remains unqualified.

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
   ZIP on [GitHub Releases](https://github.com/zuyan9/FanControl.MinisforumUM780XTX/releases).
3. In Fan Control, use **Settings > Plugins > Install plugin...** and select the
   DLL.
4. Refresh sensors and configure the two paired controls.

Do not run another EC fan-control utility at the same time. Start with an
attended, known-running system-fan target before experimenting with lower codes.

The `cpu-raw-v1` ID intentionally prevents curves made for the earlier
temperature-dependent CPU contracts from binding silently. Re-select the new
CPU control after upgrading. The unchanged system contract retains its
`system-raw-v2` ID.

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

The previous temperature-dependent build passed direct stop/restart, high-rate
CPU transaction, real Fan Control dual-control, curve, CPU-load, and repeated
iGPU-load stages on this exact machine. A separate Cinebench failure produced a
Windows `0x141` AMD display-engine timeout; later repeated runs completed both
with Fan Control off and with more aggressive cooling. These results reduce
uncertainty but do not prove that generic user-mode EC traffic can never
deadlock firmware or that an unrelated GPU/platform fault cannot recur. Save
work and test experimental builds attended.

With raw control active, every normal row has the same base and zero slope, so
heating, cooling, and row hysteresis cannot alter the requested subcritical
target. After exact B1 restoration, stock firmware row behavior resumes.
Reset/Close can restore exact B1 during a firmware-owned target transient, but
sleep/resume and abnormal termination still need separate qualification.

During preliminary `cpu-raw-v1` qualification at native code `10`, a Cinebench
GPU run reproduced the same AMD `0x141` display-engine timeout seen in the
earlier incidents. An AMD xHCI `0x144` host-controller error followed about
three seconds later and the machine reset. CPU package temperature peaked at
86.4 C, the requested CPU target and fresh EC telemetry remained valid, and a
post-reboot read-only audit found exact stock fan state. The recurring GPU
failure signature spans multiple boots and at least two AMD driver images, so
it does not specifically implicate this build; it also means this build has not
yet passed a long GPU soak. Further testing should use controlled plugin-off,
telemetry-only, and raw-target comparisons rather than an unattended low-fan
campaign.

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

To assign a version to a local build, pass the same `X.Y.Z` value used for a
release:

```powershell
dotnet build -c Release `
  "-p:FanControlDir=C:\path\to\FanControl_272_net_10_0" `
  -p:Version=0.2.0
```

### GitHub Actions releases

Pushes and pull requests to the default branch run the complete hardware-free
build, diagnostics validation, test, version-verification, and packaging flow.
Each run retains `FanControl.MinisforumUM780XTX.zip` as a workflow artifact.

After the workflow is present on the repository's default branch, a maintainer
can publish a release from **Actions > Build and release > Run workflow**:

1. Select the default branch.
2. Enter a new stable version in `X.Y.Z` form, without a leading `v`.
3. Run the workflow.

The workflow performs the same checks, verifies that the DLL reports the
requested version, creates tag and release `vX.Y.Z` at the exact tested commit,
and attaches the one-DLL ZIP. An existing tag is rejected. The equivalent
GitHub CLI command is:

```powershell
gh workflow run build.yml --ref master -f version=0.2.0
```

The offline suite checks every raw CPU request over both thermal paths, one-row
firmware transitions, all flat-target/B1 table transitions and their exact
prefixes,
immediate distinct requests, zero-I/O duplicate requests, interruption of both
the original write and its deterministic exact-prefix recovery, non-prefix rejection,
exact B1 reset, EC address
allowlists, parking and poisoning behavior, bounded system ownership/release,
cross-fan fault isolation, thermal and timing boundaries, drift, cleanup retry,
telemetry ordering, and plugin exception containment.

## Hardware validation status

The completed campaigns below exercised the predecessor v4 cool-stop policy,
not the current `cpu-raw-v1` table. They remain relevant to the shared
transport, transaction, restoration, and Fan Control integration paths. The
exact prior v4.0.0 DLL was tested directly and inside Fan Control V272 on the
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
requires a trusted signing and distribution path and is not part of this
plugin. Such a module could narrow user-mode death and transaction-race
windows; it would not provide exclusive ownership against software that ignores
the ISA mutex or
guarantee cleanup while the kernel, EC firmware, or hardware itself is frozen
or power is lost.
