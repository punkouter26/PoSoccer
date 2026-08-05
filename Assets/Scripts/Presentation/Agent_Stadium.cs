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

        Bloom _bloom;
        ChromaticAberration _chroma;
        float _pulse;

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
        }

        void BuildPostVolume()
        {
            var go = new GameObject("PostFX");
            go.transform.SetParent(transform, false);
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _bloom = profile.Add<Bloom>();
            _bloom.intensity.Override(0.9f);
            _bloom.threshold.Override(0.9f);
            _chroma = profile.Add<ChromaticAberration>();
            _chroma.intensity.Override(0f);
            volume.profile = profile;
        }

        /// <summary>Goal-moment kick: bloom flash + chromatic aberration pulse.</summary>
        public void PulseGoal() => _pulse = 1f;

        void Update()
        {
            if (_pulse <= 0f || _bloom == null) return;
            _pulse = Mathf.Max(0f, _pulse - Time.unscaledDeltaTime * 1.6f);
            _bloom.intensity.Override(0.9f + _pulse * 1.6f);
            _chroma.intensity.Override(_pulse * 0.45f);
        }
    }
}
