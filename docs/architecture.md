# PoSoccer — ML-Agents Architecture Diagram Suite

Generated **2026-08-05** against `master` @ `5f25701`.
Project: top-down 2D physics soccer. Unity 6000.5.6f1, 2D URP, **Box2D** (`Physics2D`).

**Read this before the diagrams.** Two facts shape every one of them:

1. **No joints, no articulated rig, no PhysX.** Each agent is a *single* `Rigidbody2D`
   driven by one linear force and one torque (`Agent_Soccer.OnActionReceived`). There are
   no per-joint drive targets, no $K_p$/$K_d$ gains, no DoF chain. This is a recorded
   deviation — see `rules-exemptions.md` §2. The "Actuator Map" below therefore documents
   the **traction model**, which is what actually converts action vectors into motion.
2. **No trained brain is currently assigned.** All four personality `.onnx` were deleted
   on 2026-08-05 (they declared 102 inputs against a 118-input runtime). `brainModel` is
   `null` on all five profiles, so every player falls back to `Agent_HeuristicBot`. The
   inference path below is *the contract*, not something running today.

Total model inputs: **118** = 66 ray + 52 vector. Changing any of it obsoletes every export.

---

## 1. Runtime Inference Loop

### 1-simple

```mermaid
flowchart TD
    A[FixedUpdate<br/>0.01 s] --> B[DecisionRequester<br/>every 8 steps]
    B --> C[CollectObservations]
    C --> D[Sentis / InferenceEngine]
    D --> E[OnActionReceived]
    E --> F[Box2D solver]
    F --> A
```

### 1-detailed

```mermaid
flowchart TD
    subgraph Sense
      A1["Sensor_Vision<br/>RayPerceptionSensor2D<br/>11 rays · 300° · 24 m<br/>tags Ball/Wall/Goal/Agent<br/>→ 66 floats"]
      A2["Agent_Soccer.CollectObservations<br/>self 4 + stamina 1 + ball 4<br/>+ goals 5 + teammate 4<br/>+ opponents 8 = 26<br/>× 2 stacked → 52 floats"]
    end
    A1 --> M
    A2 --> M
    M["Model input 118<br/>normalize: true"] --> N["MLP<br/>2 layers × 256 units"]
    N --> O["4 continuous actions<br/>fwd · lateral · turn · boost<br/>tanh-bounded"]
    O --> P["OnActionReceived<br/>×1.6 ActionGain, clamp ±1"]
    P --> Q["Traction budget<br/>μ·m·g = 1.2·75·9.81 ≈ 883 N"]
    Q --> R["Rigidbody2D.AddForce / AddTorque<br/>slew 1200 N/s · torque 250 N·m"]
    R --> S["Physics2D step<br/>Δt 0.01 s · vel 8 / pos 3 iters<br/>gravity (0,0)"]
    S --> T{Terminal?}
    T -- goal / stalemate / OOB --> U[Agent_EnvController]
    T -- no --> A1
```

> `RayPerceptionSensor.OutputSize()` = `(DetectableTags + 2) × (2 × RaysPerDirection + 1)`
> = `(4 + 2) × 11` = **66**. It depends on tag and ray *counts* only — **not** on arc or
> range. Changing `ArcDegrees` or `RayLength` keeps every tensor shape identical while
> silently changing what the rays mean. See the sensor-geometry landmine in `CLAUDE.md`.

---

## 2. Tensor Blueprint

### 2-simple

```mermaid
flowchart LR
    R[rays 66] --> C[concat 118]
    V[vector 52] --> C
    C --> H[2 × 256] --> A[4 actions]
```

### 2-detailed

```mermaid
flowchart LR
    subgraph obs_0["obs_0 — ray sensor"]
      R1["[batch, 66]<br/>11 rays × 6<br/>(4 tags + hit + dist)"]
    end
    subgraph obs_1["obs_1 — vector, stacked ×2"]
      V1["[batch, 52]<br/>26 per frame"]
      V2["self pos/vel 4<br/>stamina 1<br/>ball rel pos/vel 4<br/>own+opp goal 5<br/>teammate slot 4<br/>opponent slots 2×4 = 8"]
    end
    R1 --> CAT["concat → [batch, 118]"]
    V1 --> CAT
    V2 -.->|zero-padded when slot empty| V1
    CAT --> NORM["running normalizer"]
    NORM --> L1["dense 256 + swish"]
    L1 --> L2["dense 256 + swish"]
    L2 --> PI["policy head → [batch, 4] μ<br/>+ log σ (continuous)"]
    L2 --> VF["value head → [batch, 1]"]
    PI --> ACT["fwd · lateral · turn · boost"]
```

**Zero-padding is load-bearing.** Training and eval are **1v1** — `SCN_Training` holds
exactly two agents — so the teammate block and the second opponent slot are *always zero*.
No policy has ever practised with a teammate; 2v2 is untested capability, not tuned
behaviour.

---

## 3. Reward Tree

### 3-simple

```mermaid
graph TD
    E[Episode] --> T[Terminal]
    E --> D[Dense shaping]
    T --> T1[goal +1.2]
    T --> T2[conceded −1.0]
    T --> T3[stalemate −0.6]
    D --> D1[approach ball]
    D --> D2[penalties]
```

### 3-detailed

```mermaid
graph TD
    ROOT["Reward_Settings (ScriptableObject)<br/>one asset per personality"]

    ROOT --> TERM["Terminal"]
    TERM --> G1["goalScorer +1.2<br/>MATT 1.4 · KIM 1.3"]
    TERM --> G2["assist +0.3"]
    TERM --> G3["teamBaselineVictory +0.1"]
    TERM --> G4["goalConceded −1.0"]
    TERM --> G5["stalemateTimeout −0.6"]

    ROOT --> DENSE["Dense (per FixedUpdate)"]
    DENSE --> P1["ballProximityScale 0.002<br/>useDifferentialProximity: true<br/>reward = (prevDist − curDist) × scale"]
    DENSE --> P2["facingAlignmentScale 0.0002"]
    DENSE --> P3["ballToGoalVelocityScale 0.001"]
    DENSE --> P4["ballContact 0.005<br/>≈ 240 touches per goal"]
    DENSE --> P5["stepPenalty 0.0"]

    ROOT --> PEN["Penalties"]
    PEN --> N1["actionJitterScale 0.0004"]
    PEN --> N2["wallProximityPenalty 0.0005"]
    PEN --> N3["cornerBallPenalty 0.0006"]

    ROOT --> TRAIT["Personality traits"]
    TRAIT --> X1["defensivePositionScale — KIM only"]
    TRAIT --> X2["possessionScale — NICK only"]
```

**Why the terminal numbers are what they are.** The original table
(`goalScorer 0.7 / stalemateTimeout −0.1`) made stalling *optimal*: EV(stall) = −0.1 vs
EV(attack at 50/50) = −0.15, so a policy needed a **>53% win rate before attacking beat
parking the bus** — and it wins ~17%. Retuned 2026-08-04 so attacking wins even at 30%
(−0.34 vs −0.60).

**Personality lives in terminal rewards, trait scales and physique — never in the
locomotion mechanics.** `RewardProfiles_MatchCodeDefaultsOnMechanics` (EditMode) pins the
mechanics terms to the code defaults after profile drift cost four training runs. Keep it
green.

---

## 4. Actuator Map

> **There are no joints.** The rule this diagram answers asks for joint drive targets,
> $K_p$/$K_d$ and DoF limits. PoSoccer has none — one `Rigidbody2D`, one force, one torque.
> What follows is the real actuation chain: a friction-circle traction model.

### 4-simple

```mermaid
flowchart LR
    A[4 actions] --> B[intent vector]
    B --> C[friction circle μ·m·g]
    C --> D[AddForce]
    A --> E[turn] --> F[AddTorque]
```

### 4-detailed

```mermaid
flowchart LR
    A0["a0 forward"] --> I["intent = fwd·up + lat·right<br/>ClampMagnitude 1"]
    A1["a1 lateral"] --> I
    A2["a2 turn"] --> TQ
    A3["a3 boost ≥ 0.1"] --> BO

    I --> DR["drive = 236 N × (m / 75 kg)<br/>constant N/kg → same top speed<br/>every physique"]
    BO --> BM["×2.2 while stamina remains"]
    BM --> DR
    DR --> SLEW["slew ≤ 1200 N/s<br/>(muscle ramp ~0.2 s jog)"]
    SLEW --> CIRC["friction circle<br/>|F_total| ≤ μ·m·g<br/>1.2 × 75 × 9.81 ≈ 883 N"]
    BRK["active foot braking<br/>when no drive intent"] --> CIRC
    LAT["lateral drag 0.4<br/>strafe ≈ 64% of run"] --> CIRC
    CIRC --> FRC["Rigidbody2D.AddForce"]

    TQ --> TS["torque 250 N·m<br/>× turnScale(speed)<br/>360°/s at rest → 25% at sprint"]
    TS --> TRQ["AddTorque, |ω| ≤ 360°/s"]

    FRC --> BODY["Rigidbody2D<br/>mass 75 kg · linearDamping 0.7"]
    TRQ --> BODY
    STA["Agent_Stamina<br/>power floor 0.6 when spent<br/>wear 0.002/s boosting, floor 0.6"] --> DR
```

Measured on the chassis: **4.35 m/s jog, 9.54 m/s sprint, t95 ≈ 3.7 s.** Gravity is
`(0,0)` in-plane (top-down; `rules-exemptions.md` §1) but $g = 9.81$ enters *physically*
through the traction budget — body weight sets the normal force, which sets how hard the
feet can push, cut and brake.

Ball: FIFA spec — r = 0.11 m, 0.43 kg, drag ~0.1 randomized per episode, Magnus curl.
Wall kick-out: 6 N·s impulse inside a 1.0 m band, 0.5 s cooldown (corner-scrum escape).

---

## 5. Hyperparameter Matrix

### 5-simple

```mermaid
flowchart LR
    P[PPO] --> H[lr 3e-4 linear]
    P --> B[batch 2048 / buffer 20480]
    P --> N[2 × 256]
    P --> S[20M steps]
```

### 5-detailed

```mermaid
flowchart LR
    subgraph trainer["trainer — config/STANDARD_phase9_randomized.yaml"]
      T1["trainer_type: ppo"]
      T2["max_steps: 20,000,000"]
      T3["time_horizon: 1024"]
      T4["summary_freq: 50,000"]
      T5["keep_checkpoints: 20<br/>checkpoint_interval: 1,000,000"]
    end
    subgraph hyper["hyperparameters"]
      H1["batch_size: 2048"]
      H2["buffer_size: 20480"]
      H3["learning_rate: 3.0e-4<br/>schedule: linear"]
      H4["beta: 8.0e-3"]
      H5["epsilon: 0.2"]
      H6["lambd: 0.95"]
      H7["num_epoch: 3"]
    end
    subgraph net["network_settings"]
      N1["normalize: true"]
      N2["hidden_units: 256"]
      N3["num_layers: 2"]
    end
    subgraph rs["reward_signals"]
      R1["extrinsic<br/>gamma 0.99 · strength 1.0"]
    end
    subgraph env["environment_parameters"]
      E1["goal_width: 6.0 (fixed —<br/>matches eval, no mismatch)"]
      E2["bot_strength: uniform<br/>[0.2, 1.0] per episode"]
    end
    trainer --> hyper --> net --> rs --> env
```

**Phase 9 is a single-variable change: the curriculum was replaced by sampling.** A gated
ladder starved every prior run — p7 spent all 20M steps at `bot_strength` 0.2 and was then
graded at 1.0. Coverage went *backwards* across runs (p5 → 0.8, p6 → 0.5, p7 → 0.2,
p8 → 0.35). Uniform sampling makes the train and eval distributions match and leaves no
threshold to tune wrong.

Parity is enforced: embedded `com.unity.ml-agents` **4.1.0** ↔ Python `mlagents`
**1.2.0.dev0**, pinned in `requirements-training.txt`.

---

## 6. Episode Lifecycle

### 6-simple

```mermaid
stateDiagram-v2
    [*] --> Reset
    Reset --> Stepping
    Stepping --> Stepping: decision every 8
    Stepping --> Terminal: goal / timeout / OOB
    Terminal --> Reset
```

### 6-detailed

```mermaid
stateDiagram-v2
    [*] --> Initialize

    Initialize: Agent_Soccer.Awake
    Initialize: contract asserted (26 obs × 2, 4 actions)
    Initialize: Sensor_Vision auto-added if absent
    Initialize: red team → HeuristicOnly when POSOCCER_OPPONENT=bot
    Initialize --> ResetPitch

    ResetPitch: random own-half spawns
    ResetPitch: ball drag randomized
    ResetPitch: goal_width from env params
    ResetPitch: EnsureSpawnCache heals + warns
    ResetPitch --> Stepping

    Stepping: FixedUpdate 0.01 s
    Stepping: DecisionRequester period 8
    Stepping: dense rewards accumulate
    Stepping --> Stepping: step < cap

    Stepping --> GoalScored: Reward_GoalTrigger
    Stepping --> Stalemate: step ≥ maxEnvironmentSteps (5000)
    Stepping --> OutOfBounds: containment watchdog

    GoalScored: +1.2 scorer · +0.3 assist · −1.0 conceded
    Stalemate: −0.6 both sides
    OutOfBounds: reset, no terminal reward

    GoalScored --> EpisodeEnded
    Stalemate --> EpisodeEnded
    OutOfBounds --> EpisodeEnded

    EpisodeEnded: event fires BEFORE reset
    EpisodeEnded: subscribers read cumulative reward + StepCount
    EpisodeEnded: HUD · MatchFX · Audio · CameraFollow · EvalStats
    EpisodeEnded --> ResetPitch
    EpisodeEnded --> [*]: eval episode budget reached
```

**The event ordering is a contract.** `EpisodeEnded` fires *before* `ResetPitch` so
subscribers can still sample `GetCumulativeReward()` and `StepCount`. Five components
depend on it, each with a matched `+=`/`-=` in enable/disable. Moving the fire site after
the reset would silently zero every reported reward.

`stepCapOverride` on the exhibition Pitch shortens episodes to 2500 steps for match pace;
training uses the full 5000.
