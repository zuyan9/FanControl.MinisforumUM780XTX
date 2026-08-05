# FanControl.MinisforumUM780XTX

A small, hardware-specific [Fan Control](https://github.com/Rem0o/FanControl.Releases)
plugin for the Minisforum UM780 XTX. It exposes the embedded controller's CPU
and system fan RPM, raw temperatures, and native closed-loop fan targets.

## Compatibility

The plugin loads only on the tested hardware profile:

- product `Venus series`;
- baseboard `F7BSD` revision `1.1`;
- BIOS `1.06`, EC `0.8`;
- IT5571 PNP identity `55 71 02`;
- controller profile `55 71 02 43 14 7f`; and
- exact stock CPU B1 table with critical row `(51,100,93,0)`.

It targets Windows x64, .NET 10, Fan Control V272, and PawnIO API 2.0.

## Sensors and controls

| ID | Kind |
|---|---|
| `cpu-rpm` | CPU fan speed |
| `system-rpm` | System fan speed |
| `cpu-temperature` | Raw EC CPU temperature |
| `system-temperature` | Raw EC system temperature |
| `cpu-control` | CPU fan target |
| `system-control` | System fan target |

Fan Control percentages map linearly to EC codes `0..51`, nominally
`0..5100 RPM`. There are no plugin-side minimums, curves, thermal promotions,
or rate limits. Fan Control owns that policy.

CPU control sets the same target in all seven normal B1 temperature rows. The
plugin never changes the independent critical row, so firmware still requests
full speed at 94 C and above.

System control uses the firmware's `0xff` fixed-target handoff. While that
handoff is active, firmware has no automatic system-temperature fallback. On
Reset or a clean Close, the plugin seeds full speed, clears the handoff, and
verifies that firmware control resumed.

The plugin never writes fan PWM/DCR outputs, CPU temperature override, CPU
temperature bands, the CPU critical row, system temperature thresholds, or
firmware.

## Install

Install Fan Control with PawnIO enabled, download or build
`FanControl.MinisforumUM780XTX.dll`, then select it under
**Settings > Plugins > Install plugin...**. Do not run another EC or fan-control
utility at the same time.

The short sensor and control IDs are intentionally incompatible with earlier
experimental releases. Recreate existing Fan Control bindings after upgrading.

## Build

Install the .NET 10 SDK and Fan Control, then run:

```powershell
dotnet build -c Release
```

For a non-default Fan Control location or a versioned build:

```powershell
dotnet build -c Release `
  "-p:FanControlDir=C:\path\to\FanControl" `
  -p:Version=0.2.0
```

## Recovery limitation

Disabling a control, refreshing the plugin, or exiting Fan Control normally
restores the captured CPU B1 table and returns the system fan to firmware.
Force-terminating Fan Control, suspending or crashing Windows, or a complete
machine freeze can prevent cleanup and leave a raw target in volatile EC RAM.
After any uncontrolled termination, reboot before reopening Fan Control or
using another EC utility.
