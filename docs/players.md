# Player Roster

Every player shares the same body, senses, and 3-action contract (`Agent_Soccer`).
A player = a **brain name** (ML behavior + its own trained `.onnx`) + a **reward
profile** (`Reward_Settings` asset — the personality DNA) in a versioned folder.

| Player | Personality | Folder | Reward profile | Brain |
|---|---|---|---|---|
| **STANDARD** | Balanced baseline | `Assets/Agents/Standard_v01/` | `Reward_STANDARD.asset` | `STANDARD` (legacy-physics runs: 8%, 22%; realistic-physics run in progress) |
| **MATT** | The Striker — glory hunter, shoots on sight, hates draws, abandons defense | `Assets/Agents/Matt_v01/` | `Reward_MATT.asset` | `MATT` (designed, untrained) |
| **KIM** | The Wall — conceding hurts 2×, screens the ball→own-goal lane (`defensivePositionScale`), patient and smooth | `Assets/Agents/Kim_v01/` | `Reward_KIM.asset` | `KIM` (designed, untrained) |
| **NICK** | The Midfielder — paid for close control (`possessionScale`), loves the assist, stays central, passes up shots | `Assets/Agents/Nick_v01/` | `Reward_NICK.asset` | `NICK` (designed, untrained) |

Custom look: drop a per-player square texture into their folder (e.g.
`Matt_v01/MATT_square.png`) and assign it to the agent's SpriteRenderer sprite;
the team tint is just the SpriteRenderer color and can be removed per player.

Shared engine code lives in `Assets/Scripts/`; the tracked model slot and
physics materials live in `Assets/Agents/Standard_v01/`.

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
