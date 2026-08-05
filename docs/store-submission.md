# Google Play submission checklist

Status as of 2026-08-04. Covers the **project-side** configuration: what is set,
what is blocked, and what only a human with the Play Console can do.

For the **Play Console side** — the exact declarations to file (data safety,
content rating, target audience), mirrored from the live PoSumo listing, plus
the signing-and-build pipeline (`scripts/build-android-aab.ps1`,
`Assets/Editor/BuildAabCommand.cs`) — see
[play-console-submission.md](play-console-submission.md). The two were written in
parallel and overlap; this one is the project settings, that one is the listing.

## Configured (done)

| Setting | Value | Why |
|---|---|---|
| Output format | **App Bundle (.aab)** | Play requires AAB for new apps; APK uploads are rejected |
| `targetSdkVersion` | **36**, pinned | Was "Automatic (highest installed)", which silently changes with an editor upgrade. Play's floor is 35 |
| `minSdkVersion` | 26 | Well under Play's floor of 23; covers Android 8.0+ |
| Architecture | ARM64 | Play requires 64-bit. Excludes 32-bit-only devices and x86_64 Chromebooks — acceptable, revisit only if the install base matters |
| Scripting backend | IL2CPP | Required for ARM64 |
| Development build | off | `development`, `allowDebugging`, `connectProfiler` all cleared |
| Engine code stripping | on | Smaller download |
| Version | 1.0 / versionCode 1 | Fine for a first submission. **Bump `versionCode` on every upload** — Play rejects a reused code |

## Blocked — needs a human

### 1. Upload keystore (hard blocker)

`androidUseCustomKeystore` is **false**, so every build so far is signed with
Unity's debug keystore. Play rejects debug-signed uploads.

Creating a keystore means choosing and storing passwords, so this one is yours:

```bash
keytool -genkeypair -v -keystore posoccer-upload.keystore -alias posoccer -keyalg RSA -keysize 2048 -validity 10000
```

Then in Unity: **Player Settings → Publishing Settings → Custom Keystore**,
point at the file and enter the passwords. Keep the file and passwords backed up
somewhere durable — losing them means you can never update the app under the
same listing (Play App Signing softens this, but the upload key still matters).

Do not commit the keystore or its passwords to git.

### 2. Package name is permanent

`applicationIdentifier` for Android is currently **`com.punkoutersoftware.posoccer`**.
It was `com.posoccer.app` earlier today, and the build installed on the test
phone still uses the old one — so they are two different apps as far as Android
is concerned. Decide before the first upload: after publishing, the package name
can never change.

`companyName` is still `PoSoccer`, which does not match the reverse-domain
identifier. Cosmetic, but it shows up in some places.

### 3. App icon

`m_BuildTargetPlatformIcons` for Android has **no textures assigned** — the build
ships Unity's default icon. Needed:

- Adaptive icon (foreground + background layers), 432x432 source
- 512x512 PNG for the store listing (separate from the in-app icon)

### 4. INTERNET permission — RESOLVED 2026-08-04

Stripped. Note the attribution above was wrong: the permission does **not** come
from ML-Agents. Unity's own generated `unityLibrary` manifest declares it
unconditionally — verified in the previous build's merged manifest under
`Library/Bee/Android/Prj/IL2CPP/Gradle/launcher/build/intermediates/packaged_manifests/`.
Nothing in `Assets/Scripts` references `UnityWebRequest`, `System.Net`, `WWW` or
`Socket`.

Removed via `Assets/Plugins/Android/LauncherManifest.xml` with
`tools:node="remove"`, enabled by `useCustomLauncherManifest: 1`.

Deliberately **not** done with `Assets/Plugins/Android/AndroidManifest.xml` as
suggested above: that file replaces the generated `unityLibrary` manifest
wholesale, taking permanent ownership of the activity declaration, splash
meta-data, notch config, orientation and GL/Vulkan feature flags — and silently
ignoring later Player Settings changes. The launcher manifest merges last and
owns nothing else.

See `docs/android-internal-testing.md` for the full current state.

### 5. Store listing (all human-authored)

- Title (30 chars), short description (80), full description (4000)
- At least 2 phone screenshots, 16:9 or 9:16, min 320px on the short side
- Feature graphic 1024x500
- **Privacy policy URL** — mandatory, even for an app that collects nothing
- Content rating questionnaire
- Data safety form
- Target audience and content declaration
- Google Play Console developer account (one-off fee)

## Product readiness — worth a look before shipping

These are not submission blockers, but they are what a reviewer or a first user
meets:

- **Audio is placeholder.** `Assets/Audio` holds generated WAVs standing in for
  real SFX (see `docs/asset-store-free-assets.md` for zero-dependency options).
- **The trained brains lose to the scripted bot.** Latest measured evals are 15%
  and 18% wins against a bot that a mirror copy of itself beats 42.5% of the
  time. The phase-3 run in flight is the fix, but whatever ships should be the
  best model available, promoted via `scripts/update-model.ps1` and rebuilt.
- **ML-Agents ships inside the player.** The Sentis runtime and the trainer
  communicator add download size to a consumer build. Stripping the communicator
  is possible but invasive; measure the AAB first and only act if size matters.
- No in-app purchases, ads, or analytics are present, which keeps the Data Safety
  form trivial.

## Build command

Once the keystore is configured, from the running editor:

```
manage_build(action="build", target="android",
             output_path="Builds/PoSoccer/PoSoccer.aab",
             scenes="Assets/Scenes/SCN_Menu.unity,Assets/Scenes/SCN_Exhibition.unity")
```

`SCN_Training` is deliberately excluded from the store build — it is the 16-pitch
training grid and has no place in a consumer app.
