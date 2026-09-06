using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// The particle layer. Before this, the entire project contained exactly one
    /// ParticleSystem - the boost exhaust in Agent_MatchFX - and zero in any
    /// scene.
    ///
    /// Adds four effects, each driven by something the simulation already
    /// computes rather than by a timer:
    ///  - turf spray, from the lateral slip the traction model already produces,
    ///    so it appears exactly when a player is actually skidding;
    ///  - impact debris, scaled by Collision2D.relativeVelocity;
    ///  - a shockwave ring on the corner-escape wall kick, a real mechanic that
    ///    until now rendered as nothing at all;
    ///  - goal confetti.
    ///
    /// Deliberately a small number of SHARED systems emitted into with
    /// EmitParams, not one system per agent: the emit call carries position and
    /// velocity, so four systems cover the whole pitch at any squad size and the
    /// draw-call count does not scale with players.
    ///
    /// Presentation only. Self-disables in training and evaluation.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_ParticleFX : MonoBehaviour
    {
        [Tooltip("Lateral slip (m/s) at which a player starts throwing up turf.")]
        [SerializeField] private float _slipThreshold = 2.2f;
        [Tooltip("Turf particles emitted per second at full slip.")]
        [SerializeField] private float _turfRate = 45f;
        [Tooltip("Impact speed (m/s) below which no debris is emitted.")]
        [SerializeField] private float _impactThreshold = 5f;
        [SerializeField] private bool _enableParticles = true;

        Agent_EnvController _env;
        ParticleSystem _turf, _debris, _confetti;
        Transform _ringPool;
        readonly System.Collections.Generic.List<SpriteRenderer> _rings = new();
        int _nextRing;
        float _turfCredit;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            var hud = FindFirstObjectByType<Agent_HUD>();
            if (!_enableParticles || !Agent_Presentation.IsVisualScene(hud))
            {
                enabled = false;
                return;
            }

            _turf = BuildSystem("TurfSpray", new Color(0.35f, 0.62f, 0.28f), 0.10f, 0.55f, 260);
            _debris = BuildSystem("ImpactDebris", new Color(0.85f, 0.88f, 0.8f), 0.08f, 0.4f, 200);
            _confetti = BuildConfetti();
            BuildRingPool();

            _env.EpisodeEnded += OnEpisodeEnded;
            Agent_MatchFX.BallContact.Hit += OnBallHit;
            for (int i = 0; i < _env.agents.Count; i++)
                if (_env.agents[i] != null) _env.agents[i].WallKicked += OnWallKicked;
        }

        void OnDestroy()
        {
            if (_env != null)
            {
                _env.EpisodeEnded -= OnEpisodeEnded;
                for (int i = 0; i < _env.agents.Count; i++)
                    if (_env.agents[i] != null) _env.agents[i].WallKicked -= OnWallKicked;
            }
            Agent_MatchFX.BallContact.Hit -= OnBallHit;
        }

        // -- Construction ----------------------------------------------------

        ParticleSystem BuildSystem(string label, Color tint, float size, float life, int max)
        {
            var go = new GameObject(label);
            go.transform.SetParent(transform, false);

            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.startLifetime = life;
            main.startSize = size;
            main.startSpeed = 0f;               // velocity comes from EmitParams
            main.startColor = tint;
            main.maxParticles = max;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 0f;          // top-down: nothing falls in-plane

            var emission = ps.emission;
            emission.enabled = false;           // emission is entirely manual

            var shape = ps.shape;
            shape.enabled = false;

            // Drag makes debris settle instead of sliding forever on a frictionless plane.
            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.dampen = 0.12f;
            limit.limit = 0.5f;

            var sizeOverLife = ps.sizeOverLifetime;
            sizeOverLife.enabled = true;
            sizeOverLife.size = new ParticleSystem.MinMaxCurve(
                1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));

            var colorOverLife = ps.colorOverLifetime;
            colorOverLife.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            colorOverLife.color = gradient;

            SetupRenderer(ps, 5);
            return ps;
        }

        ParticleSystem BuildConfetti()
        {
            var go = new GameObject("Confetti");
            go.transform.SetParent(transform, false);

            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.startLifetime = 2.6f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.10f, 0.22f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2f, 7f);
            main.maxParticles = 400;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            // Unscaled: the goal celebration plays while Agent_TimeFreeze holds
            // the clock at zero, and scaled particles would simply stand still.
            main.useUnscaledTime = true;

            var emission = ps.emission;
            emission.enabled = false;

            var shape = ps.shape;
            shape.enabled = false;

            var rotation = ps.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(-3f, 3f);

            var colorOverLife = ps.colorOverLifetime;
            colorOverLife.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.7f),
                        new GradientAlphaKey(0f, 1f) });
            colorOverLife.color = gradient;

            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.dampen = 0.06f;
            limit.limit = 1.2f;

            SetupRenderer(ps, 20);
            return ps;
        }

        static void SetupRenderer(ParticleSystem ps, int sortingOrder)
        {
            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            var shader = Shader.Find("Sprites/Default");
            if (shader != null) renderer.material = new Material(shader);
            renderer.sortingOrder = sortingOrder;
        }

        /// <summary>
        /// Expanding rings are a sprite, not particles: one quad scaling and
        /// fading reads as a clean shockwave, where a particle ring needs dozens
        /// of billboards to look like a circle at all.
        /// </summary>
        void BuildRingPool()
        {
            var go = new GameObject("Shockwaves");
            go.transform.SetParent(transform, false);
            _ringPool = go.transform;

            for (int i = 0; i < 4; i++)
            {
                var ringGo = new GameObject($"Ring_{i}");
                ringGo.transform.SetParent(_ringPool, false);
                var renderer = ringGo.AddComponent<SpriteRenderer>();
                renderer.sprite = Agent_Art.Disc(1f, 0.78f);
                renderer.sortingOrder = 6;
                renderer.enabled = false;
                _rings.Add(renderer);
            }
        }

        // -- Emission --------------------------------------------------------

        void Update()
        {
            if (_turf == null || _env == null) return;

            // Turf spray from real lateral slip: the component of velocity across
            // the body's facing. The traction model already produces this; we are
            // only drawing what the physics is doing.
            var agents = _env.agents;
            for (int i = 0; i < agents.Count; i++)
            {
                var agent = agents[i];
                if (agent == null || agent.Body == null) continue;

                Vector2 velocity = agent.Body.linearVelocity;
                if (velocity.sqrMagnitude < 1f) continue;

                Vector2 facing = agent.transform.up;
                Vector2 side = new(-facing.y, facing.x);
                float slip = Mathf.Abs(Vector2.Dot(velocity, side));
                if (slip < _slipThreshold) continue;

                float strength = Mathf.Clamp01((slip - _slipThreshold) / 4f);
                _turfCredit += strength * _turfRate * Time.deltaTime;
                int count = Mathf.FloorToInt(_turfCredit);
                if (count <= 0) continue;
                _turfCredit -= count;

                Vector2 skidDirection = -velocity.normalized;
                for (int p = 0; p < count; p++)
                {
                    var emit = new ParticleSystem.EmitParams
                    {
                        position = agent.transform.position
                                   + (Vector3)(Random.insideUnitCircle * 0.35f),
                        velocity = skidDirection * Random.Range(1.5f, 4f)
                                   + Random.insideUnitCircle * 1.2f,
                        startLifetime = Random.Range(0.3f, 0.7f),
                    };
                    _turf.Emit(emit, 1);
                }
            }
        }

        void OnBallHit(Collision2D collision)
        {
            if (_debris == null) return;
            float impact = collision.relativeVelocity.magnitude;
            if (impact < _impactThreshold) return;

            Vector3 at = collision.contactCount > 0
                ? (Vector3)collision.GetContact(0).point
                : collision.transform.position;
            Vector2 normal = collision.contactCount > 0
                ? collision.GetContact(0).normal
                : Vector2.up;

            int count = Mathf.Clamp(Mathf.RoundToInt(impact * 0.8f), 3, 18);
            for (int i = 0; i < count; i++)
            {
                var emit = new ParticleSystem.EmitParams
                {
                    position = at,
                    velocity = (normal + Random.insideUnitCircle * 0.9f).normalized
                               * Random.Range(1.5f, impact * 0.35f),
                    startLifetime = Random.Range(0.2f, 0.45f),
                };
                _debris.Emit(emit, 1);
            }
        }

        void OnWallKicked(Vector2 position, Vector2 direction)
        {
            var ring = NextRing();
            if (ring == null) return;
            RingAsync(ring, position, this.GetCancellationTokenOnDestroy()).Forget();
        }

        SpriteRenderer NextRing()
        {
            if (_rings.Count == 0) return null;
            var ring = _rings[_nextRing];
            _nextRing = (_nextRing + 1) % _rings.Count;
            return ring;
        }

        async UniTaskVoid RingAsync(SpriteRenderer ring, Vector2 position, CancellationToken token)
        {
            ring.transform.position = position;
            ring.enabled = true;

            const float DURATION = 0.4f;
            float t = 0f;
            while (t < DURATION)
            {
                t += Time.deltaTime;
                float k = t / DURATION;
                ring.transform.localScale = Vector3.one * Mathf.Lerp(0.4f, 3.2f, k);
                ring.color = new Color(1f, 0.95f, 0.7f, 1f - k);
                await UniTask.Yield(PlayerLoopTiming.Update, token);
            }
            if (ring != null) ring.enabled = false;
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (winner == null || _confetti == null) return;

            Color team = Agent_SoccerView.TeamColor(winner.Value);
            Vector2 half = _env.PitchHalfExtents;
            Vector3 origin = _env.GetGoalPosition(Agent_Soccer.Opponent(winner.Value));

            for (int i = 0; i < 220; i++)
            {
                // Half in team colour, half white, so the burst reads as team
                // celebration rather than a coloured smear.
                Color tint = i % 2 == 0 ? team : Color.white;
                var emit = new ParticleSystem.EmitParams
                {
                    position = origin + new Vector3(Random.Range(-half.x, half.x) * 0.5f,
                                                    Random.Range(-1f, 1f), 0f),
                    velocity = Random.insideUnitCircle.normalized * Random.Range(3f, 9f),
                    startColor = tint,
                    startLifetime = Random.Range(1.6f, 3f),
                };
                _confetti.Emit(emit, 1);
            }
        }
    }
}
