# FanControl.MinisforumUM780XTX

A hardware-specific [Fan Control](https://github.com/Rem0o/FanControl.Releases)
plugin for the Minisforum UM780 XTX. It hash-pins the reviewed PawnIO,
LibreHardwareMonitor, and embedded LPC-module binaries used to access the
machine's IT5571 embedded controller (EC), and exposes:

- CPU and system fan RPM;
- CPU and system raw EC temperatures;
- an OEM-safe CPU minimum/floor control; and
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

### CPU Fan Minimum (OEM Floor)

ID: `minisforum.um780xtx.f7bsd.cpu-minimum-v2`

This is a minimum floor over the stock B1 temperature policy, not a flat raw
target. Code `0` reproduces exact B1 behavior. Higher codes compile a new
seven-row table that is never below either B1 or the requested floor at any
temperature. The independent critical row remains untouched.

Every row transition is planned byte by byte so all intermediate prefixes stay
B1-safe and never fall below the less-aggressive endpoint. The plugin verifies the selector,
temperature bands, critical row, temperature override, expected mutable table,
and write readback in the same guarded transaction. Reset and orderly close
restore exact B1.

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

The `v2` control IDs intentionally prevent configurations made for the earlier
experimental raw-control build from binding silently.

A control transaction failure latches that individual Fan Control sensor until
its `Reset` callback or plugin refresh, preventing one rejected curve output
from becoming repeated EC traffic.

## Experimental status

The earlier experimental raw-control build coincided with several complete
freezes, including a black screen, while curves were being changed. Available
logs did not establish causality, so an EC/firmware deadlock remains possible
rather than proven. This v2 build changes CPU control to an OEM floor, parks the
selector after every byte, bounds and narrows retries, caches verified duplicate
commands, and faults instead of continuously reasserting unexpected state.

Validation so far is staged and short, not a long curve-churn or soak test. Save
work and test attended. Live system code `0`, an induced 70 C trip, forced
termination, OS crash, sleep/resume, and long-duration rapidly changing curves
have not been exercised on Windows.

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

The offline suite exhaustively checks all `52 x 52 x 7` CPU policy transitions,
partial-write recovery, EC address allowlists, parking and poisoning behavior,
bounded system ownership/release, thermal and timing boundaries, drift, cleanup
retry, telemetry ordering, and plugin exception containment.

## Hardware validation

The staged build was exercised on the exact UM780 XTX profile above:

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

## Recovery and user-mode limit

When Fan Control invokes `Reset` or `Close`—normally on disable, refresh, or an
orderly exit—the plugin attempts restoration. The backend keeps CPU and system
cleanup independent, retains uncertain recovery latches, and allows bounded
cleanup retry through Reset or Close. It does not silently recapture a modified
CPU table as a new baseline.

The source verifies exact SHA-256 hashes for `PawnIO.sys`, `PawnIOLib.dll`,
`LibreHardwareMonitorLib.dll`, and the embedded `LpcIO` blob. It does not itself
validate Authenticode signer provenance; Windows driver-signing enforcement is
separate. This remains a user-mode plugin. It cannot execute its guard or
cleanup while Fan Control is hung, forcibly killed, Windows is frozen, the
machine is suspended, or the OS crashes. A forced stop can therefore leave the
CPU floor table or system fixed target active in volatile EC RAM, including a
zero system target. If Fan Control terminates abnormally or restoration cannot
be confirmed, reboot before accessing the EC again.

Stronger fail-safe behavior for user-mode death or missed heartbeats requires a
board-specific kernel driver with exclusive ownership, a worker, the same
narrow allowlist and temperature gate, and full-target/sentinel-clear logic in
handle cleanup. No driver can guarantee cleanup while the kernel, firmware, or
hardware itself is frozen or power is lost.
