using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.Serialization;

namespace PoSoccer
{
    /// <summary>
    /// Top-down 2D soccer agent (SoccerAgent_v01).
    /// Continuous actions: [0] move fwd/back along +Y eye axis, [1] turn torque, [2] boost.
    /// Observations: 14 base floats + 4 per teammate slot (see CollectObservations).
    /// Pure momentum ball interaction — all ball control is physics contact.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(Agent_Stamina))]
    public sealed class Agent_Soccer : Agent
    {
        public enum Team { Blue = 0, Red = 1 }

        [Header("Identity")]
        public Team team = Team.Blue;
        [Tooltip("ML behavior / brain name. STANDARD is the base brain; personality " +
                 "brains (MATT, KIM, NICK) get their own name + reward profile + policy.")]
        public string brainName = "STANDARD";

        [Header("Actuation (SI units - 75 kg body)")]
        [Tooltip("Drive force (N) at action = 1. 236 N on a 75 kg body with damping 0.7 " +
                 "gives a ~4.5 m/s jog reached over ~4 s, matching human sprint build-up.")]
        [FormerlySerializedAs("moveForce")]
        [SerializeField] private float _moveForce = 236f;

        [Header("Ground contact (traction)")]
        [Tooltip("Runtime linear damping. Low value = long coast; braking is done actively " +
                 "by the feet within the traction budget rather than by fake global drag.")]
        [SerializeField] private float _linearDamping = 0.7f;
        [Tooltip("Coefficient of friction, studs on turf (~1.0-1.5). Total foot force is " +
                 "capped at mu * m * g, so cuts, launches and braking are all traction-limited.")]
        [SerializeField] private float _tractionMu = 1.2f;
        [Tooltip("Extra sideways damping (body-frame). A real body skids less laterally than " +
                 "it rolls forward, but at the original 1.5 the drag was overwhelming the drive " +
                 "force for any action with a strafe component (lateral drag = 2.1x linear drag, " +
                 "so even a brain intent of magnitude 0.4 with mostly-lateral split could not " +
                 "accelerate). 0.4 keeps strafing slower than running (~64% of forward) without " +
                 "stranding cautious-trained brains at <0.3 m/s.")]
        [SerializeField] private float _lateralDrag = 0.4f;
        [Tooltip("Fraction of the standing turn rate still available at full sprint. " +
                 "Humans pivot freely at rest and barely at all at top speed.")]
        [Range(0.05f, 1f)]
        [SerializeField] private float _sprintTurnFactor = 0.25f;
        [Tooltip("Speed (m/s) treated as full sprint when scaling the turn rate.")]
        [SerializeField] private float _turnScaleSpeed = 10f;
        [Tooltip("Turn torque (N*m) at action = 1.")]
        [FormerlySerializedAs("turnTorque")]
        [SerializeField] private float _turnTorque = 250f;
        [Tooltip("Force multiplier while boosting with stamina available (PRD: 2.2x -> ~10 m/s sprint).")]
        [FormerlySerializedAs("boostMultiplier")]
        [SerializeField] private float _boostMultiplier = 2.2f;
        [Tooltip("Boost action activation threshold (PRD: 0.1).")]
        [FormerlySerializedAs("boostThreshold")]
        [SerializeField] private float _boostThreshold = 0.1f;
        [Tooltip("Human rotation limit (deg/s). Athletes cannot spin faster than ~a full turn per second.")]
        [FormerlySerializedAs("maxAngularVelocityDeg")]
        [SerializeField] private float _maxAngularVelocityDeg = 360f;
        [Tooltip("How fast the applied drive force can change (N/s). Muscle ramps over ~0.2s at jog, ~0.45s to full sprint.")]
        [FormerlySerializedAs("forceSlewRate")]
        [SerializeField] private float _forceSlewRate = 1200f;
        [Range(0.3f, 1f)]
        [Tooltip("Power fraction remaining at zero stamina (exhausted agents visibly slow).")]
        [FormerlySerializedAs("tiredPowerFloor")]
        [SerializeField] private float _tiredPowerFloor = 0.6f;

        [Header("Wall kick-out (corner escape)")]
        [Tooltip("Impulse (N*s) that pops the ball toward open field when a player touches it inside the wall band. 6 N*s on the 0.43 kg ball is a ~14 m/s pop in corners; straight walls get half.")]
        [FormerlySerializedAs("wallKickImpulse")]
        [SerializeField] private float _wallKickImpulse = 6f;
        [Tooltip("Distance from a wall inside which the pop triggers.")]
        [FormerlySerializedAs("wallKickBand")]
        [SerializeField] private float _wallKickBand = 1.0f;
        [Tooltip("Seconds between pops from the same player (corner scrums are continuous contact).")]
        [FormerlySerializedAs("wallKickCooldown")]
        [SerializeField] private float _wallKickCooldown = 0.5f;

        [Header("Wiring (set by env controller at runtime)")]
        public Agent_EnvController env;
        public Reward_Settings rewards;

        public Rigidbody2D Body { get; private set; }
        public Agent_Stamina Stamina { get; private set; }
        public bool IsBoosting { get; private set; }
        public bool TouchedBallThisEpisode { get; private set; }

        /// <summary>
        /// True when this agent is driven by <see cref="Agent_HeuristicBot"/> instead of a
        /// trained brain. Reads the policy that was actually installed (by Agent_MatchLoader
        /// or <see cref="ApplyEvalMode"/>), so it stays honest when a profile carries no
        /// brainModel and silently falls back to the bot. Valid from Awake onward.
        /// </summary>
        public bool RuleBased =>
            _behavior == null || _behavior.BehaviorType != BehaviorType.InferenceOnly;

        Agent_HeuristicBot _bot;
        BehaviorParameters _behavior;
        Transform _label;                              // identity letter, kept upright
        Vector2 _driveVec;                             // slew-limited foot force (N), world space
        float _nextWallKick;                           // kick-out cooldown timestamp
        readonly float[] _prevActions = new float[4];  // for the jitter penalty
        float _prevBallDist = float.PositiveInfinity;  // for differential-proximity reward

        /// <summary>Standard gravity, used for the traction budget (mu * m * g).</summary>
        const float Gravity = 9.81f;

        /// <summary>Body mass the tuning constants are quoted against.</summary>
        const float ReferenceMass = 75f;

        // Drive force scales with mass (constant N/kg), so every physique reaches
        // the same top speed and acceleration - bigger muscles move a bigger body -
        // while heavier players still carry more momentum into contact.
        float _driveForce;

        void CacheDriveForce()
        {
            float mass = Body != null ? Body.mass : ReferenceMass;
            _driveForce = _moveForce * (mass / ReferenceMass);
        }

        /// <summary>
        /// Vector observation count: 14 self/ball/goal floats + 4 teammate floats
        /// + 8 opponent floats (2 slots x rel-position/velocity), each zero-padded when
        /// the slot is empty so one brain contract covers 1v1, 2v2 and 3v3.
        ///
        /// 2026-08-04: 18 -> 26. Every .onnx exported before that date expects 18 and is
        /// obsolete - the inference loader rejects a shape mismatch outright, which is
        /// the safe failure (unlike a sensor-arc change, which silently reinterprets).
        /// </summary>
        public const int BaseObservationSize = 26;

        /// <summary>
        /// Opponent slots in the vector observation, nearest first, 4 floats each
        /// (relative position + velocity). Zero-padded when fewer opponents exist, so
        /// 1v1 / 2v2 / 3v3 all share one contract.
        /// </summary>
        public const int OpponentSlots = 2;

        // Reused across CollectObservations calls - zero alloc in the hot path
        // (performance.md). Never nulled, so the array survives episode resets.
        readonly Agent_Soccer[] _opponentBuffer = new Agent_Soccer[OpponentSlots];

        /// <summary>
        /// Continuous actions: [0] forward drive, [1] lateral drive (strafe),
        /// [2] turn, [3] boost. Lateral was added so the body no longer has to
        /// rotate in order to translate - real players sidestep and backpedal.
        /// </summary>
        public const int ContinuousActionCount = 4;

        // Team frame (the colored border drawn around each player) - server-side
        // tunable so the designer can thicken or thin it without recompiling.
        [Header("Team Frame")]
        [SerializeField] private float _frameInset = 0.18f;     // how far OUTSIDE the body sprite the frame sits
        [SerializeField] private float _frameThickness = 0.10f;  // line width
        [SerializeField] private float _frameZ = 0.01f;          // behind the body, in front of the pitch
        protected override void Awake()
        {
            base.Awake();
            // Sensor_Vision owns the RayPerceptionSensor2D config (11 rays, 2-4
            // detectable tags) that produces the obs_0 slot the trained brains
            // were taught on. DefaultExecutionOrder(-100) on Sensor_Vision guarantees
            // its Awake runs before Agent initialization, so the RayPerceptionSensor
            // is attached + configured before DecisionRequester fires.
            if (GetComponent<Sensor_Vision>() == null)
            {
                gameObject.AddComponent<Sensor_Vision>();
            }
            // Configure the policy contract in code (runs before Agent.OnEnable
            // initializes the policy) so scene serialization can never drift from it.
            _behavior = GetComponent<BehaviorParameters>();
            if (_behavior != null)
            {
                _behavior.BehaviorName = string.IsNullOrEmpty(brainName) ? "STANDARD" : brainName;
                _behavior.TeamId = (int)team;
                _behavior.BrainParameters.VectorObservationSize = BaseObservationSize;
                // 2 stacked frames give the policy velocity/trend context at the
                // slower decision cadence (period 8).
                _behavior.BrainParameters.NumStackedVectorObservations = 2;
                _behavior.BrainParameters.ActionSpec =
                    ActionSpec.MakeContinuous(ContinuousActionCount);
                ApplyEvalMode();
                ApplyTrainingOpponent();
            }
        }

        /// <summary>
        /// Phase-1 contract: the RED team is the scripted opponent while a trainer is
        /// attached. Opt-in via POSOCCER_OPPONENT=bot, which scripts/train-phase1.ps1
        /// sets before launching.
        ///
        /// Without this, both agents in SCN_Training keep BehaviorType.Default, which
        /// routes every agent to the trainer - so "phase 1 vs the heuristic bot"
        /// silently became symmetric self-play, Agent_HeuristicBot never ran, and the
        /// bot_strength curriculum had nothing to act on. Eval was then the first time
        /// a policy ever met the bot, which is why four runs produced strong training
        /// reward and a flat 15-19% win rate against it.
        ///
        /// Leave the variable unset for genuine self-play runs (phase 2 POCA, 3c).
        /// </summary>
        void ApplyTrainingOpponent()
        {
            // Eval owns policy assignment; it forces the opponent to heuristic itself.
            if (Agent_EvalStats.EvalMode) return;
            if (team != Team.Red) return;

            string mode = System.Environment.GetEnvironmentVariable("POSOCCER_OPPONENT");
            if (string.IsNullOrEmpty(mode) ||
                !mode.Equals("bot", System.StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _behavior.BehaviorType = BehaviorType.HeuristicOnly;
            var bot = GetComponent<Agent_HeuristicBot>();
            if (bot != null) bot.enabled = true;
            // Printed once per red agent into the env player log. Silence here means
            // the run is self-play, which is the failure this method exists to catch.
            Debug.Log($"[Agent_Soccer] {name}: scripted opponent (POSOCCER_OPPONENT=bot).");
        }

        // Evaluation switch (v1 spec): env vars are set by scripts/evaluate.ps1 before
        // launch and read here, before Agent.OnEnable initializes the policy.
        void ApplyEvalMode()
        {
            if (!Agent_EvalStats.EvalMode) return;

            if (Agent_EvalStats.BaselineMode)
            {
                _behavior.BehaviorType = BehaviorType.HeuristicOnly;
            }
            else if (team == Team.Blue)
            {
                if (_behavior.Model != null)
                {
                    _behavior.BehaviorType = BehaviorType.InferenceOnly;
                }
                else
                {
                    Debug.LogError("[Agent_Soccer] Eval mode but no model assigned - " +
                                   "run scripts/update-model.ps1 and assign the .onnx. " +
                                   "Falling back to heuristic; run marked invalid.");
                    Agent_EvalStats.MarkInvalid();
                    _behavior.BehaviorType = BehaviorType.HeuristicOnly;
                }
            }
            else
            {
                _behavior.BehaviorType = BehaviorType.HeuristicOnly;
            }
        }

        public override void Initialize()
        {
            Body = GetComponent<Rigidbody2D>();
            Stamina = GetComponent<Agent_Stamina>();
            _bot = GetComponent<Agent_HeuristicBot>();
        }

        // Runs after Agent_EnvController.Start (execution order -50) has assigned
        // the reward profile, so the personality's look is available.
        void Start()
        {
            if (rewards == null) return;

            // Physique: size and mass from the profile. Drive force and drag are
            // shared, so mass trades acceleration/top speed for shove resistance
            // while top-speed momentum stays identical across the roster.
            if (rewards.bodyScale > 0f && !Mathf.Approximately(rewards.bodyScale, 1f))
            {
                Vector3 s = transform.localScale;
                transform.localScale = new Vector3(
                    s.x * rewards.bodyScale, s.y * rewards.bodyScale, s.z);
            }
            if (rewards.bodyMass > 0f && Body != null) Body.mass = rewards.bodyMass;
            // Damping is owned by code, not the scene: the traction model brakes
            // actively, so global drag must stay low or it double-counts.
            if (Body != null) Body.linearDamping = _linearDamping;
            CacheDriveForce();

            // Body wears the personality color; the eye shows the team.
            var body = GetComponent<SpriteRenderer>();
            if (body != null && rewards.playerColor.a > 0f)
                body.color = rewards.playerColor;

            Color teamColor = team == Team.Blue
                ? new Color(0.2f, 0.5f, 1f)
                : new Color(1f, 0.25f, 0.2f);

            var eye = transform.Find("Eye");
            if (eye != null && eye.TryGetComponent(out SpriteRenderer eyeRenderer))
                eyeRenderer.color = teamColor;

            // Thick team-colored frame around the body: a 4-line LineRenderer
            // drawn just outside the body sprite so the team reads at a glance,
            // even on the small portrait phone view. We use a LineRenderer per
            // border instead of a 1.3x halo so the outline always looks line-shaped
            // and stays solid even when the sprite shape is non-square.
            if (body != null && body.sprite != null && transform.Find("TeamFrame_Top") == null)
            {
                BuildTeamFrame(transform, body, teamColor, _frameInset, _frameThickness, -_frameZ);
            }

            // Identity letter (S/M/K/N) on the body, driven by the assigned profile.
            if (!string.IsNullOrEmpty(rewards.playerName) && transform.Find("Label") == null)
            {
                var labelGo = new GameObject("Label");
                labelGo.transform.SetParent(transform, false);
                labelGo.transform.localPosition = new Vector3(0f, -0.12f, 0f);

                var text = labelGo.AddComponent<TextMesh>();
                text.text = rewards.playerName.Substring(0, 1);
                text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                text.fontSize = 96;
                text.characterSize = 0.085f;
                text.anchor = TextAnchor.MiddleCenter;
                text.alignment = TextAlignment.Center;
                text.fontStyle = FontStyle.Bold;
                text.color = Color.black;

                var renderer = labelGo.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = text.font.material;
                renderer.sortingOrder = 5;
                _label = labelGo.transform;
            }
        }

        void Update()
        {
            // Letters stay readable no matter which way the body faces.
            if (_label != null) _label.rotation = Quaternion.identity;
        }

        public override void OnEpisodeBegin()
        {
            TouchedBallThisEpisode = false;
            IsBoosting = false;
            _driveVec = Vector2.zero;
            _nextWallKick = 0f;
            System.Array.Clear(_prevActions, 0, _prevActions.Length);
            _prevBallDist = float.PositiveInfinity;
            Stamina.ResetForEpisode();
            Body.linearVelocity = Vector2.zero;
            Body.angularVelocity = 0f;
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            Vector2 half = env != null ? env.PitchHalfExtents : new Vector2(10f, 6f);
            float invMax = 1f / Mathf.Max(half.x, half.y);

            // Self state (4)
            sensor.AddObservation(Body.linearVelocity * 0.1f);          // ~[-1,1] at 10 u/s
            sensor.AddObservation((Vector2)transform.up);               // eye axis

            // Stamina (1)
            sensor.AddObservation(Stamina.Ratio);

            // Ball (4)
            Vector2 relBall = Vector2.zero, ballVel = Vector2.zero;
            if (env != null && env.Ball != null)
            {
                relBall = (env.Ball.position - Body.position) * invMax;
                ballVel = env.Ball.linearVelocity * 0.1f;
            }
            sensor.AddObservation(relBall);
            sensor.AddObservation(ballVel);

            // Goals (5)
            Vector2 relOpp = Vector2.zero, relOwn = Vector2.zero;
            float distToOppGoal = 0f;
            if (env != null)
            {
                Vector2 opp = env.GetGoalPosition(Opponent(team));
                Vector2 own = env.GetGoalPosition(team);
                relOpp = (opp - Body.position) * invMax;
                relOwn = (own - Body.position) * invMax;
                distToOppGoal = Mathf.Clamp01((opp - Body.position).magnitude * invMax);
            }
            sensor.AddObservation(relOpp);
            sensor.AddObservation(relOwn);
            sensor.AddObservation(distToOppGoal);

            // Teammate (4, zero-padded in 1v1)
            Agent_Soccer mate = env != null ? env.GetTeammate(this) : null;
            if (mate != null && mate.Body != null)
            {
                sensor.AddObservation((mate.Body.position - Body.position) * invMax);
                sensor.AddObservation(mate.Body.linearVelocity * 0.1f);
            }
            else
            {
                sensor.AddObservation(Vector2.zero);
                sensor.AddObservation(Vector2.zero);
            }

            // Opponents (4 x OpponentSlots, nearest first, zero-padded).
            // The ray sensor nominally covers opponents but only guarantees detection
            // within ~1.9 units and cannot tell a teammate from a foe (shared "Agent"
            // tag), while the scripted bot reads opponents exactly at any range. These
            // slots close that gap - see Agent_EnvController.GetOpponents.
            int found = env != null ? env.GetOpponents(this, _opponentBuffer) : 0;
            for (int slot = 0; slot < OpponentSlots; slot++)
            {
                if (slot < found && _opponentBuffer[slot] != null && _opponentBuffer[slot].Body != null)
                {
                    var foe = _opponentBuffer[slot].Body;
                    sensor.AddObservation((foe.position - Body.position) * invMax);
                    sensor.AddObservation(foe.linearVelocity * 0.1f);
                }
                else
                {
                    sensor.AddObservation(Vector2.zero);
                    sensor.AddObservation(Vector2.zero);
                }
                _opponentBuffer[slot] = null;   // drop refs so resets cannot leak stale agents
            }
        }

        /// <summary>
        /// Draws a 4-line, team-colored rectangle around the body sprite so the
        /// team reads at a glance even on the small portrait phone view. One
        /// LineRenderer per border (4 child GameObjects) is the cleanest way to
        /// get a closed, filled-edge frame that scales with the body sprite.
        /// Sits behind the body (sortingOrder = -2) so it never covers the eye
        /// or the identity letter.
        /// </summary>
        static void BuildTeamFrame(Transform parent, SpriteRenderer body, Color color,
            float inset, float thickness, float zOffset)
        {
            // Read the sprite's local bounds. SpriteRenderer.bounds is world-space;
            // we want the half-extents in the body's LOCAL space so the frame
            // follows rotation and scale exactly.
            Bounds b = body.sprite.bounds;
            // body-local size = (bounds * 2) * transform.localScale (per axis).
            Vector3 bodyScale = body.transform.localScale;
            float halfW = b.extents.x * Mathf.Abs(bodyScale.x) + inset;
            float halfH = b.extents.y * Mathf.Abs(bodyScale.y) + inset;

            BuildTeamFrameEdge(parent, "TeamFrame_Top",
                new Vector3(-halfW, halfH, zOffset),
                new Vector3(halfW, halfH, zOffset),
                color, thickness, body.sortingLayerName, body.sortingOrder - 2);
            BuildTeamFrameEdge(parent, "TeamFrame_Bottom",
                new Vector3(-halfW, -halfH, zOffset),
                new Vector3(halfW, -halfH, zOffset),
                color, thickness, body.sortingLayerName, body.sortingOrder - 2);
            BuildTeamFrameEdge(parent, "TeamFrame_Left",
                new Vector3(-halfW, -halfH, zOffset),
                new Vector3(-halfW, halfH, zOffset),
                color, thickness, body.sortingLayerName, body.sortingOrder - 2);
            BuildTeamFrameEdge(parent, "TeamFrame_Right",
                new Vector3(halfW, -halfH, zOffset),
                new Vector3(halfW, halfH, zOffset),
                color, thickness, body.sortingLayerName, body.sortingOrder - 2);
        }

        static void BuildTeamFrameEdge(Transform parent, string name, Vector3 localA,
            Vector3 localB, Color color, float thickness, string sortingLayerName,
            int sortingOrder)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.positionCount = 2;
            lr.SetPosition(0, localA);
            lr.SetPosition(1, localB);
            lr.startWidth = thickness;
            lr.endWidth = thickness;
            lr.numCornerVertices = 0;
            lr.numCapVertices = 0;
            lr.alignment = LineAlignment.View;
            lr.startColor = color;
            lr.endColor = color;
            // Use the URP unlit sprite material so the team color stays bright even
            // under the GoalGlow point lights; fall back to Sprites/Default if the
            // project doesn't ship the URP 2D unlit shader.
            var mat = new Material(Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
                ?? Shader.Find("Sprites/Default"));
            mat.color = color;
            lr.sharedMaterial = mat;
            lr.sortingLayerName = sortingLayerName;
            lr.sortingOrder = sortingOrder;
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            // Action gain: trained brains converged on cautious magnitudes
            // (~0.1-0.5; anti-twitch reward + small ball-proximity gradient
            // rewards trained them to creep). Raw drive force scaled by those
            // magnitudes (driveForce * intentMag) gets killed by linear + lateral
            // drag before the body builds visible speed. 1.6x lifts a 0.3 brain
            // output to 0.48 (well above the brake/drag cutoff) while still
            // saturating at 1 for full-throttle decisions - no contract change.
            const float ActionGain = 1.6f;
            float move = Mathf.Clamp(actions.ContinuousActions[0] * ActionGain, -1f, 1f);
            float lateral = Mathf.Clamp(actions.ContinuousActions[1] * ActionGain, -1f, 1f);
            float turn = Mathf.Clamp(actions.ContinuousActions[2] * ActionGain, -1f, 1f);
            float boost = Mathf.Clamp01(actions.ContinuousActions[3]);

            float dt = Time.fixedDeltaTime;
            Vector2 forwardAxis = transform.up;
            Vector2 rightAxis = transform.right;

            // Intent in body frame. Clamped to length 1 so diagonal input cannot
            // exceed the straight-ahead power budget.
            Vector2 intent = Vector2.ClampMagnitude(
                forwardAxis * move + rightAxis * lateral, 1f);

            // v2: boost gating - require intent to be at least half-forward before
            // boost activates. A full strafe with boost would otherwise burn stamina
            // without accelerating the agent (lateral drag immediately arrests the
            // boost-supplied motion). The heuristic bot already does this implicitly
            // (boost only fires when signedAngle < driveAngleDeg).
            float forwardShare = intent.sqrMagnitude > 0.001f
                ? Vector2.Dot(intent.normalized, forwardAxis)
                : 0f;
            IsBoosting = boost > _boostThreshold && Stamina.HasStamina && forwardShare > 0.5f;

            // Exhaustion scales available power; foot force slews rather than
            // stepping, so direction reversals take human-like time.
            float staminaPower = _tiredPowerFloor + (1f - _tiredPowerFloor) * Stamina.Ratio;
            if (_driveForce <= 0f) CacheDriveForce();
            Vector2 targetDrive =
                intent * (_driveForce * (IsBoosting ? _boostMultiplier : 1f) * staminaPower);
            _driveVec = Vector2.MoveTowards(_driveVec, targetDrive, _forceSlewRate * dt);

            // Traction budget: everything the feet do - launching, cutting, braking -
            // shares one friction circle of mu * m * g. This is what makes hard
            // direction changes cost speed instead of being free.
            float tractionBudget = _tractionMu * Body.mass * Gravity;
            Vector2 applied = Vector2.ClampMagnitude(_driveVec, tractionBudget);
            Body.AddForce(applied);

            // Active braking: when the brain is genuinely idle the feet arrest
            // residual motion using whatever traction is left, rather than
            // relying on fake global drag. Threshold is intentionally TIGHT
            // (intent magnitude < 0.05) so cautious-trained actions
            // (intent ~0.1-0.3) can still build speed - the old 0.2-magnitude
            // threshold insta-stopped any body whose trained brain wasn't
            // pinned at full throttle (net force ~-480 N against a ~45 N drive
            // when STANDARD produced forward=0.11, lateral=-0.16). The brake
            // itself also decelerates over ~0.5s rather than one timestep so
            // residual motion drifts a little (human coast, not instant snap).
            const float BrakeIntentMag = 0.05f;
            const float BrakeStopSeconds = 0.5f;
            Vector2 velocity = Body.linearVelocity;
            float intentMag = intent.magnitude;
            if (intentMag < BrakeIntentMag && velocity.sqrMagnitude > 0.0001f)
            {
                float spare = Mathf.Max(0f, tractionBudget - applied.magnitude);
                float stopping = velocity.magnitude * Body.mass / BrakeStopSeconds;
                Body.AddForce(-velocity.normalized * Mathf.Min(spare, stopping));
            }

            // Anisotropic drag: a body skids far less sideways than it rolls forward.
            float lateralSpeed = Vector2.Dot(velocity, rightAxis);
            Body.AddForce(-rightAxis * (lateralSpeed * _lateralDrag * Body.mass));

            // Turning authority falls off with speed - free pivot at rest, almost
            // none at full sprint.
            float speed01 = Mathf.Clamp01(velocity.magnitude / Mathf.Max(0.01f, _turnScaleSpeed));
            float turnCap = Mathf.Lerp(
                _maxAngularVelocityDeg, _maxAngularVelocityDeg * _sprintTurnFactor, speed01);
            Body.AddTorque(turn * _turnTorque);
            Body.angularVelocity = Mathf.Clamp(Body.angularVelocity, -turnCap, turnCap);

            Stamina.Tick(IsBoosting, dt);

            ApplyDenseRewards(move, lateral, turn, boost);
        }

        void ApplyDenseRewards(float move, float lateral, float turn, float boost)
        {
            if (rewards == null || env == null || env.Ball == null) return;

            // v2: stepPenalty default is 0 (was -0.0001) - terminal reward provides temporal credit.
            AddReward(rewards.stepPenalty);

            Vector2 toBall = env.Ball.position - Body.position;
            float d = toBall.magnitude;

            // v2: differential proximity reward. Pure chasing yields ~0 reward (everyone
            // closes on the ball at similar rates). Approaching *faster than the previous
            // step* yields positive reward. Cures double-team crowding in 2v2 self-play.
            // Legacy absolute mode (useDifferentialProximity=false) kept for A/B testing.
            if (rewards.useDifferentialProximity)
            {
                if (!float.IsPositiveInfinity(_prevBallDist))
                {
                    float delta = _prevBallDist - d;   // positive = closer this step
                    AddReward(rewards.ballProximityScale * delta);
                }
                _prevBallDist = d;
            }
            else
            {
                AddReward(rewards.ballProximityScale * (1f / (1f + d)));
            }

            float align = Vector2.Dot(transform.up, toBall.normalized);
            AddReward(rewards.facingAlignmentScale * align);

            // The "shoot goalward" gradient: reward ball velocity toward the opponent
            // net (signed - shooting at your own net costs the same amount).
            Vector2 ballToOppGoal =
                (env.GetGoalPosition(Opponent(team)) - env.Ball.position).normalized;
            float progress = Vector2.Dot(env.Ball.linearVelocity, ballToOppGoal);
            AddReward(rewards.ballToGoalVelocityScale * Mathf.Clamp(progress * 0.1f, -1f, 1f));

            // Anti-twitch: penalize per-step action change so learned movement is smooth.
            // v2: halved scale (0.001 -> 0.0004) - hard cuts are *correct* for soccer.
            float jitter = (Mathf.Abs(move - _prevActions[0])
                          + Mathf.Abs(lateral - _prevActions[1])
                          + Mathf.Abs(turn - _prevActions[2])
                          + Mathf.Abs(boost - _prevActions[3])) / 4f;
            AddReward(-rewards.actionJitterScale * jitter);
            _prevActions[0] = move; _prevActions[1] = lateral;
            _prevActions[2] = turn; _prevActions[3] = boost;

            // Wall aversion: standing in the boundary band produced wall-hug play.
            Vector2 local = Body.position - (Vector2)env.transform.position;
            float wallDist = Mathf.Min(
                env.PitchHalfExtents.x - Mathf.Abs(local.x),
                env.PitchHalfExtents.y - Mathf.Abs(local.y));
            if (wallDist < 0.8f)
                AddReward(-rewards.wallProximityPenalty * (0.8f - wallDist) / 0.8f);

            // Corner aversion: v2 team-aware - only the team that last touched the
            // ball into a corner zone bleeds reward. The defending team is not
            // punished for the opponent's corner-pin (was the v1 bug).
            if (rewards.cornerBallPenalty > 0f && env.LastToucher != null
                && env.LastToucher.team == team)
            {
                Vector2 ballLocal = env.Ball.position - (Vector2)env.transform.position;
                float cornerDist = Mathf.Max(
                    env.PitchHalfExtents.x - Mathf.Abs(ballLocal.x),
                    env.PitchHalfExtents.y - Mathf.Abs(ballLocal.y));
                if (cornerDist < 2f)
                    AddReward(-rewards.cornerBallPenalty * (1f - cornerDist / 2f));
            }

            // Personality traits (zero-cost when the profile leaves them at 0):
            // KIM-style defense - stand on the line from the ball back to own goal.
            if (rewards.defensivePositionScale > 0f)
            {
                Vector2 ballToOwnGoal = env.GetGoalPosition(team) - env.Ball.position;
                if (ballToOwnGoal.sqrMagnitude > 0.01f && d > 0.01f)
                {
                    float screen = Vector2.Dot(ballToOwnGoal.normalized, (-toBall).normalized);
                    AddReward(rewards.defensivePositionScale * Mathf.Max(0f, screen));
                }
            }

            // NICK-style possession - close control of the ball pays continuously.
            if (rewards.possessionScale > 0f && d < 1.2f)
                AddReward(rewards.possessionScale);
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var continuous = actionsOut.ContinuousActions;

            // Disable the bot component to reclaim keyboard control for play-testing.
            if (_bot != null && _bot.enabled && env != null && env.Ball != null)
            {
                var mate = env.GetTeammate(this);

                // Nearest opposing body, for the bot's shoulder-charge.
                Rigidbody2D foe = null;
                float bestSqr = float.MaxValue;
                for (int agentIndex = 0; agentIndex < env.agents.Count; agentIndex++)
                {
                    var other = env.agents[agentIndex];
                    if (other == null || other == this || other.team == team
                        || other.Body == null) continue;
                    float sqr = (other.Body.position - Body.position).sqrMagnitude;
                    if (sqr < bestSqr) { bestSqr = sqr; foe = other.Body; }
                }

                Vector4 a = _bot.ComputeActions(Body, env.Ball,
                    env.GetGoalTransform(Opponent(team)), mate != null ? mate.Body : null, foe);
                continuous[0] = a.x;   // forward
                continuous[1] = a.y;   // lateral
                continuous[2] = a.z;   // turn
                continuous[3] = a.w;   // boost
                return;
            }

            // Keyboard fallback for human play-testing (W/S drive, Q/E strafe,
            // A/D turn, K boost - Shift also works). Input System package only.
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;
            continuous[0] = (kb.wKey.isPressed ? 1f : 0f) + (kb.sKey.isPressed ? -1f : 0f);
            continuous[1] = (kb.eKey.isPressed ? 1f : 0f) + (kb.qKey.isPressed ? -1f : 0f);
            continuous[2] = (kb.aKey.isPressed ? 1f : 0f) + (kb.dKey.isPressed ? -1f : 0f);
            continuous[3] = kb.kKey.isPressed || kb.leftShiftKey.isPressed ? 1f : 0f;
        }

        void OnCollisionEnter2D(Collision2D collision)
        {
            if (!collision.collider.CompareTag("Ball")) return;

            if (!TouchedBallThisEpisode && rewards != null)
                AddReward(rewards.ballContact);
            TouchedBallThisEpisode = true;

            // Spin transfer: body rotation at contact puts curl on the ball
            // (paired with the Magnus force in Agent_EnvController).
            if (env != null && env.Ball != null)
                env.Ball.angularVelocity += Body.angularVelocity * 0.3f;

            env?.NotifyBallTouch(this);
            TryWallKick();
        }

        void OnCollisionStay2D(Collision2D collision)
        {
            // Corner scrums are continuous contact - Enter alone fires only once.
            if (collision.collider.CompareTag("Ball")) TryWallKick();
        }

        // Corner escape: touching the ball inside the wall band kicks it toward
        // open field - a deliberate "dig it out" mechanic so corners can't hold it.
        void TryWallKick()
        {
            if (env == null || env.Ball == null || Time.time < _nextWallKick) return;

            Vector2 half = env.PitchHalfExtents;
            Vector2 local = env.Ball.position - (Vector2)env.transform.position;
            Vector2 inward = Vector2.zero;
            if (half.x - Mathf.Abs(local.x) < _wallKickBand) inward.x = -Mathf.Sign(local.x);
            // Never kick away from a goal mouth - shots settling on the goal line stay live.
            bool inGoalMouth = Mathf.Abs(local.x) < env.CurrentGoalWidth * 0.5f + 0.3f;
            if (!inGoalMouth && half.y - Mathf.Abs(local.y) < _wallKickBand) inward.y = -Mathf.Sign(local.y);
            if (inward == Vector2.zero) return;

            bool corner = inward.x != 0f && inward.y != 0f;
            // Blend in the kicker's facing so the pop can be aimed a little,
            // but never let it point back into the boundary.
            Vector2 dir = (inward.normalized + (Vector2)transform.up * 0.4f).normalized;
            if (Vector2.Dot(dir, inward) <= 0f) dir = inward.normalized;

            env.Ball.AddForce(dir * (corner ? _wallKickImpulse : _wallKickImpulse * 0.5f),
                ForceMode2D.Impulse);
            _nextWallKick = Time.time + _wallKickCooldown;
        }

        public static Team Opponent(Team t) => t == Team.Blue ? Team.Red : Team.Blue;
    }
}
