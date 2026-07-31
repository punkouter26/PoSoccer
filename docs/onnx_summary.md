# ONNX & Component Grid — PoSoccer

Generated 2026-07-31 (during `soccer_p2_00` training, step ~7.1M/30M).

## Matrix A — Model Metadata

All models share the same I/O contract:
**inputs** `obs_0 [batch,66]` (11-ray vision × 6) + `obs_1 [batch,36]` (18 vector obs × 2 stacked) ·
**outputs** `continuous_actions [batch,3]`, `deterministic_continuous_actions [batch,3]`, `version_number`, `memory_size=0` (no LSTM).

| Prefab / slot | ONNX file | Size | mtime (age) | Params | Net | Run ID | Final/latest mean reward | Promotion status |
|---|---|---|---|---|---|---|---|---|
| **AgentBlue/Red (+2) — the GUID-stable inference slot** | `Assets/Agents/Standard_v01/STANDARD.onnx` | 379 KB | 2026-07-31 10:27 (today) | **93,140** | 2×256 | `soccer_p1e_00` (20M PPO vs bot) | −0.29 cum. reward (vs bot) | ✅ **PROMOTED** — wired in SCN_Training + SCN_Exhibition (GUID `f1597e34…`); eval 18% old physics / **32% corner-fixed physics, 0% stalemates** |
| candidate (in training) | `results/soccer_p2_00/STANDARD/STANDARD-6999621.onnx` (latest ckpt) | 1.27 MB | 2026-07-31 14:15 | **317,140** | 2×512 | `soccer_p2_00` (30M POCA self-play, running) | +0.87 @ 7M (self-play, not comparable to vs-bot) | ⏳ PENDING — auto-promoted by `update-model.ps1` on run completion, then eval gate |
| — legacy | `results/soccer_p1c_00/SoccerAgent.onnx` (+1 ckpt) | 357 KB | 2026-07-30 | ~93k | 2×256 | `soccer_p1c_00` (legacy physics, **legacy behavior name `SoccerAgent`**) | 22% eval, 41% stalemates | ❌ **STALE** — obsolete brain contract (14-obs legacy), archive candidate |
| — abandoned | `results/soccer_p1d_00/STANDARD/*.onnx` (5 ckpts, 6.5–7.5M) | 371 KB ea | 2026-07-31 00:25 | ~93k | 2×256 | `soccer_p1d_00` (aborted mid-run at obs-contract change) | n/a | ❌ **STALE** — superseded by p1e, archive candidate |

**Pruning note:** stale runs are not deleted while training is live; `scripts/tensorboard.ps1`
archives all but the newest 3 runs to `results/_archive/` on next TensorBoard start (UNITY_RULES).
`update-model.ps1 -Behavior` already falls back for the legacy `SoccerAgent` export name.

**⚠ Warm-start finding:** `soccer_p2_00` was launched `--initialize-from soccer_p1e_00`, but the
phase-2 config widened the network 256→512; ML-Agents logged `Failed to load for module Policy`
and re-initialized. Only shape-matching tensors leaked through (`log_sigma [3]` — hence p2's
starting entropy 0.976 exactly equals p1e's final entropy). **The p2 brain is effectively
training from scratch with a low-noise exploration head start.** See `creatures.html` §Diagnostics.

## Matrix B — Component Grid (per player / shared rig)

Bodies are identical component stacks; **personality = `Reward_Settings` asset + physique + brain**.

| Player | Rig component(s) | Functional description | Behavioral purpose |
|---|---|---|---|
| **STANDARD** (silver, 1.0×, 75 kg) | `Agent_Soccer` + `BehaviorParameters` + `DecisionRequester(8)` + `Sensor_Vision` + `Agent_Stamina` + `Agent_HeuristicBot` (fallback) + `Rigidbody2D`/`BoxCollider2D` | Full ML agent: 102-input policy, 3 continuous actions, slew-limited 700 N drive | Balanced baseline; the trained benchmark brain |
| **MATT** (orange, 1.25×, 95 kg) | same stack, `Reward_MATT.asset` | Biggest body: wins shoving duels, blocks lanes, slowest to arrive/turn | THE STRIKER — scorer 1.0, shoot gradient 0.0011, lowest conceding fear, hates draws |
| **KIM** (cyan, 0.9×, 66 kg) | same stack, `Reward_KIM.asset` | Compact, quick, disciplined (tightest jitter rein) | THE WALL — conceded −1.2, paid 0.0006/step to screen ball→own-goal lane |
| **NICK** (purple, 0.85×, 60 kg) | same stack, `Reward_NICK.asset` | Smallest & fastest, easiest to shove | THE MIDFIELDER — possession 0.0006/step inside 1.2 m, assist = goal (0.5/0.5) |
| *(shared pitch)* | `Agent_EnvController`, `Agent_PitchGuard` (runtime corner arcs + slick walls), `Reward_GoalTrigger`, `Agent_TrainingGrid` (16× clones), `Agent_EvalStats` | Episode orchestration, corner-jam prevention, eval JSON | Environment layer — identical for all brains |

Balance invariants (see `docs/players.md`): terminal budget Σ=2.2, dense budget Σ=0.0016,
top-speed momentum m·v_max equal for all masses.
