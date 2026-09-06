using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// Per-player match telemetry: the F1-style numbers that make four identical
    /// bodies read as four different players.
    ///
    /// Everything here is MEASURED, and that word is load-bearing in this
    /// codebase. Distance is the integral of |v| dt at the physics rate; top speed
    /// is a max over the same samples; possession is the fraction of physics ticks
    /// this player was the closest body to the ball; grip is the fraction of ticks
    /// the feet were inside the last tenth of the friction circle. None of it is
    /// estimated, none of it is a model, and it is deliberately kept on the
    /// opposite side of the HUD from the win-probability strip, which is.
    ///
    /// WHY IT EARNS ITS PLACE. CLAUDE.md's own measurement is that all four
    /// personalities are statistically indistinguishable on win rate despite 12x
    /// different step counts - "which is itself the finding". Win rate is one
    /// number at the end of a thousand episodes. These are six numbers per player
    /// per match, and they are where a difference between MATT and KIM would
    /// actually show up first if there is one.
    ///
    /// SAMPLED IN FixedUpdate, NOT Update. Distance covered at 60 fps and distance
    /// covered at 144 fps must be the same number, and the physics tick is the
    /// only clock in this project that is the same everywhere - including in the
    /// headless build, where there are no frames at all.
    ///
    /// Presentation only. Self-disables in training and evaluation.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_MatchStats : MonoBehaviour
    {
        /// <summary>
        /// One player's match record. A struct in a dictionary, replaced wholesale
        /// on write - no per-tick allocation, which is the bar for anything
        /// sampled at 100 Hz.
        /// </summary>
        public struct Stat
        {
            public float DistanceMetres;
            public float TopSpeed;
            public float SprintSeconds;
            public float BoostSeconds;
            public float PossessionSeconds;
            public float AtLimitSeconds;
            public float TrackedSeconds;
            public int Touches;

            public float MeanSpeed => TrackedSeconds > 0.01f ? DistanceMetres / TrackedSeconds : 0f;
            public float PossessionShare => TrackedSeconds > 0.01f
                ? PossessionSeconds / TrackedSeconds : 0f;
            public float AtLimitShare => TrackedSeconds > 0.01f
                ? AtLimitSeconds / TrackedSeconds : 0f;
        }

        [Header("Sampling")]
        [Tooltip("Speed (m/s) counted as a sprint. 6 sits between this chassis' measured " +
                 "4.35 m/s jog and its 9.54 m/s sprint.")]
        [SerializeField] private float _sprintSpeed = 6f;
        [Tooltip("Traction saturation counted as 'at the limit of grip'.")]
        [Range(0.5f, 1f)] [SerializeField] private float _limitSaturation = 0.9f;

        [Header("Ticker")]
        [Tooltip("Seconds each ticker line holds before the next one takes over.")]
        [SerializeField] private float _lineSeconds = 4.5f;
        [Tooltip("Seconds of match played before the ticker starts. Leader lines drawn from " +
                 "two seconds of data are noise wearing a broadcast font.")]
        [SerializeField] private float _warmupSeconds = 8f;

        [SerializeField] private bool _enableMatchStats = true;

        Agent_EnvController _env;
        Agent_HUD _hud;

        // Records are created LAZILY, on the first physics tick that sees a given
        // agent, rather than snapshotted in Start.
        //
        // Start-time capture is what the first version did and it was wrong in a
        // way that only showed up in a PlayMode test: it depends on
        // Agent_EnvController having finished populating `agents` first, which
        // depends on execution order between a -50 component and a 0 one that was
        // AddComponent'd during someone else's Awake. Reading the live list every
        // tick has no ordering dependency at all, costs one dictionary probe per
        // player, and additionally handles a squad that changes after kickoff -
        // which the gallery does on every pitch it clones.
        readonly Dictionary<Agent_Soccer, Stat> _stats = new();

        float _elapsed;
        float _ballTopSpeed;
        int _line;
        float _nextLineAt;

        /// <summary>Number of distinct ticker lines. Kept next to the switch that renders them.</summary>
        const int LineCount = 6;

        /// <summary>Match record for one player. False before the first physics tick.</summary>
        public bool TryGet(Agent_Soccer agent, out Stat stat) => _stats.TryGetValue(agent, out stat);

        /// <summary>
        /// The live roster, read straight off the env controller rather than
        /// cached. Empty (never null) when the controller has gone away, so every
        /// loop below can index it without a guard.
        /// </summary>
        List<Agent_Soccer> Roster => _env != null ? _env.agents : Empty;

        static readonly List<Agent_Soccer> Empty = new();

        /// <summary>Fastest the ball has travelled this match (m/s).</summary>
        public float BallTopSpeed => _ballTopSpeed;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            _hud = FindFirstObjectByType<Agent_HUD>();

            if (!_enableMatchStats || !Agent_Presentation.IsMatchScene(_hud))
            {
                enabled = false;
                return;
            }

            _nextLineAt = _warmupSeconds;
            _env.BallTouched += OnBallTouched;
            if (_hud != null) _hud.AddEndPanelSection(BuildStatsCard);
        }

        void OnDestroy()
        {
            if (_env != null) _env.BallTouched -= OnBallTouched;
            if (_hud != null) _hud.RemoveEndPanelSection(BuildStatsCard);
        }

        void OnBallTouched(Agent_Soccer toucher)
        {
            if (toucher == null) return;
            // Lazily created like every other record: the first touch can land
            // before the first physics tick that saw this agent.
            _stats.TryGetValue(toucher, out var stat);
            stat.Touches++;
            _stats[toucher] = stat;
        }

        void FixedUpdate()
        {
            if (_env == null) return;

            // A frozen clock is a paused match, and a paused match accrues no
            // stats. Without this a long look at the pause menu would quietly
            // hand the possession crown to whoever was nearest the ball.
            if (Agent_TimeFreeze.IsFrozen) return;

            float dt = Time.fixedDeltaTime;
            _elapsed += dt;

            if (_env.Ball != null)
            {
                float ballSpeed = _env.Ball.linearVelocity.magnitude;
                if (ballSpeed > _ballTopSpeed) _ballTopSpeed = ballSpeed;
            }

            Agent_Soccer nearest = NearestToBall();

            var roster = _env.agents;
            for (int i = 0; i < roster.Count; i++)
            {
                var agent = roster[i];
                if (agent == null || agent.Body == null || !agent.isActiveAndEnabled) continue;
                _stats.TryGetValue(agent, out var stat);   // default(Stat) when new

                float speed = agent.Body.linearVelocity.magnitude;
                stat.TrackedSeconds += dt;
                stat.DistanceMetres += speed * dt;
                if (speed > stat.TopSpeed) stat.TopSpeed = speed;
                if (speed >= _sprintSpeed) stat.SprintSeconds += dt;
                if (agent.IsBoosting) stat.BoostSeconds += dt;
                if (agent.TractionSaturation >= _limitSaturation) stat.AtLimitSeconds += dt;
                if (agent == nearest) stat.PossessionSeconds += dt;

                _stats[agent] = stat;
            }
        }

        void Update()
        {
            if (_hud == null || _elapsed < _warmupSeconds) return;
            if (Time.time < _nextLineAt) return;

            _nextLineAt = Time.time + _lineSeconds;
            _line = (_line + 1) % LineCount;
            _hud.SetTicker(BuildLine(_line));
        }

        Agent_Soccer NearestToBall()
        {
            if (_env.Ball == null) return null;
            Vector2 ball = _env.Ball.position;

            Agent_Soccer best = null;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < Roster.Count; i++)
            {
                var agent = Roster[i];
                if (agent == null || agent.Body == null || !agent.isActiveAndEnabled) continue;
                float d = (agent.Body.position - ball).sqrMagnitude;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = agent;
                }
            }
            return best;
        }

        // -- Ticker ----------------------------------------------------------

        /// <summary>
        /// One line, always naming the leader on that measure. Allocates a string
        /// per line - which is once every few seconds, not every frame, and is why
        /// Update gates on the timer before it gets here.
        /// </summary>
        string BuildLine(int index)
        {
            switch (index)
            {
                case 0:
                {
                    var leader = Leader(s => s.TopSpeed, out float value);
                    return leader == null ? null : $"TOP SPEED · {Name(leader)} {value:0.0} m/s";
                }
                case 1:
                {
                    var leader = Leader(s => s.DistanceMetres, out float value);
                    return leader == null ? null : $"DISTANCE · {Name(leader)} {value:0} m";
                }
                case 2:
                {
                    var leader = Leader(s => s.PossessionShare, out float value);
                    return leader == null
                        ? null : $"POSSESSION · {Name(leader)} {value * 100f:0}%";
                }
                case 3:
                {
                    var leader = Leader(s => s.Touches, out float value);
                    return leader == null ? null : $"TOUCHES · {Name(leader)} {value:0}";
                }
                case 4:
                {
                    // The friction circle, made into a stat. A player who spends a
                    // lot of the match at the limit of grip is cutting hard; one
                    // who never gets there is jogging in straight lines.
                    var leader = Leader(s => s.AtLimitShare, out float value);
                    return leader == null
                        ? null : $"AT THE LIMIT · {Name(leader)} {value * 100f:0}% of the match";
                }
                default:
                    return $"FASTEST BALL · {_ballTopSpeed:0.0} m/s";
            }
        }

        /// <summary>
        /// Highest scorer on one measure. Takes a selector, which allocates a
        /// closure - acceptable here only because BuildLine runs a handful of
        /// times a match. Do not call this from Update or FixedUpdate.
        /// </summary>
        Agent_Soccer Leader(System.Func<Stat, float> measure, out float value)
        {
            Agent_Soccer best = null;
            value = 0f;

            for (int i = 0; i < Roster.Count; i++)
            {
                var agent = Roster[i];
                if (agent == null || !_stats.TryGetValue(agent, out var stat)) continue;
                float score = measure(stat);
                if (best != null && score <= value) continue;
                best = agent;
                value = score;
            }
            return best;
        }

        static string Name(Agent_Soccer agent)
        {
            if (agent == null) return "—";
            return agent.rewards != null ? agent.rewards.playerName : agent.brainName;
        }

        // -- End-of-match card -----------------------------------------------

        /// <summary>
        /// The full table, shown once at the end where there is room for it. The
        /// ticker exists because this does not fit on a phone mid-match; this
        /// exists because the ticker only ever shows the leader.
        /// </summary>
        VisualElement BuildStatsCard()
        {
            if (Roster.Count == 0) return null;

            var card = new VisualElement();
            card.AddToClassList("card");

            var heading = new Label("MATCH TELEMETRY");
            heading.AddToClassList("card__heading");
            card.Add(heading);

            card.Add(StatsRow("PLAYER", "DIST", "TOP", "POSS", "TCH", header: true));

            for (int i = 0; i < Roster.Count; i++)
            {
                var agent = Roster[i];
                if (agent == null || !_stats.TryGetValue(agent, out var stat)) continue;

                var row = StatsRow(
                    Name(agent),
                    $"{stat.DistanceMetres:0} m",
                    $"{stat.TopSpeed:0.0}",
                    $"{stat.PossessionShare * 100f:0}%",
                    $"{stat.Touches}",
                    header: false);

                // Team colour on the name only. Tinting the whole row would fight
                // the card background at the alpha the scrim panel uses.
                var name = row.ElementAt(0) as Label;
                if (name != null) name.style.color = Agent_SoccerView.TeamColor(agent.team);
                card.Add(row);
            }

            var caption = new Label($"fastest ball {_ballTopSpeed:0.0} m/s  ·  measured, not modelled");
            caption.AddToClassList("card__tile-caption");
            card.Add(caption);

            return card;
        }

        static VisualElement StatsRow(string name, string distance, string top,
                                      string possession, string touches, bool header)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = header ? 12 : 4;

            row.Add(Cell(name, 260, TextAnchor.MiddleLeft, header));
            row.Add(Cell(distance, 150, TextAnchor.MiddleRight, header));
            row.Add(Cell(top, 120, TextAnchor.MiddleRight, header));
            row.Add(Cell(possession, 120, TextAnchor.MiddleRight, header));
            row.Add(Cell(touches, 100, TextAnchor.MiddleRight, header));
            return row;
        }

        static Label Cell(string text, int width, TextAnchor align, bool header)
        {
            var label = new Label(text)
            {
                style =
                {
                    width = width,
                    unityTextAlign = align,
                    fontSize = Agent_UIStyle.FontXS,
                    color = header ? Agent_UIStyle.TextMuted : Agent_UIStyle.TextPrimary,
                },
            };
            return label;
        }
    }
}
