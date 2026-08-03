using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Tight ball-following camera with a wide establishing shot at kickoff and
    /// after each goal. Wide baseline = the scene's authored orthoSize; tight
    /// overlay is roughly 1.5x zoom-in (~67% of orthoSize). Position lags
    /// the ball with critically-damped smoothing, clamped to the pitch so the
    /// edges never pan past the goal mouths.
    /// Execution order -50 so the camera is settled before MatchFX samples
    /// Camera.main on Start.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class Agent_CameraFollow : MonoBehaviour
    {
        [Tooltip("Start wide + zoom to tight delay after kickoff (seconds).")]
        [SerializeField] private float _kickoffHoldSeconds = 2.0f;
        [Tooltip("Wide linger after each goal (seconds).")]
        [SerializeField] private float _goalHoldSeconds = 1.5f;
        [Tooltip("Tight orthoSize = wide * (1 - zoomFraction). 0.33 = 1.5x zoom.")]
        [Range(0.05f, 0.6f)] [SerializeField] private float _zoomFraction = 0.33f;
        [Tooltip("Position lerp toward the ball (per second at 60 fps).")]
        [Range(2f, 12f)] [SerializeField] private float _followSpeed = 6f;
        [Tooltip("Half-margin added on top of pitchHalfExtents so the camera can " +
                 "pan slightly past the goal mouths before clamping at the touchline.")]
        [SerializeField] private float _edgeMargin = 0.5f;

        Camera _cam;
        Agent_EnvController _env;
        float _wideOrthoSize;
        float _tightOrthoSize;
        float _wideUntilTime;
        bool _wide;

        void Awake()
        {
            _cam = Camera.main;
            _env = FindFirstObjectByType<Agent_EnvController>();
            if (_cam == null) return;
            _wideOrthoSize = _cam.orthographicSize;
            _tightOrthoSize = _wideOrthoSize * (1f - _zoomFraction);
            _wide = true;
            _wideUntilTime = Time.time + _kickoffHoldSeconds;
            if (_env != null)
            {
                _env.EpisodeEnded += OnEpisodeEnded;
            }
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            _wide = true;
            _wideUntilTime = Time.time + _goalHoldSeconds;
        }

        void LateUpdate()
        {
            if (_cam == null || _env == null || _env.Ball == null) return;

            // Switch to tight once the hold expires.
            if (_wide && Time.time >= _wideUntilTime)
            {
                _wide = false;
            }

            float targetOrtho = _wide ? _wideOrthoSize : _tightOrthoSize;
            // Smooth the zoom so the cut from wide->tight reads as a deliberate move,
            // not a snap. t = 1 - exp(-k * dt) gives frame-rate independent lerp.
            float zoomK = _wide ? 4f : 5f;
            _cam.orthographicSize = Mathf.Lerp(_cam.orthographicSize, targetOrtho,
                1f - Mathf.Exp(-zoomK * Time.unscaledDeltaTime));

            // Position lag: follow the ball, clamped to the pitch plus the edge margin.
            Vector2 half = _env.PitchHalfExtents + Vector2.one * _edgeMargin;
            float halfH = _cam.orthographicSize;
            float halfW = halfH * _cam.aspect;
            Vector2 target = _env.Ball.position;
            target.x = Mathf.Clamp(target.x, -halfW, halfW);
            target.y = Mathf.Clamp(target.y, -halfH, halfH);

            Vector3 pos = _cam.transform.position;
            float t = 1f - Mathf.Exp(-_followSpeed * Time.unscaledDeltaTime);
            pos.x = Mathf.Lerp(pos.x, target.x, t);
            pos.y = Mathf.Lerp(pos.y, target.y, t);
            pos.z = _cam.transform.position.z; // never change camera depth
            _cam.transform.position = pos;
        }
    }
}
