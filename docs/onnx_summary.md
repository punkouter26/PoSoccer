# ONNX & Component Grid — PoSoccer

Generated **2026-08-05** against `master` @ `5f25701`.

> **The project currently ships zero `.onnx`.** All four personality models
> (`STANDARD`, `MATT`, `NICK`, `KIM`) were deleted from `Assets/Agents/` on 2026-08-05.
> They declared **102** inputs against a **118**-input runtime, so the inference loader
> rejected them outright — every player already fell back to `Agent_HeuristicBot`.
> `brainModel` is `null` on all five profiles and `m_Model` is cleared on both agents in
> `SCN_Training`. A GUID sweep of `Assets/` returns zero hits for all four.
>
> The game is fully playable; it fields rule-based bots until a 26-obs export exists.

---

## Matrix A — Model Metadata

### A.1 Shipped models (assigned to a profile / scene)

| Prefab / Profile | ONNX File | Size | mtime | I/O Tensors | Params | Run ID | Final Reward | Promotion Status |
|---|---|---|---|---|---|---|---|---|
| `Reward_STANDARD` | — | — | — | — | — | — | — | **none — `brainModel: null`** |
| `Reward_MATT` | — | — | — | — | — | — | — | **none — `brainModel: null`** |
| `Reward_NICK` | — | — | — | — | — | — | — | **none — `brainModel: null`** |
| `Reward_KIM` | — | — | — | — | — | — | — | **none — `brainModel: null`** |
| `Reward_BOT` | — | — | — | — | — | — | — | **null by design** — always fields `Agent_HeuristicBot`; this is how a trained brain gets graded against the bot inside a normal match |

### A.2 Available artifact (on disk, not assigned)

| Prefab / Profile | ONNX File | Size | mtime | I/O Tensors | Params | Run ID | Final Reward | Promotion Status |
|---|---|---|---|---|---|---|---|---|
| *(unassigned)* | `results/soccer_p9_random_00/STANDARD/STANDARD-14999792.onnx` | 396,760 B | 2026-08-05 08:47 | in `obs_0 [b,66]`, `obs_1 [b,52]` → out `[b,4]` + value `[b,1]` | ~97.5 k *(derived)* | `soccer_p9_random_00` | **not graded** | **NOT PROMOTED** — run incomplete |

**Why it is not promoted.** `soccer_p9_random_00` stopped at **14,999,792 of 20,000,000
`max_steps`** and never wrote a final export (there is no bare `STANDARD.onnx`, only
numbered checkpoints). There is no `results/eval/soccer_p9_random_00.json`, so it has
**never been evaluated**. `checkpoint.pt` (2,353,250 B) survives, so the run is resumable
via `-Resume`.

**Parameter count is derived from `network_settings`, not read from the graph:**
`118×256 + 256` (30,464) + `256×256 + 256` (65,792) + policy head `256×4 + 4` (+4 log σ)
+ value head `256×1 + 1` ≈ **97,545** trainable, plus running-normalizer statistics.

### A.3 I/O contract (shared by every export)

| Tensor | Shape | Source |
|---|---|---|
| `obs_0` | `[batch, 66]` | `Sensor_Vision` — `(4 tags + 2) × (2×5 + 1)` |
| `obs_1` | `[batch, 52]` | 26 vector obs × 2 stacked |
| **total in** | **118** | changing this obsoletes every export |
| action out | `[batch, 4]` continuous | forward · lateral · turn · boost |
| value out | `[batch, 1]` | critic head |

Behavior name `STANDARD` (from `brainName`), `DecisionRequester` period **8**. The contract
is asserted in `Agent_Soccer.Awake`, so the scene cannot drift from it.

### A.4 Graded history

Every measurement that exists, from `results/eval/*.json`. The raw run directories for
p6–p8 were pruned on 2026-08-05; these JSONs are the surviving record.

| Run | Episodes | Blue win | Red win | Stalemate | Mean steps | Mean reward |
|---|---|---|---|---|---|---|
| `baseline` (bot vs bot) | 40 | **42.5%** | 42.5% | 15.0% | 4,409 | +0.450 |
| `soccer_v2_standard` | 100 | 15.0% | 71.0% | 14.0% | 4,374 | −1.190 |
| `soccer_v3_00` | 100 | 18.0% | 64.0% | 18.0% | 4,595 | −0.340 |
| `soccer_p3_botcurric_00` | 100 | 19.0% | 69.0% | 12.0% | 3,978 | −1.140 |
| `soccer_p4_vsbot_00` | 100 | 24.0% | 62.0% | 14.0% | 4,264 | −0.161 |
| `soccer_p5_paced_00` | 100 | 24.0% | 58.0% | 18.0% | 4,764 | −0.088 |
| `soccer_p6_seeing_00` | 1000 | 17.1% | 65.9% | 17.0% | 4,524 | −0.273 |
| `soccer_p7_scoring_00` | 1000 | 16.6% | 69.0% | 14.4% | 4,264 | −0.578 |
| `soccer_p8_pilot_00` | 1000 | 16.9% | 69.1% | 14.0% | 4,321 | −0.325 |
| `p6_vs_bot050` (half-strength bot) | 1000 | 17.4% | 34.4% | **48.2%** | 6,358 | +0.275 |

**Reading this table.**

- **Parity is 42.5%**, not 50% — bot-vs-bot is symmetric, so the harness is fair and every
  brain really is worse than the scripted bot.
- **Never rank two models on 100-episode runs.** Ten repeat evals of the *identical* model
  measured 11, 12, 13, 14, 15, 15, 17, 18, 23, 24 — ordinary binomial noise (SD ≈ 3.7 at
  n=100). The four 100-episode rows above carry ±~8 points of uncertainty; the 24.0% for
  p4/p5 is not a real improvement over 17%. `evaluate.ps1 -Episodes` now defaults to 1000.
- **`p6_vs_bot050` is the decisive experiment.** The same p6 policy against a half-strength
  bot: win rate **flat** (17.1% → 17.4%) while 31 points of losses became draws. Scoring is
  pinned near 17% regardless of opponent — an offense problem, not perception and not
  opponent strength.
- **Runs before `p4` were silently self-play.** Both agents carried `BehaviorType.Default`,
  routing everything to the trainer, so `Agent_HeuristicBot` never ran and the
  `bot_strength` curriculum had nothing to act on. Any `bot_strength` claim in a pre-p4
  config is fiction.

The benchmark bar — **≥80% wins with ≤10% stalemates** — is badly unmet.

---

## Matrix B — Component Grid

Per-agent rig. `SCN_Training` holds exactly **two** agents (1v1); `SCN_Exhibition` clones to
the chosen squad size.

| Creature / Player | Rig Component | Functional Description | Behavioral Purpose |
|---|---|---|---|
| All | `Rigidbody2D` | 75 kg, `linearDamping` 0.7 (set from code, not the scene), zero in-plane gravity | The entire body. There is no articulated rig — one force, one torque |
| All | `Agent_Soccer` | ML-Agents `Agent`. 26 obs × 2, 4 continuous actions, traction-limited locomotion, dense + terminal rewards | The brain contract and the whole control loop |
| All | `Sensor_Vision` | Configures `RayPerceptionSensorComponent2D`: 11 rays, 300° arc, 24 m, sphere-cast r 0.1, tags Ball/Wall/Goal/Agent | Opponent and wall awareness. Ball position already arrives via vector obs, so coverage beats angular precision |
| All | `RayPerceptionSensorComponent2D` | Emits `obs_0 [66]` | Raycast observation channel |
| All | `BehaviorParameters` | `brainName` STANDARD, 26×2 vector obs, 4 continuous actions, `m_Model` null | Routes the agent to trainer, inference, or heuristic |
| All | `DecisionRequester` | Period 8 | One decision per 8 physics steps = 12.5 Hz at Δt 0.01 s |
| All | `Agent_Stamina` | Boost budget; power floor 0.6 when spent; wear 0.002/s while boosting, floored at 60% of max | Exertion degradation. **No recovery path** — documented trade-off, `rules-exemptions.md` §4 |
| All | `Agent_HeuristicBot` | Rule-based benchmark opponent. Receives `nearestOpponent` as a live `Rigidbody2D` — exact position and velocity, unlimited range | The bar to beat. Also the live fallback whenever `brainModel` is null, which is *every* player today |
| All | `SpriteRenderer` | Body sprite, tinted `playerColor` | Personality identity |
| All | `Collider2D` | Body collision. **Always set `size` explicitly** — a collider added to a sprite-less object auto-sizes to 0.0001 and silently disables all collisions | Physical contact and ball interaction |
| All | *(runtime)* `Agent_SoccerView` | Static helper, not a component. Builds body colour, team-coloured eye, 4-`LineRenderer` team frame and identity letter | Readability at a glance on a portrait phone |
| Pitch | `Agent_EnvController` | Owns episode flow, spawn cache, `EpisodeEnded` event, opponent queries | The environment. Fires `EpisodeEnded` *before* reset so subscribers can read final rewards |
| Pitch | `Reward_GoalTrigger` | Goal-mouth trigger | Terminal goal detection |
| Pitch | `Agent_TrainingGrid` | Clones the pitch into a 16-pitch grid when a trainer is connected or eval mode is on | Parallel sample collection |
| Pitch | `Agent_EvalStats` | Aggregates across the grid, writes JSON, quits | The evaluation gate |

### Personality differentiation

Bodies are **identical by design**; brains are interchangeable. Personality lives in the
`Reward_Settings` asset only.

| Profile | Role | Distinguishing terms |
|---|---|---|
| `STANDARD` | Balanced baseline | `goalScorer` 1.2 |
| `MATT` | Striker | `goalScorer` 1.4 |
| `KIM` | Wall / defender | `goalScorer` 1.3, `defensivePositionScale` > 0 |
| `NICK` | Midfielder | `possessionScale` > 0 |
| `BOT` | Rule-based benchmark | no brain, ever |

**The locomotion mechanics must match the code defaults across all five profiles.**
Editing a field initializer in `Reward_Settings.cs` does **not** touch an existing
`.asset`. That divergence cost four training runs: `ballProximityScale` sat at 0.0004
against a code default of 0.002 (5× too weak) while `actionJitterScale` sat at 0.001
against 0.0004 (2.5× too strong) — a **12× swing against moving** on STANDARD, 50× on KIM.
Standing still became the local optimum and 20M steps found it. Pinned since by
`RewardProfiles_MatchCodeDefaultsOnMechanics` (EditMode). Keep that test green.

### Measured locomotion

| Metric | Chassis capability | Scripted bot | Trained policy (p8) |
|---|---|---|---|
| Distance in 4 s (10.44 m away) | — | 15.08 m | **2.62 m** |
| Reached the ball | — | 2.70 s | **never** |
| Top speed | 9.54 m/s | 5.17 m/s | **0.95 m/s** |
| Heading churn | — | 79° | 199° |

Run `Agent_PlayMode_MovementProbe` (specifically `Probe_D_ChaseEfficiency_BlueTrainedSide`
— the trained side is always **Blue**) against every export before trusting any win-rate
theory. It is the only measurement that separates "bad at soccer" from "cannot move", and
three runs' worth of hypotheses once rested on the latter.
