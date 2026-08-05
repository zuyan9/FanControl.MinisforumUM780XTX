# Version 4 cool-stop hardware campaign

Date: 2026-08-04 (America/Los_Angeles)

- Machine: Minisforum UM780 XTX, F7BSD 1.1, BIOS 1.06, EC 0.8
- Host: Windows 11 x64, Fan Control V272
- Tested plugin: 4.0.0.0, 85,504 bytes
- Tested DLL SHA-256: `48563298D32476BD27A8F9B7FA802474021F0EDC7C202932D21B009859BF496D`

## Outcome

Version 4 passed the final offline suite, direct hardware stress, physical
stop/restart, autonomous zero-to-load recovery, real Fan Control dual-control
cycles, rapid transitions, curve/load operation, orderly exit, and relaunch.
No whole-machine freeze, black screen, plugin fault, relevant Windows event, or
new live-kernel dump occurred.

The first zero-load prototype deliberately exposed a weakness in the proposed
1..15 thermal ramp: this physical fan could kick and then stall at codes 1..7.
That pre-final result was not accepted as qualification. The final policy keeps
a true zero request through 66 C and uses the B1 row boundary to jump to the
hardware-proven sustainable code 10 at 67 C. Cooling retains code 10 at 66 C
and returns to the cool row below it.

The campaign also isolated a user-visible 1.7-second first-enable pause to the
runtime policy compiler, not EC I/O. Equivalent closed-form table encodings and
an optimized deterministic planner reduced the final cold hardware request to
49.8 ms while preserving a golden digest over all 18,928 compiled transition
paths.

## Method and invariants

Direct probes ran elevated with Fan Control stopped. Real-software stages used
the normal Fan Control V272 executable and its IPC configuration path; no
second process accessed the EC while Fan Control owned the plugin. Every direct
probe and every real-software campaign ended disabled, followed by an
independent exact-stock audit.

The tested code retained exact host/controller identification, the cooperative
global ISA mutex, parking after every EC and PNP byte, the narrow address
allowlist, same-transaction preconditions/readback, exact issued-prefix CPU
recovery, captured B1 restoration, an untouched CPU critical row and
temperature override, and no direct DCR/PWM writes.

Before deployment, the tool recorded an immutable verified snapshot of the
deployed 3.0.0 DLL, Fan Control `CACHE`, original `userConfig.json`, and log.
The original user configuration remained byte-for-byte unchanged. Raw logs,
backups, generated configs, and telemetry ledgers remain local and are ignored
by Git; `RESULTS.json` is the sanitized machine-readable summary.

## Final results

| Stage | Result |
|---|---|
| Offline suite | 50/50 tests passed. Main, diagnostics, IPC, CPU-burn, and mutex-helper builds completed with zero warnings/errors; all PowerShell helpers parsed cleanly. |
| Cold responsiveness | Final B1-to-code-18 request: 49.8 ms. Eight following adjacent requests: 13.9..21.3 ms. The pre-fix cold request was 1716.5 ms. |
| Direct traffic stress | 515 synchronous CPU Set calls (510 distinct EC table mutations) completed in 7.7 seconds: 13.6 ms mean, 15.6 ms p95, 45.8 ms maximum across all calls. Exact B1 restoration and the following stock/event audit passed. |
| Physical stop/restart | Three code-18 to code-0 to code-18 cycles reached three consecutive 0-RPM samples and restarted above 2,000 RPM within two one-second samples. |
| Autonomous zero-to-load | From four consecutive 0-RPM samples, CPU load raised the EC target from 0 to 10 at 67 C. Tach reported a running fan about 1.01 seconds later and remained above 800 RPM for four consecutive half-second samples under continuing load. Maximum CPU temperature was 69 C. |
| Fan Control manual lifecycle | Three complete cycles (18 stages) covered CPU 18, CPU 0, CPU restart, system 30, combined CPU 10/system 30, and disabled restoration. All controls confirmed. All three zero stages reached 0 RPM; the warmer third also exercised automatic restart at 67 C. |
| Fan Control rapid transitions | Fifteen alternations between combined CPU 18/system 30 and CPU 10/system 30 completed as 30 verified stages. Fan Control remained responsive and first-attempt cleanup passed. |
| Cool-stop curves plus load | 120 monitored samples, including 60 seconds of 16-worker CPU load. CPU was 63..86 C / 0..4091 RPM; system was 55..58 C / 905..1908 RPM. Both controls stayed active during the stages and were null on first cleanup. |
| Migrated user curves | 180/180 samples completed with 120 seconds of 16-worker CPU load. CPU was 63..84 C / 1006..3494 RPM; system was 57..60 C / 925..1891 RPM. There were no unresponsive or missing-telemetry samples. |
| Exit/relaunch | Two IPC exits, one clean relaunch, and the relaunch sensor inventory passed. The v4 CPU ID and all six plugin sensors were present; both control values were null. |
| Final stock/event audit | Exact stock passed at CPU 60 C / 2469 RPM and system 56 C / 1980 RPM. From 10:49 local time there were zero relevant system events, application events, or new LiveKernelReports. |

## Low raw codes

The plugin intentionally exposes all 52 native CPU codes. Hardware testing
showed that explicit codes roughly 3..7 can make this fan alternate between a
startup kick, low RPM, and stall. This is not an EC transaction failure: Fan
Control continued responding and higher targets restarted the fan immediately.
It is the physical consequence of asking below the fan's sustainable speed.
A practical Fan Control graph should use a genuine stop region and then jump to
about code 10 (19.61%) instead of drawing a long linear segment through codes
1..9. The EC-resident thermal tail independently enforces that jump at 67 C.

## Excluded preliminary runs

The direct logs before the sustainable-floor and cold-compiler corrections are
retained locally as development evidence but are not results for the final DLL.
They include the intentionally diagnostic low-code stall/hunt described above
and the 1716.5-ms cold request. One later elevated zero-load launcher attempt
exited before its first stock probe and left an empty log; a command attempting
incompatible `Start-Process` redirection did not launch anything. The fresh
retry executed fully and passed.

## Final state and rollback

Fan Control exited normally and is stopped. The exact tested v4 DLL remains
deployed. `CACHE` selects `v4-disabled.json`; both controls are disabled and
their Fan Control step-up/down values are 100, so the old v3 command-step delay
does not return. The user's original `userConfig.json` SHA-256 remains
`72A0B97EEC79E7A32776604FD6B9CB816ABB96F28FFBF677488DCBA6C652178E`.

The verified campaign-start snapshot can restore the prior 3.0.0 DLL and its
exact configuration/cache/log files. Its DLL SHA-256 is
`D466C4EE165DD9605E8B8D54112CDAAAC76250AEBED0AFA986C6F191F8EC57CE`.

## Residual risk

The campaign materially reduces the likelihood that ordinary curve editing or
plugin EC traffic reproduces the earlier freezes, but it cannot prove a zero
chance of firmware, transport, GPU, or platform failure. Generic PawnIO still
uses separate driver calls for the nested indexed-port sequence, and the mutex
excludes cooperating software only. User-mode cleanup cannot run while the
kernel, EC, Windows, or the whole machine is frozen.

Sleep/resume, forced termination with active controls, OS crash, live system
code 0, an actual 70 C system-failsafe promotion, an instantaneous 93 C CPU
jump, and a 24-72 hour representative-use soak remain untested. Reboot before
further EC access after abnormal termination or any unverified restoration.
