# GFX / audio pass — 2026-09-06

Ten items. Nine shipped; one (the tilt-shift focus band) is BLOCKED and item 1
shipped in a reduced form. What each one is, what it cost, what it is allowed to
be switched off by - and, for the blocked one, exactly what was measured.

## The ten

| # | Item | Where |
|---|---|---|
| 1 | Goal impact: colour flash + expanding shockwave ring (partial - see below) | `Agent_ScreenFX` |
| 2 | Real typefaces (Inter body, Oswald display), OFL | `Assets/Resources/Fonts/`, `PoSoccerTheme.uss` |
| 3 | Menu rebuilt as template + stylesheet | `Resources/Menu.uxml`, `Agent_MainMenu` |
| 4 | Adaptive music: three stems crossfaded by pressure and threat | `Editor_MakeAudioStems`, `Agent_Audio` |
| 5 | Pitch wear accumulated from traction saturation | `Agent_Wear` |
| 6 | Procedural kit patterns (stripes / hoops / sash / halves) | `PoSoccerFX.hlsl`, `Reward_Settings.kitPattern` |
| 7 | Tilt-shift focus band | **NOT SHIPPED** - blocked, see below |
| 8 | Adaptive quality tiers driven by measured p95 frame time | `Agent_Quality` |
| 9 | Amplitude-scaled Android haptics | `Agent_Haptics` |
| 10 | Colour-vision palettes, high contrast, type scale | `Agent_Palette`, `PoSoccerTheme.uss` |

## Four findings that came out of doing the work

### 1. Every UV-space shader effect was reading the wrong UV

`Assets/Art/Atlases/PitchAtlas` packs `pitch.png`, `ball.png`, `tile.png` and
`backdrop.png` — that is the pitch, the ball, and every player body. A packed
sprite's mesh UVs span its **slot on the page**, not 0..1, so:

- the team rim computed `length(uv - 0.5)` against the centre of the PAGE. For a
  slot off to one side that expression is nearly constant across the sprite, so
  the "rim light" on every player was a flat tint;
- the mown stripes ran at whatever fraction of `_StripeCount` the pitch's slot
  happened to subtend, so the lane count on screen was never the lane count
  anyone set.

The project had already hit this once — `Agent_Surfaces.BuildNetQuad` dodges the
atlas entirely with a private texture, and says so — but the fix was never
generalised. `Agent_Surfaces` now measures the slot from `Sprite.uv` and writes it
into `_SpriteRect`; `PoSoccerLocalUV` maps every term through it. Default is the
identity, so unatlased sprites are unchanged.

**This is the dangerous direction of change**: nothing about the render *failed*.
A flat tint reads as a design choice.

### 2. The shipped team colours are very nearly isoluminant

Measured, and now asserted in `Agent_EditMode_Palette`:

| pair | relative luminance | gap |
|---|---|---|
| blue `(0.20, 0.50, 1.00)` | 0.472 | |
| red `(1.00, 0.25, 0.20)` | 0.406 | **0.066** |

A gap of 0.066 means a viewer who cannot separate the two by hue has almost
nothing left to separate them by — and red-against-blue is the most common
confusion axis there is. The shipped look is unchanged (that is the game's
identity), but this is why modes 2 and 3 exist rather than being decorative:

- **Colour safe** — Okabe-Ito blue `#0072B2` / orange `#E69F00`, separable under
  protanopia, deuteranopia and tritanopia.
- **High contrast** — separated in luminance as well as hue, so the pair survives
  greyscale, glare and a cheap panel.

The test asserts the default *fails* the 0.1 gap and both alternatives *pass* it.

### 3. The frame budget was measured and never acted on

`Agent_Telemetry` graded p95 against 16.7 ms, coloured breaches red and wrote a
CSV. Nothing responded. `Agent_Quality` closes the loop, measuring its own p95
(the overlay's recorders only exist while it is open, and it is closed by
default) and shedding in a cost-per-pixel order:

1. **pitch wear** — one full-pitch transparent quad of continuous overdraw;
2. **bloom** — multi-pass post, also paid every frame;
3. **impact overlays** — the goal flash and ring, LAST (see below).

Three consecutive bad windows to drop a tier, twelve good ones to recover. The
asymmetry is deliberate: a controller that recovers as eagerly as it degrades
oscillates, and an oscillating quality setting is worse than the dropped frames.
The tier is printed into the telemetry overlay and its CSV, so a frame inside
budget can be told apart from a frame inside budget *because two effects are off*.


### 4. Full-screen passes do not execute on this project's 2D renderer

Items 1 and 7 were built as a URP full-screen pass — shockwave refraction, a
radial speed smear, and a tilt-shift focus band — because all three need to
**read the frame**, which geometry cannot do. The pass never rendered. What was
tried, and what each attempt produced:

| attempt | result |
|---|---|
| URP `FullScreenPassRendererFeature` @ `AfterRenderingPostProcessing` | **pure white** — it reached the screen, so it was enqueued and active, but `_BlitTexture` sampled as unbound |
| same @ `BeforeRenderingPostProcessing` | no effect at all |
| hand-written feature, canonical Unity 6 blit-and-swap (read `activeColorTexture`, blit through the material, assign `resources.cameraColor`) | no effect, at either event |
| same pass derived from `ScriptableRenderPass2D` with `RenderPassEvent2D.AfterRenderingPostProcessing` (the 2D renderer's own injection enum) | no effect |
| all of the above after a full editor restart | unchanged — rules out a cached renderer instance |

Ruled out along the way, each by reading the file rather than assuming:

- the feature **was** registered — an EditMode test read `Renderer2D.asset`'s
  `rendererFeatures` list and found it, with its material and shader resolved;
- the pipeline **does** use that renderer — `UniversalRP.asset` is the asset on
  every quality level and its `m_RendererDataList` is `Renderer2D.asset`;
- the feature **was** active — first via `Agent_ScreenFX`, then by committing
  `m_Active: 1` into the asset.

**Every attempt was confirmed with a probe compiled into the shader** — return
the sampled frame multiplied by a red tint — not by looking for a subtle effect.
That distinction is the whole point: "the effect is subtle" and "the pass never
ran" are indistinguishable on screen, and this project has already published one
retraction over a number that looked measured and was not.

A separate, real landmine surfaced on the way there and is worth keeping:

> **Hand-authored URP feature assets silently lose their fields.**
> `FullScreenPassRendererFeature` implements `ISerializationCallbackReceiver` and
> runs a version migration on deserialize. A field absent from a hand-written
> YAML asset deserializes as **zero, not as its C# initializer**, so the absent
> `m_Version` read as `Initial` and the migration recomputed
> `fetchColorBuffer = requirements.HasFlag(Color)` — turning the `1` written in
> the file into `false`. Any hand-authored feature asset needs its `m_Version`.

The dead pass, its shader, its material and its feature asset were **deleted**
rather than left in place: a pass that silently does nothing while costing a
colour copy per frame is worse than an absence, and
`Agent_EditMode_Palette.The2DRenderer_CarriesNoRendererFeatures` now fails if
someone adds one without revisiting this record.

What shipped instead is what geometry can do honestly: a full-view colour flash
in the scoring team's colour and an expanding shockwave ring, both created on
demand and disabled between goals. **Lost: refraction, the speed smear, and the
tilt-shift.**


### The order came from watching it, not from reasoning about it

The first version shed the goal effects **first**. A probe logged during a live
match read:

```
[ScreenFXProbe] episodeEnded winner=Red allow=False tier=3
```

A real goal, celebrated with nothing — because the editor never holds 16.7 ms,
so the controller had walked to the bottom tier inside a minute and switched off
the single moment the whole match builds towards. Two fixes came out of it:

- **the ladder is now continuous-cost-first**: wear, then bloom, then the goal
  overlays last;
- **a breach must clear a margin** (`p95 > budget * 1.25`) and the budget is
  derived from `Application.targetFrameRate` rather than hardcoded, so a device
  deliberately running at 30 fps is not graded against 60.

This is the entire argument for playing the thing rather than trusting a passing
test suite: every test passed before this was found, because every test forced
the tier by hand and never asked what tier a real machine would actually reach.

## What is NOT switched on

**Kit patterns ship as `KitPattern.None` on every profile.** UNITY_RULES reserves
how a brain looks to its author: heuristic bot red, reference brain "green,
untextured", custom brains **user-supplied** textures, "never auto-assign one". A
procedural jersey is a texture by that standard, so the machinery is built,
tested and opt-in — set `kitPattern` + `kitColor` on a profile to use it.
`Agent_EditMode_Palette.EveryShippedProfile_ShipsWithNoKit` fails if a later
session dresses the roster without recording an exemption.

## Cost control, in one place

| Thing | Cost | Gated by |
|---|---|---|
| Pitch wear | one transparent quad + a 128² upload at 8 Hz | `Agent_Quality` tier 1 |
| Bloom | multi-pass post | `Agent_Quality` tier 2 |
| Impact overlays | two transparent quads, only while a goal plays | disabled between goals; `Agent_Quality` tier 3 |
| Music stems | two extra AudioSources | silent at zero volume |
| Kits | a few ALU, no new draw call | one material per distinct (team, kit) |
| Fonts | ~1 MB of TTF | — |

Training and evaluation get **none** of it:
`Agent_PlayMode_Gfx.TrainingScene_HasNoneOfIt` asserts every component - and every
overlay object - is absent from `SCN_Training`, where 16 cloned pitches share one
camera.

## Regenerating the assets

```
PoSoccer → Generate Music Stems      # three 24 s stems, sample-aligned
```

The stems must stay the same length: they are crossfaded live, and a stem a few
milliseconds adrift drifts out of phase over a match.
`Agent_PlayMode_Gfx.MusicStems_AreLoadedAndAligned` pins it.
