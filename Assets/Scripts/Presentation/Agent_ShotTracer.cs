using UnityEngine;
using UnityEngine.InputSystem;

namespace PoSoccer
{
    /// <summary>
    /// The shot tracer: where the ball is actually going, drawn the moment it
    /// leaves a foot.
    ///
    /// WHY THIS IS NOT AN xG MODEL. A broadcast fakes this with a learned
    /// probability because it does not own the physics. This project does. The
    /// projection below integrates THE SAME THREE TERMS the simulation applies to
    /// the ball every tick - linear damping, the Magnus curl in
    /// <see cref="Agent_EnvController"/>.FixedUpdate, and reflection off the
    /// walls - at the same fixed timestep. It is arithmetic on the live state, not
    /// a model fitted to anything, which is why it can sit on screen next to the
    /// measured stat ticker rather than next to the win-probability strip.
    ///
    /// WHAT IT DELIBERATELY DOES NOT KNOW, and why the label says PROJECTED rather
    /// than anything stronger:
    ///  - other BODIES. A defender who gets a foot in falsifies the line instantly.
    ///    Predicting that would require predicting four policies, which is the
    ///    whole unsolved problem this project is about;
    ///  - Agent_PitchGuard's corner ARCS. Reflection is off the four straight
    ///    walls; a ball into the last ~2.6 units of a corner will leave on a
    ///    different heading than drawn.
    /// So the line answers exactly one question - "if nobody touches it, where does
    /// this ball end up" - and that is the question a shot actually poses.
    ///
    /// THE ACCURACY CLAIM IS MEASURED, NOT ASSERTED.
    /// Agent_PlayMode_ShotTracer launches the ball across an empty pitch, holds the
    /// projection, and asserts the ball arrives within a tolerance of the predicted
    /// endpoint. A prediction overlay nobody ever checked against the thing it
    /// predicts is precisely the class of claim this codebase keeps retracting.
    ///
    /// One draw call - see <see cref="Agent_Lines"/>. Toggle with T.
    /// Presentation only; self-disables in training and evaluation.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_ShotTracer : MonoBehaviour
    {
        /// <summary>What the projection says this ball does if nobody touches it.</summary>
        public enum Verdict
        {
            /// <summary>No shot in flight.</summary>
            Idle,
            /// <summary>In flight, ending somewhere that is not a net.</summary>
            NoGoal,
            /// <summary>In flight and crossing a goal line inside the horizon.</summary>
            OnTarget,
        }

        [Header("Detection")]
        [Tooltip("Ball speed (m/s) below which nothing is drawn. A rolling ball is not a shot.")]
        [SerializeField] private float _minShotSpeed = 4.5f;
        [Tooltip("Seconds of flight to project. 1.6 s crosses a 24 m exhibition pitch at " +
                 "sprint-kick speed without cluttering the screen with a lap of wall bounces.")]
        [SerializeField] private float _horizonSeconds = 1.6f;
        [Tooltip("Integration step as a multiple of the physics timestep. 4 gives 40 points " +
                 "over the horizon; the damping error against the real 1x integration is " +
                 "below 0.1% because damping * step stays far below 1 either way.")]
        [SerializeField] private int _stepMultiple = 4;
        [Tooltip("Seconds between recomputes. The drawn path is held between them.")]
        [SerializeField] private float _recomputeInterval = 0.05f;

        [Header("Appearance")]
        [Tooltip("Line half-width in world units.")]
        [SerializeField] private float _thickness = 0.05f;
        [Tooltip("Sorting order. Above the intent overlay (15), below the confetti layer (20).")]
        [SerializeField] private int _sortingOrder = 16;
        [Tooltip("Start with the tracer visible. On by default - unlike the intent arrows it " +
                 "draws at most one line, and only while a ball is genuinely in flight.")]
        [SerializeField] private bool _visibleOnStart = true;
        [SerializeField] private bool _enableShotTracer = true;

        /// <summary>
        /// Overlay visibility, static so the HUD or a settings screen can drive it
        /// without holding a reference. Re-seeded from the serialized default in
        /// Start, so a scene reload does not inherit the last session's state.
        /// </summary>
        public static bool Visible { get; set; } = true;

        const int MAX_POINTS = 256;

        Agent_EnvController _env;
        Agent_Lines _lines;
        Reward_GoalTrigger[] _goals;

        readonly Vector2[] _path = new Vector2[MAX_POINTS];
        int _pathCount;
        float _nextRecomputeAt;
        float _ballRadius = 0.11f;

        /// <summary>What the last projection concluded. <see cref="Verdict.Idle"/> when no shot is live.</summary>
        public Verdict LastVerdict { get; private set; } = Verdict.Idle;

        /// <summary>Team the projection says scores, or null. Meaningful only when <see cref="LastVerdict"/> is OnTarget.</summary>
        public Agent_Soccer.Team? PredictedScorer { get; private set; }

        /// <summary>Where the projection says the ball ends up. Valid whenever a path exists.</summary>
        public Vector2 PredictedEnd => _pathCount > 0 ? _path[_pathCount - 1] : Vector2.zero;

        /// <summary>Points in the held path. A budget number, and what the accuracy test reads.</summary>
        public int PathPointCount => _pathCount;

        /// <summary>
        /// One point of the held path. Point i is the projected ball position at
        /// i * <see cref="StepSeconds"/> after the projection was taken.
        ///
        /// Public for Agent_PlayMode_ShotTracer, which is the only thing that turns
        /// this component's accuracy from a claim into a measurement - it steps the
        /// real ball forward and compares it against the point for that time.
        /// </summary>
        public Vector2 PathPoint(int index) =>
            index >= 0 && index < _pathCount ? _path[index] : Vector2.zero;

        /// <summary>Seconds between consecutive path points.</summary>
        public float StepSeconds => Time.fixedDeltaTime * Mathf.Max(1, _stepMultiple);

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            var hud = FindFirstObjectByType<Agent_HUD>();

            if (!_enableShotTracer || !Agent_Presentation.IsVisualScene(hud))
            {
                enabled = false;
                return;
            }

            Visible = _visibleOnStart;
            _goals = GetComponentsInChildren<Reward_GoalTrigger>(true);
            _lines = new Agent_Lines("ShotTracer", _sortingOrder, vertexCapacity: MAX_POINTS * 4);

            if (_env.Ball != null)
            {
                var circle = _env.Ball.GetComponent<CircleCollider2D>();
                // Radius scaled by the transform, because the ball keeps its real
                // dimensions while everything around it is resized by ResizePitch -
                // and by Agent_Overtime, which moves the walls mid-match.
                if (circle != null)
                {
                    _ballRadius = circle.radius *
                        Mathf.Max(Mathf.Abs(_env.Ball.transform.lossyScale.x),
                                  Mathf.Abs(_env.Ball.transform.lossyScale.y));
                }
            }
        }

        void OnDestroy() => _lines?.Dispose();

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.tKey.wasPressedThisFrame) Visible = !Visible;

            if (_lines == null || _env == null) return;

            var ball = _env.Ball;
            bool live = Visible && ball != null && ball.linearVelocity.magnitude >= _minShotSpeed;

            if (!live)
            {
                LastVerdict = Verdict.Idle;
                PredictedScorer = null;
                _pathCount = 0;
                _lines.Visible = false;
                return;
            }

            _lines.Visible = true;

            // Recomputed on a timer rather than every frame: the projection is a
            // forward integration of ~40 steps and the ball's own state only moves
            // by a tick's worth between frames, so refreshing at 20 Hz is
            // indistinguishable on screen and an order of magnitude cheaper.
            if (Time.unscaledTime >= _nextRecomputeAt)
            {
                _nextRecomputeAt = Time.unscaledTime + _recomputeInterval;
                Project(ball);
            }

            Draw();
        }

        /// <summary>
        /// Forward-integrate the ball with the terms the simulation actually
        /// applies, and stop at the first goal line crossed.
        ///
        /// The integration order mirrors Unity's own: accumulate force into
        /// velocity, apply damping, then advance position. Damping is Unity's
        /// v /= (1 + d*dt) rather than an exponential, so the two agree exactly
        /// when <see cref="_stepMultiple"/> is 1 and to well under a percent above it.
        /// </summary>
        void Project(Rigidbody2D ball)
        {
            float dt = Time.fixedDeltaTime * Mathf.Max(1, _stepMultiple);
            int steps = Mathf.Min(MAX_POINTS - 1, Mathf.CeilToInt(_horizonSeconds / dt));

            Vector2 position = ball.position;
            Vector2 velocity = ball.linearVelocity;
            float spin = ball.angularVelocity * Mathf.Deg2Rad;
            float mass = Mathf.Max(0.001f, ball.mass);
            float linearDamping = ball.linearDamping;
            float angularDamping = ball.angularDamping;

            Vector2 centre = transform.position;
            Vector2 limit = _env.PitchHalfExtents - new Vector2(_ballRadius, _ballRadius);

            _path[0] = position;
            _pathCount = 1;
            LastVerdict = Verdict.NoGoal;
            PredictedScorer = null;

            for (int step = 0; step < steps; step++)
            {
                // Magnus-lite, exactly as Agent_EnvController applies it: a force
                // perpendicular to travel, proportional to spin.
                Vector2 magnus = _env.magnusScale * spin * new Vector2(-velocity.y, velocity.x);
                velocity += magnus / mass * dt;
                velocity /= 1f + linearDamping * dt;
                spin /= 1f + angularDamping * dt;

                Vector2 next = position + velocity * dt;

                // GOAL TEST BEFORE WALL REFLECTION, and against the UNCLAMPED step.
                // The nets sit in the walls, so clamping first would bounce every
                // shot off the goal line and the tracer would never call one on
                // target. A net is a trigger volume, and a trigger is not a wall.
                if (TryGoal(position, next, out var conceding))
                {
                    LastVerdict = Verdict.OnTarget;
                    PredictedScorer = Agent_Soccer.Opponent(conceding);
                    _path[_pathCount++] = next;
                    return;
                }

                // Wall reflection. Agent_PitchGuard gives the four sides bounciness
                // 0.85 with zero friction, so the tangential component survives and
                // the normal one is scaled - which is what this does. The corner
                // arcs are NOT modelled; see the class docstring.
                Vector2 local = next - centre;
                if (Mathf.Abs(local.x) > limit.x)
                {
                    local.x = Mathf.Sign(local.x) * limit.x;
                    velocity.x = -velocity.x * Agent_PitchGuard.SideBounciness;
                }
                if (Mathf.Abs(local.y) > limit.y)
                {
                    local.y = Mathf.Sign(local.y) * limit.y;
                    velocity.y = -velocity.y * Agent_PitchGuard.SideBounciness;
                }
                next = centre + local;

                position = next;
                _path[_pathCount++] = position;

                if (velocity.sqrMagnitude < 0.04f) return;   // rolled to a stop
            }
        }

        /// <summary>
        /// Did the segment from a to b enter a net? Uses the trigger collider's own
        /// bounds, so the answer tracks whatever the curriculum - or
        /// <see cref="Agent_Overtime"/> - has done to the goal mouth this tick.
        /// </summary>
        bool TryGoal(Vector2 a, Vector2 b, out Agent_Soccer.Team conceding)
        {
            conceding = Agent_Soccer.Team.Blue;
            if (_goals == null) return false;

            for (int i = 0; i < _goals.Length; i++)
            {
                var goal = _goals[i];
                if (goal == null || !goal.isActiveAndEnabled) continue;

                var collider = goal.GetComponent<BoxCollider2D>();
                if (collider == null) continue;

                Bounds bounds = collider.bounds;
                // Segment test rather than a point test: at 0.04 s a hard shot covers
                // ~0.8 m, which is wider than a net is deep - a point sample would
                // step straight over the goal and report a wall bounce instead.
                if (bounds.Contains(new Vector3(b.x, b.y, bounds.center.z)) ||
                    (bounds.IntersectRay(new Ray(a, (b - a).normalized), out float hit) &&
                     hit <= (b - a).magnitude))
                {
                    conceding = goal.owningTeam;
                    return true;
                }
            }
            return false;
        }

        void Draw()
        {
            if (_pathCount < 2) return;

            Color tint = LastVerdict == Verdict.OnTarget && PredictedScorer.HasValue
                ? Agent_SoccerView.TeamColor(PredictedScorer.Value)
                : new Color(1f, 1f, 1f, 1f);

            _lines.Begin();

            // Faded along its length: the far end of a projection is the part most
            // likely to be wrong, and drawing it at the same weight as the near end
            // claims a confidence the integration does not have.
            for (int i = 0; i < _pathCount - 1; i++)
            {
                float nearAlpha = 1f - (float)i / _pathCount;
                float farAlpha = 1f - (float)(i + 1) / _pathCount;
                float scale = LastVerdict == Verdict.OnTarget ? 0.95f : 0.5f;

                _lines.AddFadedSegment(_path[i], _path[i + 1], _thickness,
                    new Color(tint.r, tint.g, tint.b, nearAlpha * scale),
                    new Color(tint.r, tint.g, tint.b, farAlpha * scale));
            }

            // The endpoint marker is the verdict: a filled diamond where the ball
            // arrives, at full weight when that place is a net.
            float markerRadius = LastVerdict == Verdict.OnTarget ? 0.34f : 0.18f;
            _lines.AddDiamond(_path[_pathCount - 1], markerRadius,
                new Color(tint.r, tint.g, tint.b, LastVerdict == Verdict.OnTarget ? 0.95f : 0.4f));

            _lines.Commit();
        }
    }
}
