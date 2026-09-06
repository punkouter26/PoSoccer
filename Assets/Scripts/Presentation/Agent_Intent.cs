using UnityEngine;
using UnityEngine.InputSystem;

namespace PoSoccer
{
    /// <summary>
    /// The intent overlay: what each brain is ASKING for, drawn next to what the
    /// body is actually doing.
    ///
    /// Four marks per player, and every one of them is a number the simulation
    /// already produces:
    ///  - a solid arrow along <see cref="Agent_Soccer.IntentWorld"/>, the policy's
    ///    clamped movement demand, thickened while boosting;
    ///  - a hollow white arrow along the rigidbody's velocity;
    ///  - an arc at the body's edge filling with
    ///    <see cref="Agent_Soccer.TractionSaturation"/>, green through red, that
    ///    closes to a full ring when the feet are at the limit of grip;
    ///  - a short tick on the side the turn torque is pushing.
    ///
    /// WHY THIS IS THE HEADLINE SPECTATOR FEATURE. The two arrows disagreeing is
    /// this project's whole story rendered live: a policy that wants the right
    /// thing and cannot get there looks nothing like one that wants the wrong
    /// thing, and until now the only way to tell them apart was
    /// Agent_PlayMode_MovementProbe printing numbers to a console after the fact.
    /// Nine phases of hypotheses were built on not being able to see this.
    ///
    /// The rule-based bot writes the same action buffer as a trained brain, so it
    /// gets exactly the same marks. Drawing them on identical terms is the point:
    /// the bot is the benchmark, and now you can watch what it does differently.
    ///
    /// One draw call at any squad size - see <see cref="Agent_Lines"/>.
    /// Toggle with I. Presentation only; self-disables in training and evaluation.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_Intent : MonoBehaviour
    {
        [Header("Marks")]
        [Tooltip("World length of an intent arrow at full demand (|intent| = 1).")]
        [SerializeField] private float _intentLength = 2.4f;
        [Tooltip("World length of the velocity arrow at the reference sprint speed.")]
        [SerializeField] private float _velocityLength = 2.4f;
        [Tooltip("Sprint speed (m/s) the velocity arrow is scaled against. 9.54 is this " +
                 "chassis' measured top speed - see CLAUDE.md's physics section.")]
        [SerializeField] private float _referenceSpeed = 9.54f;
        [Tooltip("Line half-width in world units.")]
        [SerializeField] private float _thickness = 0.055f;
        [Tooltip("Radius of the traction arc as a multiple of the body's own radius.")]
        [SerializeField] private float _ringScale = 1.35f;
        [Tooltip("Nominal body radius before per-profile scaling.")]
        [SerializeField] private float _bodyRadius = 0.4f;

        [Header("Appearance")]
        [Range(0f, 1f)] [SerializeField] private float _intentAlpha = 0.9f;
        [Range(0f, 1f)] [SerializeField] private float _velocityAlpha = 0.42f;
        [Range(0f, 1f)] [SerializeField] private float _ringAlpha = 0.8f;
        [Tooltip("Sorting order. Above the pitch, players and shockwaves (0..6), " +
                 "below the confetti burst layer (20).")]
        [SerializeField] private int _sortingOrder = 15;

        [Tooltip("Start with the overlay visible. It is the clearest single answer to " +
                 "\"what is the AI doing\", so it is on by default in a match.")]
        [SerializeField] private bool _visibleOnStart = true;
        [SerializeField] private bool _enableIntent = true;

        /// <summary>
        /// Overlay visibility, static so the HUD (or a settings screen) can drive
        /// it without holding a reference. Re-seeded from the serialized default
        /// in Start, so a scene reload does not inherit the last session's state.
        /// </summary>
        public static bool Visible { get; set; } = true;

        Agent_EnvController _env;
        Agent_Lines _lines;

        static readonly Color TractionSafe = new(0.35f, 0.95f, 0.45f);
        static readonly Color TractionLimit = new(1f, 0.35f, 0.22f);

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            var hud = FindFirstObjectByType<Agent_HUD>();

            if (!_enableIntent || !Agent_Presentation.IsVisualScene(hud))
            {
                enabled = false;
                return;
            }

            Visible = _visibleOnStart;
            _lines = new Agent_Lines("IntentOverlay", _sortingOrder, vertexCapacity: 1024);
        }

        void OnDestroy() => _lines?.Dispose();

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.iKey.wasPressedThisFrame) Visible = !Visible;

            if (_lines == null || _env == null) return;

            _lines.Visible = Visible;
            if (!Visible) return;

            _lines.Begin();

            var agents = _env.agents;
            for (int i = 0; i < agents.Count; i++)
            {
                var agent = agents[i];
                if (agent == null || agent.Body == null || !agent.isActiveAndEnabled) continue;
                DrawAgent(agent);
            }

            _lines.Commit();
        }

        void DrawAgent(Agent_Soccer agent)
        {
            Vector2 origin = agent.Body.position;
            float radius = _bodyRadius * Mathf.Max(0.1f, agent.transform.localScale.x);
            Color team = Agent_SoccerView.TeamColor(agent.team);

            // -- Velocity: what the body is actually doing. Drawn first so the
            // intent arrow sits on top of it wherever the two agree.
            Vector2 velocity = agent.Body.linearVelocity;
            float speed01 = Mathf.Clamp01(velocity.magnitude / Mathf.Max(0.01f, _referenceSpeed));
            if (speed01 > 0.02f)
            {
                Vector2 direction = velocity.normalized;
                _lines.AddArrow(origin + direction * radius, direction,
                    _velocityLength * speed01, _thickness * 0.75f,
                    new Color(1f, 1f, 1f, _velocityAlpha));
            }

            // -- Intent: what the policy asked for.
            Vector2 intent = agent.IntentWorld;
            float demand = intent.magnitude;
            if (demand > 0.02f)
            {
                Vector2 direction = intent / demand;
                // Boost really is a 2.2x on the force, so it gets a heavier line.
                float width = agent.IsBoosting ? _thickness * 1.8f : _thickness;
                _lines.AddArrow(origin + direction * radius, direction,
                    _intentLength * demand, width,
                    new Color(team.r, team.g, team.b, _intentAlpha));
            }

            // -- Traction: how much of the friction circle the feet are using.
            float saturation = agent.TractionSaturation;
            if (saturation > 0.02f)
            {
                Color tint = Color.Lerp(TractionSafe, TractionLimit, saturation);
                tint.a = _ringAlpha * Mathf.Clamp01(0.35f + saturation);
                _lines.AddArc(origin, radius * _ringScale, saturation, _thickness * 0.6f, tint);
            }

            // -- Turn command, as a tick on the side the torque is pushing.
            // Sign convention matches AddTorque: positive = counter-clockwise.
            float turn = agent.TurnCommand;
            if (Mathf.Abs(turn) > 0.08f)
            {
                Vector2 facing = agent.transform.up;
                Vector2 side = new Vector2(-facing.y, facing.x) * Mathf.Sign(turn);
                _lines.AddArrow(origin + facing * (radius * 1.1f), side,
                    0.45f * Mathf.Abs(turn), _thickness * 0.8f,
                    new Color(team.r, team.g, team.b, _intentAlpha * 0.7f));
            }
        }
    }
}
