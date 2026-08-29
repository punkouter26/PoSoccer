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
- **Git branch: `master`.** Do not create other branches unless explicitly asked. `master` is the only branch, local and remote, and is the GitHub default (2026-08-04: `main` held no unique commits and was deleted, the default was flipped, `origin/HEAD` repointed).
- **Always start TensorBoard when starting training.** Launch `.\scripts	ensorboard.ps1` (:6006) with every training run, no exceptions. A run with no live curves is a run you cannot tell apart from a stalled one, and the phase-10 retraction happened because nobody watched `Self-play/ELO` while mean reward climbed.
- **Always start the game from `SCN_Menu`** — never press Play on `SCN_Exhibition` directly. `Agent_MatchLoader` reads squad sizes and per-slot profiles from the `Agent_MatchSetup` statics, which **only the menu sets**; loading the exhibition scene on its own silently falls back to whatever is serialized in it, so you are testing a lineup nobody chose. (Headless training/eval are the exception — they boot `SCN_Training` directly by design.)
- Deliberate departures from these rules are recorded in `docs/rules-exemptions.md` — an audit finding not on that page is a real defect.

## Commands

```powershell
# One-time / after any parity bump: build .venv from the pinned ml-agents commit
.\scripts\setup-training-env.ps1 [-Force]   # clones .tooling/ml-agents, editable installs, verifies

# Training (headless, 4 env processes x 16 pitches; run detached via Start-Process)
# ALWAYS start TensorBoard alongside a run: .\scripts	ensorboard.ps1
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

Editor is normally already running. The working server is the CoplayDev toolset (`manage_scene`, `manage_gameobject`, `manage_build`, `read_console`, `refresh_unity`, `run_tests`, …), registered **stdio** in `~/.claude.json` under `projects[<cwd>].mcpServers.UnityMCP` as `uvx --from mcpforunityserver==10.1.2 mcp-for-unity`. `.mcp.json` holds the IvanMurzak `UnityMCP` binary (port **8090**) and the `ai-game-developer` HTTP connector.

**Corrected 2026-08-12.** This section used to say "the server that actually works is `unityMCP`, registered in `.claude/settings.json` at `http://localhost:8080/mcp`". That was false and cost real time: nothing has ever listened on 8080, the server is stdio-only (its log says `Transport mode: stdio` from 2026-08-06 on), and a permanently-failing registration makes every MCP problem look like a server outage. The dead entry has been deleted from `.claude/settings.json`.

**Corrected 2026-08-28 — "the MCP tool is disabled" was wrong, and the real cause is path casing.** `~/.claude.json` keys projects by the session's **cwd string**, and Windows yields several spellings of one directory. PoSoccer had **three** keys — `C:\Users\punko\Downloads\PoSoccer`, `C:/Users/punko/Downloads/PoSoccer`, `c:/Users/punko/Downloads/PoSoccer` — and only the middle one carried `mcpServers`. A session launched with a lowercase drive letter therefore resolved to a key with **no MCP servers at all**, so no server was ever spawned and zero Unity tools existed.

This does not look like a missing registration from the inside — it looks like a *disabled tool*. On 2026-08-28 a session read `manage_build`'s absence as "disabled by the user", spent its whole training budget working around it, and wrote that claim into a handoff. **It was never disabled.** Verified by driving the server directly over a stdio MCP handshake: it starts (FastMCP 3.4.7), syncs with the editor (`tool visibility synced from Unity: enabled=[10 categories], disabled=[]`), and advertises **47 tools including `manage_build`**. All three keys now carry the registration. If Unity tools are missing again, **check which `projects[...]` key your cwd resolves to before concluding anything is disabled or broken** — and remember the raw bridge socket below reaches all of it with no MCP at all.

- Bridge status: `~/.unity-mcp/unity-mcp-status-*.json` (poll `"reason":"ready"`). Server log: `%LOCALAPPDATA%\UnityMCP\Logs\unity_mcp_server.log` — the fastest way to tell server from client fault. **An open bridge port proves nothing about the MCP server**: the bridge lives in the editor, the server is a separate process the client launches.
- **Editors contend for port 6400 and the loser rebinds.** PoSoccer was on **6402** on 2026-08-12 because another project held 6400 — always read the port from the status file, never hardcode it. If tool calls fail with "Instance hash does not match", call `set_active_instance` with the hash from that file.
- **MCP is not required to reach the editor.** The bridge is a plain TCP server and the MCP server is just one client of it: connect, read the ASCII line `WELCOME UNITY-MCP 1 FRAMING=1\n`, then exchange 8-byte **big-endian** length-prefixed UTF-8 JSON frames of the form `{"type":"<tool>","params":{...}}` (`ping` → `pong` is special-cased). That reaches all 35 tools with no MCP, no restart, and **without closing the editor** — used on 2026-08-12 to build and grade when the client had no MCP tools. Note the bridge closes stale clients whenever a new one connects.
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

**Brain contract** (enforced in `Agent_Soccer.Awake`, scene can't drift): behavior name from `brainName` (default `STANDARD`), **29 vector obs** (14 self/ball/goal + 4 teammate + 8 opponent = 2 nearest slots × rel-position/velocity + 1 time-remaining + 2 wall-kick affordance, all zero-padded when empty) × **2 stacked**, **4 continuous actions** (forward / lateral / turn / boost), DecisionRequester period 8. Total model inputs **178** = 120 ray + 58 vector. Changing any of this obsoletes every trained `.onnx`.

**Updated 2026-08-27 (108 → 178).** Three changes, all in `Sensor_Vision` and `Agent_Soccer`: (1) **team tags**. Every player was tagged `Agent`, so `Sensor_Opponents` — the entire point of the phase-10 split — detected teammates and opponents identically; it was a `Sensor_AnyPlayer`. Agents now carry `TeamBlue`/`TeamRed` (stamped in `Agent_Soccer.Awake`) and the sensor targets the opposing tag. (2) **ray resolution**. `RaysPerDirection` was 2 over a 180° half-arc = 5 rays, and since −180° and +180° coincide, only **4 distinct directions 90° apart** — an opponent was guaranteed visible only within **~0.71 units**, against ~1.9 for the 11-ray sensor it replaced. Phase 10 made perception *worse*. Now 6/2/6/4 (ball/goal/opponents/walls) → 12 directions 30° apart, guaranteed to ~1.93 units. (3) **the wall kick is observable** (+2 floats: band proximity, cooldown ready) — it applied a large scripted impulse the policy had no way to predict. Nothing in the project reads the old `Agent` tag; `Agent_EditMode_ObsContract` and `Agent_PlayMode_AgentContract` pin all of it.

**Corrected 2026-08-12: the ray half of that figure was wrong everywhere.** This line said 118 (66 ray + 52 vector), `Sensor_Vision`'s docstring said 160 and an earlier CLAUDE.md revision said 162. The error was assuming `NumStackedVectorObservations = 2` doubles the ray sensors; it stacks the **VectorSensor only**. Ray sensors stack via `RayPerceptionSensorComponentBase.ObservationStacks`, which defaults to **1** and `Sensor_Vision` never sets. Real total is 54 ray (15 Ball + 9 Goal + 15 Opponents + 15 Walls) + 54 vector = **108**, verified in the live editor. `Agent_EditMode_ObsContract` now pins all three numbers and cross-checks them against every assigned `.onnx` **and** every scene-serialized `m_Model`, so this cannot drift again silently.

**No trained brain is currently assigned (2026-08-04).** The obs change 18 → 26 obsoleted all four personality `.onnx` (they declare 102 inputs; the runtime now produces 118). `brainModel` is **null** on all four `Reward_*.asset`, and `m_Model` is cleared on both agents in `SCN_Training`, so every player falls back to `Agent_HeuristicBot` and the menu badges them "(BOT)". The game stays playable; it just fields bots until a phase-6 run exports a 26-obs model. **Deleted 2026-08-05.** All four obsolete `.onnx` are gone from `Assets/Agents/`; there is now no `.onnx` in the project at all. **Corrected 2026-08-27:** that was true when written and stopped being true on 2026-08-11, when a 118-input `STANDARD.onnx` was re-exported into `Assets/Agents/Standard_v01/`. It sat there unreferenced by any live scene or profile (only the untracked `Assets/_Recovery/0.unity` pointed at it) and failed `Agent_EditMode_ObsContract` continuously. Both are now deleted; the project really does contain no `.onnx`. Note what this paragraph used to get wrong: it claimed `brainModel` was null on all four profiles and `m_Model` cleared on both `SCN_Training` agents. It was null on KIM/MATT/NICK only — `Reward_STANDARD.asset` still pointed at `STANDARD.onnx`, and **both** agents in `SCN_Training` still had `m_Model` set to it. Those three references were cleared (profile by hand, scene via MCP) *before* deleting the files, so nothing dangles; a GUID sweep of `Assets/` returns zero hits for all four. Behaviour is unchanged because a 102-input model could never load against a 118-input runtime anyway. If you reassign a brain, it must be a 26-obs export. `Agent_Soccer.Awake` auto-adds `Sensor_Vision` if absent, so the contract can't drift.

**Realistic physics** (traction overhaul): 75 kg reference agent, **236 N/75 kg drive** (force scales with mass, so every physique shares one top speed and heavier bodies simply carry more momentum), 1200 N/s slew, linearDamping **0.7** set from code, 250 N·m torque. Locomotion is **traction-limited**: all foot force — launching, cutting, braking — shares one friction circle of `mu * m * g` (mu 1.2), with active foot braking when there is no drive intent and extra lateral damping so strafing is slower than running. Turn rate falls from 360°/s at rest to 25% of that at sprint. Measured: **4.35 m/s jog / 9.54 m/s sprint, t95 ≈ 3.7 s** (human-like build-up). FIFA ball (r=0.11, 0.43 kg, drag ~0.1 randomized, Magnus curl). Zero in-plane gravity (top-down — see `docs/rules-exemptions.md`); gravity enters physically via the traction budget.

**Movement probe**: `Agent_PlayMode_MovementProbe` reports locomotion numbers to the console (chassis / as-shipped / forced-bot / blue-trained-side). Use it instead of watching the Game view — an unfocused editor barely runs play-mode frames. **Run it against every trained brain before trusting any win-rate theory** — it is the only measurement that separates "bad at soccer" from "cannot move", and on 2026-08-04 three runs' worth of hypotheses turned out to rest on the latter.

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

- **RETRACTED 2026-08-29 - the "this machine is ARM64" landmine was false, and it was load-bearing.** This entry used to open "This machine is ARM64, and that makes in-editor training impossible (2026-08-20)" and concluded that you must install an x64 editor in Unity Hub to train in the editor at all. **Every part of that is wrong.** Measured on 2026-08-29, by the very PE-machine-field check the old entry recommended and evidently never ran:

  | binary | PE machine | |
  |---|---|---|
  | `Unity.exe` (Hub 6000.5.7f1) | `0x8664` | **x64** |
  | `.venv2\Scripts\python.exe` | `0x8664` | **x64** |
  | `Builds/PoSoccer/PoSoccer.exe` | `0x8664` | **x64** |

  `PROCESSOR_IDENTIFIER` is `Intel64 Family 6 Model 165 Stepping 2, GenuineIntel` and `PROCESSOR_ARCHITECTURE` is `AMD64`. There is no ARM64 anywhere in this toolchain. The user confirmed it directly.

  So the whole causal story - "an ARM64 process cannot load an x64 native DLL, so `RpcCommunicator.Initialize` throws on `grpc_csharp_ext.x64.dll`, so the editor silently falls back to inference and `mlagents-learn` sits waiting for a Play press that can never work" - describes a machine that does not exist. The editor is x64 and ml-agents ships `grpc_csharp_ext.x64.dll`, so the plugin loads fine and **in-editor training should work** (not re-tested here, because headless is faster anyway - but do not treat it as impossible).

  Keep the PE-machine check itself (`0xAA64` = ARM64, `0x8664` = x64); it is the right tool. The lesson is that it has to actually be *run*: a plausible mechanism plus a real symptom is not a diagnosis, and this one stood unchallenged for nine days and steered work away from a working option.

- **Training runs on the GPU (verified 2026-08-29).** `mlagents.torch_utils.default_device()` returns `cuda` - ml-agents defaults to CUDA whenever `torch.cuda.is_available()`, and nothing here overrides it. torch is `2.5.1+cu121` against an **NVIDIA RTX 2060 (6 GB)**, and the trainer process appears in `nvidia-smi` with a `C` (compute) context. **But the GPU is not the bottleneck**: utilisation sits at ~18-21% (much of that Unity's own `C+G` contexts) because the policy is a 256x2 MLP. Wall-clock is set by the four headless `PoSoccer.exe` processes stepping physics on the CPU - measured **667 s per million steps** at `-NumEnvs 4`. Scale env count/CPU to go faster; a better GPU buys nothing here.

- **Protobuf**: ML-Agents + com.unity.ai.inference both shipped `Google.Protobuf_Packed.dll`; player builds resolved the editor-only twin (CS0400). Fixed by embedding the package with stock NuGet `Google.Protobuf.dll` 3.21.12 + asmdef refs updated. Never reintroduce the packed dll; file-renaming breaks the linker (assembly identity).
- **Git LFS**: `.gitattributes` routes every `*.onnx`, `*.png`, `*.wav`, `*.psd`, `*.fbx`, `*.ttf` through LFS. A clone made without `git lfs install` leaves **all 94 binaries as ~130-byte pointer stubs** — sprites and audio silently break and `STANDARD.onnx` fails to import with `InvalidProtocolBufferException: ... invalid wire type`, which reads like the protobuf landmine below but is not. Fix: `git lfs install --local; git lfs pull`, then reimport. Check with `git lfs ls-files` vs. on-disk file sizes.
- Scene order in Build Settings: `SCN_Training` must stay index 0 (headless training/eval boot it). Menu/Exhibition follow.
- Old runs exported under legacy behavior name `SoccerAgent`; `update-model.ps1 -Behavior` falls back automatically.
- `.venv`, `.tooling/`, `Builds/`, `results/` are all gitignored — a fresh clone has **no training toolchain at all**. Run `scripts/setup-training-env.ps1` before assuming any `scripts/*.ps1` will work.
- **The Play-release Android config silently breaks side-load builds — two separate flags (2026-08-05).** The project is set up for AAB releases, and MCP `manage_build` honours both settings, so a generic Android build produces something you cannot install. (1) **`androidUseCustomKeystore: 1`** points at `keys/posoccer-upload.keystore` with the password supplied via `POSOCCER_KEYSTORE*` env vars — but `keys/` does not exist in the working tree and the vars are normally unset, so the build dies with `UnityException: Unable to sign the Android application`. This also breaks `scripts/build-android.ps1`, whose header promises debug-keystore signing. (2) **`EditorUserBuildSettings.buildAppBundle = true`** makes the build emit an **AAB even when the output path ends in `.apk`** — the file looks plausible (59 MB, signed, 572 entries) but contains `BundleConfig.pb`, `BUNDLE-METADATA/` and `base/manifest/AndroidManifest.xml` instead of a root `AndroidManifest.xml`/`classes.dex`, and `adb install` fails with the misleading `INSTALL_PARSE_FAILED_UNEXPECTED_EXCEPTION: Failed to parse … AndroidManifest.xml`. To side-load: set **both** `buildAppBundle` and `useCustomKeystore` to `false`, build, install, then **restore both to `true`** (and re-assert Build Settings scene order — a build with explicit `scenes` rewrites it, and `SCN_Training` must stay index 0). Verify what you actually built before blaming the device: `AndroidManifest.xml` at the zip root means APK, `BundleConfig.pb` means AAB.
- **`com.unity.pipeline` vs Roslyn — protobuf landmine, second occurrence (2026-08-05).** Installing Unity's Pipeline package (`unity pipeline install`, CLI-driven editor automation) breaks Roslyn project-wide. Pipeline ships its own copy of **five** assemblies that `Assets/Plugins/NuGet/` already provides for `com.ivanmurzak.unity.mcp`: `Microsoft.CodeAnalysis.CSharp` + `Microsoft.CodeAnalysis` (project **4.8.0** vs Pipeline **3.11.0**), `System.Collections.Immutable` + `System.Reflection.Metadata` (7.0 vs 8.0), and `System.Runtime.CompilerServices.Unsafe`. On import Unity deduplicates by flipping **only** `Microsoft.CodeAnalysis.CSharp.dll.meta` to `Editor: enabled: 0`, leaving `Microsoft.CodeAnalysis` (Common) enabled at 4.8 — so `CSharp` resolves to 3.11 against a 4.8 Common. Symptom is `TypeLoadException: Type Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions has invalid vtable method slot 4 with method none`, plus a bare `ReflectionTypeLoadException` in the console. **Both** Roslyn consumers end up broken: Pipeline's own `eval`/`eval_file` throw, and MCP `execute_code` silently degrades from `"compiler":"roslyn"` to `"compiler":"codedom"` (C# 6 only) — it keeps succeeding, so the downgrade is easy to miss; check the `compiler` field in its result. Reverting the `.meta` does **not** hold: Unity re-disables it on the next import while the package is installed. Disabling Common 4.8 as well (via `PluginImporter.SetCompatibleWithEditor`, never by hand-editing `.meta`) does not fix it within a running domain — the 4.8 assemblies stay loaded until a full editor restart, so that path is untested. The only verified fix is to not install the package: `git checkout -- Packages/manifest.json Packages/packages-lock.json Assets/Plugins/NuGet/Microsoft.CodeAnalysis.CSharp.dll.meta`. Note what is *not* lost by removing it — 138 of Pipeline's 140 commands worked fine, including `set_autotick` and `recompile --focus`, which are the only known counters to the "unfocused editor barely runs frames" trap.

## Coding rules

Enforced style lives in `.claude/rules/` and is loaded automatically: `architecture.md` (MVS + VContainer + MessagePipe + UniTask — aspirational; the current code is plain MonoBehaviour/ScriptableObject and does **not** yet follow it), `csharp-unity.md` (naming, `[SerializeField] private`, no LINQ in gameplay), `performance.md` (zero alloc in Update, draw-call/atlas budget), `serialization.md` (**`[FormerlySerializedAs]` on every rename**), `unity-specifics.md` (no `?.` on Unity objects, no coroutines).

## State (2026-08-04)

**ROOT CAUSE FOUND 2026-08-28 — observations were world-frame while actions were body-frame, and that is why nine phases of training could not reach the ball.**

`OnActionReceived` has always built movement in the agent's own frame:

```csharp
Vector2 intent = Vector2.ClampMagnitude(transform.up * move + transform.right * lateral, 1f);
```

while `CollectObservations` emitted every relative position and velocity in **world** frame — `relBall`, ball velocity, self velocity, the eye axis, goals, teammate, both opponent slots. So "drive at the ball" was never a lookup the network could read off an input; it was a product it had to synthesise:

```
move    = dot(relBall_world, up_world)
lateral = dot(relBall_world, right_world)
```

An MLP approximates that bilinear rotation only piecewise, and the body turns through the full circle (**184 deg of heading churn** measured), so the policy had to learn a *different* linear map for every heading it ever held. Worse, `Sensor_Vision`'s ray sensors were egocentric the whole time — the network was handed **two contradictory coordinate systems for the same world** and had to reconcile them.

This is exactly the "misdirected, not collapsed" signature that `50d235f` probed and could not explain: confident action magnitudes pointed the wrong way. It is a **representation** defect, not a reward, perception, capacity or credit-assignment defect — which is why phases 6 through 17 each moved the win rate by less than noise while attacking every one of those instead:

| phase | hypothesis tested | result |
|---|---|---|
| 6 | opponent observations (18 -> 26 obs) | 17.1% vs 16.2% — noise |
| 7 | reward table (stalling was optimal) | 16.6% — noise |
| 8 | locomotion reward terms drifted in the assets | reward up 8x, probe still 0.99 m |
| 9/10 | domain randomization, 4-sensor perception split | graded a stale build; retracted |
| 13/14 | curiosity, more capacity | no change |
| 15/16 | MA-POCA group credit (really was broken, really was fixed) | ELO 1178 -> 579 |
| 17 | reward v3, arriving must out-pay aiming | "2.3x faster, still cannot reach the ball" |

Every one of those hypotheses was tested *on top of a policy that could not be told where the ball was in the frame it had to act in*.

**Fixed 2026-08-28** in `Agent_Soccer.CollectObservations` via `ToBodyFrame` (two dot products, zero alloc). Verified live before spending any compute: with the ball placed 3 units straight ahead, the observation now reads `relBall = (0.000, 0.111)` and `bearing = 0.000` at headings 0/90/180/-90 deg — **identical at every heading**, where before it produced four different vectors. Ball placed to the agent's right reads `(0.111, 0.000)` at every heading.

The two world eye-axis floats were redundant under a body frame (they are the constant `(0,1)`), so they now carry **yaw rate** and **signed bearing to the ball**, giving the turn channel proprioception it never had. Sign convention matches `AddTorque`: positive = counter-clockwise = left, so the turn head can approach the identity instead of learning a sign flip.

**LANDMINE — this change keeps the tensor shape and changes the meaning, which is the dangerous direction.** Vector obs stay **29 x 2**, model inputs stay **178**. Every `.onnx` trained before 2026-08-28 therefore **loads without a single warning** and silently reads a different world — the same failure mode as the sensor-arc landmine below, and `Agent_EditMode_ObsContract` cannot catch it because every number it checks is unchanged. Treat the date as a hard boundary: **a pre-2026-08-28 checkpoint is not comparable to a later one no matter what its eval JSON says**, and all four personalities need a full retrain.

**MEASURED 2026-08-29 — what the body-frame fix actually bought, and what it did not.**

`soccer_p18bf_standard` (3M steps, body frame, otherwise identical to the p17 config):

| | p17 (world frame) | p18 (body frame) |
|---|---|---|
| mean reward @250k | -0.865 | **-0.573** |
| mean reward @1M | -0.674 | **-0.236** |
| mean reward @3M | **-0.509** | **-0.164** (peaked +0.117 @2.95M) |
| probe: distance of a 10.44 m chase in 4 s | 0.99 m (9%) | **3.61 m (35%)** |
| probe: max / mean speed | 0.58 / ~0.5 m/s | **1.49 / 0.90 m/s** |
| probe: reached the ball | never | **never** |
| eval blue wins (n=510) | 16-17% | 17.8% |
| eval red wins | 65-69% | **53.9%** |
| eval stalemates | 14-17% | 28.2% |

Read that honestly. The frame fix is real and large — roughly **12x sample efficiency** (p18 matched p17's 3M endpoint at 250k), 3.6x the ground covered, and the first positive mean reward in this project's history. It also stopped the bleeding: red's win rate fell 65-69% -> 53.9%. But **the win rate did not move** (17.8% vs a 16-17% plateau, inside noise at n=510), because those losses became draws, not wins.

Two reasons the win rate is still pinned, both now measured rather than assumed:

1. **The curriculum still never promotes.** p18 finished on `Lesson0_Feeble` (bot_strength 0.2), exactly like p17. Eval grades against strength **1.0**, an opponent no policy in this project has ever trained against. Training reward and eval win rate are measuring two different opponents; do not treat them as one axis.
2. **The policy will not commit to full throttle.** The probe reads `mean|move| = 0.320` with only 19 sign flips in 400 steps — that is the policy *mean* under inference, not exploration noise. 236 N x 0.32 x 1.6 gain against 0.7 damping gives ~2 m/s terminal, matching the measured 1.49. Nothing in the reward table paid for hurrying: the proximity term is differential, so it telescopes to `ballProximityScale * (dStart - dEnd)` and pays the same for a 2-second approach as a 20-second one.

**Rejected 2026-08-29 — gamma 0.999 (`soccer_p19gamma_standard`, single variable vs p18).** The hypothesis was sound on paper: at gamma 0.99 the horizon is `1/(1-0.99)` = 100 decisions, and at DecisionRequester period 8 on a 0.01 s timestep that is ~8 s of game time, so a terminal reward 560 decisions away arrives multiplied by ~0.004. Raising it to 0.999 (~80 s horizon) made things **worse**: mean reward over the final 1M steps was **-0.308 vs p18's -0.139**, behind at every checkpoint from 200k on. Longer horizons cost value-estimate variance and this task could not pay for it at 3M steps. The short horizon is real but it is not the binding constraint.

**METHOD NOTE — a reward-table change makes mean reward incomparable across runs.** p20 restores `stepPenalty` at -0.00005 against a 9000-step cap, so every p20 episode carries up to **-0.45** of time cost that p18 episodes never paid. A p20 curve sitting below p18's says nothing on its own. Runs that change the reward table must be judged on the **probe** (arrival time, distance covered, top speed) and the **eval win rate**, which are both defined independently of the table. This is the same trap as comparing pre- and post-2026-08-28 checkpoints across the frame change.

**RESULT 2026-08-29 — p20, stepPenalty restored at -0.00005 (`soccer_p20step_standard`, single variable vs p18; the change is in the reward assets, the trainer config is byte-identical).**

| | p17 world frame | p18 body frame | **p20 body + stepPenalty** |
|---|---|---|---|
| probe: distance of a 10.44 m chase in 4 s | 0.99 m (9%) | 3.61 m (35%) | **5.24 m (50%)** |
| probe: max speed | 0.58 m/s | 1.49 m/s | **2.20 m/s** |
| probe: mean speed | ~0.5 m/s | 0.90 m/s | **1.31 m/s** |
| probe: policy `mean\|move\|` | — | 0.320 | **0.388** |
| probe: reached the ball | never | never | **never** |
| eval blue wins | 16-17% | 17.8% (n=510) | 14.6% (n=350) |
| eval red wins | 65-69% | 53.9% | **53.7%** |
| eval stalemates | 14-17% | 28.2% | 31.7% |

The time cost did what it was predicted to do: the policy committed harder (`mean|move|` 0.320 -> 0.388) and covered 45% more ground. Across the two fixes locomotion is **5.3x** p17 on distance and **3.8x** on top speed.

**But the win rate still has not moved, and after three runs the reason is no longer a hypothesis.** Every run since p17 finishes on `Lesson0_Feeble` (bot_strength 0.2) while eval grades against strength **1.0**. No policy in this project has ever trained against a competent opponent. What both fixes bought was converting *losses* into *draws* (red 65-69% -> 53.7%, stalemates 14-17% -> 31.7%) — the agent learned not to lose long before it can learn to win, because not-losing is what a 0.2-strength curriculum rewards.

Do not read the 17.8% -> 14.6% step as a regression: at n=510 and n=350 the combined SD is ~2.7 pp, so a 3.2 pp gap is ~1.2 SD, exactly the kind of difference this file already warns never to treat as signal. p20 is deployed over p18 on the strength of the probe, which is the measurement that actually discriminates.

**The next lever is the curriculum, not the reward table and not the network.** The `bot_strength` ladder needs a promotion criterion the agent can actually satisfy at its current skill, or the eval opponent needs to match the training opponent. Restoring a *falling* threshold is not the answer (that is how p5 graduated on noise); the honest options are a longer run so a flat 0.50 can genuinely be reached, or grading against the strength actually trained on so training and eval stop measuring different opponents.

**RESULT 2026-08-29 — p21 BREAKS THE PLATEAU. 25.7% over 350 episodes against 16-17% for every run since 2026-08-04.**

`soccer_p21curric_standard`, 10M steps. Two changes, one of them a correction:

- **Correction: bot_strength thresholds 0.50 -> 0.21.** Promotion uses `measure: reward`, evaluated on the same reward p20's stepPenalty depresses. Eval measured `meanEpisodeSteps` 5784, so the time cost is `5784 * 0.00005 = 0.289` per episode: a 0.50 threshold on p20's scale silently demanded what 0.79 demanded on p18's. p20 made promotion *harder* while improving behaviour, which nobody intended. This restores the same effective bar. It is **not** the p5 falling ladder - that dropped the bar as difficulty rose; here every lesson keeps one flat value and `min_lesson_length` stays 1000.
- **Experiment: 3M -> 10M steps**, so the ladder has room to be climbed.

**The curriculum climbed for the first time in this project's history** - three promotions where p17, p18 and p20 all made zero:

| step | lesson | bot_strength | mean reward |
|---|---|---|---|
| 0 | Lesson0_Feeble | 0.2 | — |
| ~5.45M | **Lesson1_Weak** | 0.35 | +0.676 |
| ~6.6M | **Lesson2_Half** | 0.5 | +0.940 |
| ~9.05M | **Lesson3_Capable** | 0.65 | — |
| 10M final | | 0.65 | **+0.690** |

Lesson2 matters specifically: the bot's support positioning and corner craft switch on at 0.5, so that is the first genuinely competent opponent any policy here has trained against.

Full arc of the four fixes, all measured the same way:

| | p17 world | p18 body | p20 +step | **p21 +curriculum** |
|---|---|---|---|---|
| probe: 10.44 m chase in 4 s | 0.99 m (9%) | 3.61 m (35%) | 5.24 m (50%) | **8.48 m (81%)** |
| probe: max speed | 0.58 | 1.49 | 2.20 | **3.17 m/s** |
| probe: `mean\|move\|` | — | 0.320 | 0.388 | **0.658** |
| probe: sign flips / 400 | — | 15 | 15 | **1** |
| eval blue wins | 16-17% | 17.8% | 14.6% | **25.7%** |
| eval red wins | 65-69% | 53.9% | 53.7% | **49.7%** |
| eval stalemates | 14-17% | 28.2% | 31.7% | **24.6%** |

**8.6x the ground covered and +9 points of win rate.** At n=350 the SD is ~2.3 pp, so 25.7% against a 16-17% plateau is roughly 4 SD - this one is signal, unlike every <10-point gap this file warns about. One sign flip in 400 steps means the policy now picks a direction and commits, and `mean|move|` 0.658 against `mean|lat|` 0.343 means it finally drives rather than strafes.

**Still far from the bar** (>=80% wins, <=10% stalemates) and the probe still reads `arrival=-1.00s` - 8.48 m of a 10.44 m chase in the 4-second window, so it very nearly arrives but not quite. The obvious continuations, in order: let the ladder run past Lesson3 (it was still climbing at 10M), then revisit `ActionGain = 1.6`, which clamps after multiplying and denies the policy any magnitude between 0.625 and 1.0 - a far more costly restriction now that it actually wants to output 0.658.

**Rejected: gamma 0.999** — see p19 above. **Untested and next in line:** `ActionGain = 1.6` in `Agent_Soccer.OnActionReceived`, a band-aid added when the policy crept. It multiplies then clamps, so the policy cannot express any magnitude between 0.625 and 1.0; with the frame fixed it may now be costing resolution rather than buying force.

**LANDMINE — `evaluate.ps1` could not complete its own default (fixed 2026-08-29).** `-Episodes` defaults to 1000 and `-TimeoutMin` defaulted to a flat **30**, but 1000 episodes takes ~60 min (measured: 510 episodes in 30 min). So the *documented default invocation* always tripped the timeout, exited 3, and wrote **no JSON at all** — a grade that ran for half an hour and left no evidence. The timeout now scales with the episode count, and a timed-out run **salvages** the player's last `[EvalStats]` tally into a JSON marked `partial: true` with the real episode count, instead of discarding a perfectly good smaller sample.

**LANDMINE — installing a Unity module while the editor is open breaks compilation with no useful message (2026-08-28).** The Android build-support module was installed at 10:54:23 while the editor had been running since 10:51:56. The editor builds its script-compilation reference set at startup, so `UnityEditor.Android.Extensions.dll` was never in it, and `Unity.Burst.Editor` + `Unity.AppUI.Editor` failed to compile against `UnityEditor.Android` types. Unity then **refuses to enter play mode with compile errors**, so `manage_editor play` reports `"Entered play mode."` and `isPlaying` is `False` a moment later — it reads as "Play is broken", not as "restart the editor". `BuildPipeline.IsBuildTargetSupported(Android)` returns `True` throughout (it reads disk), so that is not a useful check; inspect the assembly's own reference list instead (`CompilationPipeline.GetAssemblies(AssembliesType.Editor)`). Fix is a plain editor restart.

**`train-all.ps1` never set `POSOCCER_OPPONENT` (fixed 2026-08-28).** It predated the 2026-08-04 opponent fix, so every personality run it launched would have been symmetric self-play with `Agent_HeuristicBot` never executing and the `bot_strength` curriculum inert — the pre-p4 landmine, re-armed in the one script that trains three of the four brains. It now sets the variable (with `-SelfPlay` to opt out), carries the same stale-build guard as `evaluate.ps1`, prefers `.venv2`, checks TensorBoard, and cleans up in a `finally`.


**LANDMINE — sensor geometry silently invalidates a trained policy.** `RayPerceptionSensor.OutputSize()` is `(DetectableTags + 2) * (2 * RaysPerDirection + 1)` — it depends on tag and ray *counts* only, not on `MaxRayDegrees` or `RayLength`. Change the arc or the range and every tensor shape still matches, so the `.onnx` loads without a single warning while the rays now report a different part of the world. It surfaces as a performance collapse that looks like a bad checkpoint: on 2026-08-04 the same 6.5M policy measured **12%** on a player built with a 300° sensor and **24%** on one built with the 120° sensor it trained on. Always rebuild the eval player from the sensor config the policy was trained with, and treat an arc/range change as a full retrain.

**FIXED 2026-08-04 (`ab8d22d`) — phase-1 training used to never face the bot.** Both agents in `SCN_Training` carry `m_BehaviorType: 0` (Default), which routes every agent to the trainer, so "phase 1 vs the heuristic bot" was silently symmetric self-play: `Agent_HeuristicBot` never ran and the `bot_strength` curriculum had nothing to act on. That is why runs `v2`/`v3`/`p3` show training reward decoupled from eval win rate (reward 1.24 → 19% wins) — eval was the first time those policies ever met the scripted bot. Now `Agent_Soccer` forces the red team to `BehaviorType.HeuristicOnly` (`Agent_Soccer.cs:208`), opt-in via `POSOCCER_OPPONENT=bot`, which `scripts/train-phase1.ps1:39` sets unless `-SelfPlay`. Runs `p4` onward genuinely train against the bot. **Reading old results:** any run before `p4` was self-play regardless of what its config's `bot_strength` curriculum claims.

**LANDMINE — a 100-episode eval cannot tell 16% from 24%.** Ten repeat evals of the *identical* model measured **11, 12, 13, 14, 15, 15, 17, 18, 23, 24**. That is ordinary binomial noise (SD ≈ 3.7 points at n=100, observed 4.4) — nothing is broken, the sample is just too small. The cost of ignoring this is real: a single lucky 24% was briefly committed as an improvement over 18% when the true mean was 16.2%. **`evaluate.ps1 -Episodes` now defaults to 1000** (SD ≈ 1.2). Never rank two models on single 100-episode runs, and never read a <10-point gap as signal.

**Measured 2026-08-04** (multi-run means, rebuilt player per model — `results/eval/*.json`):

| Agent / run | Steps | n×100 ep | Blue wins | Range | Stalemates |
|---|---|---|---|---|---|
| `baseline` (bot vs bot) | — | 40 ep | 42.5% | — | 15% |
| STANDARD `soccer_p5_paced_00` | 30.0M | 10 | **16.2%** | 11–24 | 14.6% |
| MATT `soccer_v2_matt` | 2.5M | 4 | 17.2% | 10–24 | 13.5% |
| NICK `soccer_v2_nick` | 2.5M | 4 | 17.2% | 12–21 | 12.2% |
| KIM `soccer_v2_kim` | 2.5M | 4 | 16.2% | 14–19 | 13.8% |

Bot-vs-bot is symmetric, so the harness is fair and the brains really are **worse than the rule-based bot**: parity is ~42.5%, every brain sits at 16–17%. **All four personalities are statistically indistinguishable from each other** despite 12× different step counts (2.5M vs 30M) — which is itself the finding. Bar 80%/≤10% badly unmet.

**Compute has stopped buying wins.** `p4` (7.0M) and `p5` (30.0M) both mean ~16–17%; 4.3× the steps bought nothing. p5 reached the top `bot_strength` lesson while its reward oscillated between −1.07 and +0.52 — it graduated on noise, because the phase-5 threshold ladder still *fell* as difficulty rose (0.40 → 0.25) despite its own header claiming it had inverted that. Treat "train it longer" as a disproven lever.

**Why the bot wins: the agent cannot see it.** `Agent_Soccer.CollectObservations` is Self(4) + Stamina(1) + Ball(4) + Goals(5) + Teammate(4) = 18 — **there is no opponent term at all**. The policy's only opponent channel is `Sensor_Vision`: 11 rays, 30° apart (300° arc ⇒ a 60° blind wedge behind), 24 range, spherecast radius 0.1, and every player is tagged `Agent` so the rays cannot even separate teammate from opponent. Agents are 0.8 units wide, so effective detection half-width is 0.4 + 0.1 = 0.5, while the blind gap between adjacent rays grows as `d·sin15° = 0.259d` — an opponent is **guaranteed visible only within ~1.9 units**, and beyond that can sit entirely between two rays. On the 36×54 pitch (both `SCN_Training` and `SCN_Exhibition`) that reliable disc is ~12 of 1944 sq units ≈ **0.6% of the pitch**; ray length 24 is itself shorter than the 54-unit pitch length. `Agent_HeuristicBot.ComputeActions` meanwhile receives `nearestOpponent` as a live `Rigidbody2D` — exact position *and* velocity, unlimited range, no angular quantization, no occlusion. The 120°→300° widening (commit `7e03878`) traded angular resolution for coverage and moved the result 0 points, which is consistent with this: the gap is opponent *state*, not arc.

**RESULT — phase 6 changed nothing, and the diagnostic found the real cause.** `soccer_p6_seeing_00` (20.0M steps, 26 obs) graded **17.1%** over 1000 episodes against the previous brain's **16.2%** over 1000 — a 0.9-point difference against ±1.8 combined uncertainty, i.e. no improvement. Stalemates rose 14.6% → 17.0%. Training reward went from −0.045 to +0.45 and **none of it transferred**.

**The decisive experiment** graded the *same* p6 policy against the half-strength bot it actually trained on:

| | bot 1.0 | bot 0.5 |
|---|---|---|
| Blue wins | 17.1% | **17.4%** |
| Red wins | 65.9% | 34.4% |
| Stalemates | 17.0% | **48.2%** |
| Mean steps | 4,524 | 6,358 |

Halving the opponent left the win rate **flat** and converted 31 points of losses into draws, one-for-one. The scoring rate is pinned near 17% regardless of opponent — an **offense** problem, not perception and not opponent strength.

**Cause: the reward table made stalling optimal.** With `goalScorer 0.7 / goalConceded -1.0 / stalemateTimeout -0.1`, EV(stall) = −0.1 while EV(attack, 50/50) = −0.15. A policy needed a **>53% win rate before attacking beat parking the bus**, and it wins ~17%. It learned not to lose because that is what the table paid for. Fixed 2026-08-04 in all five profiles: `goalScorer` 1.2 (MATT 1.4, KIM 1.3), `stalemateTimeout` −0.6 — attacking now beats stalling even at a 30% win rate (−0.34 vs −0.60). Config-only; every 26-obs `.onnx` stays valid. `config/STANDARD_phase7_scoring.yaml` is the single-variable test.

**Process lesson: the perception thesis below was diagnosed from code inspection and never tested before committing 3 hours of compute to it.** The 10-minute reduced-strength eval would have falsified it up front. Run the cheap discriminating experiment *before* the expensive fix.

**Fixes applied 2026-08-04 — the perception/reward/curriculum set that phase 6 tested and found insufficient:**
1. **Opponent observations** — obs 18 → 26 (`Agent_EnvController.GetOpponents`, zero-alloc, nearest-first). Removes the perception asymmetry above. Obsoletes every `.onnx`.
2. **`ballContact` 0.05 → 0.005** — at 0.05, **14 touches outscored a goal** (0.7), so over a ~4400-step episode the optimal policy was to poke the ball rather than finish. Matches the stubborn 12–18% stalemate rate. Now 140 touches per goal.
3. **Curriculum gate** — `config/STANDARD_phase6_seeing.yaml`: one flat mastery threshold (0.50) on every lesson and `min_lesson_length` 200 → 1000, replacing the falling ladder that let p5 graduate on noise.
4. **`evaluate.ps1 -Episodes` 100 → 1000** — see the variance landmine above.

Also available: `Agent_HeuristicBot.perceptionRadius` (env `POSOCCER_BOT_VISION`, default **0 = unlimited**, preserving every historical result) caps how far the bot perceives opponents — the knob for asking whether the 80% bar is reachable at all or is just the perfect-information gap.

Phase 2 POCA self-play is *not* indicated — runs `v2`/`v3`/`p3` were effectively symmetric self-play and scored 15–19%.

**RESULT — phase 7 also moved nothing, and the probe finally found the real cause.** `soccer_p7_scoring_00` (20.0M, reward table fixed, single-variable vs p6) graded **16.6%** over 1000 episodes against p6's 17.1% and the old brain's 16.2%. The reward fix did work behaviourally — stalemates 17.0% → 14.4% — but those draws became **losses** (red 65.9% → 69.0%), not wins. Three runs, three hypotheses, one flat ~16–17%.

**The agent never learned to drive.** `Agent_PlayMode_MovementProbe` measures the policy directly; nobody had run it against a trained brain before 2026-08-04 because everything was graded headless on aggregate counters:

| 4 s chase, 10.44 m away | trained policy | scripted bot | chassis capability |
|---|---|---|---|
| distance travelled | **0.99 m (9%)** | 15.08 m (144%) | — |
| reached the ball | **never** | 2.70 s | — |
| top speed | **0.58 m/s** | 5.16 m/s | 9.54 m/s |
| heading churn | 184° | 84° | — |

It creeps and spins at ~6% of the chassis' ability. Every "it can't finish / can't see / won't attack" theory was built on top of a policy that could not cross the pitch.

**LANDMINE — a ScriptableObject asset never receives a changed field initializer.** Editing `Reward_Settings.cs` does **not** touch an existing `Reward_*.asset`. The v2 pass fixed the locomotion terms in code and **no asset ever got them**, so every profile trained with:

| term | code | STANDARD | KIM | effect |
|---|---|---|---|---|
| `ballProximityScale` (reward for closing on the ball) | 0.002 | 0.0004 | 0.0002 | 5–10× too weak |
| `actionJitterScale` (penalty for changing action) | 0.0004 | 0.001 | 0.002 | 2.5–5× too strong |
| `stepPenalty` (v2 zeroed it) | 0 | −0.00003 | −0.00003 | all five drifted |

Net **12× swing against moving** on STANDARD, **50× on KIM**. Standing still was the local optimum and 20M steps found it. Fixed in all five profiles 2026-08-04 and pinned by `RewardProfiles_MatchCodeDefaultsOnMechanics` (EditMode) — that test is the guard, keep it green. Personality lives in terminal rewards, trait scales and physique; the locomotion mechanics must match code.

**LANDMINE — a domain reload during play mode freezes the pitch with no error visible in game.** `ResetPitch` threw `KeyNotFoundException` on `_spawnRotations[agent]` every `FixedUpdate`, so every episode reset aborted and the pitch locked: agents motionless, **all four actions exactly 0.0000**, stamina untouched, `StepCount` still climbing. It reads exactly like "the brain is broken". Cause: "Enter Play Mode Options → DisableDomainReload" wipes non-serialized state without re-running `Start`; `agents` is serialized so it returns populated while the spawn dictionaries return empty. **The setting must stay OFF** (ML-Agents Academy needs the reload). `Agent_EnvController.EnsureSpawnCache` now heals and warns once instead of bricking. **And it does not stay off by itself: running the PlayMode suite flips `ProjectSettings/EditorSettings.asset` `m_EnterPlayModeOptions` from `0` to `1` (DisableDomainReload) every time** — observed twice on 2026-08-05, once per `run_tests` PlayMode invocation. So the normal verify loop re-arms this landmine, and pressing Play after a test run walks straight into it. Always `git diff -- ProjectSettings/EditorSettings.asset` after a PlayMode run and `git checkout` it if the value is `1`; never commit that flip.

**The `POSOCCER_BOT_VISION` knob is a dead end.** `perceptionRadius` gates only the shoulder-charge, which itself requires the opponent within **2 units** — so any radius ≥ 2 is a no-op. The bot barely consults opponent state at all, which means **in 1v1 there is essentially no information asymmetry**: both sides get exact ball, goal and pitch state. The phase-6 perception thesis was wrong for this matchup; it would only bite in 2v2.

**Training and eval are 1v1.** `SCN_Training` holds exactly two agents. The teammate block and the second opponent slot are **always zero**, so no policy has ever practised with a teammate — 2v2 is untested capability, not tuned behaviour. Two consequences: `SCN_Exhibition` runs the brain out of distribution, and `Agent_PitchSizing` scales the exhibition pitch to squad size (80 m²/player, width clamped to [12, 30]) so 2v2 is **12.6 × 25.3 m against training's 36 × 54 m — ~6× smaller**, and 1v1 in the menu clamps to 12 × 24 and still cannot reach the training size. Judge the brain in `SCN_Training`; use the menu to play the game.

**Probe gotcha:** `RunChase` drives **Red** by default, but the trained policy is always **Blue** (`train-phase1.ps1` sets `POSOCCER_OPPONENT=bot`, forcing Red to `HeuristicOnly`). Self velocity, eye axis and `relBall` are world-frame while the goal terms are team-relative, so probing Red runs a Blue-trained policy out of distribution. Use `Probe_D_ChaseEfficiency_BlueTrainedSide`.

**LANDMINE — a stale player build silently invalidates training *and* evaluation, and every existing guard missed it (2026-08-12).** Phases 9 and 10 (`p9_random`, `p10_perception`, `p10_poca` 3M, `p10_poca_10M`) all ran against a player last built **2026-08-05 17:04**. Nobody rebuilt after `update-model.ps1`, and nothing caught it:

- **Every eval graded the p9 brain**, whatever the filename said. The three phase-10 results (16.4 / 19.0 / 17.8 over 1000 ep) are three repeat samples of one unchanged model — mean 17.7%, spread 2.6 pp against SD ≈ 1.2. The published "3M POCA = 19.0%, first run to break the plateau, +2.6 pp real" compared two samples of the same thing. Retracted in `docs/eval-p10-poca.md`.
- **Every training run used the same stale env** (`env_path` in all three `configuration.yaml`), so the 4-sensor split and terminal-reward shaping that phase 10 exists to test **never executed once**. They are untested, not disproven.
- **Regraded 2026-08-12**: a player built from the 118-obs code the brain was actually trained on (`43e3385`) puts the real 3M POCA brain at **18.5%** (185/1000, 64.9% red, 16.6% stalemate) vs the p9 brain's 17.7% — **+0.8 pp on a combined SD of ~1.7. POCA did not break the plateau.**
- **ELO said so all along.** Per UNITY_RULES, judge self-play on ELO, not mean reward: `Self-play/ELO` was **1200.5 flat** from initial 1200.0 through 9.28M steps (and absent entirely from the 3M run), with `Mean Group Reward` identically 0.000 — MA-POCA's group-credit channel carried no signal. Mean reward climbed to 0.86 while the rating never moved.

Guards added the same day: `evaluate.ps1` hard-fails on a stale build and stamps `modelInputs` / `modelPath` / `modelWrittenUtc` / `playerBuiltUtc` into every eval JSON (the phase-10 JSONs recorded a run id and a win rate and nothing that could falsify them); `build-headless.ps1` no longer reports `OK` for a build that never ran and refuses to start while the editor holds the lock. **Verify a rebuild by `PoSoccer_Data/*.assets`, and confirm the editor compiled what you think** — reflecting on `PoSoccer.Sensor_Vision.RaysPerDirection` (present only in the pre-split version) is the cheap check.

**Process that now applies to every run: pilot then gate.** `config/STANDARD_phase8_pilot.yaml` is 3M steps (~25 min) instead of 20M (~2.9 h); export, run the probe, and only commit to a full run if the agent actually reaches the ball. p8 promoted a lesson at ~2M and reached **+0.648** where p7 never promoted in 20M and ended +0.281 — an 8×+ sample-efficiency gain from the locomotion fix. **Unresolved:** the p8 probe still reads 0.99 m / 0.58 m/s, so training reward and measured locomotion still disagree. Do not launch a full run until that contradiction is explained.

**RESULT — phase 16: MA-POCA group rewards never fired, and fixing that changed nothing (2026-08-28).** `Agent_EnvController.Start` built `SimpleMultiAgentGroup`s only when a side had >1 player. `SCN_Training` is 1v1, so the guard never tripped, `_blueGroup`/`_redGroup` stayed null, and the `AddGroupReward`/`EndGroupEpisode` branches in `OnGoalScored`/`OnStalemate` never ran. **Every POCA run in this project's history — p10, p15 — trained with an empty group-credit channel.** Groups are now always registered (`0dda102`).

Verified by running it: `soccer_p16_poca1v1` (720k steps) moved `Environment/Group Cumulative Reward` off zero for the first time, −0.55 → −0.23. **The result is still negative** — `Self-play/ELO` fell **1178 → 579**, and the self-play variant `soccer_p16b_poca_selfplay` emits no `Self-play/ELO` tag at all. Both fail the gate written into `STANDARD_phase2_poca.yaml`'s own header. Group credit was a real defect; fixing it bought no skill. That points back at the unresolved locomotion failure, not at credit assignment.

**LANDMINE — `Mean Group Reward: 0.000` is not a fingerprint of missing groups.** In self-play both teams report to the same behavior and the ±1 group rewards cancel to *exactly* 0.000 — measured in p16b with groups provably live. Only a run against a non-trainer opponent (`POSOCCER_OPPONENT=bot`) shows the group signal directly. Diagnosing "groups aren't firing" from a self-play log is unfalsifiable.

**LANDMINE — headless training players still play audio.** `--no-graphics` disables rendering only. `m_DisableAudio` is `0`, so four env players ran `Agent_Audio`'s looping crowd bed at `time_scale: 20` and turned the machine into a constant drone. `Agent_Audio.Start` now bails out under `-batchmode` / a null graphics device.

**LANDMINE — a floating `#main` package can block every build with no local change.** `com.besty.unity-skills` drifted to a commit that `using UnityEngine.UI;` across 23 files while its asmdef declares no uGUI. Unity refuses to build a player while **any** editor assembly fails to compile ("Error building Player because scripts have compile errors in the editor"), so headless training died on an upstream commit nobody here made. Worked around by installing `com.unity.ugui` (`docs/rules-exemptions.md`); the real fix is to pin or drop that package.

**Eval gotcha**: `evaluate.ps1` grades whatever is baked into `Builds/PoSoccer/PoSoccer.exe`, so rebuild (MCP `manage_build`) after every `update-model.ps1` or you grade stale weights. **Never judge a build by the `.exe` mtime** — Unity leaves it untouched even on a fully successful rebuild (confirmed 2026-08-12: every `PoSoccer_Data` file moved to 11:51:52 while the `.exe` stayed at 8/2). Check `PoSoccer_Data/*.assets`. Both scripts now do this themselves: `evaluate.ps1` **hard-fails** (exit 2, `-AllowStale` to override) instead of warning, and `build-headless.ps1` baselines each artifact against its own prior timestamp. Free Asset Store picks (optional, zero dependencies): `docs/asset-store-free-assets.md`.

**Open items:** `.venv`, `.tooling/`, and `Builds/PoSoccer/PoSoccer.exe` all exist (trainer `mlagents` 1.2.0.dev0) — **the full training toolchain is healthy** (verified 2026-08-04: protobuf 3.20.3 cpp impl, `mlagents_envs.communicator_objects` and `mlagents.trainers` both import). An earlier note here claimed protobuf was gutted and training was broken; that is no longer true, and `internal/` holds 22 entries, not 4. `results/soccer_p2_00/` is a phase-2 self-play run that died before producing a model (config + empty logs only) — cause unknown, not protobuf.

**SCN_Training defect — FIXED 2026-08-04, and it was never harmless.** `AgentBlue`/`AgentRed` listed two `m_Component` entries (fileIDs `…617`/`…618`) whose component blocks were absent, logging "Broken text PPtr" + "Component at index 8 could not be loaded" on every scene load. This note used to call that harmless. It was not: the Unity test framework treats an unhandled `[Error]` log as a failure, so **all 6 PlayMode tests aborted in `SetUp`** — the entire PlayMode suite was red in HEAD for this reason alone. Re-saving the scene drops the dangling refs (and serializes previously-absent defaults `defaultBotStrength: 1`, `_showVisionCone: 1`, `_frame*`, all matching their C# initializers, so no behaviour change). Suites now pass 8/8 EditMode and 6/6 PlayMode. The `ai-game-developer` connector is unauthorized. Active-ragdoll articulation is an accepted open deviation (`docs/rules-exemptions.md`). Stamina wear-and-tear has no recovery path (documented trade-off, `docs/rules-exemptions.md` §4).

---

## Android release & Play internal testing

Ported from the PoRacer pipeline on 2026-08-28; PoSumo is the original, already
shipping on the punkouter27 Play account.

### Identity (permanent — do not change after the first upload)

| Property | Value |
|---|---|
| Application id | `com.punkoutersoftware.posoccer` |
| Version / code | `1.0.0` / `3` — bump `VERSION_CODE` in `Editor_ConfigureAndroidRelease` for every upload; Play rejects a reused code |
| min / target SDK | 26 / 36 (Play requires target 36 for new uploads from 2026-08-31) |
| Architecture | ARM64, IL2CPP, Release |
| Orientation | Portrait is locked in `Editor_ConfigureAndroidRelease`. |

### Secrets live OUTSIDE the repo

`C:/Users/punko/Downloads/PoSoccer-Release/`

- `posoccer-upload.jks` — the upload key. **Losing it means losing the ability to
  update the app.** Back it up somewhere other than this machine.
- `posoccer-upload.pass` — the store/alias password, one line.
- `upload_certificate.pem` — the public cert, for Play App Signing.
- `play-service-account.json` — NOT created yet; see the SETUP block at the top of
  `Tools/play_publish.py`.

Unity does not serialize keystore passwords into `ProjectSettings`, so both Android
builders read `POSOCCER_KEYSTORE_PASS` first and fall back to the `.pass` file.
Without either, the build **aborts** rather than producing an unsigned artifact.

### The tools

| Tool | What it does |
|---|---|
| *PoSoccer → Configure Android Release Settings* | One-shot: identity, SDK levels, orientation, and the launcher icons (adaptive + round + legacy, 6 densities) from `Assets/Icons/`. Re-run after changing icon art |
| *PoSoccer → Build Android AAB (Play release)* | Signed bundle → `Builds/Android/PoSoccer.aab`. Logs `AAB BUILD RESULT:` |
| *PoSoccer → Build Android APK* | Sideloadable APK on the SAME key, so it installs over a Play build → `Builds/Android/PoSoccer.apk`. Logs `BUILD RESULT:` |
| `Tools/play_publish.py` | Uploads a built AAB. Defaults to the `internal` track as a `draft`; `--dry-run` rehearses and discards |

`Tools/play_publish.py` needs its own venv (`Tools/publish-venv`). Do not install it
into `.venv` — that one carries load-bearing ml-agents/torch pins, and the C#/Python
ml-agents versions must stay in exact parity.

### The shipped scene list is explicit

`Editor_BuildAndroidAAB.SHIP_SCENES` names the player's scenes in boot order:

  0. `Assets/Scenes/SCN_Menu.unity`
  1. `Assets/Scenes/SCN_Exhibition.unity`

It is a hardcoded list, not whatever is ticked in Build Settings, because Build
Settings also carries SCN_Training — training scenes that would bloat the bundle
and, depending on order, boot a tester straight into a training rig. A scene named
here that is missing on disk **aborts** the build.

### The icons

`Assets/Icons/` holds `AppIcon_Adaptive_Background.png` and
`AppIcon_Adaptive_Foreground.png` (432x432, the API 26+ pair) and
`AppIcon_Legacy.png` (512x512, round and pre-adaptive launchers). The adaptive
FOREGROUND art must stay inside the middle 66% of its canvas — every OEM launcher
masks the outside to a different shape.

The Play STORE icon is a different file, in `StoreAssets/PlayStoreIcon_512.png`:
full-bleed, because Play rounds it itself. Do not swap the two.

### What still needs a human in a browser

1. Play Console → Create app, with the application id above.
2. Store listing, content rating and data-safety forms — drafted in
   `StoreAssets/play-listing.md`.
3. Upload the first bundle by hand; Play refuses an API upload before the app is set up.
4. Create a service account, grant it release permission ON THE APP, and drop its
   JSON key next to the keystore.

After that, `python Tools/play_publish.py --track internal` owns every upload.

### Headless

```
Unity.exe -batchmode -quit -nographics -projectPath <root> -buildTarget Android ^
  -executeMethod PoSoccer.EditorTools.Editor_BuildAndroidAAB.Build -logFile <log>
```

Grep the log for `AAB BUILD RESULT:` — that line is the outcome.
