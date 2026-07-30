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

        [Header("Actuation")]
        [Tooltip("Continuous force along the +Y eye axis at action = 1.")]
        public float moveForce = 9f;
        [Tooltip("Torque applied at turn action = 1.")]
        public float turnTorque = 7f;
        [Tooltip("Force multiplier while boosting with stamina available (PRD: 2.2x).")]
        public float boostMultiplier = 2.2f;
        [Tooltip("Boost action activation threshold (PRD: 0.1).")]
        public float boostThreshold = 0.1f;

        [Header("Observation")]
        [Tooltip("Fixed teammate observation slots (0 for 1v1, 1 for 2v2, 2 for 3v3). Obs size = 14 + 4 * slots.")]
        [Range(0, 2)] public int teammateSlots = 0;

        [Header("Wiring (set by env controller at runtime)")]
        public Agent_EnvController env;
        public Reward_Settings rewards;

        public Rigidbody2D Body { get; private set; }
        public Agent_Stamina Stamina { get; private set; }
        public bool IsBoosting { get; private set; }
        public bool TouchedBallThisEpisode { get; private set; }

        Agent_HeuristicBot _bot;
        BehaviorParameters _behavior;

        /// <summary>Base observation count without teammate slots.</summary>
        public const int BaseObservationSize = 14;

        void Awake()
        {
            // Configure the policy contract in code (runs before Agent.OnEnable
            // initializes the policy) so scene serialization can never drift from it.
            _behavior = GetComponent<BehaviorParameters>();
            if (_behavior != null)
            {
                _behavior.BehaviorName = "SoccerAgent";
                _behavior.TeamId = (int)team;
                _behavior.BrainParameters.VectorObservationSize =
                    BaseObservationSize + 4 * teammateSlots;
                _behavior.BrainParameters.NumStackedVectorObservations = 1;
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

            // Teammates (4 per slot, zero-padded so obs size stays fixed across rosters)
            for (int i = 0; i < teammateSlots; i++)
            {
                Agent_Soccer mate = env != null ? env.GetTeammate(this, i) : null;
                if (mate != null)
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
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            float move = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
            float turn = Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f);
            float boost = Mathf.Clamp01(actions.ContinuousActions[2]);

            IsBoosting = boost > boostThreshold && Stamina.HasStamina;
            float force = moveForce * (IsBoosting ? boostMultiplier : 1f);

            Body.AddForce((Vector2)transform.up * (move * force));
            Body.AddTorque(turn * turnTorque);
            Stamina.Tick(IsBoosting, Time.fixedDeltaTime);

            ApplyDenseRewards();
        }

        void ApplyDenseRewards()
        {
            if (rewards == null || env == null || env.Ball == null) return;

            AddReward(rewards.stepPenalty);

            Vector2 toBall = env.Ball.position - Body.position;
            float d = toBall.magnitude;
            AddReward(rewards.ballProximityScale * (1f / (1f + d)));

            float align = Vector2.Dot(transform.up, toBall.normalized);
            AddReward(rewards.facingAlignmentScale * align);
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

            env?.NotifyBallTouch(this);
        }

        public static Team Opponent(Team t) => t == Team.Blue ? Team.Red : Team.Blue;
    }
}
