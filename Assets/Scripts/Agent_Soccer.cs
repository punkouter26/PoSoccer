using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

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
        [Tooltip("Drive force (N) along the +Y eye axis at action = 1. ~700 N on a 75 kg body with drag 2 gives a ~4.7 m/s jog.")]
        public float moveForce = 700f;
        [Tooltip("Turn torque (N*m) at action = 1.")]
        public float turnTorque = 250f;
        [Tooltip("Force multiplier while boosting with stamina available (PRD: 2.2x -> ~10 m/s sprint).")]
        public float boostMultiplier = 2.2f;
        [Tooltip("Boost action activation threshold (PRD: 0.1).")]
        public float boostThreshold = 0.1f;
        [Tooltip("Human rotation limit (deg/s). Athletes cannot spin faster than ~a full turn per second.")]
        public float maxAngularVelocityDeg = 360f;
        [Tooltip("How fast the applied drive force can change (N/s). Full power takes ~0.3s; reversals are not instant.")]
        public float forceSlewRate = 2300f;
        [Range(0.3f, 1f)]
        [Tooltip("Power fraction remaining at zero stamina (exhausted agents visibly slow).")]
        public float tiredPowerFloor = 0.6f;

        [Header("Wall kick-out (corner escape)")]
        [Tooltip("Impulse (N*s) that pops the ball toward open field when a player touches it inside the wall band. 6 N*s on the 0.43 kg ball is a ~14 m/s pop in corners; straight walls get half.")]
        public float wallKickImpulse = 6f;
        [Tooltip("Distance from a wall inside which the pop triggers.")]
        public float wallKickBand = 1.0f;
        [Tooltip("Seconds between pops from the same player (corner scrums are continuous contact).")]
        public float wallKickCooldown = 0.5f;

        [Header("Wiring (set by env controller at runtime)")]
        public Agent_EnvController env;
        public Reward_Settings rewards;

        public Rigidbody2D Body { get; private set; }
        public Agent_Stamina Stamina { get; private set; }
        public bool IsBoosting { get; private set; }
        public bool TouchedBallThisEpisode { get; private set; }

        Agent_HeuristicBot _bot;
        BehaviorParameters _behavior;
        Transform _label;                              // identity letter, kept upright
        float _drive;                                  // slew-limited applied force (N)
        float _nextWallKick;                           // kick-out cooldown timestamp
        readonly float[] _prevActions = new float[3];  // for the jitter penalty

        /// <summary>
        /// Vector observation count: 14 self/ball/goal floats + 4 teammate floats
        /// (zero-padded in 1v1 so one brain contract covers 1v1 and 2v2).
        /// </summary>
        public const int BaseObservationSize = 18;

        protected override void Awake()
        {
            base.Awake();
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
                _behavior.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(3);
                ApplyEvalMode();
            }
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

            // Thick team-colored outline: a slightly larger frame sprite behind the body.
            if (body != null && body.sprite != null && transform.Find("TeamOutline") == null)
            {
                var outlineGo = new GameObject("TeamOutline");
                outlineGo.transform.SetParent(transform, false);
                outlineGo.transform.localScale = new Vector3(1.3f, 1.3f, 1f);
                var outline = outlineGo.AddComponent<SpriteRenderer>();
                outline.sprite = body.sprite;
                outline.sharedMaterial = body.sharedMaterial;
                outline.color = teamColor;
                outline.sortingOrder = body.sortingOrder - 1;
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
            _drive = 0f;
            _nextWallKick = 0f;
            System.Array.Clear(_prevActions, 0, _prevActions.Length);
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
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            float move = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
            float turn = Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f);
            float boost = Mathf.Clamp01(actions.ContinuousActions[2]);

            IsBoosting = boost > boostThreshold && Stamina.HasStamina;

            // Exhaustion scales available power; drive force slews rather than
            // stepping, so direction reversals take human-like time.
            float staminaPower = tiredPowerFloor + (1f - tiredPowerFloor) * Stamina.Ratio;
            float targetDrive = move * moveForce * (IsBoosting ? boostMultiplier : 1f) * staminaPower;
            _drive = Mathf.MoveTowards(_drive, targetDrive, forceSlewRate * Time.fixedDeltaTime);

            Body.AddForce((Vector2)transform.up * _drive);
            Body.AddTorque(turn * turnTorque);
            Body.angularVelocity = Mathf.Clamp(
                Body.angularVelocity, -maxAngularVelocityDeg, maxAngularVelocityDeg);
            Stamina.Tick(IsBoosting, Time.fixedDeltaTime);

            ApplyDenseRewards(move, turn, boost);
        }

        void ApplyDenseRewards(float move, float turn, float boost)
        {
            if (rewards == null || env == null || env.Ball == null) return;

            AddReward(rewards.stepPenalty);

            Vector2 toBall = env.Ball.position - Body.position;
            float d = toBall.magnitude;
            AddReward(rewards.ballProximityScale * (1f / (1f + d)));

            float align = Vector2.Dot(transform.up, toBall.normalized);
            AddReward(rewards.facingAlignmentScale * align);

            // The "shoot goalward" gradient: reward ball velocity toward the opponent
            // net (signed - shooting at your own net costs the same amount).
            Vector2 ballToOppGoal =
                (env.GetGoalPosition(Opponent(team)) - env.Ball.position).normalized;
            float progress = Vector2.Dot(env.Ball.linearVelocity, ballToOppGoal);
            AddReward(rewards.ballToGoalVelocityScale * Mathf.Clamp(progress * 0.1f, -1f, 1f));

            // Anti-twitch: penalize per-step action change so learned movement is smooth.
            float jitter = (Mathf.Abs(move - _prevActions[0])
                          + Mathf.Abs(turn - _prevActions[1])
                          + Mathf.Abs(boost - _prevActions[2])) / 3f;
            AddReward(-rewards.actionJitterScale * jitter);
            _prevActions[0] = move; _prevActions[1] = turn; _prevActions[2] = boost;

            // Wall aversion: standing in the boundary band produced wall-hug play.
            Vector2 local = Body.position - (Vector2)env.transform.position;
            float wallDist = Mathf.Min(
                env.PitchHalfExtents.x - Mathf.Abs(local.x),
                env.PitchHalfExtents.y - Mathf.Abs(local.y));
            if (wallDist < 0.8f)
                AddReward(-rewards.wallProximityPenalty * (0.8f - wallDist) / 0.8f);

            // Corner aversion: a ball parked in a corner zone bleeds reward for
            // everyone on the pitch, so both teams learn extraction over pinning.
            if (rewards.cornerBallPenalty > 0f)
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
                Vector3 a = _bot.ComputeActions(Body, env.Ball,
                    env.GetGoalTransform(Opponent(team)), mate != null ? mate.Body : null);
                continuous[0] = a.x;
                continuous[1] = a.y;
                continuous[2] = a.z;
                return;
            }

            // Keyboard fallback for human play-testing (W/S move, A/D turn,
            // K boost - Shift also works). Input System package only.
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;
            continuous[0] = (kb.wKey.isPressed ? 1f : 0f) + (kb.sKey.isPressed ? -1f : 0f);
            continuous[1] = (kb.aKey.isPressed ? 1f : 0f) + (kb.dKey.isPressed ? -1f : 0f);
            continuous[2] = kb.kKey.isPressed || kb.leftShiftKey.isPressed ? 1f : 0f;
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
            if (half.x - Mathf.Abs(local.x) < wallKickBand) inward.x = -Mathf.Sign(local.x);
            // Never kick away from a goal mouth - shots settling on the goal line stay live.
            bool inGoalMouth = Mathf.Abs(local.x) < env.CurrentGoalWidth * 0.5f + 0.3f;
            if (!inGoalMouth && half.y - Mathf.Abs(local.y) < wallKickBand) inward.y = -Mathf.Sign(local.y);
            if (inward == Vector2.zero) return;

            bool corner = inward.x != 0f && inward.y != 0f;
            // Blend in the kicker's facing so the pop can be aimed a little,
            // but never let it point back into the boundary.
            Vector2 dir = (inward.normalized + (Vector2)transform.up * 0.4f).normalized;
            if (Vector2.Dot(dir, inward) <= 0f) dir = inward.normalized;

            env.Ball.AddForce(dir * (corner ? wallKickImpulse : wallKickImpulse * 0.5f),
                ForceMode2D.Impulse);
            _nextWallKick = Time.time + wallKickCooldown;
        }

        public static Team Opponent(Team t) => t == Team.Blue ? Team.Red : Team.Blue;
    }
}
