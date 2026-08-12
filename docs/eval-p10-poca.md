# Phase 10 — POCA Self-Play at 3M and 10M — RESULT

> ## ⚠ RETRACTED 2026-08-12 — every number below is measurement error
>
> **None of the three phase-10 evals graded the model named in its filename,
> and none of the three phase-10 training runs used the code they were
> supposed to test.** The conclusions drawn from them — including the headline
> "3M POCA = 19.0%, the first real movement in 6 runs" and the decision to ship
> that brain as v5 — do not survive. Read the retraction, not the report.
>
> ### 1. Every eval graded a stale player
>
> `Builds/PoSoccer/PoSoccer.exe` and its entire `PoSoccer_Data/` were last
> written **2026-08-05 17:04**. The three evals ran 2026-08-11 21:08,
> 2026-08-12 01:04 and 01:08. Nothing in the build changed in between, so all
> three ran the same 8/5 binary with the **p9-era brain baked in** — the p10
> exports on disk were never in the player. `update-model.ps1` ran; no rebuild
> ever followed it.
>
> Confirmed independently by the eval's own timer file:
> `RayPerceptionSensor.Perceive` fired 550,688 times across 275,343 decision
> steps = **2 per step** (1 sensor × 2 agents). The p10 code splits vision into
> 4 sensors, which would be 8 per step. The binary predates the split.
>
> So the three "results" are three repeat samples of one unchanged model:
> 16.4 / 19.0 / 17.8, mean **17.7%**, spread 2.6 pp against SD ≈ 1.2 at
> n=1000. That is textbook binomial noise, and it matches the documented
> 11–24% scatter for repeat evals of an identical model. The "+2.6 pp is
> statistically real" claim below compared two samples of the same thing.
>
> ### 2. Every training run also used that stale env
>
> `env_path` in all three `configuration.yaml` files points at the same 8/5
> `PoSoccer.exe`. The shipped `STANDARD.onnx` has `obs_0 [batch, 66]` +
> `obs_1 [batch, 52]` = **118 inputs** — the pre-split contract. The current
> runtime produces 160. **The 4-sensor split and the terminal-reward shaping
> that phase 10 exists to test were never executed, not even once.** They are
> untested, not disproven.
>
> ### 3. The ELO evidence agreed all along and was not read
>
> Judged on ELO rather than mean reward, as the rules require:
>
> | | 3M POCA | 10M POCA |
> |---|---|---|
> | `Self-play/ELO` | tag absent entirely | 3 points, flat **1200.5** to 9.28M steps |
> | `Mean Group Reward` | 0.000 | 0.000 |
> | `Environment/Cumulative Reward` | −0.76 → 0.50 | 0.54 → 0.53 (max 0.94) |
>
> Initial ELO is 1200.0. The rating moved **+0.5 across 10M steps** and then
> froze. `Mean Group Reward` is identically 0.000 in every summary, so
> MA-POCA's group-credit channel — the whole reason to choose POCA over PPO —
> carried no signal. Mean reward climbing to 0.86 while ELO sits still is
> precisely the decoupling the ELO rule exists to catch.
>
> ### The POCA brain, finally graded (2026-08-12)
>
> Built a player from the 118-obs code the brain was actually trained on
> (commit `43e3385`, verified in the live editor by reflecting on
> `Sensor_Vision.RaysPerDirection == 5` before building) and ran 1000 episodes:
>
> | | blue | red | stalemate | mean steps |
> |---|---|---|---|---|
> | **3M POCA brain (real, `p10_poca3m_regrade`)** | **18.5%** | 64.9% | 16.6% | 4,381 |
> | p9 brain (what all three phase-10 evals measured) | 17.7% mean of 16.4 / 19.0 / 17.8 | — | — | — |
>
> **+0.8 pp against a combined SD of ~1.7. POCA did not break the plateau.**
> The retraction stands on its own evidence: the original claim compared two
> samples of the same p9 brain, and the true POCA value turns out to sit inside
> the same 16–19% band everything else has occupied since p5.
>
> ### What was actually learned
>
> Nothing about POCA, perception, or the plateau. What phase 10 established is
> that the harness could publish six-hour conclusions from a binary nobody had
> rebuilt. Fixed 2026-08-12:
>
> - `build-headless.ps1` baselined the **exe** mtime but tested the **data**
>   files, so once data was newer than the exe — the normal steady state —
>   every build reported `OK`, including builds aborted by a held editor lock.
>   Each artifact now checks against its own prior timestamp, and a held lock
>   is detected before Unity launches.
> - `evaluate.ps1` compared the model against the **exe** mtime (which Unity
>   leaves untouched) and only **warned**. It now compares
>   `PoSoccer_Data/*.assets` and **fails** with exit 2; `-AllowStale` is the
>   deliberate override.
> - Every eval JSON now records `modelInputs`, `modelPath`, `modelWrittenUtc`
>   and `playerBuiltUtc`, so a result can be falsified after the fact. The
>   phase-10 files record a run id and a win rate and nothing that reveals
>   they all describe one stale 118-input player.
>
> Still open: nothing inside Unity asserts that the baked model's input count
> matches the runtime's sensor battery. A 118-input `.onnx` against a 160-input
> runtime is the sensor-geometry landmine in a form the existing guard misses.
>
> **Original report follows, preserved unaltered for the record.**

**Status:** POCA is the first lever to actually move the needle off the
16-17% plateau, but the gain is bounded — **3M POCA = 19.0%** (real
+2.6 pp), and **10M POCA = 17.8%** (back to the plateau).

## What was tested

MA-POCA self-play from `STANDARD_phase2_poca.yaml`, warm-started from
the p10-perception brain (3M POCA pilot) and from the 3M POCA brain
(10M follow-up). Self-play with frozen-old-self opponent pool:
`save_steps: 50000`, `team_change: 200000`, `swap_steps: 2000`,
`window: 10`, `play_against_latest_model_ratio: 0.5`.

## What was measured

### 3M POCA pilot (warm-started from p10 perception brain)

| Step | Mean Reward |
|---|---|
| 50k | (POCA reward not directly comparable to PPO) |
| 1M | 0.144 (volatility normal) |
| 2M | volatile, oscillating positive/negative |
| 3M | **+0.502** final |

1000-episode eval: **19.0% blue / 64.6% red / 16.4% stalemate**
(190 / 1000 wins, +2.6 pp vs p9 plateau, first real movement in 6 runs).

### 10M POCA follow-up (warm-started from 3M POCA)

Wall time 6,875 s ≈ 1h 54m. POCA warmed up — actual rate faster than
the 3M pilot's rate.

1000-episode eval: **17.8% blue / 66.9% red / 15.3% stalemate**
(178 / 1000 wins, -1.2 pp vs 3M POCA, back at plateau noise floor).

## Comparison with the p5-p9 plateau

| Run | What changed | Win rate (1000 ep) |
|---|---|---|
| p5 | baseline (small sensor) | 16.2% |
| p6 | + opponent vec obs | 17.1% |
| p7 | + reward table fix | 16.6% |
| p8 | + locomotion drift fix | 16.9% |
| p9 | + randomized bot strength | 17.4% |
| p10 (perception) | + 4 specialized ray sensors + style shaping | 16.4% |
| **p10 POCA 3M** | **MA-POCA self-play, warm-started from p10** | **19.0%** |
| **p10 POCA 10M** | **continued POCA from 3M to 10M** | **17.8%** |

At n=1000, SD ≈ 1.2 pp. The 3M POCA's +2.6 pp is statistically real;
the 10M POCA's −1.2 pp vs 3M is at the noise floor but the **direction
is unambiguous** — POCA did not continue to help past 3M.

## Interpretation

- ✅ POCA was the first real unlock off the 16-17% plateau. The 19.0%
  at 3M is statistically distinct from the plateau.
- ❌ The 10M follow-up did not compound the gain. POCA's help is
  bounded — 3M was the right cap, not 30M.
- ✅ 10M POCA is still better than the worst p5-p10 PPO runs (16.2%)
  and within noise of p9 (17.4%), so the 3M POCA brain remains the
  v5 deliverable.
- ❌ The 80% acceptance bar is still not reachable on this chassis.
- ⚠️ Probe_D on the 3M POCA brain (warm-start for the 10M run) showed
  **heading churn 1132° in 4 s** (vs p8 baseline 184°). The +2.6 pp
  eval gain came from more thrashing, not better motion. The policy
  isn't driving more — it's just landing on goals more often.

## What this rules in

- **POCA helps once at 3M, then saturates.** 7M additional POCA steps
  did not improve the graded win rate.
- **More compute on the same axis is not the answer.** This was the
  explicit test of "does more POCA = more wins" — the answer is no.
- **The 80% bar is not reachable on this chassis** by tweaking POCA.

## What this does not rule out

The remaining untested axes from the p10 doc are unchanged:

| Hypothesis | Status |
|---|---|
| Discrete + continuous hybrid action space | not yet tested |
| Curriculum / goal-width lesson timing | not yet tested (POCA config has 3 lessons gated on progress which never advances) |
| Architecture change (e.g. larger / recurrent policy) | not yet tested |

## Process decision: STOP

Per the rule set declared when the 10M run was launched:

> Train until one of:
> 1. eval ≥ 25% (not reached — 17.8% peak)
> 2. three consecutive evals no improvement (1 of 1, but trajectory flat)
> 3. training diverged (clean exit, no divergence)
> 4. 7.5h wall-time budget exceeded (only ~1h55m used)

**Stopping on rule 1 + 2** — the 10M eval is at the plateau noise floor
and the trajectory is clearly flat. More POCA past 10M is unlikely to
move the needle.

## What ships as v5

- Brain: 3M POCA (`Assets/Agents/Standard_v01/STANDARD.onnx`,
  provenance "3000432 steps from soccer_p10_poca_00", eval 19.0%)
- Code: sensor-vision split (118→162 inputs), goalSpeedBonus,
  crossbarProximity, time-remaining obs, HUD/label flags
- Docs: [eval-p10-perception.md](eval-p10-perception.md) (p10 perception
  result), this file (p10 POCA result), [rl-training-tips-cheatsheet.md](rl-training-tips-cheatsheet.md)

## Files

- Train log (3M POCA): `results/soccer_p10_poca_00/logs/trainer-stdout.log`
- Eval JSON (3M POCA): `results/eval/soccer_p10_poca_00.json` — 19.0%
- Train log (10M POCA): `results/soccer_p10_poca_10M_00/logs/trainer-stdout.log`
- Eval JSON (10M POCA): `results/eval/soccer_p10_poca_10M_00.json` — 17.8%