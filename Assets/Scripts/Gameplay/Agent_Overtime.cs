using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Sudden death: when a match goes goalless for long enough, the walls start
    /// closing in and the goal mouths start widening, until somebody scores.
    ///
    /// WHY THIS IS THE HIGHEST-VALUE GAMEPLAY CHANGE IN THE SPECTATOR LAYER.
    /// CLAUDE.md's own eval record puts p22 at 26.6% wins, 46.0% losses and
    /// 27.4% STALEMATES over 350 episodes. More than a quarter of episodes end
    /// with nothing happening, and no overlay, camera cut or stat ticker fixes
    /// that - a goalless grind is not a presentation problem. This is the one
    /// feature in the spectator layer that changes what the audience is actually
    /// watching rather than how it is annotated.
    ///
    /// THE RULE CHANGE IS GEOMETRIC, NOT ARITHMETIC. Nothing about scoring,
    /// rewards or episode termination moves. The pitch shrinks uniformly toward
    /// <see cref="_minScale"/> and the mouths widen toward <see cref="_goalGain"/>,
    /// so the same two players are pushed into contact with a bigger target behind
    /// each of them. A smaller pitch with wider goals converges on a goal by
    /// construction; a timer that declared a winner would not.
    ///
    /// WHY IT CANNOT REACH TRAINING OR EVALUATION. It gates on
    /// <see cref="Agent_Presentation.IsMatchScene"/>, which is false whenever a
    /// trainer is connected or POSOCCER_EVAL is set. That gate is load-bearing
    /// rather than tidy: the benchmark's headline number is blueWins/episodes, so
    /// a mechanism that converts stalemates into goals would silently inflate the
    /// very figure the project exists to measure, and would do it in a way no eval
    /// JSON could reveal. Overtime must never be a reason a win rate moved.
    /// Agent_PlayMode_Overtime asserts its absence in SCN_Training.
    ///
    /// WHY IT IS SAFE FOR A TRAINED BRAIN. Pitch size and goal width are not in
    /// the observation vector - the brain contract carries goal POSITION - so no
    /// tensor changes and no policy goes out of distribution. Goal width in
    /// particular has been the goal_width curriculum axis since phase 1, so every
    /// brain here has already trained across a range of mouths.
    ///
    /// TIMED ON THE PHYSICS CLOCK, like every other measurement in this project,
    /// and paused with <see cref="Agent_TimeFreeze"/> - otherwise a long look at
    /// the pause menu would squeeze the pitch while nobody was playing.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_Overtime : MonoBehaviour
    {
        [Header("Trigger")]
        [Tooltip("Seconds of goalless play before the squeeze begins. Measured on the physics " +
                 "clock and reset by every goal, so a match with goals in it never sees this.")]
        [SerializeField] private float _armAfterSeconds = 40f;
        [Tooltip("Seconds from arming to the full squeeze.")]
        [SerializeField] private float _squeezeSeconds = 50f;

        [Header("Geometry")]
        [Tooltip("Pitch half-extents at the full squeeze, as a fraction of the pitch this match " +
                 "started on. UNIFORM on both axes on purpose: Agent_PitchGuard's corner arcs " +
                 "are circles, and a non-uniform scale turns them into ellipses that Box2D " +
                 "approximates by the larger axis - a collider that no longer matches the art.")]
        [Range(0.4f, 1f)] [SerializeField] private float _minScale = 0.66f;
        [Tooltip("Goal mouth multiplier at the full squeeze, applied to the width the match " +
                 "started with.")]
        [Range(1f, 3f)] [SerializeField] private float _goalGain = 1.7f;
        [Tooltip("Scale change small enough to skip. ResizePitch walks every direct child, so " +
                 "this holds the squeeze to a handful of rebuilds per second rather than one " +
                 "per physics tick.")]
        [SerializeField] private float _scaleEpsilon = 0.004f;

        [Header("Presentation")]
        [Tooltip("Sorting order for the closing boundary. Below the intent overlay (15).")]
        [SerializeField] private int _sortingOrder = 14;
        [SerializeField] private bool _enableOvertime = true;

        static readonly Color Gold = new(1f, 0.82f, 0.2f);

        Agent_EnvController _env;
        Agent_HUD _hud;
        Agent_Audio _audio;
        Agent_Lines _lines;

        Vector2 _baseHalfExtents;
        float _baseGoalWidth;
        float _appliedScale = 1f;
        float _goallessSeconds;
        float _pulse;

        /// <summary>
        /// 0 before overtime arms, rising to 1 at the full squeeze. Read by the
        /// tests, and available to any HUD element that wants to draw it.
        /// </summary>
        public float Squeeze01 { get; private set; }

        /// <summary>True once the pitch has begun to close in.</summary>
        public bool IsArmed => Squeeze01 > 0f;

        /// <summary>Seconds since the last episode end, on the physics clock.</summary>
        public float GoallessSeconds => _goallessSeconds;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            _hud = FindFirstObjectByType<Agent_HUD>();

            if (!_enableOvertime || !Agent_Presentation.IsMatchScene(_hud))
            {
                enabled = false;
                return;
            }

            _audio = GetComponent<Agent_Audio>();

            // Captured in Start, not Awake: Agent_MatchLoader runs at order -60 and
            // resizes the pitch to the squad size from its own Awake, so a baseline
            // taken any earlier would be the scene's authored size rather than the one
            // this match is actually played on - and every restore would then quietly
            // resize the pitch to a size nobody chose.
            _baseHalfExtents = _env.PitchHalfExtents;
            _baseGoalWidth = _env.CurrentGoalWidth > 0.01f
                ? _env.CurrentGoalWidth
                : _env.defaultGoalWidth;

            _env.EpisodeEnded += OnEpisodeEnded;
            _lines = new Agent_Lines("OvertimeBoundary", _sortingOrder, vertexCapacity: 64);
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
            // Restore before the component goes away. The pitch is scene state, and a
            // shrunk pitch left behind by a destroyed component is a scene that
            // silently plays smaller the next time something reuses it.
            Restore();
            _lines?.Dispose();
        }

        /// <summary>
        /// Any episode end re-opens the pitch: a goal because the deadlock broke, a
        /// stalemate reset because play restarts from kickoff either way.
        ///
        /// Fires BEFORE ResetPitch, which then re-reads the goal width from the
        /// environment parameter - so the restore below is what that kickoff picks up.
        /// </summary>
        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            bool wasArmed = IsArmed;
            _goallessSeconds = 0f;
            Restore();

            if (wasArmed && _hud != null) _hud.Toast("SUDDEN DEATH OVER", Agent_UIStyle.TextPrimary, 1.4f);
        }

        void FixedUpdate()
        {
            if (_env == null) return;
            // A paused match is not a goalless match.
            if (Agent_TimeFreeze.IsFrozen) return;

            float previous = _goallessSeconds;
            _goallessSeconds += Time.fixedDeltaTime;

            if (previous < _armAfterSeconds && _goallessSeconds >= _armAfterSeconds) Announce();

            float over = _goallessSeconds - _armAfterSeconds;
            Squeeze01 = over <= 0f ? 0f : Mathf.Clamp01(over / Mathf.Max(0.01f, _squeezeSeconds));

            ApplyGeometry();
        }

        void Announce()
        {
            if (_hud != null)
            {
                _hud.Toast("SUDDEN DEATH", Gold, 2.0f);
                _hud.Say("The pitch is closing in.", Gold, 2.6f);
            }
            if (_audio != null) _audio.Whistle(0.55f);
        }

        /// <summary>
        /// Drive the pitch toward the current squeeze.
        ///
        /// ORDER MATTERS. ResizePitch scales every direct child proportionally, and
        /// that includes the goal transforms - so on its own it would NARROW the
        /// mouths on the way in, which is the opposite of the point. SetGoalWidth is
        /// absolute and runs second, so it wins.
        /// </summary>
        void ApplyGeometry()
        {
            float scale = Mathf.Lerp(1f, _minScale, Squeeze01);
            if (Mathf.Abs(scale - _appliedScale) >= _scaleEpsilon)
            {
                _env.ResizePitch(_baseHalfExtents * scale);
                _appliedScale = scale;
            }

            // GUARDED FOR THE SAME REASON THE SCALE ABOVE IS, AND IT WAS NOT.
            //
            // This line ran every FixedUpdate - 100 Hz - for the whole match,
            // including the ~95% of it before overtime arms at all, when the value
            // it writes has not changed since Start. SetGoalWidth writes two goal
            // transforms and raises the static PitchReconfigured event, whose one
            // subscriber (Agent_GoalFrame) then rebuilds its mouth bar. That is 200
            // event deliveries a second to redraw a line in the position it was
            // already in. The scale half of this method has had an epsilon since it
            // was written; the width half simply never got one.
            float width = _baseGoalWidth * Mathf.Lerp(1f, _goalGain, Squeeze01);
            if (Mathf.Abs(width - _appliedGoalWidth) < GOAL_WIDTH_EPSILON) return;
            _appliedGoalWidth = width;
            _env.SetGoalWidth(width);
        }

        /// <summary>
        /// Width change small enough to skip, in metres. Well under a pixel at any
        /// framing this game uses - the squeeze crosses it a few times a second
        /// rather than a hundred.
        /// </summary>
        const float GOAL_WIDTH_EPSILON = 0.01f;

        float _appliedGoalWidth = -1f;

        void Restore()
        {
            Squeeze01 = 0f;
            if (_env == null || _baseHalfExtents.sqrMagnitude < 0.01f) return;

            if (!Mathf.Approximately(_appliedScale, 1f))
            {
                // Absolute target, never an inverse ratio: ResizePitch derives its
                // ratio from the CURRENT extents, so restoring to a remembered
                // absolute size cannot drift however many times it is called.
                _env.ResizePitch(_baseHalfExtents);
                _appliedScale = 1f;
            }
            _env.SetGoalWidth(_baseGoalWidth);
            // Invalidate the width cache rather than setting it to _baseGoalWidth:
            // ResetPitch runs straight after this and re-reads the mouth from the
            // `goal_width` environment parameter, so what the goals actually carry a
            // moment from now is not this method's to predict. A sentinel costs one
            // redundant write on the next tick and cannot go stale.
            _appliedGoalWidth = -1f;
        }

        /// <summary>
        /// The closing boundary, drawn as a pulsing gold frame on the live pitch
        /// edge.
        ///
        /// Its own batch rather than the HUD banner: Agent_MatchFlow owns the banner
        /// for its golden-goal card, and two components writing one element is how a
        /// message ends up depending on which Update happened to run last.
        /// </summary>
        void Update()
        {
            if (_lines == null || _env == null) return;

            _lines.Visible = IsArmed;
            if (!IsArmed) return;

            _pulse += Time.unscaledDeltaTime * 3.4f;
            float intensity = 0.45f + 0.35f * Mathf.Sin(_pulse);
            Color tint = new(Gold.r, Gold.g, Gold.b, Mathf.Clamp01(0.25f + Squeeze01 * intensity));

            Vector2 centre = transform.position;
            Vector2 half = _env.PitchHalfExtents;
            float width = 0.06f + 0.10f * Squeeze01;

            Vector2 bottomLeft = centre + new Vector2(-half.x, -half.y);
            Vector2 bottomRight = centre + new Vector2(half.x, -half.y);
            Vector2 topRight = centre + new Vector2(half.x, half.y);
            Vector2 topLeft = centre + new Vector2(-half.x, half.y);

            _lines.Begin();
            _lines.AddSegment(bottomLeft, bottomRight, width, tint);
            _lines.AddSegment(bottomRight, topRight, width, tint);
            _lines.AddSegment(topRight, topLeft, width, tint);
            _lines.AddSegment(topLeft, bottomLeft, width, tint);
            _lines.Commit();
        }
    }
}
