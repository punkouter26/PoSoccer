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

        [Header("Wiring (set by env controller at runtime)")]
        public Agent_EnvController env;
        public Reward_Settings rewards;

        public Rigidbody2D Body { get; private set; }
        public Agent_Stamina Stamina { get; private set; }
        public bool IsBoosting { get; private set; }
        public bool TouchedBallThisEpisode { get; private set; }

        Agent_HeuristicBot _bot;
        BehaviorParameters _behavior;
        float _drive;                                  // slew-limited applied force (N)
        readonly float[] _prevActions = new float[3];  // for the jitter penalty

        /// <summary>Vector observation count (teammate slots return with 2v2 in v2).</summary>
        public const int BaseObservationSize = 14;

        void Awake()
        {
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

        public override void OnEpisodeBegin()
        {
            TouchedBallThisEpisode = false;
            IsBoosting = false;
            _drive = 0f;
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
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var continuous = actionsOut.ContinuousActions;

            // Disable the bot component to reclaim keyboard control for play-testing.
            if (_bot != null && _bot.enabled && env != null && env.Ball != null)
            {
                Vector3 a = _bot.ComputeActions(Body, env.Ball, env.GetGoalTransform(Opponent(team)));
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
        }

        public static Team Opponent(Team t) => t == Team.Blue ? Team.Red : Team.Blue;
    }
}
