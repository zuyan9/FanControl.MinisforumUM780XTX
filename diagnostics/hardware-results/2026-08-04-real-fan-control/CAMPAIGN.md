# Real Fan Control hardware campaign

Date: 2026-08-04 (America/Los_Angeles)

- Machine: Minisforum UM780 XTX, F7BSD 1.1, BIOS 1.06, EC 0.8
- Host: Windows 11 x64, Fan Control V272, PawnIO 2.2.0
- Tested plugin: 3.0.1.0
- Tested DLL SHA-256: `9E46A71AD017D34ABE9B6C6C3CE210CD4928F97D429E18197F6FEBD689D8F6EA`
- Source baseline before the 3.0.1 correction: `19c0f65`

## Outcome

The campaign found one genuine plugin defect: the original 500 ms system-fan
selector wait could report a false timeout while the EC remained responsive.
Its cleanup released system ownership, but the shared system-fault gate then
disabled CPU control even though no CPU cleanup was pending.

Version 3.0.1 corrects both problems. The selector wait now allows the firmware
up to 1.5 seconds, polls only the effective-temperature byte, performs one
final full-state verification, and keeps CPU control independent after a
verified system release. The new worst-case selector read is 20 EC bytes,
versus 24 bytes in the old six-full-snapshot loop.

No whole-machine freeze or black-screen event occurred during the campaign.
This does not prove that the earlier freezes were caused by the old plugin, and
it does not exclude the generic raw indexed-port transport as a contributor.

## Method and invariants

Controls were exercised through the actual Fan Control V272 executable and its
normal configuration-loading path. Telemetry was sampled through Fan Control's
IPC service, so no second process accessed the EC while Fan Control was active.
Independent stock probes ran only after Fan Control had stopped.

The probes verified the exact CPU B1 table, zero temperature overrides, the
untouched CPU critical row, and firmware-owned system-fan state. No test wrote
DCR/PWM actuator output, system thresholds, the CPU temperature override, the
CPU critical row, firmware, or an address outside the plugin allowlist.

CPU load used a local 16-worker all-core workload. Display-path load used the
built-in WinSAT DWM assessment. Raw logs, configuration backups, deployed DLL
backups, event inventories, and dumps remain local and are ignored by Git;
`RESULTS.json` is the sanitized machine-readable summary.

## Defect reproduced and corrected

On a fresh combined `CPU 10 / system 30` engagement, the CPU policy applied and
the system override write/readback passed, but effective temperature `0x0889`
did not become `0xff` within the old 500 ms ceiling. Cleanup restored firmware
ownership. The EC, Windows, and Fan Control remained responsive, and the next
independent stock audit passed.

The old shared fault gate nevertheless made both controls unavailable while
the CPU's requested policy remained physically active until normal cleanup.
This was a false selector timeout plus cross-fan fault propagation, not an
observed EC deadlock.

A direct live-hardware A/B repeated fresh system ownership and release 12 times
on each build:

| Build | Ownership/release | Following stock audits |
|---|---:|---:|
| Before fix | 11/12 pass; one false release timeout | 12/12 pass |
| Version 3.0.1 | 12/12 pass | 12/12 pass |

## Results ledger

| Stage | Result |
|---|---|
| Offline suite | 47/47 tests passed, including maximum selector delay, bounded timeout, verified-release CPU isolation, and incomplete-cleanup CPU blocking. |
| Disabled telemetry | 180/180 one-second samples completed with no errors or missing plugin values; average IPC call time was 0.895 ms. |
| Plugin refresh | 10/10 sequential refreshes completed in approximately 0.90-1.46 seconds and repopulated transiently missing values. |
| CPU manual/load | Codes 18, 14, and 10 were exercised. Codes 18 and 10 each completed 60 seconds of all-core load; maximum CPU temperature was 85 C. |
| System manual/load | Codes 51, 30, 25, 20, 15, and 10 were exercised. System code 10 completed 60 seconds of overlapping CPU load at 56-60 C. |
| Combined ordering | `CPU 18 / system 51` and `CPU 10 / system 30` passed. After the fix, 10/10 CPU-first and 5/5 system-first fresh engagements and releases passed. |
| Combined sustained load | `CPU 10 / system 30` completed 180 seconds of all-core load and 200 monitored hold samples. CPU was 64-86 C / 936-3711 RPM; system was 58-62 C / 2272-3067 RPM. Both controls stayed exact and Fan Control responded in every sample. |
| Rapid transitions | Twenty alternations between `18/51` and `10/30` completed as 40 stages, with 120 seconds of overlapping all-core load. Fan Control remained responsive and all exact-control checks passed. |
| Real curves, bounded stop | Both saved graph curves tracked temperature for 116 seconds before the configured 65 C system stop. CPU was 70-86 C. Cleanup verified both controls null. |
| Curves plus display/CPU load | The mixed stage remained responsive for 218 seconds and stopped at its configured 69 C system limit. CPU was 67-87 C. Cleanup again verified both controls null. |
| Valid DWM display tests | Windowed DWM ran 30.05 seconds at 306.20 FPS / 26.60 GB/s. Fullscreen DWM ran 60.02 seconds, rendered 12,874 frames at 214.48 FPS, and reported 34.47 GB/s. No black screen, display reset, or freeze was observed. |
| Completed curve-only run | 360/360 monitored samples completed over 435.1 seconds, including a 240-second all-core load. Fan Control responded in every sample with no missing telemetry or controls. CPU was 67-87 C / 943-3963 RPM and exercised 13 control levels from 21.6-47.1%. System was 60-68 C / 927-1526 RPM and exercised two levels from 21.6-23.5%. First cleanup check verified both controls null. |
| Active-control mutex fault | With safe `CPU 18 / system 51` targets active, a cooperative five-second mutex hold caused the expected bounded cleanup-pending latch. The helper performed no port I/O. After the mutex released, disable plus refresh recovered complete telemetry and null controls; Fan Control never stopped responding. |
| Stock restoration | 41/41 independent stock audits passed, including the final audit after curve, display, and active-fault testing. |

## Expected stops and excluded pilot runs

Several local summaries say `FAILED` even though they are not plugin failures:

- the first CPU ladder stopped at its deliberately conservative 75 C limit;
- the two mixed curve stages stopped exactly at configured 65 C and 69 C
  system limits, then verified cleanup;
- an early CPU-code-10 load checked before Fan Control's stepped command had
  settled; the corrected retry passed;
- an early CPU-code-18 runner read a blank process exit-code property even
  though the 60.1-second workload and telemetry completed; and
- one combined-cycle invocation passed the helper DLL instead of Fan Control's
  IPC assembly, performed no hardware control, and was replaced by the passing
  ten-cycle retry.

The first explicit-resolution fullscreen WinSAT command and its three repeats
were also excluded. WinSAT rejected `winwidth 960`, ran only about 0.3 seconds,
and reported 0 MB/s despite returning exit code zero. The later supported
fullscreen command is the valid 60.02-second result above. An ad-hoc GPU-counter
wrapper that emitted no CSV was likewise discarded.

The pre-fix combined-selector failure is not in this exclusion category; it is
the genuine defect described above.

## Evidence assessment and final state

The final event/dump query, covering the curve, display, and active-fault test
window from 03:00 local time, found no WHEA, display/amdwddmg, BugCheck,
Kernel-Power, relevant application-error, LiveKernelEvent, or new dump entry.
Two LiveKernelEvent 141 reports and WATCHDOG dumps from August 3 predate this
campaign. They are consistent with GPU/display watchdog recovery but do not
identify the initiating cause of the earlier freezes.

Fan Control exited through its IPC service. The final independent stock probe
passed at CPU 70 C / 3062 RPM and system 58 C / 2085 RPM. The user's original
`userConfig.json` remained byte-for-byte unchanged, and the original Fan
Control `CACHE` was restored by its saved SHA-256. The tested 3.0.1 plugin
remains deployed; Fan Control is stopped.

## Residual risk and deferred cases

Firmware analysis validates the CPU row arithmetic and the system `0xff`
fixed-target handoff, but uncertainty remains below that policy logic:

- each generic PawnIO port operation is still a separate driver call;
- a nested `0x2e/0x2f` selector/address/data operation is not one atomic kernel
  transaction;
- the ISA mutex excludes only cooperating callers;
- the system sentinel has no confirmed autonomous target-51 thermal fallback;
  and
- user-mode cleanup cannot run while Fan Control, Windows, the kernel, or the
  whole machine is frozen.

Live system code 0, an actual 70 C system-failsafe trip, forced termination
with active controls, OS crash, sleep/resume, sustained operation near the CPU
critical tail, and a 24-72 hour representative-use soak remain untested.
WinSAT DWM exercises a relevant display path but is not an exhaustive modern
GPU workload. The campaign materially reduces uncertainty; it cannot prove
that a rare firmware, transport, GPU, or platform failure will never recur.
