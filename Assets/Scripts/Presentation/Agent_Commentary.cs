using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Event-driven commentary and crowd reactions.
    ///
    /// Every line is derived from something the simulation already knows - who
    /// touched the ball, how fast it was travelling, which goal it was heading
    /// for - rather than from a timer picking phrases at random. That is the
    /// whole point: a caller that says "OFF THE WOODWORK" when the ball actually
    /// hit the post next to the mouth makes the match legible; one that says it
    /// on a schedule makes it noise.
    ///
    /// Detection notes, all constrained by what this project actually has:
    ///  - the goal frame (Agent_GoalFrame) is a sprite with no collider, so a post
    ///    hit is read as a Wall contact within the goal mouth's width of centre,
    ///    hard against the goal line;
    ///  - Agent_EnvController.BallTouched fires on a CHANGE of possession, so a
    ///    block, a turnover and a stall all key off the same event cleanly;
    ///  - ball velocity is sampled in FixedUpdate and read one step stale, because
    ///    by the time a contact is reported the collision response has already
    ///    rewritten the live velocity.
    ///
    /// Presentation only. Self-disables in training and evaluation.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_Commentary : MonoBehaviour
    {
        [Tooltip("Minimum seconds between two ambient lines. Goals ignore this.")]
        [SerializeField] private float _cooldownSeconds = 3.5f;
        [Tooltip("Seconds without a change of possession before the crowd gets restless.")]
        [SerializeField] private float _stallSeconds = 8f;
        [Tooltip("Ball speed (m/s) that qualifies as a strike rather than a pass.")]
        [SerializeField] private float _screamerSpeed = 13f;
        [Tooltip("Ball speed (m/s) toward a goal that makes a defensive touch a block.")]
        [SerializeField] private float _blockSpeed = 6f;
        [SerializeField] private bool _enableCommentary = true;

        static readonly string[] GoalLines =
        {
            "That is a goal of real quality!",
            "Buried it. No hesitation.",
            "The keeper had no chance there.",
            "Clinical finish!",
        };

        static readonly string[] ScreamerLines =
        {
            "WHAT A STRIKE!",
            "Absolute rocket!",
            "He has hit that as hard as he can!",
        };

        static readonly string[] DistanceLines =
        {
            "FROM DISTANCE!",
            "All the way from the halfway line!",
            "He shot from there? And it is in!",
        };

        static readonly string[] BlockLines =
        {
            "GREAT BLOCK!",
            "Thrown himself in front of it!",
            "Superb defending!",
        };

        static readonly string[] WoodworkLines =
        {
            "OFF THE WOODWORK!",
            "Inches away!",
            "The post saves them!",
        };

        static readonly string[] TurnoverLines =
        {
            "Turnover in a dangerous area!",
            "He has robbed him there!",
            "Possession changes hands.",
        };

        static readonly string[] StallLines =
        {
            "The crowd wants to see some football.",
            "This has gone flat.",
            "Someone needs to take a risk here.",
        };

        Agent_EnvController _env;
        Agent_HUD _hud;
        Agent_Audio _audio;

        Vector2 _ballVelocityLastStep;
        Vector2 _possessionStartBall;
        float _maxSpeedThisPossession;
        float _lastTouchTime;
        float _nextLineTime;
        bool _stallAnnounced;
        int _cursor;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            _hud = FindFirstObjectByType<Agent_HUD>();

            if (!_enableCommentary || !Agent_Presentation.IsMatchScene(_hud))
            {
                enabled = false;
                return;
            }

            _audio = GetComponent<Agent_Audio>();
            _lastTouchTime = Time.unscaledTime;

            _env.EpisodeEnded += OnEpisodeEnded;
            _env.BallTouched += OnBallTouched;
            Agent_MatchFX.BallContact.Hit += OnBallHit;
        }

        void OnDestroy()
        {
            if (_env != null)
            {
                _env.EpisodeEnded -= OnEpisodeEnded;
                _env.BallTouched -= OnBallTouched;
            }
            Agent_MatchFX.BallContact.Hit -= OnBallHit;
        }

        void FixedUpdate()
        {
            if (_env == null || _env.Ball == null) return;
            // Read one step stale on purpose: a contact is reported after the
            // solver has already changed the live velocity, so the pre-impact
            // value is the only one that describes the shot.
            _ballVelocityLastStep = _env.Ball.linearVelocity;
            float speed = _ballVelocityLastStep.magnitude;
            if (speed > _maxSpeedThisPossession) _maxSpeedThisPossession = speed;
        }

        void Update()
        {
            if (Agent_TimeFreeze.IsFrozen) return;

            if (_stallAnnounced || Time.unscaledTime - _lastTouchTime < _stallSeconds) return;
            _stallAnnounced = true;
            Say(Pick(StallLines), Agent_UIStyle.TextMuted, 2.6f);
            if (_audio != null) _audio.Boo(0.7f);
        }

        // -- Detection -------------------------------------------------------

        void OnBallTouched(Agent_Soccer toucher)
        {
            float now = Time.unscaledTime;
            var previous = _env.PreviousToucher;

            bool blocked = DetectBlock(toucher);

            _lastTouchTime = now;
            _stallAnnounced = false;
            _possessionStartBall = _env.Ball != null ? _env.Ball.position : Vector2.zero;
            _maxSpeedThisPossession = 0f;

            if (blocked)
            {
                Say(Pick(BlockLines), Agent_SoccerView.TeamColor(toucher.team), 2.2f);
                if (_audio != null) _audio.Cheer(0.55f);
                return;
            }

            // A turnover only reads as drama in the final third; midfield exchanges
            // happen constantly and would drown everything else out.
            if (previous == null || toucher == null || previous.team == toucher.team) return;
            if (_env.Ball == null) return;

            Vector2 attackingGoal = _env.GetGoalPosition(Agent_Soccer.Opponent(toucher.team));
            float distance = Vector2.Distance(_env.Ball.position, attackingGoal);
            if (distance > _env.PitchHalfExtents.y * 0.6f) return;

            Say(Pick(TurnoverLines), Agent_SoccerView.TeamColor(toucher.team), 2.2f);
        }

        /// <summary>
        /// A block is a defender touching a ball that was travelling fast at their
        /// own goal from close range.
        /// </summary>
        bool DetectBlock(Agent_Soccer toucher)
        {
            if (toucher == null || _env.Ball == null) return false;
            if (_ballVelocityLastStep.magnitude < _blockSpeed) return false;

            Vector2 ownGoal = _env.GetGoalPosition(toucher.team);
            Vector2 toGoal = ownGoal - _env.Ball.position;
            if (toGoal.magnitude > _env.PitchHalfExtents.y * 0.75f) return false;

            // Was it actually going there, or just going fast?
            return Vector2.Dot(_ballVelocityLastStep.normalized, toGoal.normalized) > 0.72f;
        }

        void OnBallHit(Collision2D collision)
        {
            if (_env == null || _env.Ball == null) return;
            if (!collision.collider.CompareTag("Wall")) return;
            if (_ballVelocityLastStep.magnitude < 7f) return;

            Vector2 local = _env.Ball.position - (Vector2)transform.position;
            float half = _env.PitchHalfExtents.y;

            // Hard against a goal line, and inside the mouth's own width of centre:
            // that is a post, not a touchline clearance.
            if (Mathf.Abs(local.y) < half - 0.7f) return;
            if (Mathf.Abs(local.x) > _env.CurrentGoalWidth * 0.7f) return;

            Say(Pick(WoodworkLines), new Color(1f, 0.84f, 0.2f), 2.2f);
            if (_audio != null) _audio.Cheer(0.8f);
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            // A goal sequence holds the clock for several seconds of UNSCALED time
            // while nobody can touch the ball. Without this reset the stall timer
            // would already be expired at the restart and the crowd would boo the
            // kickoff of every single goal.
            _lastTouchTime = Time.unscaledTime;
            _stallAnnounced = false;
            _maxSpeedThisPossession = 0f;

            if (winner == null)
            {
                Say("Nothing between them. Reset.", Agent_UIStyle.TextMuted, 2f);
                if (_audio != null) _audio.Boo(0.5f);
                return;
            }

            var scorer = _env.LastToucher;
            Color teamColor = Agent_SoccerView.TeamColor(winner.Value);

            // Own goal: the last touch came from the side that just conceded.
            if (scorer != null && scorer.team != winner.Value)
            {
                string who = scorer.rewards != null ? scorer.rewards.playerName : "He";
                SayNow($"OH NO! {who} has put it into his own net!", teamColor, 3f);
                if (_audio != null) _audio.Boo(1f);
                return;
            }

            Vector2 concededGoal = _env.GetGoalPosition(Agent_Soccer.Opponent(winner.Value));
            float shotDistance = Vector2.Distance(_possessionStartBall, concededGoal);

            string line;
            if (_maxSpeedThisPossession > _screamerSpeed) line = Pick(ScreamerLines);
            else if (shotDistance > _env.PitchHalfExtents.y * 0.8f) line = Pick(DistanceLines);
            else line = Pick(GoalLines);

            SayNow(line, teamColor, 3f);
            if (_audio != null) _audio.Cheer(1f);
            if (Agent_Stadium.Instance != null) Agent_Stadium.Instance.CelebrateGoal(teamColor);
        }

        // -- Output ----------------------------------------------------------

        string Pick(string[] lines)
        {
            _cursor++;
            return lines[_cursor % lines.Length];
        }

        /// <summary>Rate-limited line - drops silently if something was just said.</summary>
        void Say(string text, Color color, float seconds)
        {
            if (Time.unscaledTime < _nextLineTime) return;
            SayNow(text, color, seconds);
        }

        /// <summary>Unconditional line, for moments that must never be swallowed.</summary>
        void SayNow(string text, Color color, float seconds)
        {
            if (_hud == null) return;
            _hud.Say(text, color, seconds);
            _nextLineTime = Time.unscaledTime + _cooldownSeconds;
        }
    }
}
