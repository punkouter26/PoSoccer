# Player Roster

Every player shares the same body, senses, and 3-action contract (`Agent_Soccer`).
A player = a **brain name** (ML behavior + its own trained `.onnx`) + a **reward
profile** (`Reward_Settings` asset — the personality DNA) in a versioned folder.

| Player | Personality | Folder | Reward profile | Brain |
|---|---|---|---|---|
| **STANDARD** | Balanced baseline | `Assets/Agents/Standard_v01/` | `Reward_STANDARD.asset` | `STANDARD` (legacy-physics runs: 8%, 22%; realistic-physics 20M: 18% old / 32% corner-fixed physics) |
| **MATT** | The Striker — biggest scoring reward + shoot gradient, lowest conceding fear, hates draws, urgent movement | `Assets/Agents/Matt_v01/` | `Reward_MATT.asset` | `MATT` (designed, untrained) |
| **KIM** | The Wall — deepest conceding fear, screens the ball→own-goal lane (`defensivePositionScale`), patient and smooth | `Assets/Agents/Kim_v01/` | `Reward_KIM.asset` | `KIM` (designed, untrained) |
| **NICK** | The Midfielder — paid for close control (`possessionScale`), loves the assist, stays central, passes up shots | `Assets/Agents/Nick_v01/` | `Reward_NICK.asset` | `NICK` (designed, untrained) |
| **BOT** | The Benchmark — not a personality. The rule-based `Agent_HeuristicBot` (chase, line up ball→goal, push, unstick) that every trained brain is measured against. Reward values mirror STANDARD and are inert in exhibition play. | `Assets/Agents/Bot_v01/` | `Reward_BOT.asset` | none, permanently — `brainModel` stays null so `Agent_MatchLoader` always falls back to the bot |

**Balance rule (unique but equally good):** every profile spends the same total
incentive budget — terminal stakes (`goalScorer + assist + teamBaselineVictory +
|goalConceded|`) = **2.2** and dense shaping (`ballProximityScale +
facingAlignmentScale + ballToGoalVelocityScale + defensivePositionScale +
possessionScale`) = **0.0016** — allocated differently per personality. Movement
penalties (`stepPenalty`, `actionJitterScale`, `wallProximityPenalty`) are style
flavor, not budget. Keep both sums invariant when tuning a personality.

**Physique (also balanced):** `bodyScale`/`bodyMass` in the profile. Drive force
and drag are shared, so top-speed momentum (mass × max speed) is identical for
every mass — big bodies trade speed for shove power, small bodies the reverse.
MATT 1.25×/95 kg (big, slow bulldozer), STANDARD 1.0×/75 kg, KIM 0.9×/66 kg,
NICK 0.85×/60 kg (small, quick). Applied in `Agent_Soccer.Start`.

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


## Measured: the four personalities are behaviourally indistinguishable (2026-08-27)

Multi-run evaluation means, 1000 episodes each unless noted:

| Profile | Steps trained | Blue wins |
|---|---|---|
| STANDARD | 30.0M | 16.2% |
| MATT | 2.5M | 17.2% |
| NICK | 2.5M | 17.2% |
| KIM | 2.5M | 16.2% |

A 12x difference in training budget produced a spread smaller than the
measurement noise (SD ~1.2 at n=1000). The reward-DNA differences between the
profiles - `defensivePositionScale`, `possessionScale`, tuned `goalScorer` - are
real in the config and invisible in the result.

**Decision:** keep the roster. It is the game's hook, the bodies and colours
differ, and the menu reads better with four names. But stop paying to train four
separate brains until *one* of them beats the bot: four runs cost four times the
compute to produce one number. Train STANDARD, ship it to all four slots, and
treat the personalities as cosmetic loadouts plus reward DNA that has not yet
been shown to matter.

Revisit once the benchmark is met - personality divergence is a plausible thing
to tune *after* there is a policy worth differentiating.
