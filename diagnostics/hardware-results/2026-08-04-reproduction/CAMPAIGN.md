# A5 dynamic-control reproduction campaign

Date: 2026-08-04 (America/Los_Angeles)

- Machine: Minisforum UM780 XTX, F7BSD 1.1, BIOS 1.06, EC 0.8
- Host: Windows 11 x64, Fan Control V272, PawnIO 2.2.0
- Graphics: AMD Radeon 780M, driver 32.0.31035.1003
- Tested plugin: 4.0.0.0, 85,504 bytes
- Tested DLL SHA-256: `48563298D32476BD27A8F9B7FA802474021F0EDC7C202932D21B009859BF496D`
- External USB4 hub: disconnected

## Outcome

The final A5 campaign completed with one expected thermal abort. All three
dynamic Fan Control sequences passed: 24 profile loads were acknowledged, all
24 target transitions completed, and all 204 hold samples read back the exact
requested CPU target and the fixed full system-fan target. The independent
guard completed 838/838 telemetry RPCs without an RPC error.

The decisive sequence performed 12 consecutive CPU-target changes during
continuous 96.0..98.2% iGPU load. It completed and verified its final code-18
state. About 4.82 seconds later, the fail-closed guard observed system EC
temperature 70 C above its conservative 69 C test limit and stopped the
planned 240-second WinSAT workload after about 140 seconds. The raw outer
wrapper therefore correctly records `FAIL`; this is an expected thermal abort,
not a 240-second pass and not an EC, plugin, display, or machine failure.

No freeze, black screen, EC deadlock, plugin error, relevant Windows event, or
new LiveKernelReport occurred. Fan Control remained the same responsive process
in all 354 independent harness samples. Read-only restoration, orderly Fan
Control exit, and a final exact-stock hardware audit passed.

## Scope and method

A5 followed the earlier A0-A4 isolation and static-control stages retained in
the ignored local campaign tree. This curated result covers only the final A5
dynamic-control qualification.

The sequence runner drove the real Fan Control V272 process through its IPC
`LoadConfig` path. Three validated flat-curve profiles requested native CPU
codes 10, 18, and 30 while keeping the system fan at native code 51. Before
each load, the runner pinned the exact Fan Control process identity and
revalidated the pristine profile hash, control IDs, tachometer pairings,
curve points, temperature source, and disabled unrelated controllers. Each
load was followed by exact control, RPM, and temperature reads through Fan
Control. No second process accessed the EC while Fan Control owned the plugin.

An independent high-priority guard sampled once per second and required:

- the expected control sensors to remain present and finite;
- the system control to remain exactly 100% with RPM at least 3,000;
- CPU, system, GPU, and DIMM temperatures at or below 95, 69, 85, and 75 C;
- bounded RPC and sampling cadence;
- the same responsive Fan Control process; and
- no new relevant event, workload escape, or LiveKernelReport change.

Raw configurations, IPC replies, ledgers, logs, WinSAT output, event and dump
inventories, binaries, process metadata, and machine-specific paths remain
local and are ignored by Git. `RESULTS.json` is the sanitized machine-readable
summary.

## Results ledger

| Stage | Raw result | Evidence |
|---|---|---|
| P0 read-only guard smoke | PASS | 100/100 RPCs; both controls present and null; no violations. |
| R1 static CPU 18/system 51 idle | PASS | 100/100 RPCs; CPU 35.294117%, system 100%; system fan 3348..3401 RPM. |
| R2 six idle changes | PASS | 6 transitions, 60 exact hold samples, final code 18 verified; 180/180 guard RPCs. |
| R3 six changes with 120-second iGPU workload | PASS | 6 transitions, 48 exact hold samples, natural WinSAT exit code 0; 210/210 guard RPCs. Five transitions overlapped the workload; the sixth followed its natural exit. |
| R4 twelve changes under continuous iGPU load | Expected thermal abort | Dynamic sequence PASS in 120.783 seconds: 12 transitions, 96 exact hold samples, final code 18 verified. The outer wrapper later recorded `FAIL` when system EC reached 70 C and the guard stopped WinSAT. All 148 guard RPCs succeeded. |
| R5 post-control read-only restoration | PASS | 100/100 RPCs; both controls present and null; no violations. |

Aggregate dynamic evidence:

- 24/24 Fan Control profile loads acknowledged;
- 24 transitions: code 10 eight times, code 18 twelve times, and code 30 four
  times;
- 204/204 exact hold-sample readbacks with system control exactly 100%;
- 838/838 successful guard RPCs, zero missing telemetry, and zero RPC errors;
- maximum RPC time 63.5537 ms and maximum one-second sample gap 1015.8298 ms;
- 354/354 responsive Fan Control harness samples;
- zero relevant final or late events, zero new LiveKernelReports, and zero new
  plugin log lines; and
- no freeze, black screen, or observed EC deadlock.

Across the 204 verified transition samples, CPU telemetry was 55..81 C and
1048..3369 RPM. System telemetry was 50..69 C and 3353..3428 RPM. The maximum
guarded Tctl, GPU, and DIMM temperatures were 82.875, 80, 62.25, and 46.5 C.

## GPU-load qualification

R3 was only partially overlapped: the workload exited naturally 45.85 seconds
after the first transition, so the final 13.65 seconds and last transition were
not under GPU load. Its valid 120.02-second WinSAT assessment completed at
217.93 FPS and 35,027.16 MB/s.

R4 is the decisive continuous-load result. All 12 transitions and 96 verified
hold samples occurred while WinSAT was running. All 87 sequence-window
performance samples were at least 90% GPU; maximum-engine utilization was
96.042..98.154%, averaging 96.955%.

## Expected thermal stop

R4 requested a 240-second iGPU workload. The dynamic sequence passed and its
final code-18 state was verified after 120.783 seconds. The guard then observed
the sole campaign violation 4.823 seconds later:

`system temperature 70 C exceeded 69 C.`

It created the durable abort after 139.977 seconds of workload, then stopped
WinSAT, and recorded no concurrent relevant event or dump change. Because the system
fan was already held at native code 51, this stop did not qualify an autonomous
system-failsafe promotion.

## Incident assessment

Two valid dumps preserved from the earlier freeze incidents both report
`VIDEO_ENGINE_TIMEOUT_DETECTED (0x141)` in `amdkmdag.sys` with the same failure
hash. That is positive evidence for an AMD GPU/display-engine timeout, but it
does not establish what initiated the timeout.

A5 exercises the same general user action that preceded one incident--changing
CPU fan targets through Fan Control while the iGPU is heavily loaded--without
an EC, plugin, display, or system failure. This materially weakens a
deterministic plugin/EC-deadlock hypothesis. It cannot exclude a rare EC,
PawnIO transport, GPU, firmware, or platform interaction.

## Final state

The A2 read-only configuration was restored and verified for 100 samples with
both controls null. Fan Control then exited through its IPC service. The final
independent exact-stock probe passed in 74.9 ms at CPU 62 C / 2551 RPM and
system 52 C / 2041 RPM.

The selected read-only configuration SHA-256 was
`E358D3FCFC01838760D20FB7E6D77998EE8D0869816A8288811D0ED10902F8F1`.
The tested plugin DLL was unchanged throughout the campaign.

## Residual risk and excluded evidence

- IPC profile loads exercise Fan Control's real configuration and plugin
  lifecycle but are not identical to dragging graph points in its elevated UI.
- A5 did not dynamically change the system target, exercise live system code
  zero, or combine simultaneous CPU and iGPU stress.
- Sleep/resume, forced termination, OS crash, an actual system-failsafe
  promotion, and a 24-72 hour representative-use soak remain unqualified.
- Generic PawnIO still performs the indexed-port sequence as separate driver
  calls, and the ISA mutex excludes only cooperating software.
- User-mode restoration cannot execute while Fan Control, Windows, the kernel,
  the EC, or the whole machine is frozen.

The three generated profile hashes, exact stage counts, final state, and
limitations are recorded in `RESULTS.json`. Raw evidence is intentionally not
committed because it contains personal paths, process identities, hardware
inventories, logs, and pre-existing dump metadata.
