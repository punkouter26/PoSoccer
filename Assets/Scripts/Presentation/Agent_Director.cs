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

        [Header("Zoom on impact")]
        [Tooltip("Push in on a hard body check for a moment. Off = collisions never move the camera.")]
        [SerializeField] private bool _enableImpactZoom = true;
        [Tooltip("Normal impulse (N*s, from Agent_Contact.PlayerContact) a collision needs " +
                 "before the camera reacts. Above the stagger threshold (12) and the haptic " +
                 "one (14) on purpose: a jostle that rocks a player is not yet a shot.")]
        [SerializeField] private float _impactThreshold = 18f;
        [Tooltip("Impulse at which the push-in is as tight as it gets.")]
        [SerializeField] private float _impactSaturation = 48f;
        [Tooltip("Seconds the impact shot holds, on the unscaled clock - the hit-stop that " +
                 "usually accompanies a big collision must not stretch it.")]
        [SerializeField] private float _impactSeconds = 0.4f;
        [Tooltip("Framing for a collision right at the threshold (fraction of the wide shot).")]
        [Range(0.2f, 1f)] [SerializeField] private float _impactLooseFraction = 0.6f;
        [Tooltip("Framing for a collision at or past saturation. Agent_CameraFollow clamps " +
                 "every shot to its own tightest fraction, so going below that buys nothing.")]
        [Range(0.2f, 1f)] [SerializeField] private float _impactTightFraction = 0.45f;

        [Header("Tension framing")]
        [Tooltip("Tighten the working shots as the threat signal rises toward the iso " +
                 "threshold. NEVER applied to the wide shot - see the note on TensionScale.")]
        [SerializeField] private bool _enableTension = true;
        [Tooltip("Largest fraction the working shots are tightened by, reached as threat " +
                 "arrives at the iso threshold.")]
        [Range(0f, 0.2f)] [SerializeField] private float _tensionTighten = 0.12f;
        [Tooltip("How fast the tightening follows the threat signal (per second). The signal " +
                 "is a logistic over live positions and moves every frame; framing that " +
                 "followed it directly would breathe visibly.")]
        [Range(0.5f, 8f)] [SerializeField] private float _tensionSmoothing = 2f;

        [SerializeField] private bool _enableDirector = true;

        Agent_EnvController _env;
        Agent_CameraFollow _camera;
        Agent_WinProbability _probability;
        Agent_HUD _hud;

        Shot _shot = Shot.Wide;
        float _shotStarted;
        float _silentUntil;

        // Impact shot. Written from OnCollisionEnter2D (physics step), read in
        // LateUpdate - both on the main thread, so no synchronisation is needed.
        Agent_Contact[] _contacts;
        bool _contactsSubscribed;
        Vector2 _impactPoint;
        float _impactStrength;
        float _impactFraction = 1f;
        float _impactUntil = float.NegativeInfinity;

        float _tension;

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

        /// <summary>
        /// True while a collision's push-in still owns the framing. Public for the
        /// same reason Agent_CameraFollow.ShotActive is: the visible effect is a
        /// framing that can coincide with what the ordinary shot would have chosen.
        /// </summary>
        public bool ImpactShotActive => Time.unscaledTime < _impactUntil;

        /// <summary>Smoothed tension, 0 (calm) .. 1 (threat at the iso threshold).</summary>
        public float Tension => _tension;

        /// <summary>
        /// Multiplier the tension puts on a working shot's fraction, 0.88 .. 1.
        ///
        /// WHY THE WIDE SHOT IS EXEMPT. The request was to tighten the wide shot by
        /// up to 12%. The wide shot has no 12% to give: Agent_CameraFollow sizes it
        /// so the pitch's half-length plus a 0.5 m edge margin exactly fills the
        /// clear band between the HUD bands, which at 2v2 (12.65 m half-length) is
        /// 3.8% of slack. Anything past that puts both goal lines back under the
        /// scoreboard and the chip row - the 2026-09-13 landmine - and
        /// Agent_PlayMode_Portrait.TheCamerasWideShot_ClearsTheMeasuredHudBands
        /// asserts exactly that through CurrentWideOrtho. So tension tightens
        /// Chase, Break and Scrum, which pan with the ball and are pan-clamped
        /// against the clear band, and leaves Wide and CurrentWideOrtho alone. Iso
        /// is exempt too: it IS the tension response, and it has to hold ball and
        /// goal mouth in one frame.
        /// </summary>
        float TensionScale => _enableTension ? 1f - _tensionTighten * _tension : 1f;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            _hud = FindAnyObjectByType<Agent_HUD>();

            if (!_enableDirector || !Agent_Presentation.IsMatchScene(_hud))
            {
                enabled = false;
                return;
            }

            _camera = FindAnyObjectByType<Agent_CameraFollow>();
            _probability = GetComponent<Agent_WinProbability>();
            if (_camera == null)
            {
                enabled = false;
                return;
            }

            _silentUntil = Time.unscaledTime + _openingSilence;
            _shotStarted = Time.unscaledTime;
            _env.EpisodeEnded += OnEpisodeEnded;

            // Children of the pitch root, the same sweep Agent_Haptics makes. By
            // Start the lineup is final: Agent_MatchLoader builds the squads at
            // order -60 in Awake, long before any Start runs.
            _contacts = _env.GetComponentsInChildren<Agent_Contact>();
            SubscribeContacts();
        }

        void OnEnable()
        {
            // Null until Start has passed the gate, so a director that gated itself
            // out never subscribes at all; after that this re-arms a director that
            // was disabled and switched back on.
            SubscribeContacts();
        }

        void OnDisable()
        {
            UnsubscribeContacts();
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
            UnsubscribeContacts();
        }

        void SubscribeContacts()
        {
            if (_contacts == null || _contactsSubscribed) return;
            for (int i = 0; i < _contacts.Length; i++)
            {
                if (_contacts[i] != null) _contacts[i].PlayerContact += OnPlayerContact;
            }
            _contactsSubscribed = true;
        }

        void UnsubscribeContacts()
        {
            if (_contacts == null || !_contactsSubscribed) return;
            for (int i = 0; i < _contacts.Length; i++)
            {
                if (_contacts[i] != null) _contacts[i].PlayerContact -= OnPlayerContact;
            }
            _contactsSubscribed = false;
        }

        /// <summary>
        /// A hard body check. Records where and how hard; LateUpdate turns it into
        /// a shot. Both bodies in a collision raise this with the same impulse, so
        /// the "only a stronger hit replaces a live one" rule also collapses the
        /// pair into a single shot.
        /// </summary>
        void OnPlayerContact(Vector2 point, Vector2 normal, float impulse)
        {
            if (!_enableImpactZoom || impulse < _impactThreshold) return;

            float strength = Mathf.InverseLerp(
                _impactThreshold, Mathf.Max(_impactThreshold + 0.01f, _impactSaturation), impulse);
            if (ImpactShotActive && strength <= _impactStrength) return;

            _impactPoint = point;
            _impactStrength = strength;
            _impactFraction = Mathf.Lerp(_impactLooseFraction, _impactTightFraction, strength);
            _impactUntil = Time.unscaledTime + Mathf.Max(0f, _impactSeconds);
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            // Hand the camera back for the goal celebration and the replay. Both
            // are already choreographed elsewhere and a director cutting across
            // them would be fighting two components that own the frame outright.
            _silentUntil = Time.unscaledTime + _postGoalSilence;
            _shot = Shot.Wide;
            _shotStarted = Time.unscaledTime;
            // A collision in the goalmouth scramble must not push in over the
            // celebration that follows it.
            _impactUntil = float.NegativeInfinity;
            _camera.ClearShot();
        }

        void LateUpdate()
        {
            if (_env == null || _env.Ball == null || _camera == null) return;

            UpdateBroadcastTag();

            // Smoothed whether or not the director holds the camera this frame, so
            // it is already where it should be when the camera comes back rather
            // than easing in from a stale value.
            float tensionTarget = _probability != null
                ? Mathf.Clamp01(_probability.Threat / Mathf.Max(0.01f, _isoThreat))
                : 0f;
            _tension = Mathf.Lerp(_tension, tensionTarget,
                1f - Mathf.Exp(-_tensionSmoothing * Time.unscaledDeltaTime));

            // Replay owns the frame outright; so does the opening/celebration hold,
            // and so does a spectator who has locked the camera onto a player - a
            // person choosing a subject outranks a grammar inferring one, and the
            // rig would ignore these requests anyway.
            if (_camera.HasOverrideTarget || _camera.Focus != null
                || Time.unscaledTime < _silentUntil)
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

            bool cut = false;
            if (wanted != _shot && mayChange)
            {
                cut = wanted == Shot.Iso || _shot == Shot.Iso;
                _shot = wanted;
                _shotStarted = Time.unscaledTime;
            }

            // The impact push-in is an INSERT over the current shot, not a member
            // of the vocabulary: the shot state above keeps evolving underneath it,
            // so when it lapses the camera returns to whatever the story has become
            // rather than to where it was 0.4 s ago. An iso outranks it - a chance
            // on goal is the one thing a collision may not pull the camera off.
            if (ImpactShotActive && _shot != Shot.Iso)
            {
                // Never LOOSER than the shot it interrupts: a threshold-strength
                // hit during a scrum shot would otherwise zoom out on impact.
                float fraction = Mathf.Min(_impactFraction, FractionOf(_shot));
                _camera.RequestShot(_impactPoint, _camera.CurrentWideOrtho * fraction, false);
            }
            else
            {
                ApplyShot(cut);
            }

            IsDirecting = true;
        }

        /// <summary>Framing for a shot as a fraction of the wide baseline, tension included.</summary>
        float FractionOf(Shot shot)
        {
            switch (shot)
            {
                case Shot.Iso: return _isoFraction;
                case Shot.Scrum: return _scrumFraction * TensionScale;
                case Shot.Break: return _breakFraction * TensionScale;
                case Shot.Chase: return _chaseFraction * TensionScale;
                default: return 1f;
            }
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
            float fraction = FractionOf(_shot);

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
                    break;
                }
                case Shot.Scrum:
                    focus = ball;
                    break;
                case Shot.Break:
                    focus = ball + _env.Ball.linearVelocity * _leadSeconds;
                    break;
                case Shot.Chase:
                    focus = ball + _env.Ball.linearVelocity * (_leadSeconds * 0.6f);
                    break;
                default:
                    focus = ball;
                    break;
            }

            _camera.RequestShot(focus, wide * fraction, cut);
        }
    }
}
