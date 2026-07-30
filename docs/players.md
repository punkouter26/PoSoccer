# Player Roster

Every player shares the same body, senses, and 3-action contract (`Agent_Soccer`).
A player = a **brain name** (ML behavior + its own trained `.onnx`) + a **reward
profile** (`Reward_Settings` asset — the personality DNA) in a versioned folder.

| Player | Status | Folder | Reward profile | Brain |
|---|---|---|---|---|
| **STANDARD** | Active — the base brain | `Assets/Agents/Standard_v01/` | `Reward_STANDARD.asset` | `STANDARD` (runs: soccer_p1_00 8%, soccer_p1c_00 22% vs bot) |
| **MATT** | Placeholder — striker direction | `Assets/Agents/Matt_v01/` | `Reward_MATT.asset` | `MATT` (untrained) |
| **KIM** | Placeholder — defender direction | `Assets/Agents/Kim_v01/` | `Reward_KIM.asset` | `KIM` (untrained) |
| **NICK** | Placeholder — midfielder direction | `Assets/Agents/Nick_v01/` | `Reward_NICK.asset` | `NICK` (untrained) |

Shared engine code (scripts, physics materials, tracked model slot) lives in
`Assets/Agents/SoccerAgent_v01/`.

## Giving a placeholder a real personality

1. Edit their `Reward_*.asset` — the reward mix IS the play style (see notes in each asset).
2. Put the player in a scene: set `Agent_Soccer.brainName` to their name and
   `rewards` to their profile asset.
3. Add a behavior section under their name to a training YAML (can train several
   brains in one run) and train: `scripts\train-phase1.ps1 -RunId <run> -Config <yaml>`.
4. Their trained `.onnx` exports as `results/<run>/<NAME>.onnx`; copy into their
   folder and assign to `BehaviorParameters.Model` for inference/exhibitions.

`SCN_Exhibition.unity` pits any two brains against each other: assign each agent's
`BehaviorParameters.Model` + `InferenceOnly` and press Play.
