# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

PoSoccer: a top-down 2D physics soccer game + **ML-Agents training benchmark**. Unity 6 (6000.5.4f1, 2D URP, mobile portrait) + Python `mlagents` trainer. Two faces, one codebase:
- **Benchmark**: train a brain that beats the rule-based bot ≥80% of eval episodes with ≤10% stalemates (spec: `docs/plans/2026-07-30-posoccer-v1-training-benchmark-design.md`).
- **Game**: `SCN_Menu` → pick 2v2 matchups from a roster of four personalities → exhibition match with scoreboard, first-to-5, goal toasts, stadium lighting and sound.

## Hard rules (UNITY_RULES)

- Scene setup goes through **Unity MCP tools only** — never editor scripts for scene construction. (Building things *at runtime* in gameplay code — lights, UI, particles — is fine and used heavily.)
- Script prefixes `Agent_` / `Sensor_` / `Reward_` (`Agent_` is the blanket project prefix, covering non-agents like `Agent_UIStyle`); scenes `SCN_`; folder depth ≤2 under `Assets/`; agent assets in `<Name>_v<NN>` folders.
- UI Toolkit only (runtime, code-built, shared `PanelSettings` at ScaleWithScreenSize 1170×2532 match-width; style constants in `Agent_UIStyle`). Fixed timestep 0.01s, portrait.
- **Version parity**: embedded `Packages/com.unity.ml-agents` (4.1.0 @ ab179e18, protobuf-swapped — see landmines) must match Python `mlagents` (1.2.0.dev0, editable from `/ml-agents` clone in `.venv`, Python ≤3.10.12).
- Trained `.onnx` overwrites happen **in place** (GUID-stable slot: `Assets/Agents/Standard_v01/STANDARD.onnx`). Kill orphaned trainers after runs; TensorBoard restarts prune/archive runs.

## Commands

```powershell
# Training (headless, 4 env processes x 16 pitches; run detached via Start-Process)
.\scripts\train-phase1.ps1 -RunId <run> -EnvPath Builds\PoSoccer\PoSoccer.exe -NumEnvs 4 `
    [-Config <yaml in config/>] [-InitFrom <run>] [-Resume]     # configs: STANDARD_phase*.yaml
.\scripts\train-phase2.ps1 -InitFrom <run> ...                  # MA-POCA self-play (next lever)

# Evaluation gate (exit 0 pass / 1 fail / 2-3 setup errors)
.\scripts\evaluate.ps1 -RunId <run> -Episodes 100               # trained blue vs bot
.\scripts\evaluate.ps1 -Baseline -Episodes 40                   # bot vs bot sanity (~50%)

.\scripts\update-model.ps1 -RunId <run>    # results onnx -> GUID-stable Assets slot
.\scripts\tensorboard.ps1                  # :6006; keeps newest 3 runs, archives rest
.\scripts\cleanup-training.ps1             # kill orphaned trainers/env players
```

Builds: use MCP `manage_build(action="build", target="windows64", output_path="Builds/PoSoccer/PoSoccer.exe", scenes="Assets/Scenes/SCN_Training.unity")` from the running editor (async — grep `Logs/Editor.log` for "Build Finished"; "Success" can still follow earlier partial output, verify `PoSoccer_Data/Managed`). `scripts/build-headless.ps1` only works with the editor closed.

Tests via MCP `run_tests`, **always scoped**: `assembly_names: ["PoSoccer.EditModeTests"]` / `["PoSoccer.PlayModeTests"]` (unscoped sweeps 200+ UnitySkills package tests). Tests live in `Assets/Tests/{EditMode,PlayMode}`.

## Working with the live editor (MCP)

Editor is normally already running; CoplayDev "MCPForUnity" + IvanMurzak "UnityMCP" are in `.mcp.json`. If MCP tools aren't in-session, drive the CoplayDev server with a FastMCP stdio client executing a JSON call list (spawn `uv run --project .tooling/coplay-unity-mcp/Server mcp-for-unity`). Bridge status: `~/.unity-mcp/unity-mcp-status-*.json` (poll `"reason":"ready"`).

Hard-won traps:
- **Play mode blocks everything scene-side** ("This cannot be used during play mode") — the user often has Play running; `manage_editor stop` first, then edit, then let them replay.
- `refresh_unity` after C# edits drops the MCP connection (domain reload) — run alone; "Connection closed" = success.
- Colors/Vector2 in `component_properties` are objects `{"r":..}`/`{"x":..}`, not arrays. Lists of MonoBehaviour refs don't resolve — code self-discovers (see `Agent_EnvController.Start`).
- **Collider added to a sprite-less object auto-sizes to 0.0001** — always set `size` explicitly. This once silently disabled all collisions.
- **Overwriting a PNG in place can corrupt its cached sprite geometry** (renders misshapen/wrong size) — create a new file name instead (`ball.png`, `tile.png`, `pitch.png` exist for this reason).
- `manage_asset create` only supports Material/PhysicsMaterial — author ScriptableObjects/PhysicsMaterial2D as YAML text files + refresh.
- **An unfocused editor barely runs play-mode frames** — unattended play + screenshots look "frozen" while the game is fine. Verify behavior with PlayMode tests (test runner forces frames; see `HeuristicBots_ActuallyMove`) or the headless build. `runInBackground=1` is set; "Enter Play Mode Options" must stay OFF (ML-Agents Academy needs domain reload).
- Unity 6.5 turns deprecations into errors (`GetInstanceID`, `TreeView`, old `Object.FindObjectsByType` overloads) — affects our code and third-party package version choices.
- The move/rename asset tool sometimes reports failure while succeeding — verify on disk before retrying.

## Architecture (cross-file)

**Brain contract** (enforced in `Agent_Soccer.Awake`, scene can't drift): behavior name from `brainName` (default `STANDARD`), **18 vector obs** (14 self/ball/goal + 4 teammate zero-padded in 1v1) × **2 stacked**, 3 continuous actions (move/turn/boost), DecisionRequester period 8. Changing any of this obsoletes every trained `.onnx`.

**Realistic physics** (post-overhaul): 75 kg agents, 700 N drive with 2300 N/s slew, 250 N·m torque, 360°/s rotation cap, stamina-scaled power (0.6 floor), FIFA ball (r=0.11 world, 0.43 kg, drag ~0.1 randomized per episode, Magnus curl + spin transfer). Zero in-plane gravity (top-down).

**Episode flow**: `Reward_GoalTrigger` → `Agent_EnvController.OnGoalScored/OnStalemate/OnOutOfBounds` (containment watchdog) → terminal rewards (+0.7 scorer / +0.3 assist / −1.0 conceded / −0.1 stalemate) → `EpisodeEnded` event fires **before** reset (subscribers read cumulative rewards; HUD/FX/audio all hang off it) → `ResetPitch` (domain randomization: random own-half spawns, ball drag) reads `goal_width` curriculum. `stepCapOverride` on the exhibition Pitch shortens episodes to 2500 steps for pace.

**Personalities**: a player = `Reward_Settings` asset (`Assets/Agents/<Name>_v01/Reward_<NAME>.asset`) = reward DNA + `playerName`/`playerColor`/`brainModel`. Roster: STANDARD (balanced, trained), MATT (striker), KIM (wall, `defensivePositionScale`), NICK (midfielder, `possessionScale`) — see `docs/players.md`. Bodies identical by design; brains interchangeable. Body wears `playerColor`, **team shows as thick outline frame + eye color** (built at runtime in `Agent_Soccer.Start` along with the initial letter label).

**Game flow**: `Agent_MainMenu` (4 picker rows, untrained players badged "(BOT)") → statics in `Agent_MatchSetup` → `Agent_MatchLoader` (order −60) applies profile/brain per agent (`brainModel` null ⇒ heuristic bot). `Agent_HUD` = scoreboard band + match clock + identity chips + goal/RESET toasts + first-to-5 end panel (match flow off in SCN_Training). `Agent_Stadium` builds 2D lights/shadows/post at runtime; `Agent_MatchFX` trail/shake/squash/boost particles; `Agent_Audio` velocity-scaled SFX + reactive crowd (placeholder WAVs in `Assets/Audio` — Store-pack clips drop into the same fields).

**Eval mode** is env-var driven (set by `evaluate.ps1` pre-launch, read in `Awake`): `POSOCCER_EVAL/BASELINE/EPISODES/RUNID/OUT`; `Agent_EvalStats` (Pitch root) aggregates across the 16-pitch grid, writes JSON, quits. `Agent_TrainingGrid` clones pitches when a trainer is connected or eval mode is on.

**Assemblies**: `PoSoccer.Runtime` (refs: `Unity.ML-Agents` [hyphen], `Unity.InferenceEngine`, `Unity.InputSystem`, `Unity.RenderPipelines.Universal.Runtime` + `.2D.Runtime` + Core).

## Landmines

- **Protobuf**: ML-Agents + com.unity.ai.inference both shipped `Google.Protobuf_Packed.dll`; player builds resolved the editor-only twin (CS0400). Fixed by embedding the package with stock NuGet `Google.Protobuf.dll` 3.21.12 + asmdef refs updated. Never reintroduce the packed dll; file-renaming breaks the linker (assembly identity).
- Scene order in Build Settings: `SCN_Training` must stay index 0 (headless training/eval boot it). Menu/Exhibition follow.
- Old runs exported under legacy behavior name `SoccerAgent`; `update-model.ps1 -Behavior` falls back automatically.

## State (2026-07-31)

Realistic-physics STANDARD: 20M steps, eval **18%** (trend 10→18 per 10M; never left curriculum Lesson0). Bar 80%/≤10% unmet — next levers: Phase 2 POCA self-play (`-InitFrom soccer_p1e_00`), personality brain runs, or bar recalibration. Eval reports in `results/eval/*.json`. Free Asset Store picks (optional, zero dependencies): `docs/asset-store-free-assets.md`.
