# PoSoccer — Play Store listing copy

Paste-ready. Character counts verified against Google Play's limits
(title 30 / short description 80 / full description 4000).

Written to describe what the build actually does today: four playable
personalities with distinct physiques and styles, rule-based opponents, and a
physics-driven 2D match. It makes **no claim about machine-learned opponents**,
because no trained brain is currently assigned — every player falls back to
`Agent_HeuristicBot`. Revisit this file if a phase-6+ model ever ships.

---

## Title  (limit 30)

**Primary — 27 chars**

```
PoSoccer: Pocket Soccer 2v2
```

Alternatives:

| Option | Chars |
|---|---|
| `PoSoccer` | 8 |
| `PoSoccer: Top-Down Soccer` | 25 |
| `PoSoccer: Pocket Soccer 2v2` | 27 |
| `PoSoccer - Arcade Soccer` | 24 |

Play indexes the title heavily, so the bare `PoSoccer` costs you the word
"soccer" as a search term. Prefer one of the longer forms.

---

## Short description  (limit 80)

**Primary — 68 chars**

```
Fast top-down soccer. Pick your squad, first to five goals takes it.
```

Alternatives:

| Option | Chars |
|---|---|
| `Fast top-down soccer. Pick your squad, first to five goals takes it.` | 68 |
| `Physics-driven 2v2 soccer. Four players, one ball, first to five wins.` | 70 |
| `Quick-match arcade soccer with real physics and four rival personalities.` | 73 |

This line shows above the fold and in search results — it is the single
highest-value string in the listing.

---

## Full description  (limit 4000)

**Primary — 2,156 chars of 4,000**

```
PoSoccer is a fast, physics-driven soccer game built for one hand and one
screen. No menus to wade through, no energy timers, no accounts. Pick four
players, kick off, and play until someone hits five goals.

REAL PHYSICS, NOT ANIMATION
Every touch is simulated. The ball has weight, drag and curl — strike it
off-centre and it bends. Players are traction-limited the way real athletes
are: you cannot accelerate, cut and brake all at once, because every one of those
demands shares the same grip with the ground. Sprint into a turn and you will
slide wide. Plant first, then go. Heavier players carry more momentum into a
shoulder charge; lighter ones change direction sooner. It rewards reading the
next two seconds instead of mashing a button.

FOUR PLAYERS, FOUR JOBS
Build a squad from a roster where nobody is a straight upgrade over anyone
else.

- STANDARD — the balanced all-rounder. Does everything competently, nothing
  spectacularly. The safe pick.
- MATT — the striker. Biggest, heaviest, least interested in defending. Points
  at the goal and goes.
- KIM — the wall. Sits between the ball and her own net and refuses to be moved.
  Patient, smooth, hard to get past.
- NICK — the midfielder. Smallest and quickest, keeps the ball on a string,
  looks for the pass instead of the shot.

They share one body plan and one control scheme — the differences are in mass,
size and priorities, so every matchup feels different without anyone being
broken.

A MATCH, NOT A GRIND
Kick off, scoreboard runs, clock runs, first team to five wins. Goals get a
toast and a crowd. Matches finish in minutes. Nothing is locked, nothing is
sold, nothing is waiting for you to come back tomorrow.

BUILT SMALL AND CLEAN
- Portrait, one thumb, playable standing up
- No ads
- No in-app purchases
- No accounts, no sign-in
- No data collected and nothing sent anywhere — the game does not use the
  network at all
- Runs offline, start to finish

WHAT THIS IS
PoSoccer started as a physics and machine-learning testbed and grew into a
game worth playing on its own. This is an early build shared for testing —
expect rough edges, and expect it to keep changing.
```

---

## Notes before you paste

**Play strips most formatting.** The full description supports a very small
subset of HTML (`<b>`, `<i>`, `<u>`, `<br>`, `<p>`) and nothing else. The
hyphen bullets above render fine as plain text; do not try Markdown.

**Two claims are load-bearing and must stay true**, because they have to match
your Data safety and Ads declarations exactly:

- "No ads" ⇒ App content → Ads → *does not contain ads*
- "No data collected / does not use the network" ⇒ Data safety → *no data
  collected, no data shared*

A mismatch between the description and the declarations is one of the more
common review rejections, and it is entirely self-inflicted.

**"Early build shared for testing"** is deliberate in the last paragraph. It
sets expectations for internal testers and costs you nothing, since internal
testing is not public. Cut it if the listing ever goes to production.

**What is deliberately absent:** any mention of AI, machine learning, neural
networks or trained opponents. The four personalities are real and playable,
but they are currently driven by the rule-based bot, not by a trained policy.
Claiming otherwise in a store listing is the kind of thing that is both wrong
and easy to prove wrong.
