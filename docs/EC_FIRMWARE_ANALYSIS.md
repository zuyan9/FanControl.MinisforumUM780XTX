# UM780 XTX EC firmware analysis

This note records the static evidence behind the plugin's F7BSD-specific fan
control. It is intentionally narrower than a general IT5571 description. No
firmware was flashed and no live EC access was used for this analysis. The
firmware image itself is not part of this repository.

## Provenance and extraction

The current [Minisforum support page](https://www.minisforum.com/pages/product-info)
links the UM780 XTX and UM790 XTX BIOS 1.06 package at:

```text
https://pc-file.s3.us-west-1.amazonaws.com/UM780XTX+UM790XTX/Bios/F7BSD_PHX_1.06_240328.zip
```

Locally observed hashes and sizes are:

| Object | Size | SHA-256 |
|---|---:|---|
| Current BIOS 1.06 ZIP | 10,251,290 bytes | `f88b5a69648f357d25bd5a0ba939a48a31ff6a07ba15bd723fb421cd24122f3c` |
| `F7BSD_PHX.rom` | 33,554,432 bytes | `eac306e5b8de2f9f36c5f14404d6fd480caaafd26556cf0fcc97d92b043ef58a` |
| EC payload | 131,072 bytes | `7def246db2a8fa64a6d2c2d2cbc0b932ea92551549b4abd4e5a903082a603edb` |

Minisforum does not publish a cryptographic checksum for the package, so the
hashes above are reproducibility records, not vendor signatures.

The EC payload is the exact ROM prefix `[0x00000000, 0x00020000)`. It is raw
flash-mapped data rather than a UEFI FFS file. UEFIExtract A75 describes the
larger ROM interval `[0, 0x4e5100)` as non-empty padding instead of extracting
an EC file; it does not identify the 128 KiB boundary. That boundary is visible
from the raw structure and disassembly: offset zero begins with 8051 `LJMP`
vectors, while ROM offset `0x20000` begins the next AMD structure with
`AA 55 AA 55`.

The payload identifies itself with these strings:

| EC offset | Text |
|---:|---|
| `0x0050` | `CMXG_EC-V14.6` |
| `0x41c0` | `Project:F7BSD$` |
| `0x41cf` | `ECVer:00.00.00.08$` |
| `0x41e7` | `Nov 15 2023` |

The BIOS release notes say that BIOS 1.04 updated the EC to `F7BSD08.bin`.
Officially linked BIOS 1.05 and both historical and current BIOS 1.06 packages
contain this same 128 KiB EC payload. The historical 1.06 ROM is byte-identical
to the current ROM. The only contained file-content change is `WinFlash.bat`;
the archive root name, timestamps, and ZIP metadata also differ.

The extraction can be reproduced without running any bundled flashing tool:

```powershell
$research = 'C:\UM780XTX_BIOS_RESEARCH'
New-Item -ItemType Directory -Path $research -Force | Out-Null

$url = 'https://pc-file.s3.us-west-1.amazonaws.com/UM780XTX+UM790XTX/Bios/F7BSD_PHX_1.06_240328.zip'
$zip = Join-Path $research 'F7BSD_PHX_1.06_240328.zip'
Invoke-WebRequest -UseBasicParsing $url -OutFile $zip
Get-FileHash $zip -Algorithm SHA256

Expand-Archive $zip (Join-Path $research 'package')
$rom = Get-ChildItem (Join-Path $research 'package') -Recurse -Filter F7BSD_PHX.rom |
    Select-Object -First 1 -ExpandProperty FullName
$bytes = [IO.File]::ReadAllBytes($rom)
[IO.File]::WriteAllBytes(
    (Join-Path $research 'F7BSD08.bin'),
    [byte[]]$bytes[0..0x1ffff])
Get-FileHash (Join-Path $research 'F7BSD08.bin') -Algorithm SHA256
```

## Address mapping

Firmware XRAM addresses with bit 15 set alias the host-visible 15-bit I2EC
address space. For example, firmware `0x8309` is host address `0x0309`, and
firmware `0x8884` is host address `0x0884`.

The fan-control state relevant to this plugin is:

| Host address | Firmware address | Meaning |
|---:|---:|---|
| `0x0309` | `0x8309` | raw CPU temperature |
| `0x0305` | `0x8305` | raw system temperature |
| `0x0884` | `0x8884` | CPU target, in 100-RPM units |
| `0x0885` | `0x8885` | system target, in 100-RPM units |
| `0x0886` | `0x8886` | current CPU row index |
| `0x0888` | `0x8888` | effective CPU temperature |
| `0x0889` | `0x8889` | effective system temperature |
| `0x088a` | `0x888a` | CPU temperature override |
| `0x088b` | `0x888b` | system temperature override |
| `0x1803` | `0x1803` | CPU PWM duty (firmware output) |
| `0x1804` | `0x1804` | system PWM duty (firmware output) |

## CPU policy proved by the firmware

The CPU selector is at EC code offset `0x9c8a`. A code pointer table beginning
at `0x3c0f` gives the upper, lower, base, and slope address for each normal row.
For row `r` from zero through six:

```text
base  = 0x0310 + 3*r
upper = base + 1
lower = base + 2
slope = 0x08b0 + r
```

In simplified form, the routine does this:

```text
effective = (override != 0) ? override : raw_temperature

if row < 6 and effective > upper[row]: row += 1
if row > 0 and effective < lower[row]: row -= 1

target = base[row] + floor(slope[row] * (effective - lower[row]) / 100)
if effective >= 94: target = base[7]
```

The strict comparisons prove the hysteresis behavior: heating changes rows
only above an upper boundary, and cooling changes rows only below a lower
boundary. The row moves by at most one on each invocation. The arithmetic at
`0x9cef..0x9d1f` is an unsigned multiply, division by the fixed constant 100,
and addition of the selected base. This matches the plugin's policy model
exactly; there is no signed-slope interpretation or lookup-table transform.

The one-row limit matters for discontinuous temperatures. With the v4 code-zero
table and a stale coolest-row state, a sudden 93 C sample produces normal-row
targets `0,0,0,23,50,51` over six selector invocations; a realistic 54..66 C
idle-row state produces `23,50,51`. The row-4 value now comes from the
hardware-qualified code-10 restart floor beginning at 67 C. Conversely, a
large discontinuous cooling event can leave the row several bands above the
current temperature and exercise
unsigned out-of-band subtraction until later invocations step it down. Adjacent
heating/cooling crossings are compiler-tested. Reset and recovery ignore only
that firmware-owned target output while continuing to require plausible
temperatures, override zero, the exact immutable B1 profile, and an exact
issued-table certificate, so the transient cannot strand a modified table on
orderly exit. Resume timing still requires separate live qualification.

Most importantly for byte-wise updates, this routine directly indexes the
current row and, when transitioning, the adjacent/final row. It does not scan,
checksum, copy, or validate the complete table. Normal base and slope values
feed bounded arithmetic; they are not used as loop counts or pointers.
Consequently, the plugin's compiler-validated intermediate rows can change the
requested target, but the inspected fan-policy code provides no path for those
values to create an EC infinite loop or table-latch deadlock.

The independent critical comparison is at `0x9d23..0x9d37`. At an effective
temperature of 94 C or higher it overwrites the normal result with critical-row
base `0x0325`. The plugin keeps the CPU override at zero and never writes the
critical row `(51,100,93,0)`, so real CPU temperature remains visible to this
firmware takeover.

The CPU feedback routine begins at `0x9d76`. It compares tach-derived RPM with a
deadband approximately 100 RPM below and above `target * 100`, changes DCR
`0x1803` by one step, and returns. Its periodic caller gives a zero target a
finite zero-DCR path. The target is therefore the native internal input to the
stock RPM loop; DCR is an actuator output that firmware continuously reclaims.
This is why the plugin never writes DCR or PWM registers directly.

### CPU scheduler and feedback cadence

Timer 0 is the source of the active-state fan-service schedule. Vector `0x000b`
jumps to ISR `0x0483`; the ISR reloads the timer through `0x2178` and sets the
single task bit `0x26.5` at `0x0493`. Initialization at `0x219e..0x21ad` selects
16-bit timer mode, loads `TH0:TL0 = 0xfd00`, starts timer 0, and enables its
interrupt. The main dispatcher clears that task bit and calls `0x0f69` at
`0x0eb1`.

The software dividers after that call are exact. Internal-RAM counter `0x43`
advances the fan-service slot once per five serviced timer task bits. Counter
`0x44` has ten slots: slot 2 calls the CPU selector at `0x0fb7`, and slot 6
calls the CPU feedback wrapper at `0x0fc1`. The selector and feedback therefore
each run once per 50 **serviced** timer task bits. Their phase is 20 timer tasks
from selector to feedback and 30 from feedback to the next selector. The later
`0x45`/`0x47` dividers at `0x0fdf..0x1035` group 1,000 timer tasks and then 60
of those groups, which independently identifies the intended timer task as a
one-millisecond tick.

The timer reload spans 768 timer input clocks. Public System76 firmware for the
same family documents a [9.2 MHz EC clock and the IT5571/IT5570
relationship](https://github.com/system76/ec/blob/ef05e145d554f4a4862258f582bbc309853ac606/src/ec/ite/Makefile.mk),
and its [8051 timer implementation uses a divide-by-12 input](https://github.com/system76/ec/blob/ef05e145d554f4a4862258f582bbc309853ac606/src/arch/8051/time.c).
That corroborates a nominal tick of about 1.00 ms and a nominal
selector/feedback period of about 50 ms (about 20 ms selector-to-feedback and
30 ms feedback-to-selector). These are design cadences, not hard wall-clock
deadlines: task `0x26.5` is a one-bit latch, so multiple interrupts can coalesce
while the cooperative main loop is busy, and ISR/reload latency adds a small
implementation-dependent error.

The feedback gate at `0x9ed8` requires active EC state `0x10`. For a nonzero
CPU target it transfers to `0x9d76`, whose normal path changes DCR by only one
count per feedback slot; a large correction can therefore take seconds (about
12.8 seconds for an entire 255-count span at the nominal cadence). A zero target
takes a separate path at `0x9ee9..0x9eed` that writes `0x1803 = 0` directly on
the next serviced feedback slot. Physical fan coast-down, tach reporting, and
Fan Control display polling can remain slower than that DCR write.

The v4 live campaign matches those distinctions. A cold B1-to-target request
completed in 49.8 ms, subsequent adjacent requests took about 14..21 ms, and
515 synchronous Set calls (510 distinct EC table mutations) had 13.6 ms mean /
15.6 ms p95 / 45.8 ms maximum call latency. From a physical stop under CPU
load, the target
changed from 0 to the row-4 code-10 floor at 67 C; tach reported a running fan
about one second later and remained above 800 RPM for four consecutive
half-second samples. These are observations on this machine, not firmware
deadlines.

There is no unconditional elapsed-time upper bound. The scheduler gate at
`0x9b4f` suppresses advancement in EC states `0x30` and `0x50`; the feedback
gate accepts only state `0x10`; a busy main loop can coalesce timer events; and
startup/state-transition code at `0xb5bf` loads `0x833f = 0xc8`, causing the
alternate selector at `0x9c3c` to consume one count per selector slot before
normal selector `0x9c8a` resumes. Live timing qualification must therefore
measure the active steady state and power/resume transitions separately.

The B1 and B2 initialization routines reproduce the tables already validated
by the plugin. Static cross-references to normal-row bases and slopes are the
initializers and the selector pointer table; no shadow table, integrity byte,
or table-wide validity latch was found.

## System fixed-target handoff

The system selector at `0x9eef..0x9f32` chooses the raw temperature when
override `0x088b` is zero, otherwise the override, and copies that value to
effective-temperature byte `0x0889`. It then implements:

```text
effective <  25 -> target 0
effective <  83 -> target 20
effective < 100 -> target 51
otherwise       -> retain the previous target
```

The selector is called periodically when an incremented counter exceeds ten,
on the eleventh eligible service invocation. Setting the override to `0xff`
makes every less-than comparison fail, so the routine leaves a host-written
target at `0x0885` unchanged. The separate tach feedback loop continues
converting that target into DCR changes. This proves that `0xff` is a real
fixed-RPM handoff, not direct PWM ownership.

It also proves the main system-control hazard: while the sentinel is active,
firmware sees effective temperature 255 and deliberately retains the previous
target. There is no independent target-51 equivalent of the CPU's 94 C critical
row in this selector/control path. Firmware separately sets a status bit from
raw system temperature at 100 C and clears it at 90 C; the downstream protective
effect of that bit has not been established. The plugin must therefore monitor
raw address `0x0305` to guarantee its own fixed-target policy, and code zero can
intentionally drive the stock feedback path to zero duty.

The current plugin monitors raw system temperature, promotes the target to 51
on invalid or at-least-70 C input, and releases ownership on bounded telemetry
or timing failures. Those defenses work only while Fan Control and Windows are
running. No user-mode exit handler can execute during a complete machine
freeze, kernel failure, or power loss.

## What the analysis does and does not establish

The static result substantially narrows the earlier freeze hypothesis:

- CPU base/slope writes generated by the compiler do not feed an unbounded
  loop, pointer, table scan, or table-integrity latch in the analyzed firmware.
- The CPU critical path is independent and remains effective because the
  plugin never changes the override or critical row.
- The system sentinel behavior and absence of a target-51 fallback in its
  selector/control path are now confirmed, rather than inferred only from live
  behavior. Other effects of the separate 100 C raw-temperature status bit
  remain unknown.

It does **not** prove that the complete plugin has "no chance" of freezing the
machine. The remaining uncertainty is below the fan-policy logic:

- each generic PawnIO port operation is a separate driver invocation;
- the nested `0x2e/0x2f` selector/address/data sequence is therefore not one
  atomic kernel transaction;
- `Global\Access_ISABUS.HTP.Method` excludes cooperating callers only;
- PawnIO serializes one VM context/handle, not every handle or unrelated EC
  driver; and
- no public IT5571 datasheet was found that specifies all timing, interruption,
  and concurrency requirements for this vendor-specific I2EC path.

The plugin reduces those windows by holding the ISA mutex for the whole guarded
operation, parking both selector levels after every EC byte, verifying each
write immediately, poisoning the transport after a native failure, limiting
addresses, omitting ownership-only telemetry while the system fan is
firmware-owned, eliding duplicate CPU targets, and restoring only from an exact
issued prefix. These invariants are intentionally stronger than the older M1 Pro
plugin's batch-end parking and unverified batch writes; that simpler transport
should not be copied here.

A separately reviewed and signed F7BSD-specific kernel/PawnIO module could put
a complete parked EC transaction in one driver call and reduce interruption
windows further. It could also attempt system-sentinel release when its client
handle closes. It still could not guarantee recovery from a frozen kernel or
EC, loss of power, or access by software that ignores the same ownership
protocol.

The defensible conclusion is therefore two-part: CPU policy-table semantics are
now understood well enough to rule out the specific firmware-code deadlock
mechanisms above; generic raw indexed-port transport and user-mode system-fan
ownership remain residual platform risks and require staged, attended testing.
The v4 staged campaign completed high-rate direct access, repeated dual-control
handoffs, real Fan Control curve/load operation, orderly exit, and relaunch
without a freeze, plugin fault, relevant Windows event, or new dump. That
evidence reduces—but cannot eliminate—the remaining transport/platform risk.

## Analysis tools

- [UEFITool / UEFIExtract A75](https://github.com/LongSoft/UEFITool/releases/tag/A75)
- [radare2 6.1.8](https://github.com/radareorg/radare2/releases/tag/6.1.8)
- radare2 architecture: `8051`, 8-bit

Representative disassembly ranges are `0x9c8a..0x9d38` for CPU selection and
critical takeover, `0x9d76..0x9dc6` for CPU feedback,
`0x9eef..0x9f32` for system selection, and `0xa037..0xa062` for the periodic
system service path.
