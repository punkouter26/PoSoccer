# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

PoSoccer ("Agent Soccer 2D"): a top-down 2D physics soccer **ML-Agents training benchmark** — Unity 6 (6000.5.4f1, 2D URP, mobile portrait) on the C# side, Python `mlagents` on the trainer side. V1 goal: a trained 1v1 policy that beats the rule-based bot in ≥80% of eval episodes with ≤10% stalemates (`docs/plans/2026-07-30-posoccer-v1-training-benchmark-design.md` is the reviewed spec). Playable game modes are deferred; spectator/inference only.

## Hard rules (UNITY_RULES — non-negotiable)

- Scene setup goes through **Unity MCP tools only** — never write editor scripts for scene construction.
- Script prefixes `Agent_` / `Sensor_` / `Reward_`; scene prefix `SCN_`; folder depth ≤2 under `Assets/`; agent assets in `<Name>_v<NN>` folders. `Agent_` is the blanket project prefix — it deliberately covers non-agent systems too (`Agent_UIStyle`, `Agent_Stadium`, `Agent_MainMenu`).
- UI Toolkit only (no UGUI/IMGUI). Fixed timestep 0.01s, 60 FPS target, portrait 9:16.
- **Version parity**: the C# package `Packages/com.unity.ml-agents` (embedded, 4.1.0 @ ab179e18) and Python `mlagents` (1.2.0.dev0, installed editable from the `/ml-agents` clone into `.venv`) must stay on the same commit. Re-pulling the clone requires re-syncing both.
- Overwrite trained `.onnx` files **in place** (GUID preservation); clean up orphaned training processes; prune stale TensorBoard runs on restart.

## Commands

```powershell
# Training (headless, 4 env processes x 16 pitches; detached-safe)
.\scripts\train-phase1.ps1 -RunId <run> -EnvPath Builds\PoSoccer\PoSoccer.exe -NumEnvs 4 `
    [-Config <yaml in config/>] [-InitFrom <prev-run>] [-Resume]
.\scripts\train-phase2.ps1 ...            # MA-POCA self-play (2v2/3v3, v2)

# Evaluation (the acceptance gate; exit 0 = pass, 1 = fail, 2/3 = setup errors)
.\scripts\evaluate.ps1 -RunId <run> -Episodes 100      # trained blue vs bot
.\scripts\evaluate.ps1 -Baseline -Episodes 40          # bot vs bot (~50% sanity check)

# Pipeline pieces
.\scripts\update-model.ps1 -RunId <run>   # copy results onnx -> Assets (GUID-stable slot)
.\scripts\build-headless.ps1              # only when the editor is CLOSED; else use MCP manage_build
.\scripts\tensorboard.ps1                 # restarts clean at :6006
.\scripts\cleanup-training.ps1            # kill orphaned trainers/env players
```

Tests run through MCP (`run_tests`), **always scoped** — an unscoped run sweeps 200+ UnitySkills package tests: `run_tests(mode="EditMode", assembly_names=["PoSoccer.EditModeTests"])`, PlayMode likewise. Test code: `Assets/Tests/{EditMode,PlayMode}`.

## Working with the live editor (MCP)

The Unity editor is normally already running. Two MCP servers are configured in `.mcp.json` (CoplayDev "MCPForUnity" + IvanMurzak "UnityMCP"). If MCP tools aren't in-session, drive the CoplayDev server with a FastMCP stdio client script (pattern: `fastmcp.Client` + `StdioTransport` spawning `uv run --project .tooling/coplay-unity-mcp/Server mcp-for-unity`), executing a JSON list of tool calls. Bridge status: `~/.unity-mcp/unity-mcp-status-*.json` (PoSoccer = port 6400; poll for `"reason":"ready"`).

Quirks that will bite you:
- `refresh_unity` after C# edits drops the MCP connection (domain reload) — run it alone; "Connection closed" = success. Poll the status file before continuing.
- `component_properties` colors/Vector2 need object form `{"r":..}` / `{"x":..}`, not arrays.
- **Adding a collider to an object whose SpriteRenderer has no sprite yet auto-sizes it to 0.0001** — always set `size` explicitly afterward. This once silently disabled all collisions.
- Lists of MonoBehaviour references don't resolve via `{"name":...}` — code self-discovers instead (`Agent_EnvController.Start`).
- `manage_asset create` only supports Material/PhysicsMaterial — author other assets (ScriptableObjects, physics materials) as Unity YAML text files, then refresh.
- `manage_build(action="build", target="windows64", output_path=...)` builds from the running editor (async; poll `action="status"` / grep "Build Finished" in `Logs/Editor.log`). "Result: Failure" can still leave partial output — verify `PoSoccer_Data/Managed` exists.
- Overwriting a PNG repeatedly can corrupt its cached sprite mesh — create a new file if a sprite renders misshapen.

## Architecture (the parts that span files)

**Env ↔ trainer contract.** `Agent_Soccer.Awake()` enforces the policy contract in code (behavior name from `brainName` [default `STANDARD`], 14 obs + 4/teammate-slot, 3 continuous actions: move/turn/boost) — scene serialization cannot drift. YAML `behaviors:` keys must match the brain name. Old runs (soccer_p1_00/p1c_00) exported under the legacy name `SoccerAgent`; `update-model.ps1` has a fallback.

**Mode switching is env-var driven** (set by `evaluate.ps1` before `Start-Process`, read in `Awake` before policy init): `POSOCCER_EVAL=1` → blue InferenceOnly (model from `BehaviorParameters.Model`) vs red HeuristicOnly; `POSOCCER_BASELINE=1` → both heuristic; `POSOCCER_EPISODES/RUNID/OUT` configure `Agent_EvalStats` (on the Pitch root; static aggregation across grid clones; writes JSON, quits). `Agent_TrainingGrid` clones 16 pitches when a trainer is connected **or** eval mode is on.

**Episode flow.** `Reward_GoalTrigger` (net trigger) → `Agent_EnvController.OnGoalScored/OnStalemate` → terminal rewards (+0.7 scorer via last-touch tracking, +0.3 assist, −1.0 conceding, −0.1 stalemate at 5000 steps) → fires `EpisodeEnded` **before** `EndEpisode`/reset (subscribers can still read cumulative rewards) → `ResetPitch` re-reads the `goal_width` curriculum parameter (6.0→4.0→2.5m).

**Personalities = reward profiles.** A "player" is a brain name + a `Reward_Settings` asset (`Assets/Agents/<Name>_v01/Reward_<NAME>.asset`); the reward mix is the personality, expressed via training. Roster and how to activate a placeholder (MATT/KIM/NICK): `docs/players.md`. All shared code lives in `Assets/Scripts/`; the tracked model slot is `Assets/Agents/Standard_v01/STANDARD.onnx`. All bodies identical by design so brains are interchangeable.

**Assemblies.** Game code is `PoSoccer.Runtime` (`Assets/PoSoccer.Runtime.asmdef`) referencing `Unity.ML-Agents` (hyphen!), `Unity.InferenceEngine`, `Unity.InputSystem`.

**The protobuf landmine.** ML-Agents' packed `Google.Protobuf_Packed.dll` name-collides with an editor-only twin in `com.unity.ai.inference`, which broke player builds (CS0400). Fix in place: the embedded package ships stock NuGet `Google.Protobuf.dll` 3.21.12 with its three asmdefs' `precompiledReferences` updated. Never reintroduce the packed dll; file-renaming it does NOT work (assembly identity mismatch breaks the linker).

**Scenes.** `SCN_Training` (index 0 in build; the training/eval scene) and `SCN_Exhibition` (brain-vs-brain inference showcase — assign models to both agents' `BehaviorParameters`). Keyboard play: disable Blue's `Agent_HeuristicBot`, set HeuristicOnly (W/S drive, A/D turn, K or Shift boost; ball interaction is pure momentum — no kick action by design).

## Training history / state

soccer_p1_00 (pure RL, 5M): eval 8%. soccer_p1c_00 (+`ballToGoalVelocityScale` shaping, warm-start, 10M): eval 22% wins / 41% stalemates — defense solved, finishing weak. Known levers: longer runs, stalemate penalty tuning, goal-reward dominance over shaping, Phase 2 self-play. Eval reports: `results/eval/*.json` (never pruned by tensorboard.ps1).
