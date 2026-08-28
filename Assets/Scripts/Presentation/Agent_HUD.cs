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
        // 2026-08-11: a single master switch so the user-facing scene can opt
        // out of the scoreboard, ball-control meter, identity chips, MENU
        // button and toast. enableMatchFlow only gates the score readout and
        // the end panel - the rest of the bands still render in the training
        // scene because nothing in the original code path was conditional.
        // Defaults to ON so every existing scene keeps its current look
        // without any scene-asset edit. Set to false in the Inspector to
        // hide everything in this HUD without changing the scene.
        [Tooltip("Master switch: when false, builds nothing in OnEnable. The whole HUD (top + bottom bands + toast) disappears. Use for clean pitches where you only want the chassis sprites and team frame.")]
        public bool showHud = true;

        UIDocument _doc;
        Label _score, _stepLabel, _toast;
        VisualElement _root, _endPanel, _blueChips, _redChips;
        VisualElement _ballControlBlue, _ballControlRed;   // halves of the meter
        int _blueScore, _redScore;
        float _toastUntil;
        bool _ended;

        // Broadcast chrome, added 2026-08-27 for the replay / match-flow /
        // commentary layer. All of it is optional: every accessor no-ops when
        // showHud is false, so the clean-pitch scene is unaffected.
        Label _commentary, _banner, _replayTag;
        VisualElement _letterboxTop, _letterboxBottom;
        VisualElement _pausePanel;
        float _commentaryUntil;
        bool _pendingEnd;

        /// <summary>
        /// Pause holds the clock through its own token, so it composes with the
        /// replay, the countdown and the end panel instead of fighting them - the
        /// clock resumes only when every holder has let go.
        /// </summary>
        readonly object _pauseToken = new();

        public bool IsPaused => _pausePanel != null;

        public int BlueScore => _blueScore;
        public int RedScore => _redScore;

        /// <summary>True once the match-winning goal has landed, panel shown or not.</summary>
        public bool MatchOver => _ended || _pendingEnd;

        /// <summary>
        /// When set, the match-winning goal does NOT raise the end panel; the
        /// caller does, via <see cref="ShowEndPanelNow"/>. Agent_MatchFlow sets
        /// this so the goal replay and the final whistle get to play out before
        /// the result card covers the pitch.
        /// </summary>
        public bool DeferEndPanel { get; set; }

        /// <summary>
        /// Optional extra content appended to the end panel above the buttons -
        /// Agent_MatchFlow supplies the man-of-the-match card. Kept as a callback
        /// rather than a direct reference so the HUD never has to know the match
        /// flow exists.
        /// </summary>
        public System.Func<VisualElement> endPanelExtra;

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

            // showHud = false: skip every visual element (top + bottom bands +
            // toast) so the pitch reads clean. We still build a small invisible
            // root so anything trying to log in Update doesn't NRE.
            if (!showHud)
            {
                _root.Clear();
                return;
            }

            _root.Clear();
            Agent_UIStyle.ApplyTheme(_root);

            var safe = new VisualElement();
            safe.AddToClassList("screen");
            // Safe area stays in code: a device measurement, not a design token.
            // Bound rather than applied once, so a resolution change re-insets.
            Agent_UIStyle.BindSafeArea(safe);
            _root.Add(safe);

            safe.Add(BuildTopBand());
            safe.Add(BuildBottomBand());
            BuildToast();
            BuildBroadcastChrome();

            // Statics outlive a scene load in a player build, so a REMATCH taken
            // from the frozen end panel would resume into a still-frozen clock.
            Agent_TimeFreeze.ReleaseAll();
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
            band.AddToClassList("band");

            _score = new Label("0  —  0");
            _score.AddToClassList("score");
            // The training scene has no match, so a frozen 0-0 would only mislead.
            _score.style.display = enableMatchFlow ? DisplayStyle.Flex : DisplayStyle.None;
            band.Add(_score);

            _stepLabel = new Label(string.Empty);
            _stepLabel.AddToClassList("clock");
            band.Add(_stepLabel);
            return band;
        }

        VisualElement BuildBottomBand()
        {
            var band = new VisualElement();
            band.AddToClassList("band");

            // Ball-control meter: a single bar split blue (left) / red (right).
            // Each half's width is proportional to that team's proximity to the
            // ball - the team that's pressing reads as the wider side. Simpler
            // and more readable than 4 mini stamina bars the eye can't resolve
            // during play.
            var meterBg = new VisualElement();
            meterBg.AddToClassList("meter");

            _ballControlBlue = new VisualElement();
            _ballControlBlue.AddToClassList("meter__fill");
            _ballControlBlue.AddToClassList("meter__fill--blue");
            _ballControlBlue.style.width = Length.Percent(50);

            _ballControlRed = new VisualElement();
            _ballControlRed.AddToClassList("meter__fill");
            _ballControlRed.AddToClassList("meter__fill--red");
            _ballControlRed.style.width = Length.Percent(50);

            meterBg.Add(_ballControlBlue);
            meterBg.Add(_ballControlRed);
            band.Add(meterBg);

            _blueChips = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _redChips = new VisualElement { style = { flexDirection = FlexDirection.Row } };

            var chipRow = new VisualElement();
            chipRow.AddToClassList("row");
            chipRow.style.justifyContent = Justify.SpaceBetween;
            chipRow.style.width = 900;
            chipRow.Add(_blueChips);
            chipRow.Add(_redChips);
            band.Add(chipRow);

            if (enableMatchFlow)
            {
                var controls = new VisualElement();
                controls.AddToClassList("row");
                controls.style.marginTop = 12;

                var menu = SmallButton("MENU", () =>
                {
                    Agent_TimeFreeze.ReleaseAll();
                    SceneManager.LoadScene(menuScene);
                });
                var sound = Agent_UIStyle.SoundToggleButton();
                sound.style.marginLeft = 12;   // spacing between two peers, not a token

                var pause = SmallButton("II", TogglePause);
                pause.style.marginLeft = 12;

                controls.Add(menu);
                controls.Add(sound);
                controls.Add(pause);
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
        float _shownBotStrength = -1f;   // first-frame sentinel for the opponent-curriculum readout
        float _shownRedShare = -1f;      // first-frame sentinel for ball-control meter

        static Button SmallButton(string text, System.Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.AddToClassList("btn");
            return b;
        }

        void BuildToast()
        {
            _toast = new Label(string.Empty);
            _toast.AddToClassList("toast");
            // Absolutely positioned overlays span the screen; without this they
            // would swallow taps meant for the MENU and sound buttons beneath.
            _toast.pickingMode = PickingMode.Ignore;
            _root.Add(_toast);
        }

        // Letterbox bars, a REPLAY tag, a persistent banner (GOLDEN GOAL) and a
        // commentary strip. Absolute-positioned overlays so they never disturb
        // the existing top/bottom band layout.
        void BuildBroadcastChrome()
        {
            _letterboxTop = Bar();
            _letterboxTop.style.top = 0;
            _letterboxBottom = Bar();
            _letterboxBottom.style.bottom = 0;

            _replayTag = new Label("● REPLAY");
            _replayTag.AddToClassList("replay-tag");
            _replayTag.pickingMode = PickingMode.Ignore;

            _banner = new Label(string.Empty);
            _banner.AddToClassList("banner");
            _banner.pickingMode = PickingMode.Ignore;

            _commentary = new Label(string.Empty);
            _commentary.AddToClassList("commentary");
            _commentary.pickingMode = PickingMode.Ignore;

            _root.Add(_letterboxTop);
            _root.Add(_letterboxBottom);
            _root.Add(_replayTag);
            _root.Add(_banner);
            _root.Add(_commentary);
        }

        static VisualElement Bar()
        {
            var bar = new VisualElement();
            bar.AddToClassList("letterbox");
            bar.pickingMode = PickingMode.Ignore;
            return bar;
        }

        // ── Public broadcast API ────────────────────────────────────────────

        /// <summary>Big centre callout: GOAL, the kickoff countdown, HALF TIME.</summary>
        public void Toast(string text, Color color, float seconds)
        {
            if (_toast == null) return;
            _toast.text = text;
            _toast.style.color = color;            // per-event colour is data
            Agent_UIStyle.SetShown(_toast, true);
            _toastUntil = Time.unscaledTime + seconds;
        }

        /// <summary>Commentary strip - a lane of its own so it never fights the goal toast.</summary>
        public void Say(string text, Color color, float seconds)
        {
            if (_commentary == null) return;
            _commentary.text = text;
            _commentary.style.color = color;
            Agent_UIStyle.SetShown(_commentary, true);
            _commentaryUntil = Time.unscaledTime + seconds;
        }

        /// <summary>Persistent banner that stays up until <see cref="HideBanner"/> (GOLDEN GOAL).</summary>
        public void ShowBanner(string text, Color color)
        {
            if (_banner == null) return;
            _banner.text = text;
            _banner.style.color = color;
            Agent_UIStyle.SetShown(_banner, true);
        }

        public void HideBanner() => Agent_UIStyle.SetShown(_banner, false);

        /// <summary>Letterbox bars + REPLAY tag, for the duration of a goal replay.</summary>
        public void SetReplayChrome(bool on)
        {
            Agent_UIStyle.SetShown(_letterboxTop, on);
            Agent_UIStyle.SetShown(_letterboxBottom, on);
            Agent_UIStyle.SetShown(_replayTag, on);
        }

        /// <summary>
        /// Toggles the pause overlay. Refused once the match is over - the end
        /// panel already owns the screen, and a pause layered on top of it would
        /// be a dead end with two competing sets of buttons.
        /// </summary>
        public void TogglePause()
        {
            if (!enableMatchFlow || MatchOver || _root == null) return;
            if (IsPaused) Resume();
            else Pause();
        }

        void Pause()
        {
            Agent_TimeFreeze.Acquire(_pauseToken);

            _pausePanel = new VisualElement();
            _pausePanel.AddToClassList("panel--scrim");

            var heading = new Label("PAUSED");
            heading.AddToClassList("score");
            heading.style.marginBottom = 8;
            _pausePanel.Add(heading);

            var score = new Label($"{_blueScore}  —  {_redScore}");
            score.AddToClassList("clock");
            score.style.marginBottom = 40;
            _pausePanel.Add(score);

            _pausePanel.Add(EndButton("RESUME", true, Resume));
            _pausePanel.Add(EndButton("MENU", false,
                () => { Agent_TimeFreeze.ReleaseAll(); SceneManager.LoadScene(menuScene); }));

            _root.Add(_pausePanel);
            Agent_UIStyle.PlayEntrance(_pausePanel);
        }

        void Resume()
        {
            if (_pausePanel != null && _pausePanel.parent != null)
                _pausePanel.parent.Remove(_pausePanel);
            _pausePanel = null;
            Agent_TimeFreeze.Release(_pauseToken);
        }

        /// <summary>Raise the deferred end panel. No-op once it is already up.</summary>
        public void ShowEndPanelNow()
        {
            if (_ended) return;
            ShowEndPanel();
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
                chip.AddToClassList("chip");

                var square = new Label(agent.rewards.playerName.Substring(0, 1));
                square.AddToClassList("chip__badge");
                // Profile colour and team tint are data, so they stay in code.
                square.style.backgroundColor = agent.rewards.playerColor;
                square.style.borderBottomColor = agent.team == Agent_Soccer.Team.Blue
                    ? Agent_UIStyle.BlueTeam : Agent_UIStyle.RedTeam;
                chip.Add(square);

                // Who is driving this body? The point of the BOT roster entry is
                // watching a trained brain play the rule-based benchmark, so the
                // scoreboard has to say which is which without guessing.
                var driver = new Label(agent.RuleBased ? "BOT" : "AI");
                driver.AddToClassList("chip__driver");
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
                Toast("RESET", Agent_UIStyle.TextMuted, 1f);
                return;
            }

            if (winner == Agent_Soccer.Team.Blue) _blueScore++; else _redScore++;

            var scorer = env.LastToucher;
            string who = scorer != null && scorer.rewards != null && scorer.team == winner
                ? scorer.rewards.playerName : winner.ToString().ToUpperInvariant();
            Toast($"GOAL — {who}",
                winner == Agent_Soccer.Team.Blue ? Agent_UIStyle.BlueTeam : Agent_UIStyle.RedTeam,
                1.6f);

            if (matchGoals <= 0 || (_blueScore < matchGoals && _redScore < matchGoals)) return;

            // Deferred: Agent_MatchFlow lets the replay and the final whistle run
            // first, then calls ShowEndPanelNow. Undeferred, the old behaviour.
            if (DeferEndPanel) _pendingEnd = true;
            else ShowEndPanel();
        }

        void ShowEndPanel()
        {
            _ended = true;
            _pendingEnd = false;
            // A pause left standing under the result card would keep a hold on the
            // clock that nothing can now release, because its button is gone.
            Resume();
            SetReplayChrome(false);
            HideBanner();
            Agent_TimeFreeze.Acquire(this);

            _endPanel = new VisualElement();
            _endPanel.AddToClassList("panel--scrim");

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

            // Man-of-the-match card, when a match flow is present to compute one.
            if (endPanelExtra != null)
            {
                var extra = endPanelExtra();
                if (extra != null) _endPanel.Add(extra);
            }

            _endPanel.Add(EndButton("REMATCH", true,
                () => { Agent_TimeFreeze.ReleaseAll(); SceneManager.LoadScene(SceneManager.GetActiveScene().name); }));
            _endPanel.Add(EndButton("MENU", false,
                () => { Agent_TimeFreeze.ReleaseAll(); SceneManager.LoadScene(menuScene); }));

            _root.Add(_endPanel);
            Agent_UIStyle.PlayEntrance(_endPanel);
        }

        static Button EndButton(string text, bool primary, System.Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.AddToClassList("btn");
            b.AddToClassList(primary ? "btn--primary" : "btn--ghost");
            return b;
        }

        // ── Per-frame ───────────────────────────────────────────────────────

        void Update()
        {
            HandleBackButton();

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
                float botStrength = env.CurrentBotStrength;
                if (step != _shownStep
                    || !Mathf.Approximately(goalWidth, _shownGoalWidth)
                    || !Mathf.Approximately(botStrength, _shownBotStrength))
                {
                    _stepLabel.text =
                        $"step {step}  ·  goal {goalWidth:0.0}m  ·  bot {botStrength:0.00}";
                    _shownStep = step;
                    _shownGoalWidth = goalWidth;
                    _shownBotStrength = botStrength;
                }
            }

            if (Time.unscaledTime > _toastUntil) Agent_UIStyle.SetShown(_toast, false);
            if (Time.unscaledTime > _commentaryUntil) Agent_UIStyle.SetShown(_commentary, false);

            UpdateBallControlMeter();
        }

        /// <summary>
        /// Android's back button arrives as Escape through the Input System. In a
        /// match, back means "get me out of here" - which is pause, not an
        /// immediate quit to the menu that would throw away the scoreline.
        /// </summary>
        void HandleBackButton()
        {
            if (!enableMatchFlow) return;
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null || !keyboard.escapeKey.wasPressedThisFrame) return;
            TogglePause();
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