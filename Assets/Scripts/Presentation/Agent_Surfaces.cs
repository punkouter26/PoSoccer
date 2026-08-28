using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace PoSoccer
{
    /// <summary>
    /// Assigns the PoSoccer/SpriteLitFX materials and normal maps, and gives the
    /// ball its own travelling highlight.
    ///
    /// ORDER MATTERS. Agent_Stadium, in its Awake, overwrites sharedMaterial on
    /// EVERY SpriteRenderer in the scene with the stock Sprite-Lit-Default, so
    /// anything assigned earlier is silently discarded. This work therefore
    /// happens in Start - which always follows every Awake - and re-specialises
    /// the handful of renderers that deserve more than the default: the pitch,
    /// the ball, and the player bodies.
    ///
    /// Materials are SHARED per role (one pitch, one ball, one per team) rather
    /// than per renderer. That keeps SRP batching intact and avoids the
    /// renderer.material clone trap called out in .claude/rules/performance.md -
    /// which is also why the team rim is baked into two team materials instead of
    /// a per-instance property.
    ///
    /// Everything degrades gracefully: if the shader or a normal map is missing,
    /// the affected renderer simply keeps the stock lit material.
    /// </summary>
    [DefaultExecutionOrder(-35)]
    public sealed class Agent_Surfaces : MonoBehaviour
    {
        const string SHADER_NAME = "PoSoccer/SpriteLitFX";

        static readonly int StripeStrength = Shader.PropertyToID("_StripeStrength");
        static readonly int StripeCount = Shader.PropertyToID("_StripeCount");
        static readonly int StripeAngle = Shader.PropertyToID("_StripeAngle");
        static readonly int SheenStrength = Shader.PropertyToID("_SheenStrength");
        static readonly int SheenSpeed = Shader.PropertyToID("_SheenSpeed");
        static readonly int SheenWidth = Shader.PropertyToID("_SheenWidth");
        static readonly int RimColor = Shader.PropertyToID("_RimColor");
        static readonly int RimStrength = Shader.PropertyToID("_RimStrength");
        static readonly int RimPower = Shader.PropertyToID("_RimPower");
        static readonly int NormalMap = Shader.PropertyToID("_NormalMap");

        [Tooltip("Depth of the mown-grass banding on the pitch.")]
        [Range(0f, 0.5f)] [SerializeField] private float _pitchStripes = 0.075f;
        [Tooltip("Number of mown lanes across the pitch.")]
        [SerializeField] private float _pitchStripeCount = 9f;
        [Tooltip("Wet-grass sheen travelling across the pitch.")]
        [Range(0f, 1f)] [SerializeField] private float _pitchSheen = 0.045f;
        [Tooltip("Strength of the team-coloured rim on player bodies.")]
        [Range(0f, 3f)] [SerializeField] private float _playerRim = 0.9f;
        [Tooltip("Add a small Light2D to the ball so it carries its own highlight.")]
        [SerializeField] private bool _ballLight = true;
        [SerializeField] private bool _enableSurfaces = true;

        Material _pitchMaterial, _ballMaterial, _blueMaterial, _redMaterial;

        /// <summary>
        /// Material for the advertising hoardings, with a travelling gloss.
        /// Read by Agent_Crowd, whose Start runs after this one's.
        /// Null when the shader is unavailable - callers must handle that.
        /// </summary>
        public Material BoardMaterial { get; private set; }

        void Start()
        {
            var env = FindFirstObjectByType<Agent_EnvController>();
            var hud = FindFirstObjectByType<Agent_HUD>();
            if (!_enableSurfaces || env == null || !Agent_Presentation.IsMatchScene(hud))
            {
                enabled = false;
                return;
            }

            var shader = Shader.Find(SHADER_NAME);
            if (shader == null)
            {
                // Almost always means the shader is missing from Always Included
                // Shaders in a player build. Say so rather than rendering wrong.
                Debug.LogWarning($"Agent_Surfaces: shader '{SHADER_NAME}' not found; " +
                                 "keeping stock sprite materials.");
                enabled = false;
                return;
            }

            var sphereNormal = Resources.Load<Texture2D>("sphere_normal");
            var turfNormal = Resources.Load<Texture2D>("turf_normal");

            BuildMaterials(shader, sphereNormal, turfNormal);
            Apply(env);
        }

        void BuildMaterials(Shader shader, Texture2D sphereNormal, Texture2D turfNormal)
        {
            _pitchMaterial = new Material(shader) { name = "PoSoccer_Pitch" };
            _pitchMaterial.SetFloat(StripeStrength, _pitchStripes);
            _pitchMaterial.SetFloat(StripeCount, _pitchStripeCount);
            _pitchMaterial.SetFloat(StripeAngle, 0f);            // lanes run across the pitch
            _pitchMaterial.SetFloat(SheenStrength, _pitchSheen);
            _pitchMaterial.SetFloat(SheenSpeed, 0.06f);
            _pitchMaterial.SetFloat(SheenWidth, 3.5f);
            if (turfNormal != null) _pitchMaterial.SetTexture(NormalMap, turfNormal);

            _ballMaterial = new Material(shader) { name = "PoSoccer_Ball" };
            if (sphereNormal != null) _ballMaterial.SetTexture(NormalMap, sphereNormal);

            BoardMaterial = new Material(shader) { name = "PoSoccer_Boards" };
            BoardMaterial.SetFloat(SheenStrength, 0.28f);
            BoardMaterial.SetFloat(SheenSpeed, 0.22f);
            BoardMaterial.SetFloat(SheenWidth, 7f);

            _blueMaterial = TeamMaterial(shader, sphereNormal,
                Agent_SoccerView.TeamColor(Agent_Soccer.Team.Blue), "PoSoccer_TeamBlue");
            _redMaterial = TeamMaterial(shader, sphereNormal,
                Agent_SoccerView.TeamColor(Agent_Soccer.Team.Red), "PoSoccer_TeamRed");
        }

        Material TeamMaterial(Shader shader, Texture2D sphereNormal, Color team, string label)
        {
            var material = new Material(shader) { name = label };
            material.SetColor(RimColor, team);
            material.SetFloat(RimStrength, _playerRim);
            material.SetFloat(RimPower, 5f);
            if (sphereNormal != null) material.SetTexture(NormalMap, sphereNormal);
            return material;
        }

        void Apply(Agent_EnvController env)
        {
            // PitchBG is a direct child of the pitch root in both scenes.
            var pitch = env.transform.Find("PitchBG");
            if (pitch != null && pitch.TryGetComponent(out SpriteRenderer pitchRenderer))
                pitchRenderer.sharedMaterial = _pitchMaterial;

            if (env.Ball != null)
            {
                var ballRenderer = env.Ball.GetComponentInChildren<SpriteRenderer>();
                if (ballRenderer != null) ballRenderer.sharedMaterial = _ballMaterial;
                if (_ballLight) AddBallLight(env.Ball.transform);
            }

            // Deliberately NOT env.agents: that list is filled by
            // Agent_EnvController.Start, and relying on it made this silently
            // dependent on Start ordering between two components on the same
            // GameObject. It failed exactly that way - the pitch got its material
            // and every player kept the stock one. Walking the hierarchy is what
            // the env controller itself does, and it cannot race.
            var agents = env.GetComponentsInChildren<Agent_Soccer>();
            for (int i = 0; i < agents.Length; i++)
            {
                var agent = agents[i];
                if (agent == null) continue;
                if (!agent.TryGetComponent(out SpriteRenderer body)) continue;
                body.sharedMaterial = agent.team == Agent_Soccer.Team.Blue
                    ? _blueMaterial : _redMaterial;
            }
        }

        /// <summary>
        /// A soft light riding the ball. Small radius and low intensity: it is
        /// there to keep the ball readable against a dark pitch and to throw a
        /// moving highlight, not to light the scene.
        /// </summary>
        static void AddBallLight(Transform ball)
        {
            if (ball.Find("BallLight") != null) return;
            var go = new GameObject("BallLight");
            go.transform.SetParent(ball, false);
            var light = go.AddComponent<Light2D>();
            light.lightType = Light2D.LightType.Point;
            light.color = new Color(1f, 0.97f, 0.88f);
            light.intensity = 0.55f;
            light.pointLightInnerRadius = 0.05f;
            light.pointLightOuterRadius = 1.6f;
        }

        void OnDestroy()
        {
            // Materials created with `new` are not owned by the AssetDatabase, so
            // nothing else will collect them when the scene unloads.
            DestroyMaterial(_pitchMaterial);
            DestroyMaterial(_ballMaterial);
            DestroyMaterial(_blueMaterial);
            DestroyMaterial(_redMaterial);
            DestroyMaterial(BoardMaterial);
        }

        static void DestroyMaterial(Material material)
        {
            if (material != null) Destroy(material);
        }
    }
}
