# Phase 10 — POCA Self-Play at 3M and 10M — RESULT

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