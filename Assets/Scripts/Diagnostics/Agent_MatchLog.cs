using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// A per-decision record of one match: what every policy emitted, where it was
    /// standing and how fast it was moving when it did, written as CSV.
    ///
    /// WHY THIS EXISTS NEXT TO Agent_Telemetry AND Agent_BrainCam. Telemetry logs
    /// the MACHINE (frame time, draw calls) and the brain cam shows the POLICY
    /// live, to somebody watching. Neither leaves anything behind about what the
    /// players did. Every question this project has had to answer the slow way -
    /// "does it thrash the turn channel", "does it ever cross the halfway line",
    /// "what was it emitting when it stalled in the corner" - was answered by a
    /// headless probe built for that one question. This is the same data, from a
    /// real match, in a file.
    ///
    /// ONE ROW PER DECISION, NOT PER TICK - and the hook is the brain cam's.
    /// ML-Agents repeats the last action between decisions (DecisionRequester
    /// period 8), so a per-tick log is eight copies of each row and turns every
    /// rate computed from it into an eighth of the truth. Like Agent_BrainCam, a
    /// decision is detected as a CHANGE in <see cref="Agent_Soccer.LastRawActions"/>.
    /// That alone under-counts, which matters for a log in a way it does not for a
    /// live bar: a policy that emits the identical vector twice running - the
    /// scripted bot holding full-forward does it constantly - would simply vanish
    /// from the file for as long as it was consistent. So each player also carries
    /// a tick counter, and a row is written when the action changes OR a whole
    /// decision period has gone by since the last one. A changed action
    /// re-synchronises the counter to the real decision tick; until the first
    /// change the rows are one per period but at an arbitrary phase, which can cost
    /// exactly one extra row per player per match.
    ///
    /// RAW ACTIONS, for the reason the brain cam gives: ActionGain is a
    /// multiply-then-clamp, and the gained value destroys the information.
    ///
    /// ZERO COST WHERE IT MUST BE ZERO. It is installed by
    /// Agent_Presentation.Install, which runs only in a match scene, so in
    /// SCN_Training, in an eval and on the gallery's clones the component does not
    /// exist - not disabled, absent. It then gates on IsMatchScene in Start anyway,
    /// the same belt-and-braces every sibling uses. A logger that wrote during an
    /// eval would put file I/O on the path that produces the benchmark's numbers.
    ///
    /// NO ALLOCATION PER ROW. Rows go into one StringBuilder sized up front, and
    /// every number is appended digit by digit - float.ToString and
    /// StringBuilder.Append(float) both allocate a string per call on this
    /// runtime, which at 14 numbers a row and ~50 rows a second in a 2v2 would be
    /// the largest steady source of garbage in the match. That also makes the
    /// output culture-independent: a decimal comma inside a CSV is its own bug.
    ///
    /// Opt out with <see cref="Enabled"/> (static, default on).
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_MatchLog : MonoBehaviour
    {
        /// <summary>
        /// Master switch. Static so it can be set before the match scene loads -
        /// from the menu, a test, or a settings toggle - without finding an
        /// instance that does not exist yet. Read at Start and on every tick, so
        /// switching it off mid-match stops recording and keeps what was captured.
        /// </summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>File name prefix; the telemetry session log uses "posoccer-telemetry-".</summary>
        public const string FILE_PREFIX = "posoccer-matchlog-";

        /// <summary>The column header, exactly as written. Read by the tests.</summary>
        public const string HEADER =
            "kind,t,player,team,role,fwd,lat,turn,boost,x,y,speed,ball_x,ball_y,blue,red";

        /// <summary>Generous upper bound on one row's length, for sizing the buffer.</summary>
        const int ROW_CHARS = 96;

        /// <summary>Used when a player carries no DecisionRequester to read the period from.</summary>
        const int FALLBACK_DECISION_PERIOD = 8;

        [Tooltip("Hard cap on rows. At 12.5 decisions a second per player, a 2v2 writes 50 " +
                 "rows a second, so 60 000 is twenty minutes. Recording stops at the cap and " +
                 "the file says so.")]
        [SerializeField] private int _maxRows = 60000;
        [Tooltip("Rows of buffer reserved before kickoff. Below the cap on purpose: reserving " +
                 "all 60 000 rows is ~11 MB of managed heap on a phone for a match that " +
                 "usually ends inside five minutes. Past this the buffer grows in 16 KB " +
                 "chunks - an allocation every ~80 rows, not every row.")]
        [SerializeField] private int _preallocatedRows = 16000;
        [SerializeField] private bool _enableMatchLog = true;

        static readonly string[] RoleNames = { "none", "goalie", "defender", "attacker" };

        /// <summary>One player's sampling state. A class so the list holds references, allocated once per roster.</summary>
        sealed class Track
        {
            public Agent_Soccer Agent;
            public string Name;
            public Vector4 Last;
            public bool Seeded;
            public int Period;
            public int Ticks;
        }

        Agent_EnvController _env;
        Agent_HUD _hud;

        readonly List<Track> _tracks = new();
        StringBuilder _buffer;
        string _path;
        bool _fileStarted;
        bool _closed;
        int _rows;
        int _blue;
        int _red;
        double _matchTime;

        /// <summary>Rows recorded so far this match, header excluded.</summary>
        public int RowCount => _rows;

        /// <summary>True once the row cap was hit and recording stopped.</summary>
        public bool Truncated { get; private set; }

        /// <summary>Where the log was last written, or null if nothing has been flushed yet.</summary>
        public string LastWrittenPath => _fileStarted ? _path : null;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            _hud = FindAnyObjectByType<Agent_HUD>();

            if (!_enableMatchLog || !Enabled || !Agent_Presentation.IsMatchScene(_hud))
            {
                enabled = false;
                return;
            }

            _maxRows = Mathf.Max(1, _maxRows);
            int reserve = Mathf.Clamp(_preallocatedRows, 64, _maxRows);
            _buffer = new StringBuilder(reserve * ROW_CHARS);
            _buffer.Append(HEADER).Append('\n');

            _env.EpisodeEnded += OnEpisodeEnded;
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
            Flush();
        }

        /// <summary>Android does not reliably reach OnDestroy when the process is killed.</summary>
        void OnApplicationQuit() => Flush();

        void OnApplicationPause(bool paused)
        {
            if (paused) Flush();
        }

        void Update()
        {
            // The match-end write. Not done from the EpisodeEnded handler: the HUD
            // decides the match is over in ITS handler for the same event, and
            // which of the two runs first is subscription order. FixedUpdate cannot
            // do it either - the full-time freeze stops the physics clock on the
            // very frame the match ends. One bool read per frame.
            if (_closed || _buffer == null || _hud == null) return;
            if (!_hud.MatchOver) return;

            _closed = true;
            Flush();
        }

        void FixedUpdate()
        {
            if (_closed || _buffer == null || !Enabled) return;

            // Physics time, so the column pauses with every freeze and matches the
            // tick the row was sampled on rather than the frame it was drawn in.
            _matchTime += Time.fixedDeltaTime;

            SyncTracks();

            for (int trackIndex = 0; trackIndex < _tracks.Count; trackIndex++)
            {
                Track track = _tracks[trackIndex];
                Agent_Soccer agent = track.Agent;
                if (agent == null || !agent.isActiveAndEnabled) continue;

                Vector4 action = agent.LastRawActions;
                track.Ticks++;

                bool changed = !track.Seeded || action != track.Last;
                if (!changed && track.Ticks < track.Period) continue;

                track.Seeded = true;
                track.Last = action;
                track.Ticks = 0;
                AppendDecision(track, action);

                if (_closed) return;
            }
        }

        /// <summary>
        /// Rebuilds the per-player state when the roster changes. Compared against
        /// the live list every tick rather than snapshotted in Start, for the reason
        /// Agent_MatchStats and Agent_BrainCam give: the roster is filled by a
        /// component at a different execution order. The comparison is one
        /// reference check per player; the rebuild allocates and happens once.
        /// </summary>
        void SyncTracks()
        {
            List<Agent_Soccer> roster = _env.agents;
            bool stale = _tracks.Count != roster.Count;
            for (int rosterIndex = 0; !stale && rosterIndex < roster.Count; rosterIndex++)
            {
                if (_tracks[rosterIndex].Agent != roster[rosterIndex]) stale = true;
            }
            if (!stale) return;

            _tracks.Clear();
            for (int rosterIndex = 0; rosterIndex < roster.Count; rosterIndex++)
            {
                Agent_Soccer agent = roster[rosterIndex];
                var track = new Track { Agent = agent, Period = FALLBACK_DECISION_PERIOD };
                if (agent != null)
                {
                    track.Name = CsvSafe(agent.rewards != null ? agent.rewards.playerName : agent.brainName);
                    var requester = agent.GetComponent<Unity.MLAgents.DecisionRequester>();
                    if (requester != null) track.Period = Mathf.Max(1, requester.DecisionPeriod);
                }
                _tracks.Add(track);
            }
        }

        // -- Rows --------------------------------------------------------------

        void AppendDecision(Track track, Vector4 action)
        {
            if (!ReserveRow()) return;

            Agent_Soccer agent = track.Agent;
            Vector2 position = agent.Body != null ? agent.Body.position : (Vector2)agent.transform.position;
            float speed = agent.Body != null ? agent.Body.linearVelocity.magnitude : 0f;
            int role = (int)_env.RoleOf(agent);

            _buffer.Append("decision,");
            AppendFixed(_buffer, _matchTime, 2);
            _buffer.Append(',').Append(track.Name)
                   .Append(',').Append(agent.team == Agent_Soccer.Team.Blue ? 'B' : 'R')
                   .Append(',').Append(role >= 0 && role < RoleNames.Length ? RoleNames[role] : "none")
                   .Append(',');
            AppendFixed(_buffer, action.x, 3);
            _buffer.Append(',');
            AppendFixed(_buffer, action.y, 3);
            _buffer.Append(',');
            AppendFixed(_buffer, action.z, 3);
            _buffer.Append(',');
            AppendFixed(_buffer, action.w, 3);
            _buffer.Append(',');
            AppendFixed(_buffer, position.x, 2);
            _buffer.Append(',');
            AppendFixed(_buffer, position.y, 2);
            _buffer.Append(',');
            AppendFixed(_buffer, speed, 2);
            AppendBallAndScore();
        }

        /// <summary>
        /// One row per episode end. <c>goal</c> carries the scoring side and, when
        /// the last toucher was on it, the scorer - the same credit rule
        /// Agent_EnvController.OnGoalScored and Agent_MatchFlow apply, so an own
        /// goal is not filed under somebody's name. <c>no_goal</c> is every ending
        /// without a winner: EpisodeEnded reports those as a bare null, so the
        /// stuck-ball watchdog and an out-of-bounds reset are indistinguishable
        /// from here and are deliberately not guessed at.
        ///
        /// The score is counted HERE rather than read off the HUD. Both subscribe
        /// to the same event, so the HUD's figure is either already updated or not
        /// depending on subscription order, and a log whose score column is right
        /// or one behind by accident of ordering is worse than no column.
        /// </summary>
        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (_closed || _buffer == null || !Enabled) return;

            if (winner == Agent_Soccer.Team.Blue) _blue++;
            else if (winner == Agent_Soccer.Team.Red) _red++;

            if (!ReserveRow()) return;

            string scorer = "";
            if (winner != null)
            {
                Agent_Soccer toucher = _env.LastToucher;
                if (toucher != null && toucher.team == winner.Value)
                {
                    scorer = CsvSafe(toucher.rewards != null ? toucher.rewards.playerName : toucher.brainName);
                }
            }

            _buffer.Append(winner != null ? "goal," : "no_goal,");
            AppendFixed(_buffer, _matchTime, 2);
            _buffer.Append(',').Append(scorer).Append(',');
            if (winner != null) _buffer.Append(winner.Value == Agent_Soccer.Team.Blue ? 'B' : 'R');
            // role, four actions, x, y, speed: not meaningful for an event.
            _buffer.Append(",,,,,,,,");
            AppendBallAndScore();
        }

        void AppendBallAndScore()
        {
            Vector2 ball = _env.Ball != null ? _env.Ball.position : Vector2.zero;
            _buffer.Append(',');
            AppendFixed(_buffer, ball.x, 2);
            _buffer.Append(',');
            AppendFixed(_buffer, ball.y, 2);
            _buffer.Append(',');
            AppendInteger(_buffer, _blue);
            _buffer.Append(',');
            AppendInteger(_buffer, _red);
            _buffer.Append('\n');
        }

        /// <summary>
        /// Counts a row against the cap. At the cap it writes ONE marker row and
        /// stops the recording outright - a log that silently ends reads as a match
        /// that ended, and the marker is the difference.
        /// </summary>
        bool ReserveRow()
        {
            if (_rows < _maxRows)
            {
                _rows++;
                return true;
            }

            if (!Truncated)
            {
                Truncated = true;
                _buffer.Append("truncated,");
                AppendFixed(_buffer, _matchTime, 2);
                _buffer.Append(",row cap reached,,,,,,,,,,,,");
                AppendInteger(_buffer, _blue);
                _buffer.Append(',');
                AppendInteger(_buffer, _red);
                _buffer.Append('\n');
            }
            _closed = true;
            return false;
        }

        // -- File --------------------------------------------------------------

        /// <summary>
        /// Writes what is buffered and empties the buffer. One file per match: the
        /// first flush creates it, later ones (a second OnApplicationPause, the
        /// match end after a pause, OnDestroy) append, so an interrupted session
        /// still ends up as a single CSV with a single header. Safe to call any
        /// number of times; a flush with nothing new is a no-op.
        /// </summary>
        public void Flush()
        {
            if (_buffer == null || _buffer.Length == 0) return;
            // Header only: a match scene that was entered and left before anybody
            // decided anything should not leave an empty file behind.
            if (!_fileStarted && _rows == 0) return;

            if (_path == null)
            {
                _path = System.IO.Path.Combine(
                    Application.persistentDataPath,
                    $"{FILE_PREFIX}{System.DateTime.Now:yyyyMMdd-HHmmss}.csv");
            }

            try
            {
                string text = _buffer.ToString();
                if (_fileStarted) System.IO.File.AppendAllText(_path, text);
                else System.IO.File.WriteAllText(_path, text);
                _fileStarted = true;
                _buffer.Clear();
            }
            catch (System.Exception exception)
            {
                // A Warning, not an Error: Agent_PlayMode_SmokeRun's error budget is
                // zero, and a read-only data folder is an environment fact rather
                // than a defect in the match. A diagnostic must also never be the
                // thing that takes down what it is recording. The buffer is dropped
                // so a failing disk does not grow it without bound.
                Debug.LogWarning($"Agent_MatchLog: could not write the match log: {exception.Message}");
                _buffer.Clear();
                _closed = true;
            }
        }

        // -- Formatting --------------------------------------------------------

        /// <summary>
        /// Appends a number with a fixed count of decimals (1..3), one char at a
        /// time and always with a '.' separator. See the class docstring for why
        /// this is not a ToString.
        /// </summary>
        static void AppendFixed(StringBuilder builder, double value, int decimals)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                builder.Append("nan");
                return;
            }

            long scale = decimals <= 1 ? 10L : decimals == 2 ? 100L : 1000L;
            long scaled = (long)System.Math.Round(System.Math.Abs(value) * scale);

            // Signed only when something survives the rounding, so -0.0004 prints
            // as 0.000 rather than -0.000.
            if (value < 0d && scaled != 0L) builder.Append('-');

            AppendInteger(builder, scaled / scale);
            builder.Append('.');

            long fraction = scaled % scale;
            for (long divisor = scale / 10L; divisor > 0L; divisor /= 10L)
            {
                builder.Append((char)('0' + (int)(fraction / divisor % 10L)));
            }
        }

        /// <summary>Appends a non-negative integer digit by digit.</summary>
        static void AppendInteger(StringBuilder builder, long value)
        {
            if (value < 0L)
            {
                builder.Append('-');
                value = -value;
            }
            if (value >= 10L) AppendInteger(builder, value / 10L);
            builder.Append((char)('0' + (int)(value % 10L)));
        }

        /// <summary>A name that cannot break a row: no separators, no line breaks, never null.</summary>
        static string CsvSafe(string text)
        {
            if (string.IsNullOrEmpty(text)) return "-";
            return text.Replace(',', ' ').Replace('\n', ' ').Replace('\r', ' ');
        }
    }
}
