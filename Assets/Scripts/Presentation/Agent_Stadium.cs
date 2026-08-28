using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace PoSoccer
{
    /// <summary>
    /// Runtime stadium dressing: URP 2D lighting (global fill, corner floodlights,
    /// goal glows), lit sprite materials, and a post-processing volume
    /// (bloom + goal-moment chromatic pulse). Everything is built in code; the
    /// scene only carries this component.
    ///
    /// 2026-08-05: 2D shadows and the vignette were removed. Shadows were the
    /// single largest GPU cost here — six shadow-casting point lights plus a
    /// ShadowCaster2D on every wall and agent — and on a flat top-down pitch they
    /// added almost nothing readable. The vignette darkened the edges of an
    /// already-small portrait view, working against readability rather than for
    /// it. Bloom, the lights and the goal pulse carry the look on their own.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public sealed class Agent_Stadium : MonoBehaviour
    {
        public static Agent_Stadium Instance { get; private set; }

        [Tooltip("Sprite-Lit material applied to every SpriteRenderer so 2D lights affect them.")]
        public Material litMaterial;
        [Range(0f, 1f)] public float globalFill = 0.5f;

        [Tooltip("Seconds the floodlights strobe in the scoring team's colour after a goal.")]
        [SerializeField] private float _celebrationSeconds = 2.2f;
        [Tooltip("Strobe cycles per second during a goal celebration.")]
        [SerializeField] private float _celebrationHz = 6f;

        [Tooltip("Neutral is the safe broadcast look; ACES is punchier but crushes saturated team colours.")]
        [SerializeField] private TonemappingMode _tonemapping = TonemappingMode.Neutral;

        Bloom _bloom;
        ChromaticAberration _chroma;
        ColorAdjustments _grading;
        float _pulse;

        // Floodlights are captured so a goal can recolour them; each keeps its
        // authored colour/intensity so the celebration can hand them back exactly.
        readonly List<Light2D> _floodlights = new();
        readonly List<Color> _floodBaseColor = new();
        readonly List<float> _floodBaseIntensity = new();
        float _celebrate;
        Color _celebrateColor = Color.white;

        void Awake()
        {
            Instance = this;

            if (litMaterial != null)
                foreach (var sr in FindObjectsByType<SpriteRenderer>())
                    sr.sharedMaterial = litMaterial;

            // Lights
            MakeLight("GlobalFill", Light2D.LightType.Global, Vector2.zero,
                new Color(0.8f, 0.85f, 0.95f), globalFill, 0f);
            foreach (var c in new[]
            {
                new Vector2(-5.5f, -8.5f), new Vector2(5.5f, -8.5f),
                new Vector2(-5.5f, 8.5f), new Vector2(5.5f, 8.5f),
            })
                MakeLight("Floodlight", Light2D.LightType.Point, c,
                    new Color(1f, 0.96f, 0.82f), 1.1f, 11f);
            MakeLight("GoalGlowN", Light2D.LightType.Point, new Vector2(0f, 8.9f),
                new Color(1f, 0.45f, 0.4f), 0.8f, 4.5f);
            MakeLight("GoalGlowS", Light2D.LightType.Point, new Vector2(0f, -8.9f),
                new Color(0.4f, 0.6f, 1f), 0.8f, 4.5f);

            BuildPostVolume();
        }

        void MakeLight(string label, Light2D.LightType type, Vector2 pos,
            Color color, float intensity, float radius)
        {
            var go = new GameObject(label);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = pos;
            var light = go.AddComponent<Light2D>();
            light.lightType = type;
            light.color = color;
            light.intensity = intensity;
            if (radius > 0f)
            {
                light.pointLightInnerRadius = radius * 0.15f;
                light.pointLightOuterRadius = radius;
            }

            if (label == "Floodlight")
            {
                _floodlights.Add(light);
                _floodBaseColor.Add(color);
                _floodBaseIntensity.Add(intensity);
            }
        }

        void BuildPostVolume()
        {
            var go = new GameObject("PostFX");
            go.transform.SetParent(transform, false);
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            // Beat the project's default volume profile, which sits at priority 0.
            volume.priority = 10f;

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();

            // TONEMAPPING IS THE WHOLE POINT OF THIS BLOCK. The project renders in
            // HDR, and PulseGoal drives bloom intensity to 2.5 on every goal, but
            // Assets/Settings/DefaultVolumeProfile.asset explicitly overrides
            // Tonemapping to None. With no tonemapper, everything above 1.0 clamps,
            // so the celebration - the single most important frame in the game -
            // resolved to a flat white blob. Neutral maps the highlight rolloff
            // properly and costs one full-screen curve.
            var tonemapping = profile.Add<Tonemapping>();
            tonemapping.mode.Override(_tonemapping);

            _grading = profile.Add<ColorAdjustments>();
            _grading.postExposure.Override(0.15f);
            _grading.contrast.Override(14f);
            _grading.saturation.Override(12f);

            // Floodlit-stadium look: cool shadows against warm key light. Cheap,
            // and it stops the pitch green from reading as flat poster paint.
            var splitToning = profile.Add<SplitToning>();
            splitToning.shadows.Override(new Color(0.35f, 0.45f, 0.75f));
            splitToning.highlights.Override(new Color(1f, 0.93f, 0.72f));
            splitToning.balance.Override(-15f);

            _bloom = profile.Add<Bloom>();
            _bloom.intensity.Override(0.9f);
            _bloom.threshold.Override(0.9f);
            _bloom.scatter.Override(0.72f);

            _chroma = profile.Add<ChromaticAberration>();
            _chroma.intensity.Override(0f);

            volume.profile = profile;
        }

        /// <summary>
        /// Momentary exposure lift, used by the goal celebration so the whole
        /// image blooms rather than only the emissive pixels.
        /// </summary>
        public void SetExposureBoost(float stops)
        {
            if (_grading == null) return;
            _grading.postExposure.Override(0.15f + stops);
        }

        /// <summary>Goal-moment kick: bloom flash + chromatic aberration pulse.</summary>
        public void PulseGoal() => _pulse = 1f;

        /// <summary>
        /// Full goal light show: the post pulse plus a strobe that throws the
        /// floodlights into the scoring team's colour and hands them back. Driven
        /// on unscaled time so it still plays while the goal replay has the
        /// game clock frozen (see Agent_TimeFreeze).
        /// </summary>
        public void CelebrateGoal(Color teamColor)
        {
            PulseGoal();
            _celebrateColor = teamColor;
            _celebrate = _celebrationSeconds;
        }

        void Update()
        {
            if (_celebrate > 0f) TickCelebration();

            if (_pulse <= 0f || _bloom == null) return;
            _pulse = Mathf.Max(0f, _pulse - Time.unscaledDeltaTime * 1.6f);
            _bloom.intensity.Override(0.9f + _pulse * 1.6f);
            _chroma.intensity.Override(_pulse * 0.45f);
            SetExposureBoost(_pulse * 0.5f);
        }

        void TickCelebration()
        {
            _celebrate = Mathf.Max(0f, _celebrate - Time.unscaledDeltaTime);
            // Square-ish strobe that fades out, so the last cycles ease back to
            // the authored lighting instead of snapping.
            float envelope = Mathf.Clamp01(_celebrate / Mathf.Max(0.01f, _celebrationSeconds));
            float phase = Mathf.PingPong(_celebrate * _celebrationHz, 1f);
            float mix = phase * envelope;

            for (int i = 0; i < _floodlights.Count; i++)
            {
                var light = _floodlights[i];
                if (light == null) continue;
                light.color = Color.Lerp(_floodBaseColor[i], _celebrateColor, mix);
                light.intensity = _floodBaseIntensity[i] * (1f + mix * 0.9f);
            }

            if (_celebrate > 0f) return;

            // Exact restore - a drifting floodlight colour would accumulate over a match.
            for (int i = 0; i < _floodlights.Count; i++)
            {
                var light = _floodlights[i];
                if (light == null) continue;
                light.color = _floodBaseColor[i];
                light.intensity = _floodBaseIntensity[i];
            }
        }
    }
}
