# FanControl.MinisforumUM780XTX

A small [FanControl](https://github.com/Rem0o/FanControl.Releases) plugin for
the Minisforum UM780 XTX. It uses FanControl's signed PawnIO stack to access the
machine's IT5571 embedded controller (EC) and exposes:

- CPU and system fan RPM;
- CPU and system EC temperatures;
- a CPU closed-loop target backed by a flat native seven-row curve; and
- a system closed-loop target backed by the EC's fixed-target selector.

<img width="400" alt="image" src="https://github.com/user-attachments/assets/6715b427-6425-4321-a3f6-5a8bae6a38e7" />
<img width="350" alt="image" src="https://github.com/user-attachments/assets/33ade8d4-c7ec-4bf6-839c-daf673d00b4e" />

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
| CPU critical row | `(51,100,93,0)` |

The current implementation targets FanControl V272 on Windows 11 x64. Other
Minisforum models, board revisions, BIOS versions, and EC profiles are refused
before any fan-policy byte is written.

## Control behavior

Both controls expose the complete verified EC target range to FanControl. A
FanControl request from 0–100% maps linearly to EC code `0–51`, nominally
0–5100 RPM in 100-RPM steps. These are requested closed-loop targets, not
guaranteed physical speeds: the EC still measures the tachometer and drives the
fan. FanControl should own calibration, curves, hysteresis, mixing, minimum
running speed, and start/stop behavior.

The plugin intentionally adds no thermal floor and does not quantize requests
into preset modes. In particular, code `0` is exposed and may stop a fan.

### CPU fan

The EC normally selects a target from seven temperature rows. For raw control,
the plugin sets all seven normal rows to the requested code with zero slope,
making their target flat across temperature. It never writes the independent
CPU critical row `(51,100,93,0)`, which remains firmware-owned. The 14 writable
normal-row bytes are captured at initialization and restored when control ends;
the plugin does not require a hard-coded stock CPU curve.

### System fan

On the first control request, the plugin engages the verified `0xff`
fixed-target selector and writes the requested code directly to the system-fan
target. Later requests update that target without changing the firmware's
temperature thresholds. FanControl calls `Set` on every update, so each call
also re-engages the selector if necessary and reasserts the raw target; the
plugin does not run a separate fan-policy maintenance loop.

This is the complete useful system-fan command surface discovered for this EC.
The plugin does not write PWM/DCR output: those bytes are the EC controller's
continuously updated actuator output, not a stable command interface.

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

Disabling a control invokes its FanControl `Reset` callback. A normal plugin
refresh or orderly FanControl exit also invokes reset/close handling. The plugin
uses those callbacks to restore the captured CPU table and return the system
fan from fixed-target mode to firmware control. System release first seeds a
full target, clears the fixed-target sentinel, and verifies that the live
temperature selector owns the target again.

A forced process termination cannot run those callbacks. It may therefore
leave the last CPU target or system fixed target active in volatile EC RAM,
including a zero target. If FanControl is killed, Windows crashes, or recovery
cannot be confirmed, reboot before reopening FanControl so the firmware reloads
its baseline.

The plugin remains exact-hardware-only: it verifies machine and live-controller
identity, serializes ISA access with the global mutex, parks the selector, uses
a fixed address allowlist, and verifies writes by reading them back. It never
writes fan PWM/DCR registers, the CPU temperature-override byte, the CPU
critical row, or firmware. It writes the system temperature-override byte only
as `0xff` to engage raw fixed-target control and `0` to release it.
