# PoSoccer — Android internal testing prep

Status as of 2026-08-04. Companion to `store-submission.md` (full checklist) and
`play-console-submission.md` (Play Console declarations, mirrored from PoSumo).

## Done in the project

| Item | State | Notes |
|---|---|---|
| Output format | **AAB** | `BuildAabCommand.Build`; Play rejects APK for new apps |
| Package name | `com.punkoutersoftware.posoccer` | **Permanent after first upload** |
| `targetSdkVersion` | 36, pinned | Play's floor is 35 |
| `minSdkVersion` | 26 | Android 8.0+ |
| Architecture | ARM64 | 64-bit required; excludes 32-bit-only devices and x86_64 Chromebooks |
| Scripting backend | IL2CPP | Required for ARM64 |
| Orientation | Portrait, locked | `defaultScreenOrientation: 0` |
| Development build | off | `development` / `allowDebugging` / `connectProfiler` all cleared |
| Engine code stripping | on | |
| `companyName` | `punkoutersoftware` | Matches the reverse-domain id |
| **INTERNET permission** | **stripped** | See below |
| **Privacy policy** | drafted | `docs/privacy-policy.md`, needs hosting |

### INTERNET permission removal

Unity's generated `unityLibrary` manifest declares `android.permission.INTERNET`
unconditionally — verified in the merged manifest of the previous build at
`Library/Bee/Android/Prj/IL2CPP/Gradle/launcher/build/intermediates/packaged_manifests/`.
It does **not** come from ML-Agents. Nothing in the game uses the network.

Removed via `Assets/Plugins/Android/LauncherManifest.xml` (`tools:node="remove"`),
enabled by `useCustomLauncherManifest: 1`.

The launcher manifest is used deliberately instead of
`Assets/Plugins/Android/AndroidManifest.xml`: that file *replaces* the generated
`unityLibrary` manifest, which would mean owning the activity declaration, splash
meta-data, notch config, orientation and GL/Vulkan feature flags forever — and
silently ignoring future Player Settings changes. The launcher manifest merges
last and owns nothing else.

**If you ever change install location or supported screens in Player Settings**,
mirror it into `LauncherManifest.xml` — those two values are duplicated there.

## Still needed from you

### 1. Upload keystore — hard blocker

Every build so far is signed with Unity's debug keystore, which Play rejects.
`BuildAabCommand` deliberately refuses to fall back to it.

Creating the keystore means choosing and storing passwords, so it is yours to do:

```bash
keytool -genkeypair -v -keystore posoccer-upload.keystore -alias posoccer -keyalg RSA -keysize 2048 -validity 10000
```

Store it **outside the repo** (it is not gitignored anywhere useful — do not commit
it). Back up the file and both passwords somewhere durable: lose them and you can
never update the app under the same listing.

Then set these four environment variables before building — `BuildAabCommand`
reads them so no password ever enters tracked config:

```
POSOCCER_KEYSTORE        full path to the .keystore
POSOCCER_KEYSTORE_PASS   keystore password
POSOCCER_KEYALIAS        posoccer
POSOCCER_KEYALIAS_PASS   key alias password
POSOCCER_VERSION_CODE    optional; overrides bundleVersionCode
POSOCCER_VERSION_NAME    optional; overrides bundleVersion
```

**Bump `POSOCCER_VERSION_CODE` on every upload** — Play rejects a reused code.
Currently at 1.

### 2. App icon

`m_BuildTargetPlatformIcons` for Android has no textures assigned, so the build
ships Unity's default icon. Supply:

| Asset | Size | Purpose |
|---|---|---|
| Adaptive icon foreground | 432×432 PNG, transparent | In-app launcher icon, safe zone is the centre 288×288 |
| Adaptive icon background | 432×432 PNG, opaque | Layer behind the foreground |
| Legacy/round icon | 512×512 PNG | Fallback for older launchers |
| Store icon | 512×512 PNG, 32-bit, no alpha | Play Console listing — **not** shipped in the build |

Drop the first three in `Assets/Sprites/Icons/` and assign under
**Player Settings → Icon → Adaptive**. The store icon is uploaded to Play Console
directly and does not belong in the project.

Note `.gitattributes` routes `*.png` through Git LFS — commit them normally, but a
clone without `git lfs install` will see pointer stubs.

### 3. Privacy policy hosting

`docs/privacy-policy.md` is written and accurate to what the app actually does
(no data collection, no ads, no analytics, no network access — all verified in
code, not assumed). It needs a **public URL**.

Cheapest option: enable GitHub Pages on the repo and link the rendered file. The
existing PoSumo URL (`app-popunkoutersoftware.azurewebsites.net//privacy`) returns
a 409 and has a double slash — do not reuse it.

### 4. Play Console listing

All human-authored, none of it in the repo:

- Title (30 chars), short description (80), full description (4000)
- ≥2 phone screenshots, 9:16, min 320px short side
- Feature graphic 1024×500
- Content rating questionnaire (IARC — must be filled fresh per app)
- Data safety form → "no data collected"
- Target audience → 18+ keeps you out of the Families programme
- **Advertising ID declaration** → the app does not use it. PoSumo never completed
  this; do not repeat that omission.

## Build command

Once the keystore env vars are set:

```powershell
& "C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe" -quit -batchmode -nographics -projectPath . -executeMethod BuildAabCommand.Build -logFile Logs\aab-build.log
```

The editor must be **closed** for a batchmode build. Output lands in `Builds/`.

## Product readiness caveat

The AI opponents currently lose to the scripted bots. A 2v2 STANDARD-vs-BOT match
measured **0–2** with 1 stalemate over 60s. Every trained brain grades ~16–17%
against the bot where two bots playing each other score 42.5%, and the movement
probe shows the policy travels 0.99m in 4s against the bot's 15.08m. Shipping is a
product decision — the game is playable and the bots play a competent match — but
"AI opponents" is not currently a selling point. See CLAUDE.md § State.
