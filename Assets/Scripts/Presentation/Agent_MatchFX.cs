using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Match juice, all built at runtime: ball speed trail, camera shake on
    /// goals, and a ball squash on hard contacts. Sits on the Pitch root.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_MatchFX : MonoBehaviour
    {
        /// <summary>Relays the ball's collisions to FX/audio without polling.</summary>
        public sealed class BallContact : MonoBehaviour
        {
            public static event System.Action<Collision2D> Hit;
            void OnCollisionEnter2D(Collision2D collision) => Hit?.Invoke(collision);
        }

        [Tooltip("Round sprite used for boost exhaust particles (e.g. the ball sprite).")]
        public Sprite particleSprite;

        Agent_EnvController _env;
        Camera _camera;
        Transform _ballVisual;
        CancellationTokenSource _squashCts;
        readonly System.Collections.Generic.List<(Agent_Soccer agent, ParticleSystem ps)> _boost = new();

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            _camera = Camera.main;
            _env.EpisodeEnded += OnEpisodeEnded;
            BallContact.Hit += OnBallHit;

            if (_env.Ball != null)
            {
                if (_env.Ball.GetComponent<BallContact>() == null)
                    _env.Ball.gameObject.AddComponent<BallContact>();
                BuildTrail(_env.Ball.transform);
                _ballVisual = _env.Ball.transform.Find("BallVisual");
            }

            foreach (var agent in _env.agents)
                if (agent != null)
                    _boost.Add((agent, BuildBoostExhaust(agent)));
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
            BallContact.Hit -= OnBallHit;
            _squashCts?.Cancel();
            _squashCts?.Dispose();
            _squashCts = null;
        }

        static void BuildTrail(Transform ball)
        {
            if (ball.GetComponent<TrailRenderer>() != null) return;
            var trail = ball.gameObject.AddComponent<TrailRenderer>();
            trail.time = 0.25f;
            trail.startWidth = 0.16f;
            trail.endWidth = 0f;
            trail.numCapVertices = 4;
            var shader = Shader.Find("Sprites/Default");
            if (shader != null) trail.material = new Material(shader);
            trail.startColor = new Color(1f, 1f, 1f, 0.55f);
            trail.endColor = new Color(1f, 1f, 1f, 0f);
            trail.sortingOrder = 1;
        }

        ParticleSystem BuildBoostExhaust(Agent_Soccer agent)
        {
            var go = new GameObject("BoostExhaust");
            go.transform.SetParent(agent.transform, false);
            go.transform.localPosition = new Vector3(0f, -0.55f, 0f);
            go.transform.localRotation = Quaternion.Euler(0f, 0f, 180f);

            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.startLifetime = 0.25f;
            main.startSpeed = 2.2f;
            main.startSize = 0.11f;
            main.startColor = agent.rewards != null ? agent.rewards.playerColor : Color.white;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 10f;
            // Thrust look: shrink and fade over life instead of popping out.
            var sizeOverLife = ps.sizeOverLifetime;
            sizeOverLife.enabled = true;
            sizeOverLife.size = new ParticleSystem.MinMaxCurve(
                1f, AnimationCurve.Linear(0f, 1f, 1f, 0.1f));
            var colorOverLife = ps.colorOverLifetime;
            colorOverLife.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0f, 1f) });
            colorOverLife.color = gradient;

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            var shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                renderer.material = new Material(shader);
                if (particleSprite != null)
                    renderer.material.mainTexture = particleSprite.texture;
            }
            renderer.sortingOrder = 1;
            return ps;
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            // The kickoff teleport would otherwise smear the trail across the pitch.
            ClearTrailAfterResetAsync(this.GetCancellationTokenOnDestroy()).Forget();

            if (winner == null) return;
            Agent_Stadium.Instance?.PulseGoal();
            if (_camera != null) ShakeAsync(0.35f, 0.22f, this.GetCancellationTokenOnDestroy()).Forget();
        }

        async UniTaskVoid ClearTrailAfterResetAsync(CancellationToken token)
        {
            var trail = _env.Ball != null ? _env.Ball.GetComponent<TrailRenderer>() : null;
            if (trail == null) return;
            trail.emitting = false;
            await UniTask.WaitForFixedUpdate(token);
            await UniTask.WaitForFixedUpdate(token);
            if (trail == null) return;
            trail.Clear();
            trail.emitting = true;
        }

        void Update()
        {
            foreach (var (agent, ps) in _boost)
            {
                if (agent == null || ps == null) continue;
                var emission = ps.emission;
                emission.rateOverTime = agent.IsBoosting ? 45f : 0f;
            }
        }

        void OnBallHit(Collision2D collision)
        {
            float impact = collision.relativeVelocity.magnitude;
            if (impact > 4f && _ballVisual != null)
            {
                // A new hard contact restarts the squash: cancel the in-flight one.
                _squashCts?.Cancel();
                _squashCts?.Dispose();
                _squashCts = CancellationTokenSource.CreateLinkedTokenSource(
                    this.GetCancellationTokenOnDestroy());
                SquashAsync(Mathf.Min(0.45f, impact * 0.03f), _squashCts.Token).Forget();
            }
        }

        async UniTaskVoid ShakeAsync(float duration, float amplitude, CancellationToken token)
        {
            Vector3 basePos = _camera.transform.position;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float falloff = 1f - t / duration;
                _camera.transform.position = basePos +
                    (Vector3)(Random.insideUnitCircle * amplitude * falloff);
                await UniTask.Yield(PlayerLoopTiming.Update, token);
            }
            if (_camera != null) _camera.transform.position = basePos;
        }

        async UniTaskVoid SquashAsync(float amount, CancellationToken token)
        {
            Vector3 baseScale = new(2f, 2f, 1f);
            float t = 0f;
            while (t < 0.18f)
            {
                t += Time.deltaTime;
                float k = Mathf.Sin(t / 0.18f * Mathf.PI) * amount;
                _ballVisual.localScale = new Vector3(
                    baseScale.x * (1f + k), baseScale.y * (1f - k), 1f);
                await UniTask.Yield(PlayerLoopTiming.Update, token);
            }
            if (_ballVisual != null) _ballVisual.localScale = baseScale;
        }
    }
}
