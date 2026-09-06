using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// The broadcast director: a shot grammar layered on top of
    /// <see cref="Agent_CameraFollow"/>.
    ///
    /// A single continuously-tracking camera is a security feed. What makes a
    /// match read as a broadcast is that somebody CHOOSES a shot, holds it long
    /// enough to be a shot, and CUTS when the story changes. That is all this
    /// component does: pick from five framings, refuse to change more often than
    /// a human director would, and snap rather than swoop when the change is an
    /// edit rather than a drift.
    ///
    /// WHY THIS IS NOT CINEMACHINE. The obvious implementation is a stack of
    /// CinemachineCameras and a brain, and the feature list this came from said
    /// exactly that on the strength of CLAUDE.md claiming Cinemachine 3.1.7 was
    /// installed. It is not - Packages/manifest.json has no com.unity.cinemachine
    /// entry, and installing a package mid-session is the documented way to break
    /// editor compilation until a restart. More to the point, Agent_CameraFollow
    /// already solves the hard part that a fresh Cinemachine rig would have to
    /// re-derive: the aspect-derived portrait wide shot, the pitch pan clamp, and
    /// the replay override handoff. So the director drives that rig through its
    /// shot channel instead. Swapping the backend later changes this file only.
    ///
    /// EVERY CUT IS SOURCED FROM THE SIMULATION. Threat comes from
    /// Agent_WinProbability, congestion from real body positions, pace from ball
    /// velocity. Nothing here is on a timer, because a director cutting on a
    /// metronome is the camera equivalent of commentary that fires on a schedule -
    /// see Agent_Commentary's docstring for the same argument.
    ///
    /// Presentation only. Self-disables in training and evaluation.
    /// </summary>
    /// <remarks>
    /// Execution order -60, ahead of Agent_CameraFollow's -50, so a shot requested
    /// this frame is consumed by the rig THIS frame. At the default order of 0 the
    /// director's LateUpdate runs after the camera's and every request lands a
    /// frame late - which is invisible for a drift and wrong for a cut, since the
    /// whole point of a cut is that it is instantaneous.
    /// </remarks>
    [DefaultExecutionOrder(-60)]
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_Director : MonoBehaviour
    {
        /// <summary>The shot vocabulary. Order is priority, highest last.</summary>
        public enum Shot
        {
            /// <summary>Establishing shot; the whole pitch.</summary>
            Wide,
            /// <summary>Default tracking shot, framed on the ball with a little lead.</summary>
            Chase,
            /// <summary>Ball travelling fast in open space - pull back and lead it.</summary>
            Break,
            /// <summary>Bodies packed around the ball - push in on the fight.</summary>
            Scrum,
            /// <summary>Somebody is close to scoring - frame ball and goal mouth together.</summary>
            Iso,
        }

        [Header("Shot rhythm")]
        [Tooltip("Shortest a shot may hold before the director is allowed to change it. " +
                 "Below about a second the camera strobes and reads as a bug, not an edit.")]
        [SerializeField] private float _minShotSeconds = 1.25f;
        [Tooltip("Threat (0..1, from Agent_WinProbability) at which the director cuts to an iso shot.")]
        [Range(0.1f, 1f)] [SerializeField] private float _isoThreat = 0.45f;
        [Tooltip("Threat below which an iso shot is released again. Deliberately lower than " +
                 "the entry threshold - a single hysteresis band is what stops the camera " +
                 "flickering between iso and chase while the ball hovers on the boundary.")]
        [Range(0.05f, 1f)] [SerializeField] private float _isoRelease = 0.28f;

        [Header("Framing (fractions of the wide shot)")]
        [Tooltip("Iso: tightest framing in the vocabulary.")]
        [Range(0.2f, 1f)] [SerializeField] private float _isoFraction = 0.42f;
        [Tooltip("Scrum: tight on a congested ball.")]
        [Range(0.2f, 1f)] [SerializeField] private float _scrumFraction = 0.48f;
        [Tooltip("Chase: the working shot.")]
        [Range(0.2f, 1f)] [SerializeField] private float _chaseFraction = 0.66f;
        [Tooltip("Break: wide enough that a fast ball cannot outrun the frame.")]
        [Range(0.2f, 1f)] [SerializeField] private float _breakFraction = 0.86f;

        [Header("Triggers")]
        [Tooltip("Ball speed (m/s) above which play is treated as a break.")]
        [SerializeField] private float _breakSpeed = 8f;
        [Tooltip("Ball speed (m/s) below which play is slow enough for a scrum shot.")]
        [SerializeField] private float _scrumSpeed = 3.5f;
        [Tooltip("Radius (m) around the ball counted as congestion.")]
        [SerializeField] private float _scrumRadius = 3.2f;
        [Tooltip("Players inside that radius required before the shot pushes in.")]
        [SerializeField] private int _scrumBodies = 2;
        [Tooltip("Seconds of ball travel the camera leads by. Framing where the ball WILL be " +
                 "is what stops a fast break permanently sitting on the trailing edge.")]
        [SerializeField] private float _leadSeconds = 0.35f;

        [Header("Handover")]
        [Tooltip("Seconds the director stands down after a goal, leaving the wide hold and " +
                 "then the replay to Agent_CameraFollow and Agent_Replay respectively.")]
        [SerializeField] private float _postGoalSilence = 6f;
        [Tooltip("Seconds the director stands down at kickoff so the opening wide shot plays.")]
        [SerializeField] private float _openingSilence = 2.5f;

        [SerializeField] private bool _enableDirector = true;

        Agent_EnvController _env;
        Agent_CameraFollow _camera;
        Agent_WinProbability _probability;
        Agent_HUD _hud;

        Shot _shot = Shot.Wide;
        float _shotStarted;
        float _silentUntil;

        // Last tag pushed to the HUD. The composed string allocates, so it is only
        // built when one of its two halves has actually changed.
        Shot _taggedShot = (Shot)(-1);
        string _taggedVision;

        /// <summary>Current shot, for the HUD's camera tag. Never null, never allocates.</summary>
        public Shot CurrentShot => _shot;

        /// <summary>Short broadcast label for the current shot, e.g. "ISO".</summary>
        public string CurrentShotName
        {
            get
            {
                switch (_shot)
                {
                    case Shot.Iso: return "ISO";
                    case Shot.Scrum: return "TIGHT";
                    case Shot.Break: return "BREAK";
                    case Shot.Chase: return "CHASE";
                    default: return "WIDE";
                }
            }
        }

        /// <summary>True while the director is actually driving the camera.</summary>
        public bool IsDirecting { get; private set; }

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            _hud = FindFirstObjectByType<Agent_HUD>();

            if (!_enableDirector || !Agent_Presentation.IsMatchScene(_hud))
            {
                enabled = false;
                return;
            }

            _camera = FindFirstObjectByType<Agent_CameraFollow>();
            _probability = GetComponent<Agent_WinProbability>();
            if (_camera == null)
            {
                enabled = false;
                return;
            }

            _silentUntil = Time.unscaledTime + _openingSilence;
            _shotStarted = Time.unscaledTime;
            _env.EpisodeEnded += OnEpisodeEnded;
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            // Hand the camera back for the goal celebration and the replay. Both
            // are already choreographed elsewhere and a director cutting across
            // them would be fighting two components that own the frame outright.
            _silentUntil = Time.unscaledTime + _postGoalSilence;
            _shot = Shot.Wide;
            _shotStarted = Time.unscaledTime;
            _camera.ClearShot();
        }

        void LateUpdate()
        {
            if (_env == null || _env.Ball == null || _camera == null) return;

            UpdateBroadcastTag();

            // Replay owns the frame outright; so does the opening/celebration hold.
            if (_camera.HasOverrideTarget || Time.unscaledTime < _silentUntil)
            {
                IsDirecting = false;
                return;
            }

            Shot wanted = ChooseShot();
            bool held = Time.unscaledTime - _shotStarted < _minShotSeconds;

            // The minimum hold is a floor on shot LENGTH, not a mute button: an
            // iso is the one shot allowed to interrupt, because a chance on goal
            // that the camera reaches a second late is a chance nobody saw.
            bool mayChange = !held || wanted == Shot.Iso;

            if (wanted != _shot && mayChange)
            {
                bool cut = wanted == Shot.Iso || _shot == Shot.Iso;
                _shot = wanted;
                _shotStarted = Time.unscaledTime;
                ApplyShot(cut);
            }
            else
            {
                ApplyShot(false);
            }

            IsDirecting = true;
        }

        /// <summary>
        /// The corner status bug. The director owns it because it owns the half
        /// that changes most; the vision overlay's half arrives through a static
        /// so neither component needs a reference to the other.
        /// </summary>
        void UpdateBroadcastTag()
        {
            if (_hud == null) return;

            string vision = Agent_VisionView.ModeLabel;
            if (_shot == _taggedShot && vision == _taggedVision) return;

            _taggedShot = _shot;
            _taggedVision = vision;

            _hud.SetBroadcastTag(string.IsNullOrEmpty(vision)
                ? $"CAM · {CurrentShotName}"
                : $"CAM · {CurrentShotName}   {vision}");
        }

        Shot ChooseShot()
        {
            float speed = _env.Ball.linearVelocity.magnitude;
            float threat = _probability != null ? _probability.Threat : 0f;

            // Hysteresis: entering an iso needs more threat than staying in one.
            float isoBar = _shot == Shot.Iso ? _isoRelease : _isoThreat;
            if (threat >= isoBar) return Shot.Iso;

            if (speed >= _breakSpeed) return Shot.Break;
            if (speed <= _scrumSpeed && CongestionAroundBall() >= _scrumBodies) return Shot.Scrum;
            return Shot.Chase;
        }

        int CongestionAroundBall()
        {
            Vector2 ball = _env.Ball.position;
            float radiusSquared = _scrumRadius * _scrumRadius;
            int count = 0;
            var agents = _env.agents;
            for (int i = 0; i < agents.Count; i++)
            {
                var agent = agents[i];
                if (agent == null || agent.Body == null) continue;
                if ((agent.Body.position - ball).sqrMagnitude <= radiusSquared) count++;
            }
            return count;
        }

        void ApplyShot(bool cut)
        {
            float wide = _camera.CurrentWideOrtho;
            Vector2 ball = _env.Ball.position;
            Vector2 focus;
            float fraction;

            switch (_shot)
            {
                case Shot.Iso:
                {
                    // Frame the ball and the goal it is threatening in one shot, so
                    // the audience can see the chance rather than just the ball.
                    Agent_Soccer.Team attacking = _probability != null
                        ? _probability.ThreatTeam : Agent_Soccer.Team.Blue;
                    Vector2 mouth = _env.GetGoalPosition(Agent_Soccer.Opponent(attacking));
                    focus = Vector2.Lerp(ball, mouth, 0.4f);
                    fraction = _isoFraction;
                    break;
                }
                case Shot.Scrum:
                    focus = ball;
                    fraction = _scrumFraction;
                    break;
                case Shot.Break:
                    focus = ball + _env.Ball.linearVelocity * _leadSeconds;
                    fraction = _breakFraction;
                    break;
                case Shot.Chase:
                    focus = ball + _env.Ball.linearVelocity * (_leadSeconds * 0.6f);
                    fraction = _chaseFraction;
                    break;
                default:
                    focus = ball;
                    fraction = 1f;
                    break;
            }

            _camera.RequestShot(focus, wide * fraction, cut);
        }
    }
}
