using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// Expected goals: a number on every shot, a running total per side, and a pitch
    /// overlay showing where the side with the ball could hurt the other from.
    ///
    /// WHAT THIS IS, STATED PLAINLY - the same disclaimer Agent_WinProbability carries. A
    /// hand-set logistic over three things the simulation already knows at the moment of
    /// the strike: the angle the goal mouth subtends from the ball, the distance to it,
    /// and how many defenders stand inside the triangle ball-post-post. The weights are
    /// NOT fitted to this project's matches. So:
    ///
    ///  - every surface that shows the number says MODEL;
    ///  - the weights are serialized fields;
    ///  - the component grades itself. Each shot's estimate is scored against what
    ///    happened (Brier score, 0 = perfect, 0.25 = always saying 50%), and
    ///    <see cref="Explain"/> prints it, so the model's quality is a reading rather
    ///    than a claim;
    ///  - every resolved shot is appended to xg_shots.csv under persistentDataPath with
    ///    its three features and its outcome, which is the table a logistic regression
    ///    needs to replace the hand-set weights.
    ///
    /// It sits next to Agent_ShotTracer rather than replacing it. The tracer is arithmetic
    /// on the live ball and answers "where does this ball go if nobody touches it". This
    /// answers "how often does a shot like that go in", which needs a model because it
    /// depends on what the defenders do next.
    ///
    /// A SHOT is a touch that leaves the ball travelling at shot speed on a straight line
    /// into the goal mouth. Shots played off a wall are not counted.
    ///
    /// One draw call for the shot dials (<see cref="Agent_Lines"/>) and one transparent
    /// quad for the field. Toggle the field with X. Presentation only; self-disables in
    /// training and evaluation.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_ExpectedGoals : MonoBehaviour
    {
        [Header("Model weights (hand-set, uncalibrated - see the class docstring)")]
        [Tooltip("Logistic intercept.")]
        [SerializeField] private float _intercept = -2.2f;
        [Tooltip("Weight per radian of goal mouth visible from the ball.")]
        [SerializeField] private float _angleWeight = 2.4f;
        [Tooltip("Weight per metre from the ball to the centre of the goal (subtracted).")]
        [SerializeField] private float _distanceWeight = 0.06f;
        [Tooltip("Weight per defender standing inside the shooting triangle (subtracted).")]
        [SerializeField] private float _blockerWeight = 1.1f;

        [Header("Shot detection")]
        [Tooltip("Ball speed (m/s) a touch must produce to count as a shot. Matches the shot tracer.")]
        [SerializeField] private float _minShotSpeed = 4.5f;
        [Tooltip("Physics ticks to wait after a touch before reading the ball's velocity.")]
        [SerializeField] private int _settleTicks = 3;
        [Tooltip("Seconds after which an unanswered shot is scored as not a goal.")]
        [SerializeField] private float _shotTimeout = 3f;
        [Tooltip("How far outside the posts, as a multiple of the goal width, a line may " +
                 "cross the goal line and still count as a shot at goal.")]
        [SerializeField] private float _mouthTolerance = 1.25f;

        [Header("Threat field")]
        [Tooltip("Start with the pitch overlay visible. Off by default: it covers the turf.")]
        [SerializeField] private bool _fieldVisibleOnStart = false;
        [Tooltip("Opacity of a cell worth 1.0 xG.")]
        [Range(0f, 1f)] [SerializeField] private float _fieldOpacity = 0.6f;
        [Tooltip("Field refreshes per second.")]
        [SerializeField] private float _fieldHz = 6f;

        [Header("Shot dial")]
        [Tooltip("Seconds a shot's dial stays on the pitch.")]
        [SerializeField] private float _dialSeconds = 2.5f;
        [Tooltip("Sorting order. With the shot tracer (16), under the confetti layer (20).")]
        [SerializeField] private int _sortingOrder = 16;

        [SerializeField] private bool _enableExpectedGoals = true;

        /// <summary>
        /// Threat-field visibility, static so the health panel can drive it without a
        /// reference. Re-seeded from the serialized default in Start.
        /// </summary>
        public static bool FieldVisible { get; set; }

        const int FIELD_COLUMNS = 18;
        const int FIELD_ROWS = 27;
        const string SHOT_LOG = "xg_shots.csv";

        Agent_EnvController _env;
        Agent_HUD _hud;
        Agent_Lines _dials;

        readonly Vector2[] _defenders = new Vector2[Agent_MatchSetup.MAX_SQUAD];

        // Per side, indexed by (int)Agent_Soccer.Team.
        readonly float[] _xg = new float[2];
        readonly int[] _shots = new int[2];
        readonly int[] _goals = new int[2];
        float _brierSum;
        int _resolved;

        Agent_Soccer _pendingToucher;
        int _pendingTicks;

        bool _shotOpen;
        Agent_Soccer.Team _shotTeam;
        float _shotValue, _shotAngle, _shotDistance;
        int _shotBlockers;
        Vector2 _shotOrigin;
        float _shotOpenedAt;
        float _dialUntil;

        Texture2D _fieldTexture;
        Color32[] _fieldPixels;
        SpriteRenderer _fieldRenderer;
        Material _fieldMaterial;
        float _nextFieldAt;

        /// <summary>Summed xG of every shot <paramref name="team"/> has taken this match.</summary>
        public float TotalFor(Agent_Soccer.Team team) => _xg[(int)team];

        /// <summary>Shots <paramref name="team"/> has taken this match.</summary>
        public int ShotsFor(Agent_Soccer.Team team) => _shots[(int)team];

        /// <summary>Shots scored against their estimate so far.</summary>
        public int ResolvedShots => _resolved;

        /// <summary>
        /// Mean squared error of the estimates against the outcomes. 0 is perfect, 0.25 is
        /// what always answering 50% scores. -1 before the first shot resolves.
        /// </summary>
        public float Brier => _resolved > 0 ? _brierSum / _resolved : -1f;

        /// <summary>One line for the health panel. Allocates; never call it from Update.</summary>
        public string Explain()
        {
            return $"xG blue={_xg[0]:0.00}/{_shots[0]} red={_xg[1]:0.00}/{_shots[1]}  " +
                   (_resolved > 0 ? $"brier={Brier:0.000} n={_resolved}" : "brier=n/a");
        }

        // -- The model ---------------------------------------------------------

        /// <summary>
        /// Probability that a shot from <paramref name="ball"/> at the goal centred on
        /// <paramref name="goalCentre"/> scores. <paramref name="goalLine"/> is the unit
        /// vector along the goal line. Static and pure so it can be pinned without a scene.
        /// </summary>
        public static float Estimate(Vector2 ball, Vector2 goalCentre, Vector2 goalLine,
            float goalWidth, Vector2[] defenders, int defenderCount,
            float intercept, float angleWeight, float distanceWeight, float blockerWeight,
            out float angle, out float distance, out int blockers)
        {
            Vector2 postA = goalCentre - goalLine * (goalWidth * 0.5f);
            Vector2 postB = goalCentre + goalLine * (goalWidth * 0.5f);

            angle = Vector2.Angle(postA - ball, postB - ball) * Mathf.Deg2Rad;
            distance = (goalCentre - ball).magnitude;

            blockers = 0;
            for (int defenderIndex = 0; defenderIndex < defenderCount; defenderIndex++)
            {
                if (InTriangle(defenders[defenderIndex], ball, postA, postB)) blockers++;
            }

            float z = intercept + angleWeight * angle - distanceWeight * distance
                      - blockerWeight * blockers;
            return 1f / (1f + Mathf.Exp(-z));
        }

        static bool InTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(point, a, b);
            float d2 = Cross(point, b, c);
            float d3 = Cross(point, c, a);
            bool anyNegative = d1 < 0f || d2 < 0f || d3 < 0f;
            bool anyPositive = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(anyNegative && anyPositive);
        }

        static float Cross(Vector2 p, Vector2 a, Vector2 b) =>
            (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);

        /// <summary>xG for <paramref name="attacker"/> shooting from <paramref name="from"/> right now.</summary>
        float EstimateFor(Agent_Soccer.Team attacker, Vector2 from,
            out float angle, out float distance, out int blockers)
        {
            Agent_Soccer.Team defender = Agent_Soccer.Opponent(attacker);
            Vector2 goal = _env.GetGoalPosition(defender);
            Vector2 axis = _env.GetGoalPosition(attacker) - goal;
            Vector2 goalLine = axis.sqrMagnitude > 0.0001f
                ? new Vector2(-axis.y, axis.x).normalized : Vector2.right;

            int count = 0;
            var agents = _env.agents;
            for (int agentIndex = 0; agentIndex < agents.Count && count < _defenders.Length; agentIndex++)
            {
                var agent = agents[agentIndex];
                if (agent == null || agent.Body == null || agent.team != defender) continue;
                _defenders[count++] = agent.Body.position;
            }

            return Estimate(from, goal, goalLine, Mathf.Max(0.1f, _env.CurrentGoalWidth),
                _defenders, count, _intercept, _angleWeight, _distanceWeight, _blockerWeight,
                out angle, out distance, out blockers);
        }

        // -- Lifecycle ---------------------------------------------------------

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            _hud = FindAnyObjectByType<Agent_HUD>();

            if (!_enableExpectedGoals || !Agent_Presentation.IsMatchScene(_hud))
            {
                enabled = false;
                return;
            }

            FieldVisible = _fieldVisibleOnStart;
            _dials = new Agent_Lines("ExpectedGoals", _sortingOrder, vertexCapacity: 256);
            BuildField();

            _env.BallTouched += OnBallTouched;
            _env.EpisodeEnded += OnEpisodeEnded;
            if (_hud != null) _hud.AddEndPanelSection(BuildCard);
        }

        void OnDestroy()
        {
            if (_env != null)
            {
                _env.BallTouched -= OnBallTouched;
                _env.EpisodeEnded -= OnEpisodeEnded;
            }
            if (_hud != null) _hud.RemoveEndPanelSection(BuildCard);
            _dials?.Dispose();
            if (_fieldMaterial != null) Destroy(_fieldMaterial);
            if (_fieldTexture != null) Destroy(_fieldTexture);
        }

        void OnBallTouched(Agent_Soccer toucher)
        {
            if (toucher == null) return;

            // Any touch ends the shot in flight: a defender got to it, or a team-mate
            // turned it into a different chance, which is scored on its own.
            if (_shotOpen) Resolve(false);

            _pendingToucher = toucher;
            _pendingTicks = Mathf.Max(1, _settleTicks);
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            _pendingToucher = null;
            if (!_shotOpen) return;
            Resolve(winner.HasValue && winner.Value == _shotTeam);
        }

        void FixedUpdate()
        {
            if (_env == null || _env.Ball == null) return;
            if (Agent_TimeFreeze.IsFrozen) return;

            if (_shotOpen && Time.time - _shotOpenedAt > _shotTimeout) Resolve(false);

            if (_pendingToucher == null) return;
            if (--_pendingTicks > 0) return;

            var toucher = _pendingToucher;
            _pendingToucher = null;
            TryOpenShot(toucher);
        }

        void TryOpenShot(Agent_Soccer toucher)
        {
            Vector2 ball = _env.Ball.position;
            Vector2 velocity = _env.Ball.linearVelocity;
            if (velocity.magnitude < _minShotSpeed) return;

            Agent_Soccer.Team attacker = toucher.team;
            Vector2 goal = _env.GetGoalPosition(Agent_Soccer.Opponent(attacker));
            Vector2 axis = goal - _env.GetGoalPosition(attacker);
            if (axis.sqrMagnitude < 0.0001f) return;

            // Does the ball's straight line cross the goal line inside the mouth?
            Vector2 toward = axis.normalized;
            float closing = Vector2.Dot(velocity, toward);
            if (closing < 0.5f) return;
            float seconds = Vector2.Dot(goal - ball, toward) / closing;
            if (seconds < 0f) return;
            Vector2 crossing = ball + velocity * seconds;
            float lateral = Mathf.Abs(Vector2.Dot(crossing - goal, new Vector2(-toward.y, toward.x)));
            if (lateral > _env.CurrentGoalWidth * 0.5f * _mouthTolerance) return;

            _shotValue = EstimateFor(attacker, ball, out _shotAngle, out _shotDistance, out _shotBlockers);
            _shotTeam = attacker;
            _shotOrigin = ball;
            _shotOpenedAt = Time.time;
            _shotOpen = true;
            _dialUntil = Time.time + _dialSeconds;

            _xg[(int)attacker] += _shotValue;
            _shots[(int)attacker]++;

            if (_hud != null)
            {
                string name = toucher.rewards != null ? toucher.rewards.playerName : toucher.brainName;
                _hud.SetTicker($"SHOT · {name} {_shotValue:0.00} xG · MODEL");
            }
        }

        void Resolve(bool scored)
        {
            _shotOpen = false;
            float outcome = scored ? 1f : 0f;
            _brierSum += (_shotValue - outcome) * (_shotValue - outcome);
            _resolved++;
            if (scored) _goals[(int)_shotTeam]++;
            AppendShotLog(scored);
        }

        /// <summary>
        /// One row per resolved shot: the calibration table. A few rows a match, so the
        /// append is not on a hot path, and a failed write must never cost a frame of the
        /// match it describes.
        /// </summary>
        void AppendShotLog(bool scored)
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, SHOT_LOG);
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, "team,angle_rad,distance_m,blockers,xg,goal\n");
                }
                var culture = CultureInfo.InvariantCulture;
                File.AppendAllText(path, string.Concat(
                    _shotTeam.ToString(), ",",
                    _shotAngle.ToString("0.0000", culture), ",",
                    _shotDistance.ToString("0.00", culture), ",",
                    _shotBlockers.ToString(culture), ",",
                    _shotValue.ToString("0.0000", culture), ",",
                    scored ? "1\n" : "0\n"));
            }
            catch (IOException)
            {
                // Read-only or full storage: the match goes on without the log.
            }
            catch (System.UnauthorizedAccessException)
            {
            }
        }

        // -- Drawing -----------------------------------------------------------

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.xKey.wasPressedThisFrame) FieldVisible = !FieldVisible;

            DrawDial();
            DrawField();
        }

        /// <summary>
        /// The shot's number, drawn as a dial where it was struck: the ring fills by the
        /// xG, so a tap-in closes the circle and a speculative effort is a sliver.
        /// </summary>
        void DrawDial()
        {
            if (_dials == null) return;

            float remaining = _dialUntil - Time.time;
            if (remaining <= 0f)
            {
                _dials.Visible = false;
                return;
            }

            float fade = Mathf.Clamp01(remaining / Mathf.Max(0.01f, _dialSeconds));
            Color tint = Agent_SoccerView.TeamColor(_shotTeam);

            _dials.Visible = true;
            _dials.Begin();
            _dials.AddArc(_shotOrigin, 0.7f, 1f, 0.03f, new Color(1f, 1f, 1f, 0.25f * fade));
            _dials.AddArc(_shotOrigin, 0.7f, Mathf.Clamp01(_shotValue), 0.09f,
                new Color(tint.r, tint.g, tint.b, 0.95f * fade));
            _dials.Commit();
        }

        void BuildField()
        {
            _fieldTexture = new Texture2D(FIELD_COLUMNS, FIELD_ROWS, TextureFormat.RGBA32, false)
            {
                name = "PoSoccer_ThreatField",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            _fieldPixels = new Color32[FIELD_COLUMNS * FIELD_ROWS];
            _fieldTexture.SetPixels32(_fieldPixels);
            _fieldTexture.Apply(false);

            var go = new GameObject("ThreatField");
            go.transform.SetParent(_env.transform, false);

            _fieldRenderer = go.AddComponent<SpriteRenderer>();
            _fieldRenderer.sprite = Sprite.Create(
                _fieldTexture, new Rect(0, 0, FIELD_COLUMNS, FIELD_ROWS), new Vector2(0.5f, 0.5f),
                1f, 0, SpriteMeshType.FullRect);
            _fieldRenderer.sprite.name = "PoSoccer_ThreatFieldSprite";
            _fieldRenderer.sprite.hideFlags = HideFlags.HideAndDontSave;

            // Unlit, like the wear quad: it is information laid on the ground, and a
            // floodlight brightening one corner of it would read as more threat there.
            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            if (shader != null)
            {
                _fieldMaterial = new Material(shader) { name = "PoSoccer_ThreatField" };
                _fieldRenderer.sharedMaterial = _fieldMaterial;
            }

            var pitch = _env.transform.Find("PitchBG");
            if (pitch != null && pitch.TryGetComponent(out SpriteRenderer pitchRenderer))
            {
                _fieldRenderer.sortingLayerID = pitchRenderer.sortingLayerID;
                // Above the wear quad (+1), below everything that stands on the pitch.
                _fieldRenderer.sortingOrder = pitchRenderer.sortingOrder + 2;
            }
            else
            {
                _fieldRenderer.sortingOrder = -1;
            }
            _fieldRenderer.enabled = false;
        }

        void DrawField()
        {
            if (_fieldRenderer == null) return;

            if (_fieldRenderer.enabled != FieldVisible) _fieldRenderer.enabled = FieldVisible;
            if (!FieldVisible) return;
            if (Time.unscaledTime < _nextFieldAt) return;
            _nextFieldAt = Time.unscaledTime + 1f / Mathf.Max(1f, _fieldHz);

            // One texel is one unit of sprite, so the scale IS the pitch size. Re-read
            // every refresh: Agent_Overtime moves the walls mid-match.
            Vector2 half = _env.PitchHalfExtents;
            _fieldRenderer.transform.localPosition = new Vector3(0f, 0f, 0.04f);
            _fieldRenderer.transform.localScale = new Vector3(
                half.x * 2f / FIELD_COLUMNS, half.y * 2f / FIELD_ROWS, 1f);

            Agent_Soccer.Team attacker = SideWithTheBall();
            Color32 tint = Agent_SoccerView.TeamColor(attacker);
            Vector2 centre = _env.transform.position;

            for (int row = 0; row < FIELD_ROWS; row++)
            {
                float y = ((row + 0.5f) / FIELD_ROWS * 2f - 1f) * half.y;
                for (int column = 0; column < FIELD_COLUMNS; column++)
                {
                    float x = ((column + 0.5f) / FIELD_COLUMNS * 2f - 1f) * half.x;
                    float value = EstimateFor(attacker, centre + new Vector2(x, y),
                        out _, out _, out _);
                    tint.a = (byte)Mathf.RoundToInt(Mathf.Clamp01(value * _fieldOpacity) * 255f);
                    _fieldPixels[row * FIELD_COLUMNS + column] = tint;
                }
            }

            _fieldTexture.SetPixels32(_fieldPixels);
            _fieldTexture.Apply(false);
        }

        /// <summary>The last side to touch the ball, or the side nearest it before any touch.</summary>
        Agent_Soccer.Team SideWithTheBall()
        {
            var last = _env.LastToucher;
            if (last != null) return last.team;

            Agent_Soccer.Team nearest = Agent_Soccer.Team.Blue;
            if (_env.Ball == null) return nearest;

            float best = float.PositiveInfinity;
            Vector2 ball = _env.Ball.position;
            var agents = _env.agents;
            for (int agentIndex = 0; agentIndex < agents.Count; agentIndex++)
            {
                var agent = agents[agentIndex];
                if (agent == null || agent.Body == null) continue;
                float sqr = (agent.Body.position - ball).sqrMagnitude;
                if (sqr >= best) continue;
                best = sqr;
                nearest = agent.team;
            }
            return nearest;
        }

        // -- End-of-match card -------------------------------------------------

        VisualElement BuildCard()
        {
            if (_shots[0] + _shots[1] == 0) return null;

            var card = new VisualElement();
            card.AddToClassList("card");

            var heading = new Label("EXPECTED GOALS · MODEL");
            heading.AddToClassList("card__heading");
            card.Add(heading);

            card.Add(Row("SIDE", "SHOTS", "xG", "SCORED", Agent_UIStyle.TextMuted));
            card.Add(Row("BLUE", _shots[0].ToString(), _xg[0].ToString("0.00"),
                _goals[0].ToString(), Agent_SoccerView.TeamColor(Agent_Soccer.Team.Blue)));
            card.Add(Row("RED", _shots[1].ToString(), _xg[1].ToString("0.00"),
                _goals[1].ToString(), Agent_SoccerView.TeamColor(Agent_Soccer.Team.Red)));

            string grade = _resolved > 0
                ? $"Brier {Brier:0.000} over {_resolved} shots (0.25 = always saying 50%)"
                : "no shot resolved yet";
            var caption = new Label($"hand-set weights, not fitted  ·  {grade}");
            caption.AddToClassList("card__tile-caption");
            card.Add(caption);

            return card;
        }

        static VisualElement Row(string side, string shots, string total, string scored, Color sideColor)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 4;

            row.Add(Cell(side, 260, TextAnchor.MiddleLeft, sideColor));
            row.Add(Cell(shots, 150, TextAnchor.MiddleRight, Agent_UIStyle.TextPrimary));
            row.Add(Cell(total, 150, TextAnchor.MiddleRight, Agent_UIStyle.TextPrimary));
            row.Add(Cell(scored, 150, TextAnchor.MiddleRight, Agent_UIStyle.TextPrimary));
            return row;
        }

        static Label Cell(string text, int width, TextAnchor align, Color color)
        {
            return new Label(text)
            {
                style =
                {
                    width = width,
                    unityTextAlign = align,
                    fontSize = Agent_UIStyle.FontXS,
                    color = color,
                },
            };
        }
    }
}
