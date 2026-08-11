# RL Soccer Training Tips — Cheat Sheet

Distilled from **"AI Learns to Play Soccer (and breaks physics)"**
(Hugging Face ML-Agents unit, 2023), applied to PoSoccer's
ML-Agents PPO + Unity 2D setup.

> **Why this doc exists.** Four runs (p5-p9) plateaued at 16-17%
> win rate against the heuristic bot. The bot wins because it
> reads opponents exactly at any range; the agent cannot. The
> video's breakthrough tip was *one raycast set per object class*
> — which addresses exactly that gap. This file is the mapping
> into our codebase.

---

## 1. Stronger Neural Network Architecture

| Knob | Video | PoSoccer today | Action |
|---|---|---|---|
| `hidden_units` | 256 | 256 | **keep** |
| `num_layers` | 2 | 2 | **keep** |

256 × 2 is the sweet spot for PPO continuous control on small
input spaces. Don't shrink — the action distribution needs the
capacity to converge.

**Use when:** Always, for PPO continuous control.
**Skip when:** Quick pilots under 1M steps where a smaller model
trains faster (you've already established that 6.5M-param model
fits in the 118 → 160 input range).

---

## 2. Specialized Raycast Sets — **HIGHEST LEVERAGE FOR US**

The video's #1 breakthrough. One `RayPerceptionSensorComponent2D`
per object class instead of one fat sensor with multiple tags.

| Sensor | Tag | Rays | Half-arc | Stack ×2 |
|---|---|---|---|---|
| `Sensor_Ball` | Ball | 2 | 180° (full) | 30 |
| `Sensor_Goal` | Goal | 1 | 30° (forward) | 18 |
| `Sensor_Opponents` | Agent | 2 | 180° (full) | 30 |
| `Sensor_Walls` | Wall | 2 | 180° (full) | 30 |
| **Ray total** | | | | **108** |
| Vector obs | | | | 52 |
| **Grand total model inputs** | | | | **160** |

**Why it works for us:** the agent's opponent observation only
covers the nearest 2 opponents (vector), so it never learns
*where the defender is* when there are 3+ on the pitch. A
dedicated `Sensor_Opponents` ray at 360° gives the agent a
continuous "opponent in that direction" signal that does not
require triangulation.

**Use when:**
- Agent cannot see an object class at all (your 2026-08-04
  "opponent blind beyond 1.9 units" finding is exactly this).
- Angular resolution is hurting more than coverage (11 rays in
  300° = 27° per ray is too coarse for fine positioning).

**Cost:** Obsoletes every `.onnx`. Fine — `CLAUDE.md` says none
are assigned as of 2026-08-05; clean break on the next run.

**Skip when:** Ray sensor budget is already tight (>200 inputs
total). Not the case here.

**Where in code:** [Assets/Scripts/Agents/Sensor_Vision.cs](../Assets/Scripts/Agents/Sensor_Vision.cs)
is the new 4-sensor battery; the old 11-ray single-tag setup is
gone.

---

## 3. Direct Observations

| Channel | Why | Status |
|---|---|---|
| Time remaining (`1 - StepCount/MaxStep`) | Pacing, late-episode risk-taking | **TODO** — single float, cheap add |
| Boost cooldown (stamina ratio) | Don't fire-and-forget | Already in obs (stamina ratio) |
| Distance + direction to ball | Split is easier to learn than a single vector | Already split (relBall is `Vector2`, not a float) |
| Distance + direction to goals | Avoids triangulation from ray hits | Already in obs (relOpp, relOwn, distToOppGoal) |

**Use when:** Reward shaping is hard; an obs-side hint is cheaper
than another reward term.

**Skip when:** Obs budget is already bloated (>100 vector floats).

**Concrete add** (when you want it):
```csharp
// In Agent_Soccer.CollectObservations, after stamina:
int max = env != null ? env.MaxEnvironmentSteps : 5000;
sensor.AddObservation(1f - Mathf.Clamp01((float)StepCount / max));
```

---

## 4. Reward Shaping — Score Fast, Score Clean

| Term | Value | Effect | Status |
|---|---|---|---|
| `goalScorer` | 1.2 | Must beat stalemate EV | **keep** (p7 fix) |
| `goalConceded` | −1.0 | Loss penalty | **keep** |
| `stalemateTimeout` | −0.6 | Kills the parking-the-bus local optimum | **keep** (p7 fix) |
| `goalSpeedBonus` (new) | +0.05 / sec remaining | Reward scoring in first 60% of episode | **TODO** |
| `crossbarProximity` (new) | +0.001 / step | Reward shots launched from inside the box | **TODO** |

The current reward table is good (p7 fix landed). The video's
"score with style" idea adds a small positive gradient for
**fast** scoring and **central** shots — agents that race to
score converge faster than agents with no time pressure.

**Use when:**
- Agent learns to score but takes forever → `goalSpeedBonus`.
- Agent wins but launches from distance → `crossbarProximity`.

**Skip when:** Agent can't score at all — fix perception first.

**Concrete add** (when you want it):
```csharp
// In Reward_Settings.cs
[Tooltip("Bonus per second remaining when this side scores. Rewards fast goals.")]
public float goalSpeedBonus = 0.05f;

// In Agent_Soccer.OnGoalScored (or wherever scoring is signalled):
float secsLeft = (maxStep - stepCount) * Time.fixedDeltaTime;
_addGroupReward(secsLeft * rewards.goalSpeedBonus);
```

---

## 5. Communication Collider

Not applicable for our 1v1. **Skip.**

Keep in mind for the eventual 2v2 phase: a teammate-only "I'm open"
flag, visible only to teammates, lets the policy learn coordination
without burning an action slot. Implementation would be a
`Collider2D` on a child GameObject with a layer mask that excludes
opponent rays.

---

## Decision Tree — Which Lever to Pull

```
Symptom?                              Try first               Then
────────────────────────────          ─────────               ────
Cannot see opponents                  (2) Specialized         Add opponent-layer
Stalls instead of scoring             (4) Reward shape        (already p7-fixed)
Creeps at 6% of chassis               Check ballProximity-    (already p8-fixed)
                                      Scale drift
Wins but slow                         (3) Time-remaining obs  —
Wins but launches from distance       (4) crossbarProximity   —
Wins but scores late                  (4) goalSpeedBonus      —
Wins 11v11 but no coordination        (5) Comm collider       —
```

## Mapping to Our Runs

| Run | Lever pulled | Graded win rate | Lesson |
|---|---|---|---|
| p5 | baseline | 16.2% | Reward ≠ objective; 100 ep is noise |
| p6 | + opponent vec obs | 17.1% | Vector obs helped perception, not offense |
| p7 | reward table fix | 16.6% | Stopped stalling, but losses just rotated |
| p8 | locomotion fix | 16.9% | Sample-efficient training, no eval gain |
| **phase 10 (planned)** | **specialized ray sensors** | **TBD** | **Last untested lever from the video** |

---

## Process Rule (carried over from `CLAUDE.md`)

> Always pilot before a full run. Phase 10's config caps at **3M
> steps** (≈25 min on 4×16 pitches). Run
> `Agent_PlayMode_MovementProbe` against the resulting brain.
> If `RunChase` still measures < 2 m / 0.6 m/s, **do not** escalate
> to a 20M run — there is still something fundamentally wrong
> and more compute will not find it.