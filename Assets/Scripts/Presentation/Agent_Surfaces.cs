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
        static readonly int NetStrength = Shader.PropertyToID("_NetStrength");
        static readonly int NetTiling = Shader.PropertyToID("_NetTiling");
        static readonly int NetRipple = Shader.PropertyToID("_NetRipple");

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

        [Header("Goal nets")]
        [SerializeField] private bool _goalNets = true;
        [Tooltip("How far the net extends behind the goal line, in pitch metres.")]
        [SerializeField] private float _netDepth = 1.5f;
        [Tooltip("Cords across the net quad. Higher = finer mesh.")]
        [SerializeField] private float _netCords = 15f;
        [Tooltip("Opacity of the cords themselves.")]
        [Range(0f, 1f)] [SerializeField] private float _netOpacity = 0.75f;
        [Tooltip("Seconds a net keeps rippling after the ball hits it.")]
        [SerializeField] private float _rippleSeconds = 1.1f;
        [Tooltip("Net quads sit above the walls (0) and below the goal mouth bar (6).")]
        [SerializeField] private int _netSortingOrder = 5;

        Material _pitchMaterial, _ballMaterial, _blueMaterial, _redMaterial;
        Material _blueNetMaterial, _redNetMaterial;
        Transform _blueNet, _redNet;
        Agent_EnvController _env;
        float _blueRipple, _redRipple;
        float _shownGoalWidth = -1f;

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
            if (!_enableSurfaces || env == null || !Agent_Presentation.IsVisualScene(hud))
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

            _env = env;
            BuildMaterials(shader, sphereNormal, turfNormal);
            Apply(env);

            if (_goalNets)
            {
                BuildNets(shader);
                _env.EpisodeEnded += OnEpisodeEnded;
            }
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

        // -- Goal nets --------------------------------------------------------

        /// <summary>
        /// A net quad behind each goal mouth. Two materials rather than one so a
        /// goal at one end can ripple without the other end twitching in sympathy
        /// - _NetRipple lives in the material, and one shared material would drive
        /// both nets from the same value.
        ///
        /// The quads are parented to the PITCH ROOT, not to the goal transforms.
        /// The goals carry a non-uniform scale of roughly 4.8 x 0.26 to stretch
        /// their sprite (Agent_GoalFrame's docstring records what that did to the
        /// first attempt at drawing under them), so a child quad would need the
        /// same inverse-scale dance. Positioning in world metres avoids the whole
        /// class of bug.
        /// </summary>
        void BuildNets(Shader shader)
        {
            _blueNetMaterial = NetMaterial(shader, "PoSoccer_NetBlue");
            _redNetMaterial = NetMaterial(shader, "PoSoccer_NetRed");

            // Same tints Agent_EnvController.EnsureGoalFrame uses for the mouths,
            // so the net reads as part of the same goal rather than a new object.
            _blueNet = BuildNetQuad("Net_BlueGoal", _env.blueGoal, _blueNetMaterial,
                new Color(0.15f, 0.55f, 1f, _netOpacity));
            _redNet = BuildNetQuad("Net_RedGoal", _env.redGoal, _redNetMaterial,
                new Color(1f, 0.5f, 0.05f, _netOpacity));

            ApplyNetWidth(_env.CurrentGoalWidth);
        }

        Material NetMaterial(Shader shader, string label)
        {
            var material = new Material(shader) { name = label };
            material.SetFloat(NetStrength, 1f);
            material.SetFloat(NetTiling, _netCords);
            material.SetFloat(NetRipple, 0f);
            return material;
        }

        Transform BuildNetQuad(string label, Transform goal, Material material, Color tint)
        {
            if (goal == null) return null;

            var go = new GameObject(label);
            go.transform.SetParent(_env.transform, false);

            var renderer = go.AddComponent<SpriteRenderer>();
            // FullRect, NOT Square: the net mask is a uv-space effect, and an
            // atlased sprite's uv spans its slot on the page rather than 0..1.
            // Measured on the atlased version: uv in [0.0039, 0.0117], which
            // collapsed the whole mask to a constant and drew a solid board.
            // See Agent_Art.FullRect.
            renderer.sprite = Agent_Art.FullRect(1f);   // scaled to size below
            renderer.sharedMaterial = material;
            renderer.color = tint;
            renderer.sortingOrder = _netSortingOrder;
            return go.transform;
        }

        /// <summary>
        /// Size and place both nets for the current goal width. Called on build and
        /// whenever the width moves - the curriculum steps it, and
        /// Agent_PitchSizing rescales it per squad size.
        /// </summary>
        void ApplyNetWidth(float width)
        {
            if (width <= 0f) return;
            _shownGoalWidth = width;
            PlaceNet(_blueNet, _env.blueGoal, width);
            PlaceNet(_redNet, _env.redGoal, width);
        }

        void PlaceNet(Transform net, Transform goal, float width)
        {
            if (net == null || goal == null) return;

            // Outward is simply "further from the centre circle" - which end of
            // the pitch this goal is on. Reading it from the goal's own position
            // means a pitch resize moves the net with the goal for free.
            float outward = Mathf.Sign(goal.localPosition.y);
            if (Mathf.Approximately(outward, 0f)) outward = 1f;

            net.localPosition = new Vector3(
                goal.localPosition.x,
                goal.localPosition.y + outward * _netDepth * 0.5f,
                0.1f);
            net.localScale = new Vector3(width, _netDepth, 1f);
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            // Which net to shake is decided by where the BALL is, not by which
            // team scored. EpisodeEnded fires before ResetPitch (documented in
            // Agent_EnvController), so the ball is still sitting in the goal that
            // just conceded - and this stays correct however the team-to-goal
            // mapping is wired, including own goals.
            if (winner == null || _env.Ball == null) return;

            Vector2 ball = _env.Ball.position;
            float toBlue = _env.blueGoal != null
                ? Vector2.Distance(ball, _env.blueGoal.position) : float.MaxValue;
            float toRed = _env.redGoal != null
                ? Vector2.Distance(ball, _env.redGoal.position) : float.MaxValue;

            if (toBlue <= toRed) _blueRipple = 1f;
            else _redRipple = 1f;
        }

        void Update()
        {
            if (!_goalNets || _env == null) return;

            if (!Mathf.Approximately(_env.CurrentGoalWidth, _shownGoalWidth))
                ApplyNetWidth(_env.CurrentGoalWidth);

            // unscaledDeltaTime: the goal replay freezes the clock the instant a
            // goal lands, and a net frozen mid-ripple is a net that never settles.
            float decay = Time.unscaledDeltaTime / Mathf.Max(0.05f, _rippleSeconds);
            Decay(ref _blueRipple, _blueNetMaterial, decay);
            Decay(ref _redRipple, _redNetMaterial, decay);
        }

        static void Decay(ref float ripple, Material material, float step)
        {
            if (ripple <= 0f || material == null) return;
            ripple = Mathf.Max(0f, ripple - step);
            material.SetFloat(NetRipple, ripple);
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
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;

            // Materials created with `new` are not owned by the AssetDatabase, so
            // nothing else will collect them when the scene unloads.
            DestroyMaterial(_pitchMaterial);
            DestroyMaterial(_ballMaterial);
            DestroyMaterial(_blueMaterial);
            DestroyMaterial(_redMaterial);
            DestroyMaterial(BoardMaterial);
            DestroyMaterial(_blueNetMaterial);
            DestroyMaterial(_redNetMaterial);
        }

        static void DestroyMaterial(Material material)
        {
            if (material != null) Destroy(material);
        }
    }
}
