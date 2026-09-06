using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Live win probability and attacking threat - the two continuous numbers a
    /// broadcast uses to keep the stretches between goals legible.
    ///
    /// WHAT THIS IS, STATED PLAINLY: a hand-tuned logistic over four features the
    /// simulation already computes. It is NOT a learned model and it is NOT
    /// calibrated against this project's eval record. A number on screen that
    /// looks measured but is not is exactly the failure mode this codebase keeps
    /// writing retractions about, so:
    ///
    ///  - the HUD labels the bar "WIN CHANCE · MODEL", never a bare percentage;
    ///  - every weight is a serialized field with its reasoning attached;
    ///  - <see cref="Explain"/> returns the term-by-term breakdown, so the number
    ///    can always be taken apart rather than argued about.
    ///
    /// To calibrate it properly later: log (features -> episode outcome) rows and
    /// fit the four weights by logistic regression. Until someone does that, treat
    /// the bar as a tension meter, which is what it is actually good for.
    ///
    /// THREAT is the other output and the one the director consumes. It answers
    /// "how close is somebody to scoring right now", independent of the score, so
    /// a 0-4 drubbing still cuts to an iso shot when the losing side finally gets
    /// a chance.
    ///
    /// Presentation only. Self-disables in training and evaluation - it reads the
    /// HUD's score, and there is no score in a training scene.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_WinProbability : MonoBehaviour
    {
        [Header("Logistic weights (uncalibrated - see the class docstring)")]
        [Tooltip("Weight on the goal lead, per goal. Dominant term: in a first-to-5 " +
                 "match a two-goal lead really is most of the story.")]
        [SerializeField] private float _leadWeight = 0.85f;
        [Tooltip("Weight on field position (-1 deep in blue's half .. +1 deep in red's). " +
                 "Territory is worth much less than a goal and this encodes that.")]
        [SerializeField] private float _fieldWeight = 0.45f;
        [Tooltip("Weight on the press: which side's nearest player is closer to the ball.")]
        [SerializeField] private float _pressWeight = 0.35f;
        [Tooltip("Weight on smoothed ball velocity toward red's goal - the momentum term.")]
        [SerializeField] private float _momentumWeight = 0.30f;
        [Tooltip("Extra multiplier on the lead term as either side approaches the target " +
                 "score. A one-goal lead at 4-3 is worth far more than at 1-0.")]
        [SerializeField] private float _endgameLeadGain = 1.6f;

        [Header("Smoothing")]
        [Tooltip("Seconds for the displayed probability to travel most of the way to a new " +
                 "value. Too fast reads as noise; too slow misses the moment it exists for.")]
        [SerializeField] private float _probabilitySmoothing = 1.1f;
        [Tooltip("Seconds of smoothing on the momentum feature.")]
        [SerializeField] private float _momentumSmoothing = 0.8f;

        [Header("Threat")]
        [Tooltip("Distance (m) from the attacking goal inside which threat is at its maximum.")]
        [SerializeField] private float _threatRadius = 7f;
        [Tooltip("Ball speed toward a goal (m/s) that counts as a full-blooded attack.")]
        [SerializeField] private float _threatSpeed = 9f;

        [SerializeField] private bool _enableWinProbability = true;

        Agent_EnvController _env;
        Agent_HUD _hud;

        float _momentum;                 // smoothed, +ve = ball travelling toward red's goal
        float _probability = 0.5f;       // smoothed output

        // Explain() breakdown, kept as fields so reading it allocates nothing.
        float _lead, _field, _press;

        /// <summary>Smoothed probability that BLUE wins the match, 0..1.</summary>
        public float BlueWinProbability => _probability;

        /// <summary>
        /// How close anybody is to scoring right now, 0..1. Score-independent by
        /// design: the losing side's one chance is still the shot worth cutting to.
        /// </summary>
        public float Threat { get; private set; }

        /// <summary>Team currently doing the threatening - the side attacking, not defending.</summary>
        public Agent_Soccer.Team ThreatTeam { get; private set; } = Agent_Soccer.Team.Blue;

        /// <summary>
        /// Term-by-term breakdown of the current estimate, for the telemetry
        /// overlay and for anyone who wants to argue with the bar. Allocates one
        /// string per call - never call it from Update.
        /// </summary>
        public string Explain()
        {
            return $"p(blue)={_probability:0.00}  lead={_lead:+0.00;-0.00}  " +
                   $"field={_field:+0.00;-0.00}  press={_press:+0.00;-0.00}  " +
                   $"momentum={_momentum:+0.00;-0.00}";
        }

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            _hud = FindFirstObjectByType<Agent_HUD>();

            if (!_enableWinProbability || !Agent_Presentation.IsMatchScene(_hud))
            {
                enabled = false;
                return;
            }
        }

        void Update()
        {
            if (_env == null || _env.Ball == null) return;

            float dt = Time.unscaledDeltaTime;
            Vector2 ball = _env.Ball.position;
            Vector2 blueGoal = _env.GetGoalPosition(Agent_Soccer.Team.Blue);
            Vector2 redGoal = _env.GetGoalPosition(Agent_Soccer.Team.Red);

            UpdateFeatures(ball, blueGoal, redGoal, dt);
            UpdateThreat(ball, blueGoal, redGoal);

            float z = _lead + _fieldWeight * _field + _pressWeight * _press
                      + _momentumWeight * _momentum;
            float target = 1f / (1f + Mathf.Exp(-z));

            // Exponential smoothing, frame-rate independent. Unscaled time so a
            // hit-stop dip or a replay freeze does not stall the bar.
            float k = _probabilitySmoothing > 0.01f
                ? 1f - Mathf.Exp(-dt / _probabilitySmoothing)
                : 1f;
            _probability = Mathf.Lerp(_probability, target, k);

            // SetWinProbability is a no-op unless the rounded percentage actually
            // changed, so calling it every frame costs one comparison.
            if (_hud != null) _hud.SetWinProbability(_probability);
        }

        void UpdateFeatures(Vector2 ball, Vector2 blueGoal, Vector2 redGoal, float dt)
        {
            // -- Lead. Weighted up as either side closes on the target score.
            int blue = _hud != null ? _hud.BlueScore : 0;
            int red = _hud != null ? _hud.RedScore : 0;
            int target = _hud != null ? Mathf.Max(1, _hud.matchGoals) : 5;
            int best = Mathf.Max(blue, red);
            float endgame = Mathf.Lerp(1f, _endgameLeadGain, (float)best / target);
            _lead = _leadWeight * endgame * (blue - red);

            // -- Field position. +1 = ball on red's goal line, -1 = on blue's.
            // Measured along the actual goal-to-goal axis rather than assuming Y,
            // so it survives Agent_PitchSizing reshaping the pitch.
            Vector2 axis = redGoal - blueGoal;
            float axisLength = axis.magnitude;
            if (axisLength > 0.01f)
            {
                Vector2 unit = axis / axisLength;
                float along = Vector2.Dot(ball - blueGoal, unit);   // 0 .. axisLength
                _field = Mathf.Clamp(along / axisLength * 2f - 1f, -1f, 1f);
            }

            // -- Press. +1 = blue's nearest body is on the ball and red's is far.
            float blueNearest = NearestDistance(Agent_Soccer.Team.Blue, ball);
            float redNearest = NearestDistance(Agent_Soccer.Team.Red, ball);
            float sum = blueNearest + redNearest;
            _press = sum > 0.01f ? Mathf.Clamp((redNearest - blueNearest) / sum, -1f, 1f) : 0f;

            // -- Momentum. Ball velocity projected onto the attacking axis and
            // smoothed, so a single deflection does not swing the bar.
            if (axisLength > 0.01f)
            {
                float toward = Vector2.Dot(_env.Ball.linearVelocity, axis / axisLength);
                float instant = Mathf.Clamp(toward / _threatSpeed, -1f, 1f);
                float k = _momentumSmoothing > 0.01f
                    ? 1f - Mathf.Exp(-dt / _momentumSmoothing)
                    : 1f;
                _momentum = Mathf.Lerp(_momentum, instant, k);
            }
        }

        void UpdateThreat(Vector2 ball, Vector2 blueGoal, Vector2 redGoal)
        {
            // Threat is evaluated against BOTH goals and the larger wins, so an own-goal
            // scramble in front of your own net is as newsworthy as an attack.
            float blueAttack = GoalThreat(ball, redGoal);    // blue attacking red's goal
            float redAttack = GoalThreat(ball, blueGoal);

            if (blueAttack >= redAttack)
            {
                Threat = blueAttack;
                ThreatTeam = Agent_Soccer.Team.Blue;
            }
            else
            {
                Threat = redAttack;
                ThreatTeam = Agent_Soccer.Team.Red;
            }
        }

        /// <summary>
        /// Proximity to a goal, lifted by ball speed toward it. Two factors rather
        /// than a sum: a ball parked on the goal line with nobody moving it is not
        /// a threat, and a rocket from the halfway line is not one yet either.
        /// </summary>
        float GoalThreat(Vector2 ball, Vector2 goal)
        {
            Vector2 toGoal = goal - ball;
            float distance = toGoal.magnitude;
            float proximity = Mathf.Clamp01(1f - distance / Mathf.Max(0.01f, _threatRadius));
            if (proximity <= 0f) return 0f;

            float closing = distance > 0.01f
                ? Vector2.Dot(_env.Ball.linearVelocity, toGoal / distance)
                : 0f;
            float pace = Mathf.Clamp01(closing / Mathf.Max(0.01f, _threatSpeed));

            // 0.35 floor on the pace factor: a ball sitting in the six-yard box
            // with players around it is still a moment, even at rest.
            return proximity * Mathf.Lerp(0.35f, 1f, pace);
        }

        float NearestDistance(Agent_Soccer.Team team, Vector2 ball)
        {
            float best = float.PositiveInfinity;
            var agents = _env.agents;
            for (int i = 0; i < agents.Count; i++)
            {
                var agent = agents[i];
                if (agent == null || agent.Body == null || agent.team != team) continue;
                float d = Vector2.Distance(agent.Body.position, ball);
                if (d < best) best = d;
            }
            // No players on that side (a handicap lineup can do this): treat as
            // maximally far rather than returning infinity into the ratio below.
            return float.IsPositiveInfinity(best) ? _env.PitchHalfExtents.magnitude * 2f : best;
        }
    }
}
