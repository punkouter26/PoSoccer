using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Impact effects drawn over the whole view: a goal flash in the scoring
    /// team's colour, and a shockwave ring expanding from where the ball was.
    ///
    /// READ THIS BEFORE ADDING A "PROPER" POST EFFECT HERE. This was first built
    /// as a URP fullscreen pass - refraction on the shockwave, a radial speed
    /// smear, and a tilt-shift focus band - because those need to READ the frame,
    /// which geometry cannot do. It was then MEASURED not to work on this
    /// project's renderer, and the measurements are worth more than the code was:
    ///
    ///   - the feature was correctly registered (an EditMode test read
    ///     Renderer2D.asset's rendererFeatures list and found it, with its
    ///     material and shader resolved);
    ///   - URP's own FullScreenPassRendererFeature at AfterRenderingPostProcessing
    ///     drew PURE WHITE - it reached the screen, so it was enqueued and active,
    ///     but `_BlitTexture` sampled as unbound;
    ///   - at BeforeRenderingPostProcessing it had no effect at all;
    ///   - a hand-written ScriptableRendererFeature doing the canonical Unity 6
    ///     blit-and-swap (read activeColorTexture, blit through the material,
    ///     assign resources.cameraColor) had no effect either, at either event;
    ///   - nor did the same pass derived from ScriptableRenderPass2D with
    ///     RenderPassEvent2D.AfterRenderingPostProcessing, which is the 2D
    ///     renderer's own injection enum;
    ///   - none of it changed after a full editor restart, which rules out a
    ///     cached renderer instance.
    ///
    /// Every one of those was confirmed with a PROBE COMPILED INTO THE SHADER -
    /// return the frame multiplied by a red tint - rather than by looking for a
    /// subtle effect, because "the effect is subtle" and "the pass never ran"
    /// look identical from the outside. That is the same trap that produced this
    /// project's phase-10 retraction, and it is why the dead code was deleted
    /// rather than left in place looking plausible.
    ///
    /// SO WHAT IS LOST, PLAINLY: refraction, the radial speed smear, and the
    /// tilt-shift focus band. All three need the frame as an input texture.
    /// What survives is what geometry can do honestly - a coloured flash and an
    /// expanding ring - and those are the two that carry the goal moment anyway.
    ///
    /// Both overlays are ONE SpriteRenderer each, parented to the camera (flash)
    /// and to the pitch (ring), created on demand and left disabled between
    /// goals, so the resting cost is zero draw calls rather than two.
    /// </summary>
    [DefaultExecutionOrder(60)]
    public sealed class Agent_ScreenFX : MonoBehaviour
    {
        [Header("Goal flash")]
        [Tooltip("Peak opacity of the full-view colour flash on a goal.")]
        [Range(0f, 1f)] [SerializeField] private float _flashStrength = 0.35f;
        [Tooltip("Seconds for the flash to fade out.")]
        [SerializeField] private float _flashSeconds = 0.55f;

        [Header("Shockwave")]
        [Tooltip("Peak opacity of the expanding ring.")]
        [Range(0f, 1f)] [SerializeField] private float _ringStrength = 0.7f;
        [Tooltip("Seconds for the ring to expand and vanish.")]
        [SerializeField] private float _ringSeconds = 0.6f;
        [Tooltip("Final ring diameter in pitch metres.")]
        [SerializeField] private float _ringDiameter = 22f;
        [Tooltip("Overlays draw above every gameplay sprite.")]
        [SerializeField] private int _sortingOrder = 900;

        Agent_EnvController _env;
        Camera _camera;
        SpriteRenderer _flash;
        SpriteRenderer _ring;
        Material _material;

        float _flashAge = -1f;
        float _ringAge = -1f;
        Color _tint = Color.white;

        /// <summary>
        /// Set false by Agent_Quality when the frame budget is breached. These
        /// overlays are cheap and brief, but they are also the least load-bearing
        /// thing on screen, so they are the first to go.
        /// </summary>
        public bool AllowContinuous { get; set; } = true;

        void Start()
        {
            var hud = FindFirstObjectByType<Agent_HUD>();
            // IsMatchScene, not IsVisualScene: the flash covers the CAMERA, and
            // the gallery's six pitches share one.
            if (!Agent_Presentation.IsMatchScene(hud))
            {
                enabled = false;
                return;
            }

            _env = GetComponent<Agent_EnvController>();
            if (_env == null) _env = FindFirstObjectByType<Agent_EnvController>();
            _camera = Camera.main;

            if (_env != null) _env.EpisodeEnded += OnEpisodeEnded;
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
            if (_material != null) Destroy(_material);
        }

        void OnDisable()
        {
            Hide(_flash);
            Hide(_ring);
        }

        /// <summary>
        /// A goal: flash in the scoring team's colour, ring from where the ball is.
        ///
        /// The ball's position is the honest origin. EpisodeEnded fires BEFORE
        /// ResetPitch (documented in Agent_EnvController), so the ball is still
        /// sitting in the goal that just conceded - the same fact Agent_Surfaces
        /// uses to decide which net to shake.
        /// </summary>
        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (winner == null || !AllowContinuous) return;

            _tint = Agent_Palette.Team(winner.Value);
            _flashAge = 0f;
            _ringAge = 0f;

            EnsureOverlays();
            if (_ring != null && _env != null && _env.Ball != null)
            {
                Vector3 ball = _env.Ball.position;
                _ring.transform.position = new Vector3(ball.x, ball.y, -0.5f);
            }
        }

        void EnsureOverlays()
        {
            if (_material == null)
            {
                // Unlit: these are light sources in their own right, and running
                // them through the 2D lit path would let the stadium's own
                // floodlights dim a goal flash.
                var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
                if (shader != null) _material = new Material(shader) { name = "PoSoccer_ScreenFX" };
            }

            if (_flash == null && _camera != null)
            {
                var go = new GameObject("GoalFlash");
                go.transform.SetParent(_camera.transform, false);
                // Just in front of the camera plane; for an orthographic camera
                // any positive z inside the clip range renders identically.
                go.transform.localPosition = new Vector3(0f, 0f, 1f);

                _flash = go.AddComponent<SpriteRenderer>();
                _flash.sprite = Agent_Art.FullRect(1f);
                if (_material != null) _flash.sharedMaterial = _material;
                _flash.sortingOrder = _sortingOrder;
                _flash.enabled = false;
            }

            if (_ring == null && _env != null)
            {
                var go = new GameObject("GoalShockwave");
                go.transform.SetParent(_env.transform, false);

                _ring = go.AddComponent<SpriteRenderer>();
                // A disc with a hole in it IS the shockwave: the ring is the
                // front, and everything behind it has already passed.
                _ring.sprite = Agent_Art.Disc(1f, 0.78f);
                if (_material != null) _ring.sharedMaterial = _material;
                _ring.sortingOrder = _sortingOrder - 1;
                _ring.enabled = false;
            }
        }

        void Update()
        {
            // unscaledDeltaTime: a goal freezes the clock for the replay, and an
            // effect measured in scaled time would hang on screen at whatever
            // frame the freeze caught it.
            float dt = Time.unscaledDeltaTime;

            TickFlash(dt);
            TickRing(dt);
        }

        void TickFlash(float dt)
        {
            if (_flashAge < 0f || _flash == null) return;

            _flashAge += dt;
            float progress = _flashAge / Mathf.Max(0.05f, _flashSeconds);
            if (progress >= 1f)
            {
                _flashAge = -1f;
                Hide(_flash);
                return;
            }

            // Cover exactly the viewport, re-measured each frame: the camera
            // zooms with the pitch (Agent_PitchSizing) and the aspect changes
            // with the device, so a size fixed at build time is wrong on the
            // second frame of half the sessions.
            if (_camera != null && _camera.orthographic)
            {
                float height = _camera.orthographicSize * 2f;
                _flash.transform.localScale = new Vector3(height * _camera.aspect * 1.1f, height * 1.1f, 1f);
            }

            // A spike, not a fade: the light of a goal arrives all at once and
            // leaves slowly, which a linear ramp gets exactly backwards.
            float alpha = Mathf.Pow(1f - progress, 3f) * _flashStrength;
            _flash.color = new Color(_tint.r, _tint.g, _tint.b, alpha);
            _flash.enabled = true;
        }

        void TickRing(float dt)
        {
            if (_ringAge < 0f || _ring == null) return;

            _ringAge += dt;
            float progress = _ringAge / Mathf.Max(0.05f, _ringSeconds);
            if (progress >= 1f)
            {
                _ringAge = -1f;
                Hide(_ring);
                return;
            }

            // Expands fast and decelerates - a wave front loses energy to the
            // medium, and a linear expansion reads as an animation rather than
            // as a release.
            float eased = 1f - Mathf.Pow(1f - progress, 2.2f);
            float diameter = eased * _ringDiameter;
            _ring.transform.localScale = new Vector3(diameter, diameter, 1f);

            float alpha = (1f - progress) * (1f - progress) * _ringStrength;
            _ring.color = new Color(_tint.r, _tint.g, _tint.b, alpha);
            _ring.enabled = true;
        }

        static void Hide(SpriteRenderer renderer)
        {
            if (renderer != null) renderer.enabled = false;
        }

        /// <summary>
        /// Fires a shockwave at a world point. Public so contact FX can call it -
        /// Agent_ImpactFX owns the "something hit something" signal and there is
        /// no reason for two components to re-derive it.
        /// </summary>
        public void Impact(Vector2 world, float strength)
        {
            if (!enabled || !AllowContinuous || _ringStrength <= 0f) return;
            // A wave already in flight is not restarted by a later one in its
            // first third: the ring would jump backwards, which reads as a glitch
            // rather than as a second hit.
            if (_ringAge >= 0f && _ringAge < _ringSeconds * 0.35f) return;

            EnsureOverlays();
            if (_ring == null) return;

            _tint = Color.white;
            _ringAge = 0f;
            _ring.transform.position = new Vector3(world.x, world.y, -0.5f);
            _ring.transform.localScale = Vector3.zero;
        }
    }
}
