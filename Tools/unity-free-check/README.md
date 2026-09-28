# unity-free-check

Compiles the parts of PoSoccer that do not need Unity and runs their EditMode tests
under plain .NET 8 + NUnit. It exists for machines with no Unity editor, such as a
cloud session, where otherwise nothing can be compiled at all.

```bash
Tools/unity-free-check/run.sh        # installs a local .NET 8 SDK into .dotnet/ if none is on PATH
```

## What it covers

- `Assets/Scripts/Agents/Agent_TeamRoles.cs`, compiled whole.
- The static helpers `extract.py` lifts **verbatim** from `Agent_EnvController`
  (`ParseSquadPattern`, `SquadForPitch`, `SpacingPenalty`, `TEAM_SPACING_RADIUS`) and
  `Agent_MatchSetup.MAX_SQUAD`. They are re-extracted on every run, so they cannot drift
  from the real source.
- Every test in `Agent_EditMode_TeamTraining.cs` that touches no engine-only API. The
  tests it cannot run are **printed by name** at the start of every run.

`UnityStubs.cs` stands in for `Vector2` and `Mathf` with the same names and behaviour,
including Unity's definition of `Mathf.Approximately`.

## What it does NOT cover

This is not a Unity compile. Anything using a real engine object — MonoBehaviours,
`Rigidbody2D`, ML-Agents, the asset database — is out of scope. That includes the
role *assignment* in `Agent_EnvController`, the bot, the agent and the overlay. A green
run here does not mean the project compiles in Unity: open it in the editor, check the
console, and run the EditMode suite (`PoSoccer.EditModeTests`) for that.

## Adding coverage

- A new Unity-free runtime file → add a `<Compile Include>` line to `UnityFreeCheck.csproj`.
- A pure static helper inside a MonoBehaviour → add its name to `HELPERS` in `extract.py`.
- A new test file → add it to `TESTS` in `extract.py`. Members that use an API in
  `ENGINE_ONLY` are skipped and listed.
- A missing `Vector2`/`Mathf` member → add it to `UnityStubs.cs`, matching Unity's semantics.
