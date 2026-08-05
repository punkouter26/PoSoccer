# PoSoccer v2 — Agent Retraining Plan

## Summary

V1 trained STANDARD to a **18% win-rate plateau** after 20M steps (CLAUDE.md state).
Live play-mode observation on 2026-08-02 reproduced the failure mode: brains are
wired to all four personality `.onnx` slots (commit eab3b43), the physics is
correctly tuned, and agents idle in their halves rather than chase the ball
because the dense reward signal has been optimizing the wrong objective for
20M steps.

V2 retrains the four personalities (STANDARD + MATT + KIM + NICK) using
**MA-POCA self-play with reward re-shaping**, on the existing 36×54 m pitch.
Target: **≥80% win-rate vs the heuristic bot in 1v1 evaluation, with ≤10%
stalemates**, matching the v1 spec bar at
`docs/plans/2026-07-30-posoccer-v1-training-benchmark-design.md`.

The v1 evaluation harness (`scripts/evaluate.ps1`, `Agent_EvalStats`) is reused
unchanged. Only the **training side** changes.

## Context (what we learned from playing the game)

The game is otherwise healthy:

- `Physics2D.gravity=(0,0)` ✓ (rules-exemptions §1)
- `Physics2D.contactThreshold=0.005` ✓ (UNITY_RULES compliance fix `8efc919`)
- 4 agents correctly wire to `.onnx` models in their reward assets
  (commit `eab3b43`)
- HUD labels mount at the correct positions, score stays 0-0
- Ball is FIFA-spec (r=0.11, m=0.43) with realistic damping
- Heuristic bot is **strong** — implements closest-man pressing, boost-shot,
  shoulder-charge, unstick flanking, decelerate-into-target
  (`Assets/Scripts/Agent_HeuristicBot.cs`)

What is broken on the training side:

- Agents idle 25 m from the ball for 154 s of game time and never chase it
- Cumulative rewards during this idle: STANDARD = -0.279, NICK = +0.009,
  MATT = +0.196, KIM = +0.580 — i.e., the trained policy has learned that
  sitting near a wall and not touching the ball still scores positive dense
  reward because `stepPenalty = -0.0001` per step is overwhelmed by
  `ballProximityScale = 0.0004` whenever the agent is in the same half as
  the ball
- 18% eval plateau is exactly this: the brain learned a degenerate
  "be in my half and stay still" policy that nets positive dense reward
  without ever scoring

Two failure shapes combine:
1. **Reward shaping rewards idling** — `stepPenalty` is O(1) of the budget;
   `ballProximityScale` pays for being near the ball even when the ball
   never moves; `cornerBallPenalty` fires for both teams regardless of
   fault.
2. **Static-bot curriculum plateaus** — 20M steps against a deterministic
   bot with no ELO snapshotting means the brain improves up to bot level
   and then **no further** (Silver 2017 / Bansal 2018 / AlphaStar).

## Design

### Architecture

```
scripts/train-phase2.ps1 ──> mlagents-learn (MA-POCA, self-play)
                              │
                              ├── snapshot opponent every N updates
                              ├── ELO rating of snapshots
                              ├── curriculum: goal_width only (no reward gate)
                              │
                              ▼
                       results/<run>/STANDARD.onnx  (best ELO)
                       results/<run>/MATT.onnx
                       results/<run>/KIM.onnx
                       results/<run>/NICK.onnx
                              │ scripts/update-model.ps1 -Profile <name>
                              ▼
       Assets/Agents/<Name>_v01/<NAME>.onnx  (GUID-pinned, in-place)
                              │
                              ▼
       scripts/evaluate.ps1  →  results/eval/*.json  →  80% bar
```

### Components

| Component | Responsibility | File Path |
|---|---|---|
| **STANDARD_phase2_poca.yaml** (modify) | Lower curriculum thresholds to **0.15** (Lesson0) and **0.30** (Lesson1) so the brain can advance without first learning to score; bump `num_envs: 1 → 16`; raise `buffer_size: 20480 → 102400` and `batch_size: 2048 → 4096` to use the 16 parallel pitches | `config/STANDARD_phase2_poca.yaml` |
| `STANDARD_phase1_ppo.yaml` (modify) | Same num_envs/batch/buffer changes for Phase 1 bootstrap if used as warm-start; lower thresholds identical to phase2 | `config/STANDARD_phase1_ppo.yaml` |
| `STANDARD_phase1c_shaped.yaml` (modify) | Same — this is the most recent successful warm-start source | `config/STANDARD_phase1c_shaped.yaml` |
| `STANDARD_phase1e_resume.yaml` (modify) | Same | `config/STANDARD_phase1e_resume.yaml` |
| `TRAIN_<NAME>_v2.yaml` (new, one per personality) | Mirror STANDARD_phase2_poca.yaml but with `BehaviorName` = personality name (MATT / KIM / NICK / STANDARD) and a per-profile reward shaping pointer; uses MA-POCA | `config/TRAIN_<NAME>_v2.yaml` (4 files) |
| **`Reward_Settings`** (modify) | **Default `stepPenalty` to `0`** (was `-0.0001`); replace `ballProximityScale` semantics with a **differential proximity** helper invoked from `Agent_Soccer.ApplyDenseRewards`; team-aware `cornerBallPenalty` (only fires for the team that last touched); halve `actionJitterScale` | `Assets/Scripts/Reward_Settings.cs` |
| **`Agent_Soccer.ApplyDenseRewards`** (modify) | Implement differential proximity: `reward += (prevDist - curDist) * scale`. Pure chasing yields ~0 reward, *approaching faster than the teammate* yields positive. Track `prevBallDist` per agent, recompute each FixedUpdate. | `Assets/Scripts/Agent_Soccer.cs` (~line 384) |
| **`Agent_Soccer.OnActionReceived`** (modify) | Gate `IsBoosting` on `Vector2.Dot(intent, forwardAxis) > 0.5` — boost only fires when intent is at least half-forward. Prevents boost burn on strafe. | `Assets/Scripts/Agent_Soccer.cs` (~line 332) |
| **`Agent_HeuristicBot`** (modify) | Add `POSOCCER_BOT_STRENGTH ∈ [0..1]` env-var knob read in `Awake`. Low strength = longer reaction time + simpler support-positioning logic. Curriculum opponent. | `Assets/Scripts/Agent_HeuristicBot.cs` |
| **`scripts/train-phase2.ps1`** (modify) | Add `--initialize-from` arg (already present) and a `-BotStrength` arg that exports `POSOCCER_BOT_STRENGTH` for the env. Loop the strength 0.3 → 1.0 across curriculum lessons. | `scripts/train-phase2.ps1` |
| **`scripts/train-all.ps1`** (new) | Run all four personality trainings sequentially, each starting from the Phase 1 STANDARD warm-start, with 16 envs and 20M-step budget per profile. Total ~80M steps wall-clock. | `scripts/train-all.ps1` |
| **`scripts/evaluate.ps1`** (modify) | Already supports `-Profile`. Add `-PersonalityOpponent` to evaluate a trained personality against the heuristic bot (not against itself). | `scripts/evaluate.ps1` |

### Reward re-shaping (concrete numbers)

| Signal | Before | After | Why |
|---|---|---|---|
| `stepPenalty` | -0.0001 | **0** | Step cost is `-0.5` over a 5000-step episode — washes out gradients. Terminal reward already provides temporal credit. |
| `ballProximityScale` | +0.0004 per step at d=0 | `(prevDist - curDist) * 0.002` | Differential. Crowd both agents on the ball → both get ~0; one approaches faster than the other → the faster one gets the reward. Cures double-team crowding in 2v2 self-play. |
| `ballToGoalVelocityScale` | 0.001 | unchanged | Already correctly signed. |
| `actionJitterScale` | 0.001 | **0.0004** | Hard cuts are *correct* for soccer (cutting inside the box). The current penalty is teaching the brain to be smooth and idle. |
| `cornerBallPenalty` | -0.0006 for both teams | **-0.0006 for the team that last touched** | Don't bleed reward for a corner the opponent created. |
| `teamBaselineVictory` | 0.2 | **0.1** | Half credit for being on the winning team without touching the ball — currently rewards passive play. |

### MA-POCA self-play (concrete recipe)

ml-agents 1.2.0.dev0 supports MA-POCA self-play via the `self_play` block under
each behavior. Phase 2 config:

```yaml
behaviors:
  STANDARD:
    trainer_type: poca
    hyperparameters:
      batch_size: 4096
      buffer_size: 102400
      learning_rate: 3.0e-4
      learning_rate_schedule: linear
      beta: 5.0e-3
      epsilon: 0.2
      lambd: 0.95
      num_epoch: 3
    network_settings:
      normalize: true
      hidden_units: 256
      num_layers: 2
    reward_signals:
      extrinsic:
        gamma: 0.99
        strength: 1.0
    self_play:
      window: 10
      play_against_latest_model_ratio: 0.5
      save_steps: 50000
      swap_steps: 2000
      team_change: 100000
    max_steps: 20000000
    time_horizon: 512
    summary_freq: 20000
    checkpoint_interval: 250000
    keep_checkpoints: 5
    threaded: false

environment_parameters:
  goal_width:
    curriculum:
      - name: Lesson0_WideNet
        completion_criteria:
          measure: reward
          behavior: STANDARD
          signal_smoothing: true
          min_lesson_length: 50
          threshold: 0.15
        value: 6.0
      - name: Lesson1_MidNet
        completion_criteria:
          measure: reward
          behavior: STANDARD
          signal_smoothing: true
          min_lesson_length: 50
          threshold: 0.30
        value: 4.0
      - name: Lesson2_TightNet
        value: 2.5

env_settings:
  num_envs: 16

engine_settings:
  time_scale: 20
  target_frame_rate: -1
  capture_frame_rate: 60
  no_graphics: true
```

### Curriculum philosophy change

**Before:** "Advance only when mean reward ≥ 0.35 / 0.55." This is a
chicken-and-egg gate — you can't achieve 0.35 mean reward without scoring,
and you can't easily score at Lesson0 until you've trained enough against
the bot. The brain gets stuck at Lesson0 (CLAUDE.md state: never left
Lesson0 in 20M steps).

**After:** Lower thresholds (0.15 / 0.30) AND shorten `min_lesson_length`
from 100 to 50. The brain can advance with **gradual improvement**, not
peak performance.

**Why this works:** the curriculum is teaching the brain to chase a smaller
target, not to score. As long as the brain's reward is climbing, advancing
the curriculum gives it a better-aligned task.

### Phased execution plan

| Phase | Steps | Goal | Expected eval lift |
|---|---|---|---|
| **Phase A**: Reward re-shaping + curriculum thresholds + `num_envs=16` | 5M | Confirm new reward signal is non-degenerate; should see immediate win-rate climb to 30-40% | +20-30% |
| **Phase B**: Add MA-POCA self-play | 10M | Brain improves past heuristic bot ceiling; target 50-65% | +20-25% |
| **Phase C**: Drop action jitter penalty; team-aware corner penalty | 5M (continuation from B's checkpoint) | Cleaner movement + sharper cuts in the box | +5-10% |
| **Phase D**: Bot strength curriculum (0.3 → 1.0) | 5M | Curriculum opponent ensures the brain doesn't over-fit to a single opponent policy | +5% |
| **Total** | **25M** | — | **target ≥80%** |

If Phase A alone fails to break 25% eval, the diagnosis is wrong (likely a
bug in the differential proximity implementation). If Phase B alone stalls
at 50%, the issue is self-play opponent quality (snapshotting too
infrequently, or `play_against_latest_model_ratio` too low).

### Personalities

MATT (striker), KIM (defensive wall), NICK (midfielder) each get their own
training run starting from the Phase A STANDARD checkpoint as warm-start.
The reward re-shaping is the same across personalities; the difference is
the per-profile `defensivePositionScale` / `possessionScale` settings
(already in their `Reward_<NAME>.asset` files).

Training all four personalities sequentially from a shared Phase A warm-start
is the cheapest path that doesn't re-learn shared dynamics from scratch.

## Data Flow (retraining)

1. **Phase A warm-start.** Resume `soccer_v2_standard` checkpoint with the
   new Phase A config (5M steps). Export `POSOCCER_PROFILE=STANDARD`,
   `POSOCCER_BOT_STRENGTH=1.0`.
2. **Phase A eval.** Run `evaluate.ps1 -RunId soccer_v2a_00 -Episodes 40`.
   Compare against the 18% baseline. **Decision gate**: if eval < 25%, stop
   and re-examine the reward re-shaping.
3. **Phase B self-play.** Same warm-start, with `STANDARD_phase2_poca.yaml`
   (10M steps, `trainer_type: poca`, `self_play` block).
4. **Phase B eval.** Run `evaluate.ps1 -RunId soccer_v2b_00 -Episodes 100`.
   **Decision gate**: target ≥50% win-rate vs heuristic bot.
5. **Phase C** (5M steps continuation) + **Phase D** (5M bot-strength
   curriculum). Final eval at `soccer_v2d_00`.
6. **Personality runs.** For each of MATT/KIM/NICK, copy the final
   STANDARD checkpoint as warm-start, set `BehaviorName` to the personality
   in the YAML, and run 10M steps. Update model in place.
7. **Final eval.** `evaluate.ps1 -PersonalityOpponent STANDARD -Episodes 100`
   against each personality's brain. Each must score ≥80% against the
   heuristic bot.

## Error Handling

| Failure | Diagnosis | Action |
|---|---|---|
| Phase A eval <25% | Reward re-shaping broke the signal | Revert to differential-only, re-run |
| Phase B self-play stalls | Opponent snapshotting frequency wrong | Lower `swap_steps` to 500; raise `play_against_latest_model_ratio` to 0.7 |
| Eval vs bot ≥80% but eval vs self <30% | Brain overfit to heuristic bot patterns | Add Phase D bot strength curriculum |
| Personality eval <70% | Per-profile reward shaping (defensivePositionScale etc.) too strong | Reduce by 50%, re-run that personality from the STANDARD checkpoint |
| `mcpforunity://scene/gameobject/...` lookup fails mid-training | Editor instance flapping between PoSoccer and PoSumo | `set_active_instance 4d02d63b` (recurring; documented in CLAUDE.md) |
| `setup-training-env.ps1 -Force` fails on protobuf .pyd lock | Unity Editor holds the file | Stop Unity Editor, retry; documented in repo state |

## Risks

1. **Reward re-shaping may regress STANDARD** before it improves. The 20M-step
   checkpoint encodes the old reward shape; resuming it into a new reward
   function means unlearning old policies. **Mitigation**: Phase A is only 5M
   steps — short enough that we can re-baseline. If regression, restart from
   scratch with the new reward shape (no warm-start).
2. **Self-play diverges**. MA-POCA without a strong initial policy can oscillate
   between "agent kicks ball straight up" and "agent kicks ball straight down"
   without converging. **Mitigation**: warm-start from Phase A. The 5M-step
   Phase A policy is the floor that self-play must improve on.
3. **Wall-clock cost**. 25M steps × 4 personalities = 100M steps total. At
   the project's 4-env headless rate (~5 steps/s/env), this is **~5800 hours**.
   With `num_envs=16` and the 16-pitch grid: **~1450 hours = 60 days**.
   **Mitigation**: Phase A is a fast filter — if it doesn't lift eval, abort
   before Phase B. Personality runs can be skipped if Phase D doesn't show
   a strong trend.
4. **Active-ragdoll articulation is still deferred** (`rules-exemptions.md §2`).
   Any articulation change obsoletes all 4 `.onnx` files. v2 does NOT touch
   the body model.

## Open Decisions

| Question | Default if no answer | Better answer? |
|---|---|---|
| Should Phase B warm-start from Phase A checkpoint? | Yes | If Phase A regresses (risk 1), restart Phase B from scratch |
| Personality runs in parallel or sequential? | Sequential | Parallel needs 4× the cores; sequential fits the existing 4-env setup |
| Goal-width curriculum at all in Phase B? | Yes, with new thresholds | Some self-play papers (AlphaStar) skip curriculum entirely; could simplify |
| Keep `teamBaselineVictory` at 0.2 or drop to 0.1? | 0.1 | If Phase A still rewards passive play at 0.1, drop to 0.05 or 0 |
| `play_against_latest_model_ratio` initial value? | 0.5 | Bansal 2018 uses 0.5; AlphaStar uses 0.35 |

## Open items (resolved vs new)

**Resolved during this work**:
- Reward re-shaping is now specified with concrete numbers
- Curriculum threshold values chosen (0.15 / 0.30 instead of 0.35 / 0.55)
- MA-POCA self-play config written
- Bot strength curriculum identified as Phase D lever

**New open items**:
- `Reward_Settings.cs` defaults will need to be re-tuned (maybe move
  rebalanced values back into each personality's `.asset` rather than
  touching the SO defaults)
- `Agent_HUD._shownGoalWidth=NaN` bug noticed during play-mode observation
  (2026-08-02). Fix as a separate ticket; not blocking training.
- `Agent_MatchLoader` direct-scene-load fallback gives `rewards=null` when
  scene opens without menu visit. Fix by wiring serialized `defaultBlue` etc.
  on `SCN_Exhibition.unity`'s `Agent_MatchLoader`. Separate ticket.
- The `Action_/Effect_` decision on `POSOCCER_BOT_STRENGTH` curriculum:
  should it be a one-shot gradient or a slow drift? Default: gradient with
  0.1 increments every 1M steps.

## Validation

Per phase, before advancing:

1. `evaluate.ps1 -Baseline -Episodes 40` → expected **~50%** (bot vs bot,
   symmetry). If not, the bot itself has drifted — bug.
2. `evaluate.ps1 -RunId soccer_v2a_00 -Episodes 40` → expected **≥25%** if
   Phase A reward re-shaping works.
3. `evaluate.ps1 -RunId soccer_v2b_00 -Episodes 100` → expected **≥50%** if
   Phase B self-play works.
4. `evaluate.ps1 -RunId soccer_v2d_00 -Episodes 100` → expected **≥80%** if
   Phase D curriculum works.
5. Personality evals at the end. Each personality must score **≥80%** vs
   the bot; if any falls below 70%, its per-profile shaping is wrong.

## References

- v1 spec: `docs/plans/2026-07-30-posoccer-v1-training-benchmark-design.md`
- Current rules-exemptions: `docs/rules-exemptions.md`
- Top-10 movement review (which generated this plan): 2026-08-02 conversation
- Play-mode observation findings: 2026-08-02 conversation
- ML-Agents self-play docs:
  https://docs.unity3d.com/Packages/com.unity.ml-agents@4.0/manual/ML-Agents-Overview.html
- Bansal et al. 2018, "Emergent Complexity via Multi-agent Competition":
  https://arxiv.org/abs/1710.03748
- Silver et al. 2017, "Mastering Chess and Shogi by Self-Play":
  https://arxiv.org/abs/1712.01815
