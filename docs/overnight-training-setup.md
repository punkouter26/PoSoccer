# Overnight training setup

Added 2026-08-04. Nightly unattended ML-Agents experiment queue + a morning
review that reads its output.

## The split

Training runs **on this machine**, launched by Windows Task Scheduler. The
morning review runs **in Claude's cloud** as a Cowork scheduled task and reads
the summary through the desktop-app folder bridge. Cowork scheduled tasks start
a fresh cloud session each firing — they cannot hold an 8-hour session and
cannot launch a Windows process, which is why the launcher is Task Scheduler
and not Cowork.

## Files

| File | Role |
|---|---|
| `scripts/train-overnight.ps1` | The queue runner. Hard stop at 08:00. |
| `scripts/register-overnight-task.ps1` | One-time (elevated) Task Scheduler registration. |
| `config/STANDARD_phase3b_entropy.yaml` | Run B: phase 3 + 3× entropy bonus, no decay. |
| `config/STANDARD_phase3c_selfplay.yaml` | Run C: PPO self-play seeded from run A. |
| `results/overnight/<yyyyMMdd>/summary.json` | What the morning review reads. |
| `results/overnight/<yyyyMMdd>/orchestrator.log` | Full transcript of the night. |

## Tonight's queue

Measured throughput is ~2,425 steps/s (4 envs × 16 pitches, `time_scale 20`),
taken from `soccer_v3_00.log`: 5.36M steps in 2,211 s. A 00:05→08:00 window
divides into three ~133-minute slices after the 25-minute build+eval reserve,
which is ~19.4M steps each — just inside each config's 20M `max_steps`.

| Run | Config | Question it answers |
|---|---|---|
| A `p3a_<date>` | `STANDARD_phase3_opponent.yaml` | Does curriculum-on-opponent-strength beat curriculum-on-goal-width? (your control — unmodified) |
| B `p3b_<date>` | `STANDARD_phase3b_entropy.yaml` | Is the 15–18% plateau also an exploration failure? |
| C `p3c_<date>` | `STANDARD_phase3c_selfplay.yaml` | Does a self-improving opponent beat the fixed deterministic bot? |

Each run: train → export → promote to the GUID-stable `.onnx` slot → rebuild the
headless player → 100-episode eval vs the bot → append to `summary.json`.
`summary.json` is rewritten after **every** run, so a 04:00 reboot still leaves
the completed runs readable.

## Two findings from the audit, neither fixed

**1. `CLAUDE.md` is stale about the venv.** It says training is broken because
`.venv`'s protobuf is gutted (4 files in `google/protobuf/internal/` instead of
~40). Both `.venv` and `.venv2` now hold a complete 22-entry protobuf. Training
is unblocked. `train-overnight.ps1` verifies this at startup anyway — it imports
`mlagents_envs.communicator_objects` in each candidate venv and picks the first
that survives, because a half-installed protobuf imports fine until the trainer
opens its gRPC channel and *then* dies, which overnight is indistinguishable
from a healthy start.

**2. `CLAUDE.md` is wrong that the runs stayed pinned in Lesson 0.** Parsing
`results/soccer_v3_00.log`:

```
step         0  goal_width -> Lesson0_WideNet  = 6.0
step   2640000  goal_width -> Lesson1_MidNet   = 4.0
step   2800000  goal_width -> Lesson2_TightNet = 2.5
```

It cleared the whole curriculum by step 2.8M and spent the remaining ~13M steps
at `goal_width 2.5`, while `evaluate.ps1` grades at the scene default of 6.0.
So the train/eval mismatch is real but points the *other* way than documented:
the policy practised on a goal **2.4× narrower** than the one it was graded on,
which plausibly explains a good chunk of the 18% win rate on its own. Phase 3
pinning `goal_width: 6.0` is the right fix.

The same log implies a **curriculum-blow-through risk for phase 3**. Mean reward
in that run reached ~1.7 — far above phase 3's `bot_strength` advance thresholds
of 0.30 / 0.20 / 0.10. A fresh policy starts lower, but expect all four lessons
to clear early and the run to face the full-strength bot for most of its length.
Deliberately not changed: run A is the control that isolates your curriculum
edit, and raising thresholds would confound it. The morning review checks lesson
pacing explicitly and will say so if it happens. If it does, raising the
thresholds is the obvious candidate for tomorrow night.

## Things that will bite

- **The script closes your Unity editor** (politely first, then force after 60 s).
  `build-headless.ps1` cannot take the project lock with the editor open, and a
  stale build means grading the wrong weights. Unsaved scene edits are lost.
  Use `-KeepUnity` to opt out — that disables eval too.
- **`build-headless.ps1`'s default editor path is wrong.** It hardcodes
  `6000.5.4f1`; this project is on `6000.5.6f1`. `train-overnight.ps1` resolves
  the editor from `ProjectSettings/ProjectVersion.txt` instead and passes it in.
  Worth fixing in `build-headless.ps1` directly at some point.
- **Deadline-stopped runs have no root `.onnx`.** `update-model.ps1` only looks
  at `results/<run>/<Behavior>.onnx`, which a killed trainer never writes — this
  is why `soccer_v3_00` has checkpoints only. The orchestrator promotes the
  newest checkpoint into that name first, and records `exportedFrom` in the
  summary so you know whether you're looking at a clean export or a checkpoint.
- **Run C may not work first time.** Phase-2 self-play has never completed on
  this project (`results/soccer_p2_00/` died before producing a model). It is
  last in the queue so a failure costs nothing but its own slice. It uses PPO,
  not POCA, because `--initialize-from` cannot carry a PPO checkpoint into a
  POCA trainer — POCA's centralised critic changes the network shape.
- **Read ELO, not mean reward, for run C.** Self-play rewards are zero-sum and
  sit near zero by construction. A flat reward curve there is expected.
- **Sleep settings.** The task registers with `WakeToRun`, but a machine set to
  hibernate or with wake timers disabled will not fire. Check `powercfg /waketimers`.

## Commands

```powershell
# One time, elevated:
.\scripts\register-overnight-task.ps1

# Try it right now without waiting for midnight (short window, one run):
.\scripts\train-overnight.ps1 -EndTime (Get-Date).AddMinutes(45).ToString("HH:mm") -Only a

# Inspect / disable
Get-ScheduledTask -TaskName "PoSoccer Overnight Training" | Get-ScheduledTaskInfo
Disable-ScheduledTask -TaskName "PoSoccer Overnight Training"
```
