using System.Collections.Generic;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PoSoccer
{
    /// <summary>
    /// "See what the AI sees": the four ray sensors from <see cref="Sensor_Vision"/>
    /// drawn as a telestrator fan over the pitch.
    ///
    /// THIS READS THE REAL SENSOR OUTPUT, NOT A RE-CAST. Every mark comes from
    /// RayPerceptionSensor.RayPerceptionOutput - the exact array of hits that was
    /// written into the observation vector the policy acted on. The tempting
    /// alternative, re-running Physics2D.CircleCast with the same parameters,
    /// would cost 40 extra casts per player per frame AND would be a different
    /// measurement: it would show what the world looks like now, not what the
    /// agent was told. When the two disagree - and at DecisionRequester period 8
    /// they disagree by up to 0.08 s of body motion - the sensor is the honest one.
    ///
    /// That lag is visible and intentional. The fan trails the body slightly
    /// because the agent's picture of the world genuinely is that stale; smoothing
    /// it away would be drawing a comforting fiction over the actual input.
    ///
    /// WHAT IT MAKES LEGIBLE. Every perception claim in CLAUDE.md - the 12
    /// directions 30 degrees apart, the narrow 60-degree goal wedge, the ~1.93 m
    /// guaranteed detection radius, the fact that an opponent can sit entirely
    /// between two rays - is an argument from arithmetic that nobody has ever
    /// watched happen. This is that argument, live, at 60 fps.
    ///
    /// Colour is by object class, matching the sensor split:
    ///   ball amber, goal gold, opponents red, walls slate.
    /// A ray that found its tag is drawn bright with a diamond at the hit point;
    /// a ray that found nothing fades out along its length.
    ///
    /// Cycle with V: off, then the last player to touch the ball, then everyone.
    /// One draw call - see <see cref="Agent_Lines"/>.
    /// Presentation only; self-disables in training and evaluation.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_VisionView : MonoBehaviour
    {
        public enum Mode
        {
            /// <summary>Nothing drawn.</summary>
            Off,
            /// <summary>Just the player who last touched the ball - the one worth watching.</summary>
            Focus,
            /// <summary>Every player on the pitch. Dense past 2v2, which is the point of Focus.</summary>
            All,
        }

        [Header("Appearance")]
        [Tooltip("Half-width of a ray line in world units.")]
        [SerializeField] private float _thickness = 0.028f;
        [Tooltip("Alpha at the start of a ray that hit its tag.")]
        [Range(0f, 1f)] [SerializeField] private float _hitAlpha = 0.85f;
        [Tooltip("Alpha at the start of a ray that found nothing. These are most of the fan, " +
                 "so keeping them faint is what stops the overlay reading as noise.")]
        [Range(0f, 1f)] [SerializeField] private float _missAlpha = 0.16f;
        [Tooltip("World radius of the marker drawn where a ray found its tag.")]
        [SerializeField] private float _markerRadius = 0.16f;
        [Tooltip("Sorting order. Under the intent overlay (15) so arrows stay readable on top.")]
        [SerializeField] private int _sortingOrder = 13;

        [SerializeField] private Mode _startMode = Mode.Off;
        [SerializeField] private bool _enableVisionView = true;

        /// <summary>Current mode, static so the HUD can label it without a reference.</summary>
        public static Mode CurrentMode { get; set; } = Mode.Off;

        /// <summary>
        /// Short HUD label, e.g. "VISION · KIM". Empty while off. Rebuilt only when
        /// the mode or the focused player changes, so reading it every frame is free.
        /// </summary>
        public static string ModeLabel { get; private set; } = string.Empty;

        Agent_EnvController _env;
        Agent_Lines _lines;

        // Sensor components per agent, resolved once. Agent_Soccer.Awake adds
        // Sensor_Vision if absent and Sensor_Vision reconfigures in place, so the
        // component set is fixed by the time any Start runs.
        readonly Dictionary<Agent_Soccer, RayPerceptionSensorComponent2D[]> _sensors = new();

        Agent_Soccer _focus;
        Mode _labelledMode = (Mode)(-1);
        Agent_Soccer _labelledFocus;

        // Class colours. Keyed off the sensor NAMES declared in Sensor_Vision.Battery,
        // so adding a sensor there and forgetting here degrades to a neutral tint
        // rather than throwing.
        static readonly Color BallTint = new(1f, 0.72f, 0.2f);
        static readonly Color GoalTint = new(1f, 0.93f, 0.55f);
        static readonly Color OpponentTint = new(1f, 0.33f, 0.3f);
        static readonly Color WallTint = new(0.5f, 0.62f, 0.75f);
        static readonly Color UnknownTint = new(0.75f, 0.75f, 0.75f);

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            var hud = FindFirstObjectByType<Agent_HUD>();

            if (!_enableVisionView || !Agent_Presentation.IsVisualScene(hud))
            {
                enabled = false;
                return;
            }

            CurrentMode = _startMode;
            // 40 rays x 4 vertices, plus markers, with room for a full 5v5.
            _lines = new Agent_Lines("VisionOverlay", _sortingOrder, vertexCapacity: 2048);
            _env.BallTouched += OnBallTouched;
        }

        void OnDestroy()
        {
            if (_env != null) _env.BallTouched -= OnBallTouched;
            _lines?.Dispose();
        }

        void OnBallTouched(Agent_Soccer toucher)
        {
            if (toucher != null) _focus = toucher;
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.vKey.wasPressedThisFrame)
            {
                CurrentMode = CurrentMode == Mode.All ? Mode.Off : CurrentMode + 1;
            }

            if (_lines == null || _env == null) return;

            bool on = CurrentMode != Mode.Off;
            _lines.Visible = on;
            RefreshLabel();
            if (!on) return;

            _lines.Begin();

            if (CurrentMode == Mode.All)
            {
                var agents = _env.agents;
                for (int i = 0; i < agents.Count; i++) DrawAgent(agents[i]);
            }
            else
            {
                DrawAgent(FocusedAgent());
            }

            _lines.Commit();
        }

        /// <summary>
        /// The last player to touch the ball, or the first live one before anybody
        /// has. Falls back rather than drawing nothing, because an empty overlay
        /// after pressing V reads as a broken feature.
        /// </summary>
        Agent_Soccer FocusedAgent()
        {
            if (_focus != null && _focus.isActiveAndEnabled) return _focus;

            var agents = _env.agents;
            for (int i = 0; i < agents.Count; i++)
            {
                if (agents[i] != null && agents[i].isActiveAndEnabled) return agents[i];
            }
            return null;
        }

        void RefreshLabel()
        {
            Agent_Soccer focus = CurrentMode == Mode.Focus ? FocusedAgent() : null;
            if (CurrentMode == _labelledMode && focus == _labelledFocus) return;

            _labelledMode = CurrentMode;
            _labelledFocus = focus;

            switch (CurrentMode)
            {
                case Mode.Off:
                    ModeLabel = string.Empty;
                    break;
                case Mode.All:
                    ModeLabel = "VISION · ALL";
                    break;
                default:
                    string who = focus != null && focus.rewards != null
                        ? focus.rewards.playerName
                        : "—";
                    ModeLabel = $"VISION · {who}";
                    break;
            }
        }

        void DrawAgent(Agent_Soccer agent)
        {
            if (agent == null || !agent.isActiveAndEnabled) return;

            if (!_sensors.TryGetValue(agent, out var sensors) || sensors == null)
            {
                sensors = agent.GetComponents<RayPerceptionSensorComponent2D>();
                _sensors[agent] = sensors;
            }

            for (int i = 0; i < sensors.Length; i++)
            {
                var component = sensors[i];
                if (component == null) continue;

                // RaySensor is null until the Agent has created its sensors, and
                // RayPerceptionOutput is null until the first decision. Both are
                // normal for a frame or two after a scene load.
                var sensor = component.RaySensor;
                var output = sensor?.RayPerceptionOutput;
                var rays = output?.RayOutputs;
                if (rays == null) continue;

                Color tint = TintFor(component.SensorName);
                for (int r = 0; r < rays.Length; r++) DrawRay(rays[r], tint);
            }
        }

        void DrawRay(RayPerceptionOutput.RayOutput ray, Color tint)
        {
            Vector2 start = ray.StartPositionWorld;
            Vector2 end = ray.EndPositionWorld;

            // HitTaggedObject, not HasHit: the sensor writes a 1 into the
            // observation only for its own detectable tag, so a ray that stopped
            // on some other collider contributed nothing but distance.
            if (ray.HitTaggedObject)
            {
                Vector2 impact = Vector2.Lerp(start, end, ray.HitFraction);
                Color bright = tint;
                bright.a = _hitAlpha;
                _lines.AddSegment(start, impact, _thickness, bright);
                _lines.AddDiamond(impact, _markerRadius, bright);
            }
            else
            {
                Color near = tint;
                near.a = _missAlpha;
                Color far = tint;
                far.a = 0f;
                _lines.AddFadedSegment(start, end, _thickness * 0.7f, near, far);
            }
        }

        static Color TintFor(string sensorName)
        {
            switch (sensorName)
            {
                case "Sensor_Ball": return BallTint;
                case "Sensor_Goal": return GoalTint;
                case "Sensor_Opponents": return OpponentTint;
                case "Sensor_Walls": return WallTint;
                default: return UnknownTint;
            }
        }
    }
}
