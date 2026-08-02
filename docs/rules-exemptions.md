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

**Revisit if:** the game ever adds a Z axis (lofted passes, headers, chips). At
that point gravity becomes real and this exemption must be withdrawn.

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
frozen and enforced in `Agent_Soccer.Awake` — 18 vector observations × 2 stacked,
3 continuous actions. An articulated body changes both the observation space
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

## 3. Default branch (UPDATES) — RESOLVED, see below

**Rule:** "Use master branch for everything."

The repository's history was created on `main`. A `master` branch has been
created from it and pushed. Changing the *default* branch on GitHub and
retiring `main` must be done in the repository settings on github.com — it
cannot be done from the working tree, and doing it breaks existing clones
until they re-point. See the note in the session that created `master`.
