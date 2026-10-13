# Community brains (brain hub)

Added 2026-10-11. Code: `Assets/Scripts/Gameplay/Agent_BrainHub.cs`, the HUB button in
`Agent_MainMenu`. Tests: `Agent_EditMode_BrainHub`.

A community brain is a trained policy that did not ship in the build. It is loaded from a
`.sentis` file at runtime and joins the menu's roster like any other player.

## State

- Export, the file loader and its checks, and the menu screen are built and tested.
- **No hub repo exists yet.** The default repo name is `punkouter26/posoccer-brains`
  (`Agent_BrainHub.DEFAULT_REPO`). Until someone creates it and uploads an export, FETCH
  FROM HUB reports "not found on the hub".
- The download path has not been exercised against a live repo. The loader it feeds has:
  the round-trip test exports the shipped STANDARD brain and loads it back through the
  same code a download takes.

## Publishing brains

1. In the editor: **PoSoccer -> Export Brains For Hub**. It writes one `NAME.sentis` per
   roster profile whose brain fits the current contract, plus `index.json`, into
   `Builds/BrainHub/`.
2. Edit `index.json` if you want an `author` or `notes` on an entry. `evalWinRate` and
   `evalEpisodes` are copied from the profile, so they are whatever `evaluate.ps1` last
   stamped.
3. Upload the folder to a Hugging Face **model** repo, files at the repo root:

   ```powershell
   hf upload punkouter26/posoccer-brains Builds\BrainHub . --repo-type model
   ```

The game reads `https://huggingface.co/<repo>/resolve/main/index.json` and then
`.../resolve/main/<file>`. A public repo needs no token.

## Installing without a network

Copy a `.sentis` file (and optionally its `NAME.json` sidecar, one index entry) into the
app's `brains` folder under `Application.persistentDataPath`. It is picked up the next time
the menu opens. This works on every platform, including Android.

## Why `.sentis` and not `.onnx`

The ONNX importer is editor-only, so a player cannot read an `.onnx`. `.sentis` is
Inference Engine's own serialized format and is what `ModelWriter.Save` emits. The trainer
still produces `.onnx`; the export step converts.

## What is checked before a brain is fielded

- The file name is letters, digits, dash, underscore and `.sentis`; nothing else is ever
  written to disk, so an index cannot address a path outside the brains folder.
- Downloads are `https` only, 8 MB at most for a brain and 256 KB for an index, and are
  cut off while they arrive if they pass the cap.
- The file must parse as a model, have a `continuous_actions` output and no
  `discrete_actions` output, and its inputs must sum to the runtime's observation contract
  (`Agent_Soccer.ModelFitsContract`).
- A community brain cannot take the name of a shipped player or the bot.

A `.sentis` file is a graph description and weights; nothing in it runs as code. What
remains possible is a brain that plays badly, or one trained under a different meaning of
the same tensor shape (see "Changing any of this obsoletes every trained .onnx" in
`CLAUDE.md`). The loader cannot detect the second case. The win rate shown on the HUB
screen is the uploader's claim and is labelled that way.

## Android and the privacy policy

`docs/privacy-policy.md` says the app requests no `INTERNET` permission and makes no
network connections, and Unity adds that permission as soon as a script references a
networking API. So the hub client is compiled out of Android builds:

```csharp
#if !UNITY_ANDROID || POSOCCER_BRAIN_HUB
```

On Android the HUB screen still lists and removes installed brains, and FETCH FROM HUB
is disabled. To ship the client on Android, in this order: update the privacy policy and
the Play data-safety form (the app would then contact huggingface.co, which sees the
device's IP address), then add `POSOCCER_BRAIN_HUB` to the Android scripting defines.

## Reflection and stripping

ML-Agents accepts only a `ModelAsset`, and that type's two fields are internal. The loader
sets them by reflection. `Assets/link.xml` keeps the three types from being stripped, and
the round-trip test fails if a package update renames a field. The reflection path has not
been run in an IL2CPP player.
