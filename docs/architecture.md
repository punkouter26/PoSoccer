# PoSoccer — ML-Agents Architecture Diagram Suite

Generated 2026-07-31 against commit `521293c` (+ bot boost-shot/shoulder-charge working tree).
Project: top-down 2D physics soccer (Unity 6000.5.4f1, 2D URP, **Box2D** physics — this
project has no 3D PhysX, no joints, no articulated rigs; agents are momentum-driven boxes).
Note: the generation template referenced `CreatureTrainingRace.unity`; this project's
training scene is `Assets/Scenes/SCN_Training.unity` — all diagrams reflect the real scene.

Brains: `STANDARD` (trained), `MATT` / `KIM` / `NICK` (reward-DNA designed, untrained — bot-driven).

---

## 1. Runtime Inference Loop

### 1-simple
```mermaid
flowchart TD
    DR[DecisionRequester\nevery 8 physics steps] --> OBS[Collect observations\nvector + vision rays]
    OBS --> NN[STANDARD.onnx\nUnity InferenceEngine]
    NN --> ACT[OnActionReceived\nmove / turn / boost]
    ACT --> PHYS[Box2D physics\nforces move body & ball]
    PHYS --> WORLD[Goals, walls, opponents]
    WORLD --> DR
```

### 1-detailed
```mermaid
flowchart TD
    FU[FixedUpdate @ 0.01 s\nAcademy.EnvironmentStep] --> DR{DecisionRequester\nDecisionPeriod = 8\n→ 12.5 decisions/s}
    DR -->|decision step| CO["Agent_Soccer.CollectObservations\n18 floats: vel(2) eye(2) stamina(1)\nrelBall(2) ballVel(2) relOppGoal(2)\nrelOwnGoal(2) distOppGoal(1) mate(4)\n× 2 stacked → obs_1 [1,36]"]
    DR -->|decision step| SV["Sensor_Vision (RayPerceptionSensor2D)\n11 rays, 120° arc, 12 u range\ntags: Ball Wall Goal Agent\n11 × (4+2) = obs_0 [1,66]"]
    CO --> BP[BehaviorParameters\nname STANDARD - enforced in Awake\nTeamId = team enum]
    SV --> BP
    BP -->|InferenceOnly / Default| IE["Unity.InferenceEngine (Sentis lineage)\nSTANDARD.onnx - 93,140 params\nCPU burst inference"]
    BP -->|HeuristicOnly| HB[Agent_HeuristicBot\nchase / line-up / corner-craft\nboost-shot / shoulder-charge]
    IE --> AB["continuous_actions [1,3] ∈ [-1,1]"]
    HB --> AB
    AB --> OAR["OnActionReceived\ndrive = MoveTowards(prev, a0·700N·boost·stamina, 2300 N/s)\ntorque = a1·250 N·m, ω clamp 360°/s\nboost: a2>0.1 & stamina → ×2.2"]
    OAR --> B2D["Rigidbody2D (Box2D)\nagent 60-95 kg, ball 0.43 kg\nMagnus curl + spin transfer\nwall kick-out impulse 6 N·s"]
    B2D --> EV[Reward_GoalTrigger / walls / corner arcs]
    EV --> ENVC[Agent_EnvController\nrewards, episode events]
    ENVC --> FU
```

---

## 2. Tensor Blueprint

### 2-simple
```mermaid
flowchart LR
    A[Vision rays\n66 numbers] --> N[Neural network\n2 hidden layers]
    B[Body & ball state\n36 numbers] --> N
    N --> C[3 actions\nmove, turn, boost]
```

### 2-detailed
```mermaid
flowchart LR
    subgraph Inputs
        O0["obs_0 [B,66]\nray hits one-hot 4 tags\n+ miss flag + distance,\n× 11 rays"]
        O1["obs_1 [B,36]\n18 obs × 2 stacked frames\n(velocity/trend context\nat DecisionPeriod 8)"]
    end
    O0 --> NRM["VectorNormalizer\n(normalize: true,\nrunning mean/var)"]
    O1 --> NRM
    NRM --> H1["Linear 102→H + Swish"]
    H1 --> H2["Linear H→H + Swish"]
    H2 --> MU["mu [B,3]"]
    H2 --> SIG["log_sigma [3]\n(state-independent)"]
    MU --> SAMPLE["Gaussian sample → tanh-free clamp [-1,1]"]
    SIG --> SAMPLE
    SAMPLE --> OUT1["continuous_actions [B,3]"]
    MU --> OUT2["deterministic_continuous_actions [B,3]\n(used by eval/exhibition inference)"]
    NOTE["H = 256 (phase 1 PPO, STANDARD.onnx 93,140 params)\nH = 512 (phase 2 POCA, checkpoints 317,140 params)\nextra outputs: version_number, memory_size=0 (no LSTM)"]
```

---

## 3. Reward Tree (per-brain personality DNA in `Reward_Settings` assets)

### 3-simple
```mermaid
graph TD
    R[Rewards] --> W[Win the episode]
    R --> P[Play well each step]
    W --> G[Score +, Assist +, Concede −, Draw −]
    P --> D[Chase ball, face ball,\nshoot goalward, avoid walls & corners]
    R --> U[Every player: same total budget,\nspent differently = personality]
```

### 3-detailed
```mermaid
graph TD
    ROOT[Reward stream] --> TERM["Terminal (episode end)\nbudget: Σ = 2.2 for every player"]
    ROOT --> DENSE["Dense (per decision)\nbudget: Σ scales = 0.0016"]
    ROOT --> SPARSE["Sparse"]
    ROOT --> GROUP["MA-POCA group (2v2)\nwin +1 / lose −1 team reward"]
    TERM --> TS["goalScorer: STD 0.7, MATT 1.0,\nKIM 0.45, NICK 0.5"]
    TERM --> TA["assist: STD 0.3, MATT 0.15,\nKIM 0.35, NICK 0.5"]
    TERM --> TB["teamBaselineVictory: 0.15–0.2"]
    TERM --> TC["goalConceded: STD −1.0, MATT −0.9,\nKIM −1.2, NICK −1.0"]
    TERM --> TD2["stalemateTimeout: MATT −0.2 … KIM −0.02"]
    DENSE --> D1["ballProximity 1/(1+d) × 0.0002–0.0004"]
    DENSE --> D2["facingAlignment dot(eye,toBall) × 0.0002"]
    DENSE --> D3["ballToGoalVelocity (the shoot gradient)\nSTD 0.0010, MATT 0.0011, KIM/NICK 0.0004–6"]
    DENSE --> D4["KIM trait: defensivePosition 0.0006\n(stand on ball→own-goal line)"]
    DENSE --> D5["NICK trait: possession 0.0006\n(ball within 1.2 m)"]
    DENSE --> PEN["Penalties (style, off-budget):\nstep −0.0001/−0.0002, jitter ×0.0005–0.002,\nwall band −0.0005, corner zone −0.0006"]
    SPARSE --> S1["first ballContact +0.05/episode"]
```

---

## 4. Actuator Map (no joints/Kp-Kd — force-driven rigid bodies)

### 4-simple
```mermaid
flowchart LR
    A0[Action 0\nmove] --> F[Push body forward/back]
    A1[Action 1\nturn] --> T[Spin body]
    A2[Action 2\nboost] --> S[Sprint ×2.2\nwhile stamina lasts]
    F --> BALL[Run through ball = shot\nrun into opponent = shove]
```

### 4-detailed
```mermaid
flowchart LR
    A0["a[0] move ∈ [-1,1]"] --> SLEW["target = a0 × 700 N × boostMul × staminaPow\nslew: MoveTowards @ 2300 N/s\n(reversals take ~0.3-0.6 s)"]
    SLEW --> ADDF["AddForce(eyeAxis × drive)\nmass 60 (NICK) / 66 (KIM) / 75 (STD) / 95 (MATT) kg\n→ m·v_max invariant: equal top-speed momentum"]
    A1["a[1] turn ∈ [-1,1]"] --> TQ["AddTorque(a1 × 250 N·m)\nangularVelocity clamp ±360°/s"]
    A2["a[2] boost ∈ [0,1]"] --> BST{"a2 > 0.1\n& stamina > 0?"}
    BST -->|yes| MUL["boostMul = 2.2\nstamina drains (Agent_Stamina)"]
    BST -->|no| ONE["boostMul = 1\nstaminaPow = 0.6 + 0.4·ratio"]
    ADDF --> CONTACT["Ball contact = momentum transfer\n+ spin ×0.3 → Magnus curl"]
    CONTACT --> KICKOUT["Auto wall kick-out:\ntouch ball in 1 m wall band →\nimpulse 6 N·s corners / 3 N·s walls\n(goal mouths exempt, 0.5 s cooldown)"]
```

---

## 5. Hyperparameter Matrix

### 5-simple
```mermaid
flowchart LR
    P1[Phase 1: PPO\nvs rule-based bot\n20M steps done] --> B[STANDARD brain]
    P2[Phase 2: MA-POCA\nself-play, both teams learn\n30M steps running] --> B
```

### 5-detailed
```mermaid
flowchart LR
    subgraph "Phase 1 - PPO (soccer_p1e_00, complete)"
        P1A["trainer: ppo | lr 3e-4 linear→0\nbatch 2048 | buffer 20480 | epochs 3\nβ 5e-3 | ε 0.2 | λ 0.95 | γ 0.99\nnet 2×256 Swish, normalize\ntime_horizon 512 | max 20M"]
    end
    subgraph "Phase 2 - MA-POCA (soccer_p2_00, running)"
        P2A["trainer: poca | lr 3e-4 constant\nbatch 2048 | buffer 20480 | epochs 3\nβ 5e-3 | ε 0.2 | λ 0.95 | γ 0.99\nnet 2×512 Swish, normalize\ntime_horizon 1000 | max 30M"]
        P2B["self_play: save every 50k,\nteam_change 200k, swap 2k,\nwindow 10, latest-model 50%,\nELO start 1200"]
    end
    P1A -->|"--initialize-from\n⚠ arch mismatch 256→512:\npolicy re-initialized (see report)"| P2A
    subgraph "Curriculum goal_width (progress-gated)"
        C0["Lesson0 wide 6.0\n0–20%"] --> C1["Lesson1 mid 4.0\n20–50% (active)"] --> C2["Lesson2 tight 2.5\n50–100%"]
    end
```

---

## 6. Episode Lifecycle

### 6-simple
```mermaid
stateDiagram-v2
    [*] --> Kickoff
    Kickoff --> Playing
    Playing --> Goal: ball fully in a net
    Playing --> Timeout: step cap reached
    Goal --> Kickoff: rewards paid, pitch reset
    Timeout --> Kickoff
```

### 6-detailed
```mermaid
stateDiagram-v2
    [*] --> ResetPitch
    ResetPitch: ResetPitch\nread goal_width curriculum\nball center + jitter, random drag 0.08-0.15\nagents random own-half spawns ±20° facing
    ResetPitch --> StepLoop
    StepLoop: Step loop (FixedUpdate 0.01 s)\ndecision every 8 steps → dense rewards\nMagnus force · stamina tick · kick-out pops
    StepLoop --> GoalScored: Reward_GoalTrigger\n(ball fully inside net)
    StepLoop --> Stalemate: StepCount ≥ cap\n(5000 train / 2500 exhibition)
    StepLoop --> OutOfBounds: containment watchdog\n(anything beyond extents +1.5)
    GoalScored: OnGoalScored\nscorer +0.7·profile / assist +0.3\nconceders −1.0 / others +0.2\nPOCA group +1 / −1, EndEpisode
    Stalemate: OnStalemate\nall agents stalemateTimeout\nGroupEpisodeInterrupted
    OutOfBounds: OnOutOfBounds\nno rewards, episode interrupted
    GoalScored --> EpisodeEnded
    Stalemate --> EpisodeEnded
    OutOfBounds --> EpisodeEnded
    EpisodeEnded: EpisodeEnded event fires BEFORE reset\n(HUD toast, FX, audio, eval stats\nread cumulative rewards here)
    EpisodeEnded --> ResetPitch
```
