using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// The pitch remembers the match: scuffs where players fought for grip, a
    /// worn line where the ball has rolled, mud in front of the goals.
    ///
    /// WHY THIS IS THE MOST HONEST EFFECT IN THE PRESENTATION LAYER. Every mark
    /// is a number the simulation already computed and threw away.
    /// Agent_Soccer.TractionSaturation is how much of the friction circle
    /// (mu * m * g) a player is currently spending; that is precisely the
    /// quantity real turf records, because turf tears when a boot asks for more
    /// grip than it can supply. So the darkest patches on the pitch at full time
    /// are where the hardest cuts happened, not where a designer expected them.
    ///
    /// IT IS A CPU TEXTURE, NOT A RENDER TARGET, and that is a deliberate
    /// downgrade. A RenderTexture with an additive brush blit is the "correct"
    /// implementation and it would need: a second camera or a CommandBuffer, a
    /// brush mesh, a platform-dependent format check, and a story for what
    /// happens on a graphics-device reset (contents lost, silently). A 128x128
    /// Color32 array stamped on the CPU and uploaded a few times a second costs
    /// ~16k pixel writes per upload, cannot be lost, works identically on every
    /// backend, and is trivially testable. At this resolution one texel is
    /// roughly 30 cm of pitch - finer than the marks are.
    ///
    /// COST CONTROL. Uploads are rate-limited (Texture2D.Apply is the expensive
    /// half, not the stamping) and skipped entirely when nothing changed. The
    /// quad is one extra transparent full-pitch draw, which is real overdraw on
    /// a phone - Agent_Quality switches it off first when the frame budget goes
    /// over.
    /// </summary>
    [DefaultExecutionOrder(-30)]
    public sealed class Agent_Wear : MonoBehaviour
    {
        // 128 is chosen against the pitch, not against a texture-size habit: the
        // training pitch is 36 x 54 m, so a texel is ~0.3 x 0.4 m. Doubling it
        // would quadruple the upload cost to record marks smaller than a boot.
        const int RES = 128;

        [Tooltip("Draw the accumulated wear over the pitch.")]
        [SerializeField] private bool _enableWear = true;
        [Tooltip("How dark a fully worn texel gets.")]
        [Range(0f, 1f)] [SerializeField] private float _opacity = 0.5f;
        [Tooltip("Wear added per second by a player at full traction saturation.")]
        [Range(0f, 4f)] [SerializeField] private float _scuffRate = 1.6f;
        [Tooltip("Wear added per second under a rolling ball.")]
        [Range(0f, 2f)] [SerializeField] private float _ballRate = 0.35f;
        [Tooltip("Traction saturation below which a player leaves no mark at all.")]
        [Range(0f, 1f)] [SerializeField] private float _scuffThreshold = 0.35f;
        [Tooltip("Uploads per second. The stamping is cheap; Apply() is not.")]
        [SerializeField] private float _uploadHz = 8f;
        [Tooltip("Seconds for a mark to fade completely. 0 = marks last the whole match.")]
        [SerializeField] private float _fadeSeconds = 90f;

        Agent_EnvController _env;
        Agent_Soccer[] _agents;
        Texture2D _texture;
        Color32[] _pixels;
        SpriteRenderer _renderer;
        Material _material;
        bool _dirty;
        float _uploadTimer;
        float _fadeTimer;

        /// <summary>Switched off by Agent_Quality under frame-budget pressure.</summary>
        public bool Visible
        {
            get => _renderer != null && _renderer.enabled;
            set { if (_renderer != null) _renderer.enabled = value; }
        }

        void Start()
        {
            var hud = FindFirstObjectByType<Agent_HUD>();
            var env = GetComponent<Agent_EnvController>();
            if (env == null) env = FindFirstObjectByType<Agent_EnvController>();

            // IsVisualScene, not IsMatchScene: this draws only its OWN pitch and
            // holds no global state, so the gallery's cloned pitches each keep
            // their own wear - which is the point, they are different matches.
            if (!_enableWear || env == null || !Agent_Presentation.IsVisualScene(hud))
            {
                enabled = false;
                return;
            }

            _env = env;
            _agents = env.GetComponentsInChildren<Agent_Soccer>();
            Build();
        }

        void Build()
        {
            _texture = new Texture2D(RES, RES, TextureFormat.RGBA32, false)
            {
                name = "PoSoccer_Wear",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };

            _pixels = new Color32[RES * RES];
            // Fully transparent, so an untouched pitch is exactly the pitch.
            for (int i = 0; i < _pixels.Length; i++) _pixels[i] = new Color32(0, 0, 0, 0);
            _texture.SetPixels32(_pixels);
            _texture.Apply(false);

            var go = new GameObject("PitchWear");
            go.transform.SetParent(_env.transform, false);

            _renderer = go.AddComponent<SpriteRenderer>();
            _renderer.sprite = Sprite.Create(
                _texture, new Rect(0, 0, RES, RES), new Vector2(0.5f, 0.5f),
                RES, 0, SpriteMeshType.FullRect);
            _renderer.sprite.name = "PoSoccer_WearSprite";
            _renderer.sprite.hideFlags = HideFlags.HideAndDontSave;

            // Dirt, not shadow: a brown-grey multiply over green turf. Alpha
            // carries the amount, so the tint stays constant and only coverage
            // grows - which is how a worn patch actually behaves.
            _renderer.color = new Color(0.28f, 0.22f, 0.14f, _opacity);

            // Unlit on purpose. The wear is part of the GROUND, and running it
            // through the 2D lit path would let a floodlight brighten a mud patch
            // more than the grass it sits on, which reads as a decal peeling off.
            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            if (shader != null)
            {
                _material = new Material(shader) { name = "PoSoccer_Wear" };
                _renderer.sharedMaterial = _material;
            }

            var pitch = _env.transform.Find("PitchBG");
            if (pitch != null && pitch.TryGetComponent(out SpriteRenderer pitchRenderer))
            {
                _renderer.sortingLayerID = pitchRenderer.sortingLayerID;
                _renderer.sortingOrder = pitchRenderer.sortingOrder + 1;
            }
            else
            {
                // Below the walls (0), which is where the ground is.
                _renderer.sortingOrder = -1;
            }

            Resize();
        }

        /// <summary>
        /// Sizes the quad to the pitch. Called on build and whenever the pitch
        /// changes size - Agent_PitchSizing rescales it per squad, so a fixed
        /// size would leave the wear hanging over the touchline at 1v1.
        /// </summary>
        void Resize()
        {
            Vector2 half = _env.PitchHalfExtents;
            _renderer.transform.localPosition = new Vector3(0f, 0f, 0.05f);
            _renderer.transform.localScale = new Vector3(half.x * 2f, half.y * 2f, 1f);
        }

        void Update()
        {
            if (_renderer == null || !_renderer.enabled) return;

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            Resize();
            StampPlayers(dt);
            StampBall(dt);
            Fade(dt);

            _uploadTimer += dt;
            float interval = 1f / Mathf.Max(1f, _uploadHz);
            if (_dirty && _uploadTimer >= interval)
            {
                _uploadTimer = 0f;
                _dirty = false;
                _texture.SetPixels32(_pixels);
                _texture.Apply(false);
            }
        }

        void StampPlayers(float dt)
        {
            if (_agents == null) return;

            for (int i = 0; i < _agents.Length; i++)
            {
                var agent = _agents[i];
                if (agent == null) continue;

                // The friction circle is the whole story: below the threshold the
                // boot is inside what the surface can supply and nothing tears.
                float grip = agent.TractionSaturation;
                if (grip <= _scuffThreshold) continue;

                float amount = (grip - _scuffThreshold) / Mathf.Max(0.01f, 1f - _scuffThreshold);
                Stamp(agent.transform.position, 2, amount * _scuffRate * dt);
            }
        }

        void StampBall(float dt)
        {
            if (_env.Ball == null) return;
            // A stationary ball does not wear anything; a rolling one does, and
            // faster means a longer mark per second rather than a darker one.
            float speed = _env.Ball.linearVelocity.magnitude;
            if (speed < 1f) return;
            Stamp(_env.Ball.position, 1, Mathf.Min(speed / 8f, 1f) * _ballRate * dt);
        }

        /// <summary>
        /// Adds wear in a square brush around a world point.
        ///
        /// Square rather than round, and radius measured in texels rather than
        /// metres: at 128 across the pitch a "radius 2" brush is already ~1 m,
        /// wider than anything it represents, so shaping the falloff buys
        /// nothing a bilinear filter does not give for free.
        /// </summary>
        void Stamp(Vector2 world, int radius, float amount)
        {
            if (amount <= 0f) return;

            Vector2 local = _env.transform.InverseTransformPoint(world);
            Vector2 half = _env.PitchHalfExtents;
            if (half.x <= 0.01f || half.y <= 0.01f) return;

            int cx = Mathf.RoundToInt((local.x / half.x * 0.5f + 0.5f) * (RES - 1));
            int cy = Mathf.RoundToInt((local.y / half.y * 0.5f + 0.5f) * (RES - 1));

            int add = Mathf.Clamp(Mathf.RoundToInt(amount * 255f), 1, 255);

            for (int y = cy - radius; y <= cy + radius; y++)
            {
                if (y < 0 || y >= RES) continue;
                int row = y * RES;
                for (int x = cx - radius; x <= cx + radius; x++)
                {
                    if (x < 0 || x >= RES) continue;
                    int index = row + x;
                    int alpha = _pixels[index].a + add;
                    _pixels[index] = new Color32(255, 255, 255, (byte)(alpha > 255 ? 255 : alpha));
                }
            }

            _dirty = true;
        }

        /// <summary>
        /// Grass grows back. Without this a long match saturates every texel the
        /// play ever crossed and the pitch ends up uniformly brown, which is both
        /// ugly and uninformative - the whole value of the effect is the
        /// CONTRAST between where the game was fought and where it was not.
        ///
        /// Applied as one decrement across the whole buffer at a low rate rather
        /// than per-texel timestamps: the fade only has to be slow and even.
        /// </summary>
        void Fade(float dt)
        {
            if (_fadeSeconds <= 0f) return;

            _fadeTimer += dt;
            // One step per second of wall clock; at the default 90 s lifetime
            // that is a decrement of about 3 per step out of 255.
            if (_fadeTimer < 1f) return;
            _fadeTimer = 0f;

            int step = Mathf.Max(1, Mathf.RoundToInt(255f / _fadeSeconds));
            bool changed = false;
            for (int i = 0; i < _pixels.Length; i++)
            {
                byte alpha = _pixels[i].a;
                if (alpha == 0) continue;
                _pixels[i].a = (byte)(alpha > step ? alpha - step : 0);
                changed = true;
            }
            if (changed) _dirty = true;
        }

        void OnDestroy()
        {
            if (_material != null) Destroy(_material);
            if (_texture != null) Destroy(_texture);
        }
    }
}
