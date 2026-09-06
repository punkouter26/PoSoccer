using System.Collections.Generic;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Gives every player a pair of legs that stride with the locomotion model,
    /// so four identical discs read as four moving characters.
    ///
    /// This is the 2D reading of "detailed meshes". The bodies are sprites and
    /// will stay sprites - a skeletal rig would mean per-agent prefabs, which
    /// means scene authoring, which is MCP-only per UNITY_RULES and would have to
    /// be redone for every squad size. Two extra quads per player, both off the
    /// Agent_Art atlas page and both on one shared material, buy most of the
    /// legibility for none of that.
    ///
    /// NOTHING HERE INVENTS ITS OWN MOTION. The stride phase is integrated from
    /// the body's FORWARD speed - the component of velocity along transform.up,
    /// not the raw magnitude - so a player being shoved sideways does not
    /// moonwalk, a reversing player's legs run backwards, and a stationary player
    /// stands still instead of jogging on the spot. That is the whole reason this
    /// reads as walking rather than as an animation playing nearby.
    ///
    /// One component on the pitch root rather than one per agent: a single shared
    /// material is what keeps every leg in the same batch, and a per-agent
    /// component would need its own copy or a static with a reference count.
    ///
    /// Presentation only. Self-disables in training and evaluation.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    [DefaultExecutionOrder(55)]
    public sealed class Agent_Limbs : MonoBehaviour
    {
        // 2026-09-05: the first version of these numbers produced legs that
        // animated correctly and were INVISIBLE, every frame, in every match.
        // Measured live: body world half-width 0.400, leg centre at world x 0.120
        // with half-width 0.080, so the leg's outer edge reached 0.200 - entirely
        // inside the torso silhouette - while sorting BEHIND it. The tell is in
        // Agent_Limbs' own diagnostic: visibleOutsideBody = False.
        //
        // Two changes fix it and both are needed. The hips are wide enough that
        // the legs straddle the body edge, and they now draw IN FRONT of the
        // torso rather than behind it - which is also just correct for a top-down
        // camera, where you look down ON the limbs.
        [Tooltip("Leg length as a fraction of body width.")]
        [Range(0.1f, 1.5f)] [SerializeField] private float _legLength = 0.62f;
        [Tooltip("Leg thickness as a fraction of body width.")]
        [Range(0.05f, 0.5f)] [SerializeField] private float _legWidth = 0.22f;
        [Tooltip("Sideways separation of the two legs, as a fraction of body width. " +
                 "Must be large enough that the legs clear the torso silhouette.")]
        [Range(0f, 1.5f)] [SerializeField] private float _hipWidth = 0.95f;
        [Tooltip("How far a leg swings fore and aft at full stride, as a fraction of body width.")]
        [Range(0f, 1f)] [SerializeField] private float _stride = 0.3f;
        [Tooltip("Forward speed (m/s) at which the stride reaches full amplitude.")]
        [SerializeField] private float _fullStrideSpeed = 6f;
        [Tooltip("Stride cycles per second per m/s of forward speed.")]
        [SerializeField] private float _cadence = 0.42f;
        [Tooltip("Extra stride amplitude while boosting.")]
        [Range(0f, 1f)] [SerializeField] private float _boostStretch = 0.35f;
        [Tooltip("How much darker the legs are than the personality colour.")]
        [Range(0f, 1f)] [SerializeField] private float _shade = 0.45f;
        [SerializeField] private bool _enableLimbs = true;

        sealed class Rig
        {
            public Agent_Soccer Agent;
            public Rigidbody2D Body;
            public Transform Left;
            public Transform Right;
            public float Phase;
            public float BodyWidth;
        }

        readonly List<Rig> _rigs = new();
        Material _material;

        void Start()
        {
            var hud = FindFirstObjectByType<Agent_HUD>();
            if (!_enableLimbs || !Agent_Presentation.IsVisualScene(hud))
            {
                enabled = false;
                return;
            }

            // Lit, unlike the shadows: a limb is part of the body and should sit
            // under the same floodlights the torso does, or it reads as a decal.
            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Lit-Default")
                ?? Shader.Find("Sprites/Default");
            if (shader == null)
            {
                enabled = false;
                return;
            }
            _material = new Material(shader) { name = "PoSoccer_Limbs" };

            var agents = GetComponentsInChildren<Agent_Soccer>();
            for (int i = 0; i < agents.Length; i++) Build(agents[i]);
            if (_rigs.Count == 0) enabled = false;
        }

        void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }

        void Build(Agent_Soccer agent)
        {
            if (agent == null) return;
            if (!agent.TryGetComponent(out SpriteRenderer body) || body.sprite == null) return;
            if (agent.transform.Find("Leg_L") != null) return;

            // Body-local width, not world bounds: the legs are children of the
            // agent and so already inherit the physique scale Agent_Soccer applies
            // from Reward_Settings.bodyScale. Using world bounds would apply it
            // twice and give the heaviest player comically long legs.
            float width = body.sprite.bounds.size.x;

            Color tint = agent.rewards != null ? agent.rewards.playerColor : Color.white;
            tint = Color.Lerp(tint, Color.black, _shade);
            tint.a = 1f;

            var rig = new Rig
            {
                Agent = agent,
                Body = agent.GetComponent<Rigidbody2D>(),
                BodyWidth = width,
                // Half a cycle apart, which is what makes it a gait rather than a hop.
                Phase = Random.Range(0f, Mathf.PI * 2f),
            };
            rig.Left = BuildLeg(agent.transform, body, "Leg_L", tint, width);
            rig.Right = BuildLeg(agent.transform, body, "Leg_R", tint, width);
            _rigs.Add(rig);
        }

        Transform BuildLeg(Transform parent, SpriteRenderer body, string label, Color tint, float width)
        {
            var go = new GameObject(label);
            go.transform.SetParent(parent, false);
            // A disc squashed into an oval: a capsule shape without a second
            // entry on the atlas page, and it keeps the rounded foot.
            go.transform.localScale = new Vector3(width * _legWidth, width * _legLength, 1f);

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = Agent_Art.Disc(1f);
            renderer.sharedMaterial = _material;
            renderer.color = tint;
            renderer.sortingLayerName = body.sortingLayerName;
            // IN FRONT of the torso. Behind it (body - 1) is where these started
            // and it made them permanently invisible - see the note on _hipWidth.
            // Drawing limbs over the body is also the physically right answer for
            // a top-down view: you are looking down at them, not through the torso.
            renderer.sortingOrder = body.sortingOrder + 1;
            return go.transform;
        }

        void Update()
        {
            float dt = Time.deltaTime;

            for (int i = 0; i < _rigs.Count; i++)
            {
                var rig = _rigs[i];
                if (rig.Agent == null || rig.Left == null || rig.Right == null) continue;

                // Signed forward speed. The sign is the point: it is what makes a
                // backpedalling player's legs run the other way.
                float forwardSpeed = rig.Body != null
                    ? Vector2.Dot(rig.Body.linearVelocity, rig.Agent.transform.up)
                    : 0f;

                rig.Phase += forwardSpeed * _cadence * Mathf.PI * 2f * dt;

                float effort = Mathf.Clamp01(Mathf.Abs(forwardSpeed) / Mathf.Max(0.01f, _fullStrideSpeed));
                float amplitude = rig.BodyWidth * _stride * effort;
                if (rig.Agent.IsBoosting) amplitude *= 1f + _boostStretch;

                float swing = Mathf.Sin(rig.Phase) * amplitude;
                float hip = rig.BodyWidth * _hipWidth * 0.5f;

                // Local space, so the legs follow the body's heading for free -
                // and this is exactly why the legs are children of the agent while
                // Agent_Shadows' blobs deliberately are not.
                rig.Left.localPosition = new Vector3(-hip, swing, 0f);
                rig.Right.localPosition = new Vector3(hip, -swing, 0f);
            }
        }
    }
}
