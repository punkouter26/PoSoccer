using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Ball-following camera that frames the pitch to the actual viewport and tightens
    /// on the action when play is slow.
    ///
    /// Three things drive the zoom, widest wins:
    ///  - a wide establishing shot at kickoff and after each goal;
    ///  - the bounding box of the ball plus every player, so a scrum in one corner
    ///    fills the screen while a stretched counter-attack pulls back;
    ///  - ball speed, which biases wider so a fast break does not outrun the frame.
    ///
    /// The wide baseline is DERIVED, not authored: it is the smallest orthographic size
    /// that still shows the whole pitch at the current aspect. On a 9:16 phone the pitch
    /// is narrower than the screen, so this resolves to "fit the pitch height", filling
    /// the portrait viewport top to bottom instead of leaving the authored letterbox.
    /// Recomputed every frame so Agent_PitchSizing resizing the pitch per squad size (and
    /// any resolution or orientation change) is picked up for free.
    ///
    /// Execution order -50 so the camera is settled before MatchFX samples Camera.main.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class Agent_CameraFollow : MonoBehaviour
    {
        [Tooltip("Start wide + zoom to tight delay after kickoff (seconds).")]
        [SerializeField] private float _kickoffHoldSeconds = 2.0f;
        [Tooltip("Wide linger after each goal (seconds).")]
        [SerializeField] private float _goalHoldSeconds = 1.5f;
        [Tooltip("Tightest allowed framing as a fraction of the wide shot. " +
                 "0.45 = the camera may zoom to 2.2x when play is slow and compact.")]
        [Range(0.25f, 1f)] [SerializeField] private float _tightestFraction = 0.45f;
        [Tooltip("World units of breathing room kept around the ball and players.")]
        [SerializeField] private float _actionPadding = 3.0f;
        [Tooltip("Ball speed (m/s) at which the camera is pulled fully back to the wide " +
                 "shot. Below this it interpolates toward the tight action framing.")]
        [SerializeField] private float _speedForWide = 7.0f;
        [Tooltip("Position lerp toward the ball (per second at 60 fps).")]
        [Range(2f, 12f)] [SerializeField] private float _followSpeed = 6f;
        [Tooltip("Extra world units the view may show beyond the touchline.")]
        [SerializeField] private float _edgeMargin = 0.5f;
        [Tooltip("Fraction of the pitch WIDTH the wide shot may crop on very tall screens, " +
                 "so the pitch keeps filling the height instead of shrinking. The camera pans " +
                 "horizontally to cover whatever is cropped.")]
        [Range(0f, 0.35f)] [SerializeField] private float _maxWidthCrop = 0.14f;

        Camera _cam;
        Agent_EnvController _env;
        float _wideUntilTime;
        bool _wide;
        Transform _overrideTarget;
        float _overrideOrtho;

        /// <summary>
        /// Point the camera at something other than the live ball, at a fixed
        /// framing. Used by <see cref="Agent_Replay"/>: a goal replay plays back
        /// ghosts near the goal mouth while the real ball has ALREADY been reset
        /// to the centre spot by Agent_EnvController.ResetPitch, so without an
        /// override the camera would pan away from the very thing it is showing.
        /// The pitch pan clamp still applies, so an override can never reveal the
        /// void outside the touchline. Pass null (or call
        /// <see cref="ClearOverrideTarget"/>) to hand the camera back to the ball.
        /// </summary>
        public void SetOverrideTarget(Transform target, float orthoSize)
        {
            _overrideTarget = target;
            _overrideOrtho = Mathf.Max(1f, orthoSize);
        }

        public void ClearOverrideTarget() => _overrideTarget = null;

        /// <summary>Wide baseline for the current pitch and aspect - the caller's zoom reference.</summary>
        public float CurrentWideOrtho
        {
            get
            {
                if (_cam == null || _env == null) return 10f;
                return PitchWideOrthoSize(_env.PitchHalfExtents + Vector2.one * _edgeMargin);
            }
        }

        void Awake()
        {
            _cam = Camera.main;
            _env = FindFirstObjectByType<Agent_EnvController>();
            _wide = true;
            _wideUntilTime = Time.time + _kickoffHoldSeconds;
            if (_env != null) _env.EpisodeEnded += OnEpisodeEnded;
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

        /// <summary>
        /// Smallest orthographic size showing the whole of <paramref name="half"/> at the
        /// current aspect. orthographicSize is the HALF height, so the width constraint has
        /// to be divided by aspect before comparing. Max of the two = nothing gets cropped.
        /// Used for the action framing, where cropping would hide a player.
        /// </summary>
        float FitOrthoSize(Vector2 half)
        {
            float aspect = _cam.aspect > 0.01f ? _cam.aspect : 0.5625f;   // 9:16 fallback
            return Mathf.Max(half.y, half.x / aspect);
        }

        /// <summary>
        /// The wide establishing shot, which is allowed to crop the pitch's WIDTH.
        ///
        /// A pure fit was measured to frame badly on modern phones. It resolves to
        /// max(halfY, halfX / aspect), and at 18:9 and taller the width term wins, so the
        /// camera pulls back and the pitch shrinks - on a 20:9 screen that left ~2.5 world
        /// units of dead ground past each goal line and the pitch filling only ~84% of the
        /// height. The taller the phone, the worse it got, which is exactly backwards.
        ///
        /// Cropping width instead is safe here in a way cropping height would not be: the
        /// goal mouths are on the Y ends, the crop is symmetric about the centre line, and
        /// the pan clamp below already tracks the ball horizontally whenever the view is
        /// narrower than the pitch. So the tall-screen case degrades into a tracking shot
        /// rather than a letterbox.
        /// </summary>
        float PitchWideOrthoSize(Vector2 half)
        {
            float aspect = _cam.aspect > 0.01f ? _cam.aspect : 0.5625f;
            float heightFit = half.y;
            float widthFit = half.x * (1f - _maxWidthCrop) / aspect;
            return Mathf.Max(heightFit, widthFit);
        }

        void LateUpdate()
        {
            if (_cam == null || _env == null || _env.Ball == null) return;

            if (_wide && Time.time >= _wideUntilTime) _wide = false;

            Vector2 pitchHalf = _env.PitchHalfExtents + Vector2.one * _edgeMargin;
            float wideOrtho = PitchWideOrthoSize(pitchHalf);
            float tightestOrtho = wideOrtho * _tightestFraction;

            // The replay override retargets the camera; everything downstream
            // (smoothing, pan clamp) is shared with the live shot.
            bool overriding = _overrideTarget != null;
            Vector2 ballPos = overriding
                ? (Vector2)_overrideTarget.position
                : _env.Ball.position;
            float targetOrtho;

            if (overriding)
            {
                targetOrtho = Mathf.Clamp(_overrideOrtho, wideOrtho * 0.18f, wideOrtho);
            }
            else if (_wide)
            {
                targetOrtho = wideOrtho;
            }
            else
            {
                // Frame everything that matters: the ball and every player. Manual loop
                // rather than LINQ - this runs every frame (performance.md).
                float minX = ballPos.x, maxX = ballPos.x;
                float minY = ballPos.y, maxY = ballPos.y;
                var agents = _env.agents;
                for (int i = 0; i < agents.Count; i++)
                {
                    var a = agents[i];
                    if (a == null || a.Body == null) continue;
                    Vector2 p = a.Body.position;
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.y < minY) minY = p.y;
                    if (p.y > maxY) maxY = p.y;
                }

                // Half-extents of the action, measured from the camera's focus point (the
                // ball) so the framing stays ball-centred rather than drifting to the
                // bounding-box centre, which would fight the position follow below.
                float actionHalfX = Mathf.Max(Mathf.Abs(maxX - ballPos.x),
                                              Mathf.Abs(ballPos.x - minX)) + _actionPadding;
                float actionHalfY = Mathf.Max(Mathf.Abs(maxY - ballPos.y),
                                              Mathf.Abs(ballPos.y - minY)) + _actionPadding;
                float actionOrtho = FitOrthoSize(new Vector2(actionHalfX, actionHalfY));

                // A fast ball needs more lead room; a slow one means a scrum worth
                // filling the screen with.
                float speed01 = Mathf.Clamp01(_env.Ball.linearVelocity.magnitude / _speedForWide);
                targetOrtho = Mathf.Lerp(actionOrtho, wideOrtho, speed01);
                targetOrtho = Mathf.Clamp(targetOrtho, tightestOrtho, wideOrtho);
            }

            // Frame-rate independent smoothing; easing out of the wide shot is a shade
            // slower than easing into it so goal replays do not snap.
            float zoomK = overriding ? 7f : (_wide ? 4f : 3f);
            _cam.orthographicSize = Mathf.Lerp(_cam.orthographicSize, targetOrtho,
                1f - Mathf.Exp(-zoomK * Time.unscaledDeltaTime));

            // Clamp so the view never pans past the pitch. This previously computed
            // pitchHalf and then clamped against the CAMERA's half-extents instead,
            // leaving the pitch bounds unused and letting the camera drift off the pitch.
            float camHalfH = _cam.orthographicSize;
            float camHalfW = camHalfH * _cam.aspect;
            float panX = Mathf.Max(0f, pitchHalf.x - camHalfW);
            float panY = Mathf.Max(0f, pitchHalf.y - camHalfH);

            Vector2 target = ballPos;
            target.x = Mathf.Clamp(target.x, -panX, panX);
            target.y = Mathf.Clamp(target.y, -panY, panY);

            Vector3 pos = _cam.transform.position;
            float followSpeed = overriding ? _followSpeed * 2f : _followSpeed;
            float t = 1f - Mathf.Exp(-followSpeed * Time.unscaledDeltaTime);
            pos.x = Mathf.Lerp(pos.x, target.x, t);
            pos.y = Mathf.Lerp(pos.y, target.y, t);
            _cam.transform.position = pos;   // z untouched: never change camera depth
        }
    }
}
