# FanControl.MinisforumUM780XTX

A small [FanControl](https://github.com/Rem0o/FanControl.Releases) plugin for
the Minisforum UM780 XTX. It uses FanControl's signed PawnIO stack to access the
machine's IT5571 embedded controller (EC) and exposes:

- CPU and system fan RPM;
- CPU and system EC temperatures;
- a CPU fan control backed by the native seven-row curve; and
- a system fan control backed by the firmware's three native states.

The plugin is deliberately specific to the hardware and firmware combination
on which the register map was verified. It does not scan EC memory or attempt
generic Minisforum support.

## Compatibility

Initialization succeeds only when every gate matches:

| Property | Required value |
|---|---|
| Product | `Venus series` |
| Baseboard | `F7BSD` revision `1.1` |
| BIOS / SMBIOS EC | `1.06` / `0.8` |
| Live controller profile | `55 71 02 43 14 7f` |
| CPU profile | firmware baseline `0`, `b1`, or `b2` |
| System thresholds | stock `(25,83,100)` |
| CPU critical row | `(51,100,93,0)` |

The current implementation targets FanControl V272 on Windows 11 x64. Other
Minisforum models, board revisions, BIOS versions, and EC profiles are refused
before any fan-policy byte is written.

## Control behavior

### CPU fan

FanControl's 0–100% request maps to a 0–5100 RPM requested target, but the EC
curve compiler always applies this independent thermal floor:

| CPU temperature | Minimum target |
|---|---:|
| through 74 C | 1000 RPM |
| 82 C | 3000 RPM |
| 88 C | 4000 RPM |
| 93 C | 5100 RPM |
| 94 C and above | firmware critical row, 5100 RPM |

Consequently, 0% does not stop the CPU fan. The plugin changes only the seven
normal base/slope pairs. It computes a byte-at-a-time transition whose every
intermediate curve stays at or above the lower of the old and new targets,
verifies every write and the final aggregate, and never writes the critical
row. A transition may conservatively evaluate to code 52 for one step to avoid
an undershoot; normal and final curves remain capped at code 51.

### System fan

The system fan has no crash-safe arbitrary-RPM handoff on Windows. This first
version therefore quantizes FanControl's request to the closest native state:

| Displayed control | Normal-temperature target | Full-speed transition |
|---:|---:|---:|
| 0% | off | 70 C |
| 39.2% | about 2000 RPM | 70 C |
| 100% | about 5100 RPM | already full |

In practice, requests up to roughly 18% select off, 19–69% select quiet, and
70–100% select full. The off and quiet policies retain a firmware-owned 5100
RPM branch from 70 C, even if FanControl exits unexpectedly.

If the system-fan temperature byte is invalid, the plugin forces and retains
the full-speed state instead of restoring a policy that could stop the fan.

The verified `0xff` fixed-target sentinel is intentionally not used: it would
disable firmware target selection and cannot be cleared by an in-process plugin
after a forced FanControl termination.

## Install

1. Install and start FanControl with PawnIO enabled.
2. Build the plugin or extract `FanControl.MinisforumUM780XTX.dll` from the
   release ZIP.
3. In FanControl, use **Settings > Plugins > Install plugin...** and select the
   DLL.
4. Let FanControl refresh its sensors, then calibrate both paired controls.

Do not run another EC fan-control utility at the same time.

## Build and test

Install the .NET 10 SDK and FanControl, then run from this directory:

```powershell
dotnet build -c Release
dotnet run --project .\tests\FanControl.MinisforumUM780XTX.Tests.csproj -c Release
```

If FanControl is not installed in `C:\Program Files (x86)\FanControl`, pass its
directory explicitly:

```powershell
dotnet build -c Release `
  "-p:FanControlDir=C:\path\to\FanControl_272_net_10_0"
```

## Recovery and safety

Disabling a control restores that fan's captured firmware baseline when the
live EC identity, override bytes, and sensor state remain valid. A normal plugin
refresh or FanControl exit attempts to restore both. If safe restoration cannot
be proved, the plugin reports the failure and leaves the safer active policy in
place. A forced process termination can leave the CPU curve and/or system mode
in volatile EC RAM; restart Windows before reopening FanControl so firmware
reloads its configured baseline.

The plugin never writes fan PWM/DCR registers, either temperature override,
the CPU critical row, or firmware. PawnIO access uses the global ISA mutex,
the validated slot 0 / `0x2e` transport, a fixed address allowlist, live profile
revalidation, exact reviewed PawnIO/LibreHardwareMonitor hashes, selector
parking, immediate read-back, and aggregate verification.
