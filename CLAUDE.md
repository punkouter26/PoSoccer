# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

PoSoccer: a top-down 2D physics soccer game + **ML-Agents training benchmark**. Unity 6 (6000.5.6f1, 2D URP, mobile portrait) + Python `mlagents` trainer. Two faces, one codebase:
- **Benchmark**: train a brain that beats the rule-based bot ≥80% of eval episodes with ≤10% stalemates (spec: `docs/plans/2026-07-30-posoccer-v1-training-benchmark-design.md`).
- **Game**: `SCN_Menu` → pick 2v2 matchups from a roster of four personalities → exhibition match with scoreboard, first-to-5, goal toasts, stadium lighting and sound.

## Hard rules (UNITY_RULES)

- Scene setup goes through **Unity MCP tools only** — never editor scripts for scene construction. (Building things *at runtime* in gameplay code — lights, UI, particles — is fine and used heavily.)
- Script prefixes `Agent_` / `Sensor_` / `Reward_` (`Agent_` is the blanket project prefix, covering non-agents like `Agent_UIStyle`); scenes `SCN_`; folder depth ≤2 under `Assets/`; agent assets in `<Name>_v<NN>` folders.
- UI Toolkit only (runtime, code-built, shared `PanelSettings` at ScaleWithScreenSize **1080×1920** (9:16) match-width; style constants in `Agent_UIStyle`). Fixed timestep 0.01s, portrait **locked** (`defaultScreenOrientation: 0`, landscape autorotate off).
- **Version parity**: embedded `Packages/com.unity.ml-agents` (4.1.0 @ ab179e18, protobuf-swapped — see landmines) must match Python `mlagents` 1.2.0.dev0. Pins live in `requirements-training.txt`; `scripts/setup-training-env.ps1` builds `.venv` from that commit and **fails loudly on drift**. Python ≤3.10.12.
- Trained `.onnx` overwrites happen **in place** (GUID-stable slot per personality: `Assets/Agents/<Name>_v01/<NAME>.onnx`). Kill orphaned trainers after runs; TensorBoard restarts prune/archive runs.
- **Git branch: `master`.** Do not create other branches unless explicitly asked. (`main` still exists on the remote; the GitHub default has not been flipped yet.)
- Deliberate departures from these rules are recorded in `docs/rules-exemptions.md` — an audit finding not on that page is a real defect.

## Commands

```powershell
# One-time / after any parity bump: build .venv from the pinned ml-agents commit
.\scripts\setup-training-env.ps1 [-Force]   # clones .tooling/ml-agents, editable installs, verifies

# Training (headless, 4 env processes x 16 pitches; run detached via Start-Process)
.\scripts\train-phase1.ps1 -RunId <run> -EnvPath Builds\PoSoccer\PoSoccer.exe -NumEnvs 4 `
    [-Config <yaml in config/>] [-InitFrom <run>] [-Resume]     # configs: STANDARD_phase*.yaml
.\scripts\train-phase2.ps1 -InitFrom <run> ...                  # MA-POCA self-play (next lever)

# Evaluation gate (exit 0 pass / 1 fail / 2-3 setup errors)
.\scripts\evaluate.ps1 -RunId <run> -Episodes 100               # trained blue vs bot
.\scripts\evaluate.ps1 -Baseline -Episodes 40                   # bot vs bot sanity (~50%)

.\scripts\update-model.ps1 -RunId <run> [-Profile STANDARD|MATT|KIM|NICK]   # onnx -> GUID-stable slot
.\scripts\tensorboard.ps1                  # :6006; keeps newest 3 runs, archives rest
.\scripts\cleanup-training.ps1             # kill orphaned trainers/env players
```

Builds: use MCP `manage_build(action="build", target="windows64", output_path="Builds/PoSoccer/PoSoccer.exe", scenes="Assets/Scenes/SCN_Training.unity")` from the running editor (async — grep `Logs/Editor.log` for "Build Finished"; "Success" can still follow earlier partial output, verify `PoSoccer_Data/Managed`). `scripts/build-headless.ps1` only works with the editor closed.

Tests via MCP `run_tests`, **always scoped**: `assembly_names: ["PoSoccer.EditModeTests"]` / `["PoSoccer.PlayModeTests"]` (unscoped sweeps 200+ UnitySkills package tests). Tests live in `Assets/Tests/{EditMode,PlayMode}`.

## Working with the live editor (MCP)

Editor is normally already running. **The server that actually works is `unityMCP`, registered in `.claude/settings.json` at `http://localhost:8080/mcp`** (CoplayDev toolset: `manage_scene`, `manage_gameobject`, `read_console`, `refresh_unity`, `run_tests`, …). `.mcp.json` holds the IvanMurzak `UnityMCP` binary (moved to port **8090** — 8080 is taken) and the `ai-game-developer` HTTP connector.

- Bridge status: `~/.unity-mcp/unity-mcp-status-*.json` (poll `"reason":"ready"`).
- **Multiple editors share port 6400.** If another project (e.g. PoSumo) is open, tool calls fail with "Instance hash does not match"; call `set_active_instance` with the hash from the status file first.
- The ~80 `assets-*` / `gameobject-*` / `scene-*` **skills** in `.claude/skills/` belong to the IvanMurzak server. Its plugin config (`UserSettings/AI-Game-Developer-Config.json`) is `connectionMode: "Cloud"`, so those skills stay unusable until the `ai-game-developer` connector is authorized.

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
- **The PlayMode test runner can wedge on the first run after a package-install domain reload** — it reports `stuck_suspected` with `last_update` frozen, and `run_tests` then returns `tests_running` forever. It is *not* a real test failure: `manage_editor stop` + a forced script recompile clears it, and the suite passes normally afterward. Do not start reverting code over this.

## Architecture (cross-file)

**Brain contract** (enforced in `Agent_Soccer.Awake`, scene can't drift): behavior name from `brainName` (default `STANDARD`), **18 vector obs** (14 self/ball/goal + 4 teammate zero-padded in 1v1) × **2 stacked**, **4 continuous actions** (forward / lateral / turn / boost), DecisionRequester period 8. Changing any of this obsoletes every trained `.onnx` — the 3-action `STANDARD.onnx` from the 20M-step run **is already obsolete** and is no longer assigned anywhere.

**Trained brains work and are wired** (verified 2026-08-03, correcting an earlier note that called them inert). All four personality `.onnx` in `Assets/Agents/{Standard,Matt,Nick,Kim}_v01/` are the 2026-08-02 `soccer_v2_*` exports and declare `obs_0 [batch, 66]` + `obs_1 [batch, 36]` = 102 inputs, which is exactly what today's runtime produces: `Sensor_Vision` = 11 rays × (4 tags + 2) = 66 under ML-Agents 4.1.0's `OutputSize()`, plus 18 vector obs × 2 stacked = 36. `Agent_Soccer.Awake` auto-adds `Sensor_Vision` if absent, so the contract can't drift. `brainModel` is assigned on all four `Reward_*.asset` profiles; play-tested in `SCN_Exhibition` with zero inference errors (HUD chip reads AI). A newer unexported STANDARD run sits at `results/soccer_v3_00/` (~15.7M steps, same 102-input shape, checkpoints only — no final `.onnx` at the run root).

**Realistic physics** (traction overhaul): 75 kg reference agent, **236 N/75 kg drive** (force scales with mass, so every physique shares one top speed and heavier bodies simply carry more momentum), 1200 N/s slew, linearDamping **0.7** set from code, 250 N·m torque. Locomotion is **traction-limited**: all foot force — launching, cutting, braking — shares one friction circle of `mu * m * g` (mu 1.2), with active foot braking when there is no drive intent and extra lateral damping so strafing is slower than running. Turn rate falls from 360°/s at rest to 25% of that at sprint. Measured: **4.35 m/s jog / 9.54 m/s sprint, t95 ≈ 3.7 s** (human-like build-up). FIFA ball (r=0.11, 0.43 kg, drag ~0.1 randomized, Magnus curl). Zero in-plane gravity (top-down — see `docs/rules-exemptions.md`); gravity enters physically via the traction budget.

**Movement probe**: `Agent_PlayMode_MovementProbe` reports locomotion numbers to the console (chassis / as-shipped / forced-bot). Use it instead of watching the Game view — an unfocused editor barely runs play-mode frames.

**Episode flow**: `Reward_GoalTrigger` → `Agent_EnvController.OnGoalScored/OnStalemate/OnOutOfBounds` (containment watchdog) → terminal rewards (+0.7 scorer / +0.3 assist / −1.0 conceded / −0.1 stalemate) → `EpisodeEnded` event fires **before** reset (subscribers read cumulative rewards; HUD/FX/audio all hang off it) → `ResetPitch` (domain randomization: random own-half spawns, ball drag) reads `goal_width` curriculum. `stepCapOverride` on the exhibition Pitch shortens episodes to 2500 steps for pace.

**Personalities**: a player = `Reward_Settings` asset (`Assets/Agents/<Name>_v01/Reward_<NAME>.asset`) = reward DNA + `playerName`/`playerColor`/`brainModel`. Roster: STANDARD (balanced, trained), MATT (striker), KIM (wall, `defensivePositionScale`), NICK (midfielder, `possessionScale`), plus **BOT** (`Assets/Agents/Bot_v01/Reward_BOT.asset`) — the rule-based benchmark opponent, `brainModel` permanently null so picking it always fields `Agent_HeuristicBot` (this is how a trained brain gets played against the bot inside a normal match; the HUD chip tags each player AI/BOT). Pinned by `Agent_EditMode_BotProfile`. See `docs/players.md`. Bodies identical by design; brains interchangeable. Body wears `playerColor`, **team shows as thick outline frame + eye color** (built at runtime in `Agent_Soccer.Start` along with the initial letter label).

**Game flow**: `Agent_MainMenu` (4 picker rows, untrained players badged "(BOT)") → statics in `Agent_MatchSetup` → `Agent_MatchLoader` (order −60) applies profile/brain per agent (`brainModel` null ⇒ heuristic bot). `Agent_HUD` = scoreboard band + match clock + identity chips + goal/RESET toasts + first-to-5 end panel (match flow off in SCN_Training). `Agent_Stadium` builds 2D lights/shadows/post at runtime; `Agent_MatchFX` trail/shake/squash/boost particles; `Agent_Audio` velocity-scaled SFX + reactive crowd (placeholder WAVs in `Assets/Audio` — Store-pack clips drop into the same fields).

**Eval mode** is env-var driven (set by `evaluate.ps1` pre-launch, read in `Awake`): `POSOCCER_EVAL/BASELINE/EPISODES/RUNID/OUT`; `Agent_EvalStats` (Pitch root) aggregates across the 16-pitch grid, writes JSON, quits. `Agent_TrainingGrid` clones pitches when a trainer is connected or eval mode is on.

**Assemblies** (4 total; scene *authoring* stays MCP-only, but headless CLI builds need an editor entry point):
- `PoSoccer.Editor.Build` (`Assets/Editor`) → `BuildPlayerCommand.Build` (side-load APK) and `BuildAabCommand.Build` (Play-uploadable AAB, signing read from `POSOCCER_KEYSTORE*` env vars). Editor-only; excluded from player builds.
- `PoSoccer.Runtime` (`Assets/`) → `Unity.ML-Agents` [hyphen], `Unity.InferenceEngine`, `Unity.InputSystem`, `Unity.RenderPipelines.Universal.Runtime` + `.2D.Runtime` + Core
- `PoSoccer.EditModeTests` (`Assets/Tests/EditMode`) → `PoSoccer.Runtime`, `Unity.ML-Agents`, TestRunner
- `PoSoccer.PlayModeTests` (`Assets/Tests/PlayMode`) → same

**Key packages** (68 installed; `manage_packages list_packages` for the full set): `com.unity.ml-agents` 4.1.0 **Embedded**, Sentis/`com.unity.ai.inference` 2.6.1, URP 17.5.0 (2D renderer), Input System 1.20.0, Addressables 3.1.0, Cinemachine 3.1.7, Recorder 5.1.6, Memory Profiler, Profile Analyzer, Burst 1.8.29, **UniTask 2.5.11** (Git; async replaces coroutines project-wide — `Agent_MatchFX`, `Agent_Audio`). Tooling packages: UnitySkills 2.4.2 (Git), CoplayDev MCP for Unity 10.1.0 (Git), IvanMurzak AI Game Developer 0.86.3 (OpenUPM). No DOTween/UniTask/VContainer/Zenject/Odin, no networking stack, no TextMeshPro (UI Toolkit only). Scoped registry: OpenUPM for `com.ivanmurzak` + `extensions.unity`.

## Landmines

- **Protobuf**: ML-Agents + com.unity.ai.inference both shipped `Google.Protobuf_Packed.dll`; player builds resolved the editor-only twin (CS0400). Fixed by embedding the package with stock NuGet `Google.Protobuf.dll` 3.21.12 + asmdef refs updated. Never reintroduce the packed dll; file-renaming breaks the linker (assembly identity).
- **Git LFS**: `.gitattributes` routes every `*.onnx`, `*.png`, `*.wav`, `*.psd`, `*.fbx`, `*.ttf` through LFS. A clone made without `git lfs install` leaves **all 94 binaries as ~130-byte pointer stubs** — sprites and audio silently break and `STANDARD.onnx` fails to import with `InvalidProtocolBufferException: ... invalid wire type`, which reads like the protobuf landmine below but is not. Fix: `git lfs install --local; git lfs pull`, then reimport. Check with `git lfs ls-files` vs. on-disk file sizes.
- Scene order in Build Settings: `SCN_Training` must stay index 0 (headless training/eval boot it). Menu/Exhibition follow.
- Old runs exported under legacy behavior name `SoccerAgent`; `update-model.ps1 -Behavior` falls back automatically.
- `.venv`, `.tooling/`, `Builds/`, `results/` are all gitignored — a fresh clone has **no training toolchain at all**. Run `scripts/setup-training-env.ps1` before assuming any `scripts/*.ps1` will work.

## Coding rules

Enforced style lives in `.claude/rules/` and is loaded automatically: `architecture.md` (MVS + VContainer + MessagePipe + UniTask — aspirational; the current code is plain MonoBehaviour/ScriptableObject and does **not** yet follow it), `csharp-unity.md` (naming, `[SerializeField] private`, no LINQ in gameplay), `performance.md` (zero alloc in Update, draw-call/atlas budget), `serialization.md` (**`[FormerlySerializedAs]` on every rename**), `unity-specifics.md` (no `?.` on Unity objects, no coroutines).

## State (2026-08-02)

**LANDMINE — sensor geometry silently invalidates a trained policy.** `RayPerceptionSensor.OutputSize()` is `(DetectableTags + 2) * (2 * RaysPerDirection + 1)` — it depends on tag and ray *counts* only, not on `MaxRayDegrees` or `RayLength`. Change the arc or the range and every tensor shape still matches, so the `.onnx` loads without a single warning while the rays now report a different part of the world. It surfaces as a performance collapse that looks like a bad checkpoint: on 2026-08-04 the same 6.5M policy measured **12%** on a player built with a 300° sensor and **24%** on one built with the 120° sensor it trained on. Always rebuild the eval player from the sensor config the policy was trained with, and treat an arc/range change as a full retrain.

**LANDMINE — phase-1 training has never faced the bot.** Both agents in `SCN_Training` carry `m_BehaviorType: 0` (Default), which routes to the trainer whenever one is attached, and `Agent_Soccer.Awake` gives both the same behavior name. So with a trainer connected there is **no heuristic bot on the pitch at all**: one policy drives both teams and collects experience from both. `Agent_HeuristicBot.ComputeActions` is only reachable from `Agent_Soccer.Heuristic()`, which only runs under `BehaviorType.HeuristicOnly` — set by `ApplyEvalMode` in eval, and by `Agent_MatchLoader` in exhibition, but never in training. Consequences: (1) the `bot_strength` curriculum is **inert during training** — the lessons advance on reward but change nothing; (2) every run to date has been accidental symmetric self-play; (3) eval is the first time a policy ever meets the scripted bot, which is why training reward and eval win rate are decoupled (reward 1.24 → 19% wins). `scripts/train-phase1.ps1` is documented as "training vs the heuristic bot" and does not do that. Fixing it means forcing the opposing team to `HeuristicOnly` when a trainer is connected.

**Measured 2026-08-03/04** (100-episode evals, rebuilt player each time — `results/eval/*.json`):

| Run | Steps | Blue wins | Stalemates |
|---|---|---|---|
| `soccer_v2_standard` | ~5M | **15%** | 14% |
| `soccer_v3_00` (checkpoint 15,749,576, now in the STANDARD slot) | ~15.7M | **18%** | 18% |
| `baseline` (bot vs bot, 40 ep) | — | 42.5% (17–17–6) | 15% |

The baseline is symmetric, so the harness is fair and the brains really are **worse than the rule-based bot** — 3× more steps bought +3 points and cost 4 points of stalemate. Bar 80%/≤10% unmet; next levers: Phase 2 POCA self-play, personality brain runs, or bar recalibration.

**Eval gotcha**: `evaluate.ps1` grades whatever is baked into `Builds/PoSoccer/PoSoccer.exe`, so rebuild (MCP `manage_build`) after every `update-model.ps1` or you grade stale weights. Its staleness warning compares the **.exe** mtime, which Unity often leaves untouched even on a successful rebuild — check `PoSoccer_Data/resources.assets` instead, and ignore the warning when that file is fresh. Free Asset Store picks (optional, zero dependencies): `docs/asset-store-free-assets.md`.

**Open items:** `.venv`, `.tooling/`, and `Builds/PoSoccer/PoSoccer.exe` all exist (trainer `mlagents` 1.2.0.dev0) — eval works end to end. **But training is broken: the venv's protobuf 3.20.3 is gutted** — `.venv/lib/site-packages/google/protobuf/internal/` holds 4 entries instead of ~40, so `api_implementation`, `type_checkers`, and `enum_type_wrapper` are missing. Anything importing protobuf dies: `mlagents_envs.communicator_objects` (the trainer↔player channel), `onnx`, `tensorboard`. Eval is unaffected because it never runs Python. Fix: `.venv\Scripts\pip install --force-reinstall --no-cache-dir protobuf==3.20.3`. `results/soccer_p2_00/` is a phase-2 self-play run that died before producing a model (config + empty logs only) — likely the same cause.

**SCN_Training defect** (pre-existing, in HEAD): `AgentBlue`/`AgentRed` list two `m_Component` entries (fileIDs `…617`/`…618`) whose component blocks are absent, so every scene load logs "Broken text PPtr" + "Component at index 8 could not be loaded. Removing it." Harmless in practice — the missing pair is the ray-sensor duo that `Agent_Soccer.Awake` re-adds at runtime, and all of Rigidbody2D / BoxCollider2D / SpriteRenderer / Agent_Soccer / BehaviorParameters / DecisionRequester / Agent_HeuristicBot / Agent_Stamina survive. The `ai-game-developer` connector is unauthorized. Active-ragdoll articulation is an accepted open deviation (`docs/rules-exemptions.md`). Stamina wear-and-tear has no recovery path (documented trade-off, `docs/rules-exemptions.md` §4).
