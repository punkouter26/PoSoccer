using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// Match HUD (UI Toolkit, portrait, safe-area). Uses the dead bands above and
    /// below the pitch: scoreboard on top, ball-control meter + identity chips on
    /// the bottom - nothing ever covers the play area. Includes a goal toast, a
    /// MENU button, and a first-to-N end panel (match flow off in training).
    /// Styling comes from Agent_UIStyle so menu and HUD stay consistent.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Agent_HUD : MonoBehaviour
    {
        public Agent_EnvController env;
        [Tooltip("Enable goal toasts, scoreboard persistence and the end panel. Off in the training scene.")]
        public bool enableMatchFlow = true;
        [Tooltip("First team to this many goals ends the match (0 = endless).")]
        public int matchGoals = 5;
        public string menuScene = "SCN_Menu";

        UIDocument _doc;
        Label _score, _stepLabel, _toast;
        VisualElement _root, _endPanel, _blueChips, _redChips;
        VisualElement _ballControlBlue, _ballControlRed;   // halves of the meter
        int _blueScore, _redScore;
        float _toastUntil;
        bool _ended;

        void OnEnable()
        {
            _doc = GetComponent<UIDocument>();
            _root = _doc.rootVisualElement;
            if (_doc.panelSettings == null || _root == null)
            {
                Debug.LogWarning("Agent_HUD: assign a PanelSettings asset; HUD disabled.");
                enabled = false;
                return;
            }

            _root.Clear();
            var safe = new VisualElement();
            safe.style.flexGrow = 1;
            Agent_UIStyle.ApplySafeArea(safe);
            safe.style.justifyContent = Justify.SpaceBetween;
            _root.Add(safe);

            safe.Add(BuildTopBand());
            safe.Add(BuildBottomBand());
            BuildToast();
        }

        void Start()
        {
            if (env != null) env.EpisodeEnded += OnEpisodeEnded;
        }

        void OnDestroy()
        {
            if (env != null) env.EpisodeEnded -= OnEpisodeEnded;
        }

        // ── Bands ───────────────────────────────────────────────────────────

        VisualElement BuildTopBand()
        {
            var band = new VisualElement();
            band.style.alignItems = Align.Center;
            Agent_UIStyle.PadAll(band);

            _score = new Label("0  —  0");
            _score.style.fontSize = Agent_UIStyle.FontL;
            _score.style.unityFontStyleAndWeight = FontStyle.Bold;
            _score.style.color = Agent_UIStyle.TextPrimary;
            // The training scene has no match, so a frozen 0-0 would only mislead.
            _score.style.display = enableMatchFlow ? DisplayStyle.Flex : DisplayStyle.None;
            band.Add(_score);

            _stepLabel = new Label(string.Empty);
            _stepLabel.style.fontSize = Agent_UIStyle.FontS;
            _stepLabel.style.color = Agent_UIStyle.TextMuted;
            band.Add(_stepLabel);
            return band;
        }

        VisualElement BuildBottomBand()
        {
            var band = new VisualElement();
            band.style.alignItems = Align.Center;
            Agent_UIStyle.PadAll(band);

            // Ball-control meter: a single bar split blue (left) / red (right).
            // Each half's width is proportional to that team's proximity to the
            // ball - the team that's pressing reads as the wider side. Simpler
            // and more readable than 4 mini stamina bars the eye can't resolve
            // during play.
            var meterBg = new VisualElement();
            meterBg.style.flexDirection = FlexDirection.Row;
            meterBg.style.width = 600;
            meterBg.style.height = 14;
            meterBg.style.marginBottom = 16;
            meterBg.style.backgroundColor = new Color(1f, 1f, 1f, 0.15f);
            Agent_UIStyle.Round(meterBg, 7);

            _ballControlBlue = new VisualElement();
            _ballControlBlue.style.height = 14;
            _ballControlBlue.style.backgroundColor = Agent_UIStyle.BlueTeam;
            _ballControlBlue.style.borderTopLeftRadius = 7;
            _ballControlBlue.style.borderBottomLeftRadius = 7;
            _ballControlBlue.style.width = Length.Percent(50);

            _ballControlRed = new VisualElement();
            _ballControlRed.style.height = 14;
            _ballControlRed.style.backgroundColor = Agent_UIStyle.RedTeam;
            _ballControlRed.style.borderTopRightRadius = 7;
            _ballControlRed.style.borderBottomRightRadius = 7;
            _ballControlRed.style.width = Length.Percent(50);

            meterBg.Add(_ballControlBlue);
            meterBg.Add(_ballControlRed);
            band.Add(meterBg);

            _blueChips = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _redChips = new VisualElement { style = { flexDirection = FlexDirection.Row } };

            var chipRow = new VisualElement();
            chipRow.style.flexDirection = FlexDirection.Row;
            chipRow.style.justifyContent = Justify.SpaceBetween;
            chipRow.style.alignItems = Align.Center;
            chipRow.style.width = 900;
            chipRow.Add(_blueChips);
            chipRow.Add(_redChips);
            band.Add(chipRow);

            if (enableMatchFlow)
            {
                var controls = new VisualElement();
                controls.style.flexDirection = FlexDirection.Row;
                controls.style.marginTop = 12;

                var menu = SmallButton("MENU", () =>
                {
                    Time.timeScale = 1f;
                    SceneManager.LoadScene(menuScene);
                });
                var sound = Agent_UIStyle.SoundToggleButton();
                sound.style.marginLeft = 12;
                controls.Add(menu);
                controls.Add(sound);
                band.Add(controls);
            }

            return band;
        }

        float _matchSeconds;

        // Update runs at 60 fps but these labels change a few times a match
        // (score) or once a second (clock). Cache the last rendered values so the
        // string is rebuilt only on change - see .claude/rules/performance.md
        // (zero alloc in Update).
        int _shownBlue = -1, _shownRed = -1;
        int _shownSecond = -1;
        int _shownStep = -1;
        float _shownGoalWidth = -1f;     // first-frame sentinel for goal-width label
        float _shownRedShare = -1f;      // first-frame sentinel for ball-control meter

        static Button SmallButton(string text, System.Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.fontSize = Agent_UIStyle.FontS;
            b.style.color = Agent_UIStyle.TextPrimary;
            b.style.backgroundColor = Agent_UIStyle.PanelBg;
            Agent_UIStyle.Round(b);
            b.style.paddingLeft = 28; b.style.paddingRight = 28;
            b.style.paddingTop = 14; b.style.paddingBottom = 14;
            return b;
        }

        void BuildToast()
        {
            _toast = new Label(string.Empty);
            _toast.style.position = Position.Absolute;
            _toast.style.left = 0; _toast.style.right = 0;
            _toast.style.top = Length.Percent(38);
            _toast.style.unityTextAlign = TextAnchor.MiddleCenter;
            _toast.style.fontSize = Agent_UIStyle.FontXL;
            _toast.style.unityFontStyleAndWeight = FontStyle.Bold;
            _toast.style.color = Agent_UIStyle.TextPrimary;
            _toast.style.display = DisplayStyle.None;
            _root.Add(_toast);
        }

        void BuildChips()
        {
            if (_blueChips == null) return;
            _blueChips.Clear();
            _redChips.Clear();
            foreach (var agent in env.agents)
            {
                if (agent == null || agent.rewards == null) continue;

                var chip = new VisualElement();
                chip.style.alignItems = Align.Center;
                chip.style.marginLeft = 10; chip.style.marginRight = 10;

                var square = new Label(agent.rewards.playerName.Substring(0, 1));
                square.style.width = 84; square.style.height = 84;
                square.style.backgroundColor = agent.rewards.playerColor;
                square.style.unityTextAlign = TextAnchor.MiddleCenter;
                square.style.fontSize = Agent_UIStyle.FontM;
                square.style.unityFontStyleAndWeight = FontStyle.Bold;
                square.style.color = Color.black;
                Agent_UIStyle.Round(square, 12);
                float bw = 6f;
                var teamColor = agent.team == Agent_Soccer.Team.Blue
                    ? Agent_UIStyle.BlueTeam : Agent_UIStyle.RedTeam;
                square.style.borderBottomWidth = bw;
                square.style.borderBottomColor = teamColor;
                chip.Add(square);

                // Who is driving this body? The point of the BOT roster entry is
                // watching a trained brain play the rule-based benchmark, so the
                // scoreboard has to say which is which without guessing.
                var driver = new Label(agent.RuleBased ? "BOT" : "AI");
                driver.style.fontSize = 24;
                driver.style.marginTop = 4;
                driver.style.unityFontStyleAndWeight = FontStyle.Bold;
                driver.style.color = agent.RuleBased
                    ? Agent_UIStyle.TextMuted : Agent_UIStyle.Accent;
                chip.Add(driver);

                (agent.team == Agent_Soccer.Team.Blue ? _blueChips : _redChips).Add(chip);
            }
        }

        // ── Match flow ──────────────────────────────────────────────────────

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (!enableMatchFlow || _ended) return;

            if (winner == null)
            {
                // Stalemate: announce the reset so it reads as intended.
                _toast.text = "RESET";
                _toast.style.color = Agent_UIStyle.TextMuted;
                _toast.style.display = DisplayStyle.Flex;
                _toastUntil = Time.unscaledTime + 1f;
                return;
            }

            if (winner == Agent_Soccer.Team.Blue) _blueScore++; else _redScore++;

            var scorer = env.LastToucher;
            string who = scorer != null && scorer.rewards != null && scorer.team == winner
                ? scorer.rewards.playerName : winner.ToString().ToUpperInvariant();
            _toast.text = $"GOAL — {who}";
            _toast.style.color = winner == Agent_Soccer.Team.Blue
                ? Agent_UIStyle.BlueTeam : Agent_UIStyle.RedTeam;
            _toast.style.display = DisplayStyle.Flex;
            _toastUntil = Time.unscaledTime + 1.6f;

            if (matchGoals > 0 && (_blueScore >= matchGoals || _redScore >= matchGoals))
                ShowEndPanel();
        }

        void ShowEndPanel()
        {
            _ended = true;
            Time.timeScale = 0f;

            _endPanel = new VisualElement();
            _endPanel.style.position = Position.Absolute;
            _endPanel.style.left = 0; _endPanel.style.right = 0;
            _endPanel.style.top = 0; _endPanel.style.bottom = 0;
            _endPanel.style.backgroundColor = new Color(0f, 0f, 0f, 0.75f);
            _endPanel.style.alignItems = Align.Center;
            _endPanel.style.justifyContent = Justify.Center;

            var headline = new Label(_blueScore > _redScore ? "BLUE WINS" : "RED WINS");
            headline.style.fontSize = Agent_UIStyle.FontXL;
            headline.style.unityFontStyleAndWeight = FontStyle.Bold;
            headline.style.color = _blueScore > _redScore
                ? Agent_UIStyle.BlueTeam : Agent_UIStyle.RedTeam;
            _endPanel.Add(headline);

            var score = new Label($"{_blueScore}  —  {_redScore}");
            score.style.fontSize = Agent_UIStyle.FontL;
            score.style.color = Agent_UIStyle.TextPrimary;
            score.style.marginBottom = 40;
            _endPanel.Add(score);

            _endPanel.Add(EndButton("REMATCH", Agent_UIStyle.Accent,
                () => { Time.timeScale = 1f; SceneManager.LoadScene(SceneManager.GetActiveScene().name); }));
            _endPanel.Add(EndButton("MENU", Agent_UIStyle.PanelBg,
                () => { Time.timeScale = 1f; SceneManager.LoadScene(menuScene); }));

            _root.Add(_endPanel);
        }

        static Button EndButton(string text, Color bg, System.Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.fontSize = Agent_UIStyle.FontM;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.style.color = Agent_UIStyle.TextPrimary;
            b.style.backgroundColor = bg;
            Agent_UIStyle.Round(b);
            b.style.marginTop = 16;
            b.style.paddingLeft = 90; b.style.paddingRight = 90;
            b.style.paddingTop = 20; b.style.paddingBottom = 20;
            return b;
        }

        // ── Per-frame ───────────────────────────────────────────────────────

        void Update()
        {
            if (env == null || _score == null) return;
            if (_blueChips != null && _blueChips.childCount == 0 && env.agents.Count > 0) BuildChips();

            if (enableMatchFlow)
            {
                if (!_ended) _matchSeconds += Time.deltaTime;

                if (_blueScore != _shownBlue || _redScore != _shownRed)
                {
                    _score.text = $"{_blueScore}  —  {_redScore}";
                    _shownBlue = _blueScore;
                    _shownRed = _redScore;
                }

                int second = (int)_matchSeconds;
                if (second != _shownSecond)
                {
                    _stepLabel.text = $"{second / 60:0}:{second % 60:00}";
                    _shownSecond = second;
                }
            }
            else
            {
                // Training telemetry: raw steps + curriculum state belong here only.
                int step = env.StepCount;
                float goalWidth = env.CurrentGoalWidth;
                if (step != _shownStep || !Mathf.Approximately(goalWidth, _shownGoalWidth))
                {
                    _stepLabel.text = $"step {step}  ·  goal {goalWidth:0.0}m";
                    _shownStep = step;
                    _shownGoalWidth = goalWidth;
                }
            }

            if (_toast.style.display == DisplayStyle.Flex && Time.unscaledTime > _toastUntil)
                _toast.style.display = DisplayStyle.None;

            UpdateBallControlMeter();
        }

        // Ball-control meter: the team whose goal the ball is closer to is
        // winning the pressing battle, so its half of the meter widens. Cached
        // ratio keeps Update alloc-free when nothing changes.
        void UpdateBallControlMeter()
        {
            if (_ballControlBlue == null || _ballControlRed == null || env == null || env.Ball == null) return;
            Vector2 ball = env.Ball.position;
            float dBlue = Vector2.Distance(ball, env.GetGoalPosition(Agent_Soccer.Team.Blue));
            float dRed = Vector2.Distance(ball, env.GetGoalPosition(Agent_Soccer.Team.Red));
            float total = dBlue + dRed;
            if (total < 0.001f) return;
            // Closer to red goal = red is defending/pressing harder = red half grows.
            float redShare = dBlue / total;
            if (!Mathf.Approximately(redShare, _shownRedShare))
            {
                _ballControlBlue.style.width = Length.Percent((1f - redShare) * 100f);
                _ballControlRed.style.width = Length.Percent(redShare * 100f);
                _shownRedShare = redShare;
            }
        }
    }
}