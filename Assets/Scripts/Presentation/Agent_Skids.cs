using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PoSoccer
{
    /// <summary>
    /// Skid marks and a g reading: what the friction circle looks like from above.
    ///
    /// A body here turns faster than its velocity can follow, so a hard cut leaves it
    /// travelling partly sideways for a moment: Agent_Soccer.LateralSlipSpeed, the speed
    /// across the direction the body faces. While that slip is above a threshold this
    /// lays two dark streaks along the path the body actually took. A hard cut leaves a
    /// curved pair of lines; running straight leaves nothing.
    ///
    /// WHY IT KEYS ON SLIP AND NOT ON Agent_Soccer.TractionSaturation, which is the
    /// number Agent_Wear and Agent_Intent read. Saturation is drive force over the
    /// mu * m * g budget, and on the shipped bodies the drive tops out at
    /// 236 N * 2.2 boost = 519 N against a budget of 883 N: it cannot pass 0.59, so
    /// "at the limit of grip" never happens. Sideways slip does, on every sharp turn.
    ///
    /// THE G READING is measured, not modelled: Agent_Soccer.GroundForce (drive, active
    /// braking and the sideways drag, as applied that tick) over body weight, sampled at
    /// the physics rate. The peak per player feeds one line of the stat ticker.
    ///
    /// One draw call (<see cref="Agent_Lines"/>), rebuilt at a fixed rate and only while a
    /// mark is alive. Toggle with G. Presentation only; self-disables in training and
    /// evaluation. Agent_Quality hides it with the wear quad when the frame budget goes.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_Skids : MonoBehaviour
    {
        [Header("Detection")]
        [Tooltip("Sideways slip (m/s) at which a streak starts.")]
        [SerializeField] private float _slipSpeed = 1.2f;
        [Tooltip("Sideways slip (m/s) at which a streak is drawn at full weight.")]
        [SerializeField] private float _fullSlipSpeed = 3.5f;
        [Tooltip("Physics ticks between path samples. 4 is 25 Hz, about 0.2 m per segment at a sprint.")]
        [SerializeField] private int _sampleTicks = 4;

        [Header("Appearance")]
        [Tooltip("Seconds a streak takes to fade out.")]
        [SerializeField] private float _lifeSeconds = 6f;
        [Tooltip("Half the gap between the two streaks, in world units.")]
        [SerializeField] private float _stanceHalfWidth = 0.14f;
        [Tooltip("Half-width of one streak, in world units.")]
        [SerializeField] private float _streakHalfWidth = 0.035f;
        [Range(0f, 1f)] [SerializeField] private float _opacity = 0.5f;
        [Tooltip("Redraws per second while any mark is alive.")]
        [SerializeField] private float _redrawHz = 15f;
        [SerializeField] private bool _visibleOnStart = true;
        [SerializeField] private bool _enableSkids = true;

        /// <summary>
        /// Overlay visibility, static so the health panel can drive it without a
        /// reference. Re-seeded from the serialized default in Start.
        /// </summary>
        public static bool Visible { get; set; } = true;

        /// <summary>Cleared by Agent_Quality under frame-budget pressure.</summary>
        public bool Allowed { get; set; } = true;

        const int MAX_MARKS = 384;
        const float GRAVITY = 9.81f;

        struct Mark
        {
            public Vector2 From, To;
            public float BornAt;
            public float Weight;
        }

        struct Track
        {
            public Vector2 LastPosition;
            public bool WasSliding;
            public float LastSlip;
            public float PeakG;
        }

        static readonly Color StreakTint = new(0.1f, 0.08f, 0.05f);

        Agent_EnvController _env;
        Agent_Lines _lines;

        readonly Mark[] _marks = new Mark[MAX_MARKS];
        int _head;
        float _newestMarkAt = float.NegativeInfinity;

        readonly Dictionary<Agent_Soccer, Track> _tracks = new();
        int _tick;
        float _nextRedrawAt;

        /// <summary>Marks laid since the match began. A budget number, and what the test reads.</summary>
        public int MarksLaid { get; private set; }

        /// <summary>Hardest this player has pushed this match, in g. 0 before its first sample.</summary>
        public float PeakG(Agent_Soccer agent) =>
            agent != null && _tracks.TryGetValue(agent, out var track) ? track.PeakG : 0f;

        /// <summary>The player with the highest peak this match, or null before any sample.</summary>
        public Agent_Soccer PeakLeader(out float peak)
        {
            Agent_Soccer best = null;
            peak = 0f;
            var roster = _env != null ? _env.agents : null;
            if (roster == null) return null;

            for (int agentIndex = 0; agentIndex < roster.Count; agentIndex++)
            {
                var agent = roster[agentIndex];
                if (agent == null || !_tracks.TryGetValue(agent, out var track)) continue;
                if (best != null && track.PeakG <= peak) continue;
                best = agent;
                peak = track.PeakG;
            }
            return best;
        }

        /// <summary>Ground force over body weight: the g this body is pulling right now.</summary>
        public static float CurrentG(Agent_Soccer agent)
        {
            if (agent == null || agent.Body == null) return 0f;
            float weight = agent.Body.mass * GRAVITY;
            return weight > 0.01f ? agent.GroundForce.magnitude / weight : 0f;
        }

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            var hud = FindAnyObjectByType<Agent_HUD>();

            if (!_enableSkids || !Agent_Presentation.IsMatchScene(hud))
            {
                enabled = false;
                return;
            }

            Visible = _visibleOnStart;
            // Two streaks of four vertices per mark.
            _lines = new Agent_Lines("SkidMarks", 0, vertexCapacity: MAX_MARKS * 8);

            // On the ground: above the turf and its wear, below every body and the ball.
            var pitch = _env.transform.Find("PitchBG");
            if (pitch != null && pitch.TryGetComponent(out SpriteRenderer pitchRenderer))
            {
                _lines.Renderer.sortingLayerID = pitchRenderer.sortingLayerID;
                _lines.Renderer.sortingOrder = pitchRenderer.sortingOrder + 3;
            }
            else
            {
                _lines.Renderer.sortingOrder = -1;
            }
        }

        void OnDestroy() => _lines?.Dispose();

        void FixedUpdate()
        {
            if (_env == null) return;
            // A frozen clock is a paused match: nobody is sliding.
            if (Agent_TimeFreeze.IsFrozen) return;

            bool sampleNow = ++_tick % Mathf.Max(1, _sampleTicks) == 0;

            var roster = _env.agents;
            for (int agentIndex = 0; agentIndex < roster.Count; agentIndex++)
            {
                var agent = roster[agentIndex];
                if (agent == null || agent.Body == null || !agent.isActiveAndEnabled) continue;

                _tracks.TryGetValue(agent, out var track);   // default(Track) when new

                float g = CurrentG(agent);
                if (g > track.PeakG) track.PeakG = g;

                if (sampleNow)
                {
                    Vector2 position = agent.Body.position;
                    float slip = Mathf.Abs(agent.LateralSlipSpeed);
                    bool sliding = slip >= _slipSpeed;

                    // Both ends of a segment must be sliding, so a reset that teleports
                    // the body between two samples cannot draw a line across the pitch.
                    if (sliding && track.WasSliding)
                    {
                        Lay(track.LastPosition, position, Mathf.Max(slip, track.LastSlip));
                    }

                    track.LastPosition = position;
                    track.WasSliding = sliding;
                    track.LastSlip = slip;
                }

                _tracks[agent] = track;
            }
        }

        void Lay(Vector2 from, Vector2 to, float slip)
        {
            // Longer than a sprint can cover between two samples: the body was moved,
            // not driven (kickoff reset, replay scrub).
            float maxStep = 12f * Time.fixedDeltaTime * Mathf.Max(1, _sampleTicks);
            if ((to - from).sqrMagnitude > maxStep * maxStep) return;

            float weight = Mathf.InverseLerp(_slipSpeed, Mathf.Max(_slipSpeed + 0.01f, _fullSlipSpeed), slip);
            _marks[_head] = new Mark
            {
                From = from, To = to, BornAt = Time.time,
                Weight = Mathf.Lerp(0.35f, 1f, weight),
            };
            _head = (_head + 1) % MAX_MARKS;
            _newestMarkAt = Time.time;
            MarksLaid++;
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.gKey.wasPressedThisFrame) Visible = !Visible;

            if (_lines == null) return;

            bool anyAlive = Time.time - _newestMarkAt < _lifeSeconds;
            bool show = Visible && Allowed && anyAlive;
            if (_lines.Visible != show) _lines.Visible = show;
            if (!show) return;

            if (Time.unscaledTime < _nextRedrawAt) return;
            _nextRedrawAt = Time.unscaledTime + 1f / Mathf.Max(1f, _redrawHz);

            float now = Time.time;
            _lines.Begin();
            for (int markIndex = 0; markIndex < MAX_MARKS; markIndex++)
            {
                float age = now - _marks[markIndex].BornAt;
                // A slot never written has Weight 0.
                if (_marks[markIndex].Weight <= 0f || age < 0f || age >= _lifeSeconds) continue;

                Vector2 from = _marks[markIndex].From;
                Vector2 to = _marks[markIndex].To;
                Vector2 delta = to - from;
                float length = delta.magnitude;
                if (length < 0.0001f) continue;

                Vector2 offset = new Vector2(-delta.y, delta.x) / length * _stanceHalfWidth;
                var tint = new Color(StreakTint.r, StreakTint.g, StreakTint.b,
                    _opacity * _marks[markIndex].Weight * (1f - age / _lifeSeconds));

                _lines.AddSegment(from + offset, to + offset, _streakHalfWidth, tint);
                _lines.AddSegment(from - offset, to - offset, _streakHalfWidth, tint);
            }
            _lines.Commit();
        }
    }
}
