# FanControl.MinisforumUM780XTX

A small, hardware-specific [Fan Control](https://github.com/Rem0o/FanControl.Releases)
plugin for the Minisforum UM780 XTX. It exposes the embedded controller's CPU
and system fan RPM, raw temperatures, and native closed-loop fan targets.

<img width="400" alt="image" src="https://github.com/user-attachments/assets/6715b427-6425-4321-a3f6-5a8bae6a38e7" />
<img width="350" alt="image" src="https://github.com/user-attachments/assets/33ade8d4-c7ec-4bf6-839c-daf673d00b4e" />

## Compatibility

The plugin loads only on the tested hardware profile:

- product `Venus series`;
- baseboard `F7BSD` revision `1.1`;
- BIOS `1.06`, EC `0.8`;
- IT5571 PNP identity `55 71 02`;
- controller profile `55 71 02 43 14 7f`; and
- a recognized BIOS CPU fan table and critical row `(51,100,93,0)`.

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

CPU control sets the same target in all seven normal temperature rows. The
plugin never changes the independent critical row, so firmware still requests
full speed at 94 C and above. It recognizes the BIOS-selected Default, Balance,
or Performance table and restores that exact table when raw control ends.

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

Pushes and pull requests to `master` are built automatically. To publish a
release, run the **Build and release** workflow from `master` and enter an
unused `X.Y.Z` version. It verifies the binary version, uploads the plugin ZIP,
and creates the matching `vX.Y.Z` GitHub release.

## Recovery

Disabling a control, refreshing the plugin, or exiting Fan Control normally
restores the selected BIOS CPU table and returns the system fan to firmware.

Force-terminating Fan Control, suspending or crashing Windows, or a complete
machine freeze can prevent that cleanup. On its next load, the plugin repairs an
exact leftover raw state or interrupted restoration sequence before exposing its
controls. This is a one-time handoff, not a background fan policy.

The CPU controller has no ownership marker, so automatic recovery relies on the
requirement that no other EC-writing utility runs concurrently. If the table or
override state is not an exact state this plugin can produce, it refuses to write;
fully power off the machine before accessing the controller again.
