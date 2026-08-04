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

- engagement and release use at most six polls, with no sleep after the final
  poll;
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

The v3 CPU policy has now passed the attended stages described below on this
exact machine. Those short stages reduce uncertainty; they do not prove that
generic user-mode EC traffic can never deadlock firmware or that an unrelated
GPU/platform fault cannot recur. Save work and test attended. Live system code
`0`, an induced 70 C system-fan trip, forced termination, OS crash,
sleep/resume, multi-hour operation, and sustained high-temperature CPU-tail
operation have not been exercised on Windows.

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

The 46-test offline suite checks every v3 CPU request over both thermal paths, all
`52 x 52 x 7` compiled-policy transitions, one-second mutation coalescing,
zero-I/O equivalent requests, interruption of both the original write and its
direct exact-prefix recovery, non-prefix rejection, exact B1 reset, EC address
allowlists, parking and poisoning behavior, bounded system ownership/release,
thermal and timing boundaries, drift, cleanup retry, telemetry ordering, and
plugin exception containment.

## Hardware validation status

The prior CPU-minimum-v2/system-raw-v2 staged build was exercised on the exact
UM780 XTX profile above:

- repeated read-only identity, profile, telemetry, and stock-state audits;
- CPU floor codes `0`, `28`, and `29`, including `28 -> 29 -> 28` and a
  30-second code-28 hold;
- system raw code `51` and code `30` for 10 seconds each;
- the plugin API/control-sensor harness at CPU code `28` and system code `30`;
  and
- that same harness with both controls together at CPU code `28` and system
  code `30`.

Every completed stage was followed by an independent stock-state audit. The
combined runs held CPU at 57-70 C and system at 51-52 C; no WHEA, display-reset,
Kernel-Power, relevant application-crash, or new dump event appeared in the
validation window. This is validation evidence, not a guarantee against every
firmware or operating-system failure.

The `cpu-native-v3` build was then validated CPU-only, with the system fan left
firmware-owned throughout:

- isolated native target codes `18`, `16`, `14`, `12`, and `10`, descending
  from 1800 to 1000 RPM requests, with a fresh exact-B1 audit after every run;
- `18 -> 16 -> 14 -> 12 -> 10 -> 12 -> 18` transitions at five-second
  intervals;
- rapid plugin callback changes in both directions, confirming that each burst
  coalesced to its final endpoint after the one-second quiet interval;
- a 120-second static code-10 soak, settling around 995-1077 RPM at 61-69 C;
  and
- a delayed final exact-B1 audit, which observed the CPU fan back at 2563 RPM.

The system fan stayed under firmware control around 2053-2073 RPM while its raw
temperature remained 53-57 C. CPU mutation transactions completed in roughly
29-32 ms. Every stock restoration and post-stage audit passed, with no new
WHEA, display-reset, Kernel-Power, relevant Windows Error Reporting, or
LiveKernelReport evidence in either validation window. These are short
functional and stability checks, not a guarantee against a rare firmware or
platform failure during longer real-world use. The callback-coalescing stage
used the real plugin and hardware backend inside the diagnostic host; the saved
curve has not yet been enabled in the Fan Control executable for a long-running
session.

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
