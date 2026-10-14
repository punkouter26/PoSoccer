# Phase 10 — Perception (Specialized Ray Sensors) — RESULT

**Status:** Negative result. Phase 10 hit the same plateau as p5-p9.

## What was tested

Implementation of the "AI Learns to Play Soccer" specialized-raycast tip
(see [rl-training-tips-cheatsheet.md](rl-training-tips-cheatsheet.md)
section 2): one `RayPerceptionSensor2D` per object class.

| Channel | Before (p9) | After (p10) |
|---|---|---|
| Ray sensors | 1 (4-tag, 11 rays @ 300°) | **4** (Ball, Goal, Opponents, Walls) |
| Ray inputs | 66 | 108 |
| Vector obs (×2 stacks) | 52 | 54 (+1 time-remaining scalar) |
| **Total model inputs** | **118** | **162** |
| `BaseObservationSize` | 26 | 27 |
| New reward terms | — | `goalSpeedBonus 0.05`, `crossbarProximity 0.0005` |

Plus: `time-remaining` scalar in vector obs, `goalSpeedBonus` on scoring side
anchored on last toucher's elapsed steps, `crossbarProximity` for close-range
shot gradient. All three default to small/visible values; existing profiles
opt in by leaving defaults in place.

## What was measured

### 3M-step pilot

| Step | Mean Reward |
|---|---|
| 50k | −0.747 |
| 500k | −0.278 |
| 1.0M | +0.107 |
| 1.5M | +0.195 |
| 2.0M | +0.073 |
| 3.0M | **+0.199** |

Training signal crossed zero around 600k steps and stayed positive through
the end. Trainer healthy throughout: 0 stderr exceptions, 4 env players
bound, 50k steps per 52 s (~22 min wall time for 3M).

### `Probe_D_ChaseEfficiency_BlueTrainedSide` (PlayMode test)

| Metric | p8 baseline | **p10** | Verdict |
|---|---|---|---|
| distance travelled in 4 s | 0.99 m (9%) | **2.90 m (28%)** | ✅ 3× better |
| reached the ball | never | **never** | ❌ still no |
| top speed | 0.58 m/s | **1.14 m/s** | ✅ 2× better |
| mean speed | — | 0.72 m/s | — |
| heading churn | 184° | **316°** | ❌ worse |

Phase 10 taught the policy to drive faster and farther — but it spins more
(316° vs 184°) and still never reaches the ball in 4 s. The motion is more
energetic, not more directed.

### 1000-episode eval (`scripts/evaluate.ps1 -Episodes 1000`)

```json
{
  "runId": "soccer_p10_perception_00",
  "episodes": 1000,
  "blueWins": 164,
  "redWins": 654,
  "stalemates": 182,
  "meanEpisodeSteps": 4553.58,
  "meanBlueReward": -0.128,
  "modelFile": "SoccerAgent_v01.onnx",
  "baseline": false,
  "invalid": false
}
```

**16.4% blue win rate, 65.4% red (bot), 18.2% stalemates.**

## Comparison with the p5-p9 plateau

| Run | What changed | Win rate (1000 ep) |
|---|---|---|
| p5 | baseline (small sensor) | 16.2% |
| p6 | + opponent vec obs | 17.1% |
| p7 | + reward table fix | 16.6% |
| p8 | + locomotion drift fix | 16.9% |
| p9 | + randomized bot strength | 17.4% |
| **p10** | **+ 4 specialized ray sensors + style shaping** | **16.4%** |

At n=1000, SD ≈ 1.2 pp. The −1.0 pp swing vs p9 is well within noise.
**p10 is statistically indistinguishable from p5.**

## Interpretation

- Perception was not the gap. The policy-vs-bot asymmetry that motivated
  phase 10 (ray vs exact-position reading) does not show up in the win-rate
  metric at the policy capability we have today.
- The policy *did* learn something — top speed doubled, travel distance
  tripled — but those gains translated to **one percentage point of win
  rate change**, which is the noise floor.
- Heading churn got *worse*. The policy is moving faster but spinning
  more, suggesting it learned "thrash" rather than "drive". This is the
  next thing to investigate.

## What this rules out

- ❌ Specialized ray sensors alone do not unlock the win rate.
- ❌ "Score with style" terminal shaping (goalSpeedBonus + crossbarProximity)
  does not move the win rate.
- ❌ The 80% acceptance bar will not be reached by tweaking perception OR
  the current reward table OR curriculum.

## What this does not rule out

The locomotion gain is real but uncoordinated. The most likely
remaining-causes (none of which the previous runs tested):

| Hypothesis | Why it still fits | Cheapest test |
|---|---|---|
| ~~**Action jitter is the bottleneck.** `actionJitterScale` penalizes per-step action change. A policy that learned "be smooth" learns to spin in place rather than commit to a direction; it cannot express "cut hard toward the ball".~~ | ~~p8 doubled top speed but eval didn't move. p10 doubled top speed again but eval didn't move. The common pattern is "more motion, same win rate" — exactly what an anti-jitter reward produces.~~ | **TESTED 2026-08-11 → RULED OUT.** `Probe_E_ChaseEfficiency_JitterZeroed` (in-memory jitter=0 mutation, asset untouched): distance 2.70 → 3.68 m (better), top speed 1.13 → 1.54 m/s (better), mean speed 0.68 → 0.92 m/s (better), heading churn 311° → 364° (**worse**, more spinning per second). Removing the smoothness reward makes the policy *more* energetic but still does not reach the ball. Hypothesis falsified. |
| **The action space is unrepresentable.** 4 continuous floats to express "intercept + position + shoot" every decision step. A bot reads the world 1:1 and acts in 1 frame. | All 6 runs learned the same shape: creep + spin. That's the signature of a control problem, not a perception problem. | Pilot 1M with a discrete + continuous hybrid (`BehaviourProvider` + discrete action head). |
| **The opponent distribution is wrong.** `bot_strength` uniform, but the *kind* of opponent never varies. The policy overfits to the bot's quirks. | p9 partially tested this with uniform strength; p9 didn't test varying *kind*. | POCA self-play with frozen-old-self opponent pool (per the video's "communication collider" tip; phase 2 POCA configs exist). |

## Process lesson

> Always pilot then gate. Phase 10's contribution is **negative evidence**:
> perception is not the gap. That is a real finding, and it short-circuits
> every future "more sensors / wider arc / more tags" suggestion.

> **Do not** promote to a 20M-step phase 11 without a single-variable test
> of one of the three remaining hypotheses first.

## Files

- Code: [Assets/Scripts/Agents/Sensor_Vision.cs](../Assets/Scripts/Agents/Sensor_Vision.cs),
  [Agent_Soccer.cs](../Assets/Scripts/Agents/Agent_Soccer.cs),
  [Agent_EnvController.cs](../Assets/Scripts/Agents/Agent_EnvController.cs),
  [Reward_Settings.cs](../Assets/Scripts/Agents/Reward_Settings.cs)
- Config: [config/STANDARD_phase10_perception.yaml](../config/STANDARD_phase10_perception.yaml)
- Cheat sheet: [rl-training-tips-cheatsheet.md](rl-training-tips-cheatsheet.md)
- Train log: `results/soccer_p10_perception_00/logs/trainer-stdout.log`
- Eval JSON: `results/eval/soccer_p10_perception_00.json`
- Commit: `19fe1ad` on `master`