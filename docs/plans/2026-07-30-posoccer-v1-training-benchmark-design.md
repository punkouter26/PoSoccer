# PoSoccer v1 — Training Benchmark Design Spec

## Summary
V1 ships PoSoccer as a **training benchmark**: a headless ML-Agents pipeline that produces a 1v1 PPO policy which beats the rule-based heuristic bot in **≥80% of evaluation episodes**, plus an in-editor/spectator playback of the trained policy. No human input, no menus, no art beyond the existing programmer art. Playable mobile modes, 2v2/3v3 rosters, and visual polish are explicitly deferred.

## Context
The environment already exists and play-tests clean: `Assets/Agents/SoccerAgent_v01/` (agent, stamina, vision sensor, heuristic bot, reward settings), `Assets/Scripts/` (env controller, goal triggers, 16-pitch grid, bootstrap, UI Toolkit HUD), scene `Assets/Scenes/SCN_Training.unity`, trainer configs `config/SoccerAgent_v01_phase1_ppo.yaml` / `_phase2_poca.yaml`, ops scripts under `scripts/`. ML-Agents C# 4.1.0 and Python mlagents 1.2.0.dev0 are pinned to the same clone commit. What v1 still lacks is an **objective finish line**: nothing measures win-rate, and no one has run the training loop end to end.

## Design

### Architecture
```
scripts/train-phase1.ps1 ──> mlagents-learn (PPO, curriculum) ──> results/<run>/SoccerAgent.onnx
                                                                      │ scripts/update-model.ps1 (in-place, GUID stable)
                                                                      ▼
Builds/PoSoccer/PoSoccer.exe (headless, 16 pitches)   Assets/Agents/SoccerAgent_v01/SoccerAgent_v01.onnx
                                                                      │
scripts/evaluate.ps1 ──> eval build run (Blue=model, Red=bot) ──> results/eval/<run>.json ──> pass/fail vs 80% bar
```

### Components
| Component | Responsibility | File Path |
|---|---|---|
| `Agent_EvalStats` (new) | Subscribes to `Agent_EnvController.EpisodeEnded` on every pitch; counts per-team goals, stalemates, episode lengths; writes JSON and calls `Application.Quit` after `POSOCCER_EPISODES` episodes | `Assets/Scripts/Agent_EvalStats.cs` |
| `evaluate.ps1` (new) | Sets `$env:POSOCCER_EVAL = "1"` / `$env:POSOCCER_EPISODES` before `Start-Process` of the built player (`-batchmode -nographics`); waits (30 min timeout), parses `results/eval/<run>.json`, prints win-rate verdict vs the 80% bar, exits nonzero on fail | `scripts/evaluate.ps1` |
| `Agent_Soccer` (modify) | In `Awake()` (already configures `BehaviorParameters` before policy init, [Agent_Soccer.cs:53–67]): when `Environment.GetEnvironmentVariable("POSOCCER_EVAL") == "1"`, set Blue → `BehaviorType.InferenceOnly` (model from the serialized `BehaviorParameters.Model` slot), Red → `BehaviorType.HeuristicOnly` | `Assets/Agents/SoccerAgent_v01/Agent_Soccer.cs` |
| `Agent_EnvController` (modify) | Expose `public event System.Action<Agent_Soccer.Team?> EpisodeEnded` (winning team, `null` = stalemate); fire it from `OnGoalScored` / `OnStalemate` **before** `EndEpisode`/`ResetPitch`, so subscribers can still sample `agent.GetCumulativeReward()` and `StepCount` for the JSON means | `Assets/Scripts/Agent_EnvController.cs` |
| `Agent_TrainingGrid` (modify) | Also clone the 16-pitch grid when `POSOCCER_EVAL=1` (today `onlyWhenTraining` skips cloning without a trainer connected, which would leave eval on 1 pitch) | `Assets/Scripts/Agent_TrainingGrid.cs` |
| Model bootstrap (one-time op) | After the first training run: `update-model.ps1` copies the first `.onnx` to `Assets/Agents/SoccerAgent_v01/SoccerAgent_v01.onnx`, Unity imports it (generates the `.meta`/GUID that later overwrites preserve), assign it to both agents' `BehaviorParameters.Model` slot via MCP, save scene, commit the `.meta` | — |
| Phase 1 training run (op) | Execute `train-phase1.ps1` to `max_steps` 5M; early-stop when the eval bar passes at two consecutive checkpoints (decided — see Decision Log) | `scripts/train-phase1.ps1` (exists) |
| EditMode tests (new) | Stamina drain/recharge/wear math; reward constants match PRD; heuristic bot turns toward ball; `Agent_EvalStats` tally/JSON logic via direct `EpisodeEnded` invocations | `Assets/Tests/EditMode_SoccerAgent.cs` |
| PlayMode test (new) | Load `SCN_Training`, locate the `Pitch` root scene object (no prefab exists), teleport ball into `GoalBlue` trigger → episode ends, −1.0/+0.7 applied, pitch resets | `Assets/Tests/PlayMode_GoalFlow.cs` |

Note: `Assets/Tests/` requires an asmdef referencing `Unity.MLAgents` + `Assembly-CSharp` (folder depth 1 — within the 2-level rule).

### Data Model
`results/eval/<run>.json` (written by `Agent_EvalStats`):
```json
{
  "runId": "soccer_p1_00",
  "episodes": 100,
  "blueWins": 84, "redWins": 9, "stalemates": 7,
  "meanEpisodeSteps": 1830.5,
  "meanBlueReward": 0.61,
  "modelFile": "SoccerAgent_v01.onnx",
  "timestampUtc": "2026-07-30T21:00:00Z"
}
```
Win = the opposing net's `Reward_GoalTrigger` fires; stalemate = `maxEnvironmentSteps` (5000) reached.

### Data Flow (evaluation)
1. `evaluate.ps1 -RunId soccer_p1_00 -Episodes 100` → ensures `update-model.ps1` copied the run's `.onnx` into `Assets/Agents/SoccerAgent_v01/SoccerAgent_v01.onnx`, rebuilds headless player if stale.
2. Script sets `$env:POSOCCER_EVAL = "1"` and `$env:POSOCCER_EPISODES = "100"`, then `Start-Process Builds\PoSoccer\PoSoccer.exe -ArgumentList "-batchmode","-nographics"` (env vars inherit into the child process — they are not CLI tokens).
3. On startup, `Agent_Soccer.Awake` flips behavior types (Blue inference / Red heuristic) and `Agent_TrainingGrid` clones the 16-pitch grid because the eval var is set.
4. `Agent_EvalStats` (on the `Pitch` root, cloned with the grid) subscribes to every pitch's `EpisodeEnded` and aggregates via a static counter; at the target episode count it writes the JSON and calls `Application.Quit`.
5. Script parses JSON: **pass** if `blueWins / episodes ≥ 0.80` and `stalemates / episodes ≤ 0.10`; prints verdict and exits nonzero on fail (CI-friendly).

## Error Handling
| Failure | Behavior |
|---|---|
| No `.onnx` at the tracked path | `evaluate.ps1` aborts with "train first"; `Agent_Soccer` falls back to heuristic and `Agent_EvalStats` marks the run `"invalid": true` |
| Eval player hangs | `evaluate.ps1` timeout (default 30 min) → kill via `cleanup-training.ps1`, report fail |
| Training crash mid-run | `mlagents-learn --resume` supported; `finally` block already kills orphans |

## Testing Strategy
- **EditMode (5)**: stamina drains 60/s and recharges 25/s; wear lowers `EffectiveMax` but never below `wearFloor`; `Reward_Settings` defaults equal PRD table; `Agent_HeuristicBot.ComputeActions` yields positive turn for a ball to the left; `Agent_Soccer.Opponent` mapping.
- **PlayMode (2)**: load `SCN_Training`, find the `Pitch` root object, call `NotifyBallTouch(redAgent)` then force the ball into `GoalBlue` → episode resets, Blue gets −1.0 and Red (last toucher) +0.7; boost with zero stamina applies 1× (not 2.2×) force. (Without the touch call the scorer path pays `teamBaselineVictory` +0.2 — see `Agent_EnvController.OnGoalScored`.)
- **End-to-end baseline**: `evaluate.ps1 -Baseline` sets `POSOCCER_BASELINE=1` → both teams `HeuristicOnly`, JSON tagged `"baseline": true`; expected ~50% win-rate validates the harness before it judges the trained model.

## Decision Log
| Decision | Options Considered | Chosen | Rationale |
|---|---|---|---|
| V1 deliverable | training benchmark / mobile game / both | **Training benchmark** | User choice; policies must exist before a game vs AI is meaningful |
| Human input | spectator only / touch joystick / tap-to-target | **Spectator only** | User choice; removes input & UX scope from v1 |
| Acceptance bar | ≥80% vs bot / ELO milestone / curves only | **≥80% win-rate vs heuristic bot (100 eps, ≤10% stalemates)** | User choice; objective, cheap to measure, maps to Phase 1 |
| Art direction | programmer art / asset-pack facelift / Blender backdrop | **Programmer art** | User choice; free assets + Blender GLB delivered as optional, unwired extras — nothing depends on them |
| Eval mode switch | separate eval scene / CLI flag / env-var switch | Env vars `POSOCCER_EVAL` + `POSOCCER_EPISODES`, read in `Agent_Soccer.Awake` / `Agent_TrainingGrid.Awake` | Zero scene duplication; set by `evaluate.ps1` before `Start-Process`; read before policy init so `BehaviorType` applies cleanly |
| Eval parallelism | 1 pitch / clone grid in eval too | Clone 16 pitches in eval (`POSOCCER_EVAL` bypasses `onlyWhenTraining`) | 100 episodes finish ~16× faster; identical physics per pitch |
| 2v2 / 3v3 | in v1 / deferred | Deferred to v2 | Bar is 1v1; POCA config already exists for v2 |
| Phase 1 early-stop | fixed 5M steps / eval-gated | Eval at each checkpoint; stop after two consecutive passes | Saves compute once the bar is met; two passes filters checkpoint noise |
