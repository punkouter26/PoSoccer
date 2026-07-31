using System.Collections;
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

        Agent_EnvController _env;
        Camera _camera;
        Transform _ballVisual;
        Coroutine _squash;

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
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
            BallContact.Hit -= OnBallHit;
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

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (winner == null) return;
            Agent_Stadium.Instance?.PulseGoal();
            if (_camera != null) StartCoroutine(Shake(0.35f, 0.22f));
        }

        void OnBallHit(Collision2D collision)
        {
            float impact = collision.relativeVelocity.magnitude;
            if (impact > 4f && _ballVisual != null)
            {
                if (_squash != null) StopCoroutine(_squash);
                _squash = StartCoroutine(Squash(Mathf.Min(0.45f, impact * 0.03f)));
            }
        }

        IEnumerator Shake(float duration, float amplitude)
        {
            Vector3 basePos = _camera.transform.position;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float falloff = 1f - t / duration;
                _camera.transform.position = basePos +
                    (Vector3)(Random.insideUnitCircle * amplitude * falloff);
                yield return null;
            }
            _camera.transform.position = basePos;
        }

        IEnumerator Squash(float amount)
        {
            Vector3 baseScale = new(2f, 2f, 1f);
            float t = 0f;
            while (t < 0.18f)
            {
                t += Time.deltaTime;
                float k = Mathf.Sin(t / 0.18f * Mathf.PI) * amount;
                _ballVisual.localScale = new Vector3(
                    baseScale.x * (1f + k), baseScale.y * (1f - k), 1f);
                yield return null;
            }
            _ballVisual.localScale = baseScale;
            _squash = null;
        }
    }
}
