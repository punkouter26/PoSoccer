using System.Collections.Generic;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Contact shadows under every player and under the ball.
    ///
    /// WHY THIS EXISTS RATHER THAN 2D SHADOW CASTERS. Agent_Stadium's docstring
    /// records that ShadowCaster2D was removed on 2026-08-05 as the single
    /// largest GPU cost in the scene - six shadow-casting point lights plus a
    /// caster on every wall and agent - for almost nothing readable on a flat
    /// top-down pitch. That was the right call for CAST shadows and the wrong
    /// conclusion about GROUNDING: with no occlusion at all, the bodies read as
    /// discs floating over a green field. This restores the grounding cue and
    /// none of the cost. Every shadow is one quad, one shared unlit material and
    /// one sprite off the Agent_Art page, so the whole system is a single
    /// additional draw call at any squad size.
    ///
    /// The offset is not physical and is not trying to be. Real stadium lighting
    /// is four towers, so a player's shadows fan OUTWARD from the middle of the
    /// pitch; that is reproduced here by pushing each shadow along the vector
    /// from pitch centre to body, plus a constant bias so a player standing on
    /// the centre spot still has one. It costs two multiplies and reads correctly
    /// the moment anyone moves.
    ///
    /// Shadows are parented to the pitch root, NOT to the body they belong to.
    /// A child would inherit the agent's rotation - and these agents turn through
    /// the full circle - so the ellipse would spin on the spot like a compass
    /// needle, which is the one thing a shadow must never do.
    ///
    /// Presentation only. Self-disables in training and evaluation.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    [DefaultExecutionOrder(60)]
    public sealed class Agent_Shadows : MonoBehaviour
    {
        // 2026-09-05: these were first set by eye and produced shadows that were
        // present, correct and invisible. Measured live: body 0.800 wide, shadow
        // 0.920, offset only (-0.06, -0.28). The shadow therefore protruded about
        // 0.34 past the torso - and that outer band is the softest part of the
        // blob, where alpha is roughly 0.015. Nothing to see.
        //
        // The offset now clears the body by a visible margin and the blob is
        // wider than the torso rather than barely larger, so what shows is the
        // dark core of the falloff instead of its faintest rim.
        [Tooltip("Shadow width as a multiple of the body it sits under.")]
        [SerializeField] private float _bodyScale = 1.35f;
        [Tooltip("Darkness of a shadow directly under a stationary body.")]
        [Range(0f, 1f)] [SerializeField] private float _opacity = 0.42f;
        [Tooltip("How far a shadow is pushed away from the centre of the pitch, in metres at the touchline.")]
        [SerializeField] private float _spread = 0.34f;
        [Tooltip("Constant offset so a body on the centre spot still casts something. " +
                 "Must exceed the body's half-extent or the shadow hides behind it.")]
        [SerializeField] private Vector2 _bias = new(0.10f, -0.42f);
        [Tooltip("Fraction of opacity still present at full sprint - a moving body lifts off the turf.")]
        [Range(0f, 1f)] [SerializeField] private float _motionFade = 0.65f;
        [Tooltip("Speed (m/s) treated as full sprint for the motion fade.")]
        [SerializeField] private float _sprintSpeed = 9.5f;
        [Tooltip("Drawn above PitchBG (-10) and below the ball (2) and the bodies (3).")]
        [SerializeField] private int _sortingOrder = -2;
        [SerializeField] private bool _enableShadows = true;

        readonly struct Caster
        {
            public readonly Transform Source;
            public readonly Rigidbody2D Body;
            public readonly Transform Shadow;
            public readonly SpriteRenderer Renderer;

            public Caster(Transform source, Rigidbody2D body, Transform shadow, SpriteRenderer renderer)
            {
                Source = source;
                Body = body;
                Shadow = shadow;
                Renderer = renderer;
            }
        }

        readonly List<Caster> _casters = new();
        Agent_EnvController _env;
        Material _material;
        Transform _root;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            var hud = FindFirstObjectByType<Agent_HUD>();
            if (!_enableShadows || !Agent_Presentation.IsVisualScene(hud))
            {
                enabled = false;
                return;
            }

            // Unlit on purpose. A shadow that responds to the floodlights gets
            // BRIGHTER under a light, which is exactly backwards.
            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
                ?? Shader.Find("Sprites/Default");
            if (shader == null)
            {
                enabled = false;
                return;
            }
            _material = new Material(shader) { name = "PoSoccer_Shadow" };

            var rootGo = new GameObject("Shadows");
            rootGo.transform.SetParent(transform, false);
            _root = rootGo.transform;

            var agents = GetComponentsInChildren<Agent_Soccer>();
            for (int i = 0; i < agents.Length; i++)
            {
                var agent = agents[i];
                if (agent == null) continue;
                float width = BodyWidth(agent.transform) * _bodyScale;
                Add(agent.transform, agent.GetComponent<Rigidbody2D>(), width, $"Shadow_{agent.name}");
            }

            if (_env.Ball != null)
            {
                // The ball is small and fast; a slightly tighter, darker patch
                // under it is what sells it as rolling rather than sliding.
                float width = BodyWidth(_env.Ball.transform) * 0.95f;
                Add(_env.Ball.transform, _env.Ball, width, "Shadow_Ball");
            }

            if (_casters.Count == 0) enabled = false;
        }

        void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }

        void Add(Transform source, Rigidbody2D body, float worldWidth, string label)
        {
            var go = new GameObject(label);
            go.transform.SetParent(_root, false);

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = Agent_Art.Blob(Mathf.Max(0.05f, worldWidth));
            renderer.sharedMaterial = _material;
            renderer.color = new Color(0f, 0f, 0f, _opacity);
            renderer.sortingOrder = _sortingOrder;

            _casters.Add(new Caster(source, body, go.transform, renderer));
        }

        /// <summary>
        /// World-space width of whatever sprite the caster draws, so a heavier
        /// physique (Reward_Settings.bodyScale) gets a bigger shadow without this
        /// component knowing anything about physiques.
        ///
        /// NOT renderer.bounds.size.x, which is what this used to be. Renderer
        /// bounds are a world-axis-aligned box, so for a body that ROTATES - and
        /// these turn through the full circle - it reports the diagonal, not the
        /// width. Measured on a 0.800-wide agent mid-turn: 1.118, i.e. 40% too
        /// big, and it would have breathed in and out as the player turned.
        /// The sprite's own bounds times lossyScale is rotation-independent.
        /// </summary>
        static float BodyWidth(Transform source)
        {
            var renderer = source.GetComponentInChildren<SpriteRenderer>();
            if (renderer == null || renderer.sprite == null) return 1f;
            return renderer.sprite.bounds.size.x * Mathf.Abs(renderer.transform.lossyScale.x);
        }

        /// <summary>
        /// LateUpdate, not Update: the bodies are moved by the physics step and
        /// by Agent_CameraFollow's framing, and a shadow that updates first
        /// trails its own body by a frame at sprint speed.
        /// </summary>
        void LateUpdate()
        {
            Vector2 centre = transform.position;
            float halfSpan = Mathf.Max(0.01f, _env.PitchHalfExtents.magnitude);

            for (int i = 0; i < _casters.Count; i++)
            {
                var caster = _casters[i];
                if (caster.Source == null || caster.Shadow == null) continue;

                Vector2 position = caster.Source.position;
                Vector2 fromCentre = (position - centre) / halfSpan;
                Vector2 offset = fromCentre * _spread + _bias;

                caster.Shadow.position = new Vector3(
                    position.x + offset.x, position.y + offset.y, caster.Source.position.z);

                if (caster.Body == null) continue;
                float speed01 = Mathf.Clamp01(caster.Body.linearVelocity.magnitude / _sprintSpeed);
                float alpha = _opacity * Mathf.Lerp(1f, _motionFade, speed01);
                var color = caster.Renderer.color;
                if (!Mathf.Approximately(color.a, alpha))
                {
                    color.a = alpha;
                    caster.Renderer.color = color;
                }
            }
        }
    }
}
