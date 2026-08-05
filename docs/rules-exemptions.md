# UNITY_RULES — recorded exemptions and open deviations

Every item here is a place where PoSoccer knowingly departs from UNITY_RULES.
Anything not listed here is expected to comply; if an audit flags something that
is not on this page, it is a real defect, not a known deviation.

---

## 1. Earth gravity is not applied in the physics plane (Rule 2) — EXEMPT

**Rule:** "Enforce Earth gravity (g = -9.81 m/s²)."

**What the project does:** `Physics2D.gravity = Vector2.zero`, set in
`Agent_Bootstrap.Awake` and stored the same way in `SCN_Training` and
`SCN_Exhibition`.

**Why:** PoSoccer is a top-down (bird's-eye) 2D game. The simulated plane is the
*ground plane*, viewed from above. Real gravity acts along the unmodeled Z axis,
perpendicular to everything Physics2D simulates. Applying -9.81 to the 2D plane
would accelerate every player and the ball toward one touchline — it would not
model gravity, it would model a tilted pitch.

**How realism is preserved instead:** gravity's real in-plane consequences are
modeled explicitly rather than emergently — linear drag stands in for rolling
resistance and turf friction, the ball uses FIFA mass (0.43 kg) and radius
(0.11 m) with per-episode drag randomization, and agents use SI mass (75 kg)
with bounded drive force and torque. Every other clause of Rule 2 (SI units,
realistic material friction, deterministic scaling) is met in full.

**Status: PERMANENT.** A Z axis (lofted passes, headers, chips) was considered
and **explicitly rejected by the project owner on 2026-08-02** — PoSoccer is a
2D game and stays 2D. This exemption is therefore not provisional and should
not be re-raised.

**Where gravity does enter:** `g = 9.81` is used for real in the locomotion
model. The traction budget is `mu * m * g` — body weight sets the normal force,
which sets how hard the feet can push, cut and brake. Gravity is modelled where
it physically acts on a top-down game, rather than being bolted onto the wrong
axis for the sake of a checkbox.

---

## 2. No active-ragdoll articulation (Rule 2) — OPEN DEVIATION, not exempt

**Rule:** "Emulate real-world creature mass, proportions, joint degrees of
freedom, and maximum torque limits."

**What the project does:** each agent is a single `Rigidbody2D` driven by one
linear force and one torque (`Agent_Soccer.OnActionReceived`). Mass, proportions,
maximum torque, actuation slew rate, and stamina-based exertion degradation are
all modeled; **joint degrees of freedom are not** — there is no articulated body,
so there are no per-joint drive targets or per-joint torque limits.

**Why it has not been fixed:** this is not a local change. The brain contract is
frozen and enforced in `Agent_Soccer.Awake` — 26 vector observations × 2 stacked,
4 continuous actions. An articulated body changes both the observation space
(per-joint state) and the action space (per-joint drive targets), which
**obsoletes every trained `.onnx`**, including the current STANDARD checkpoint,
and invalidates the training benchmark's accumulated results.

**Scope if adopted:** new agent body prefab with articulated segments, rewritten
observation/action space, new `Sensor_` components for joint state, a full
retrain from scratch, and a re-baselined evaluation gate.

**Status:** deliberately deferred. This is the single largest gap against the
active-ragdoll brief and should be scheduled as its own project phase, not
folded into a compliance pass.

---

## 3. Default branch (UPDATES) — RESOLVED, no longer a deviation

**Rule:** "Use master branch for everything."

The repository's history was created on `main`. A `master` branch was created
from it and pushed, and on **2026-08-04** the changeover was completed on
github.com: `main` held no unique commits and was deleted, the GitHub default
was flipped to `master`, and `origin/HEAD` was repointed. `master` is now the
only branch, local and remote.

**Status: CLOSED.** Retained here only as a record of the migration; there is
no outstanding deviation. Do not create other branches unless explicitly asked.

---

## 4. Wear-and-tear has no recovery path (Rule 2) — DELIBERATE TRADE-OFF

**Rule:** "Implement exertion degradation so agents experience stamina
loss/wear-and-tear over continuous evaluation cycles during game play."

**What the project does:** `Agent_Stamina.Wear` accumulates at
`wearPerBoostSecond` (0.002/s) whenever the agent is boosting, shrinks the
effective max stamina ceiling, and is floored at 60% of the configured max
(`wearFloor`). Wear persists across matches — `resetWearOnEpisode` defaults
to `false`. There is no off-pitch or post-match recovery: once an agent hits
the floor it stays there until the Unity domain reloads (editor scene
switch).

**Why no recovery yet:** wear is currently a *diagnostic* signal that lets
players feel that sustained boost use has consequences, not a balancing
lever that needs to be tuned per match. Adding recovery (rest button, time-
based decay, kit recharge) is design-side work that affects HUD scope,
match-flow code, and reward signal shape — none of which can be safely
folded into a rule-compliance pass.

**Rule status:** the rule's letter ("agents experience wear-and-tear over
continuous cycles") is met. The rule's spirit (a complete stamina loop
with recovery) is not, by design.

**Scope if implemented:** HUD readout for current wear, a match-flow hook
to reset wear on main-menu return (or after N seconds idle in the menu),
possible Agent_Stamina API addition `DecayWear(float amount)` and a
recovery invocation site in `Agent_HUD` or a new `Agent_Roster` system.
