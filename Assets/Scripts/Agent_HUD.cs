using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// Match HUD (UI Toolkit, portrait, safe-area). Uses the dead bands above and
    /// below the pitch: scoreboard on top, per-player identity chips with stamina
    /// on the bottom - nothing ever covers the play area. Includes a goal toast,
    /// a MENU button, and a first-to-N end panel (match flow off in training).
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
        VisualElement _root, _endPanel;
        readonly List<(Agent_Soccer agent, VisualElement fill)> _chips = new();
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
            band.style.flexDirection = FlexDirection.Row;
            band.style.justifyContent = Justify.SpaceBetween;
            band.style.alignItems = Align.Center;
            Agent_UIStyle.PadAll(band);

            _blueChips = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _redChips = new VisualElement { style = { flexDirection = FlexDirection.Row } };

            var center = new VisualElement();
            center.style.alignItems = Align.Center;
            center.Add(SmallButton("MENU",
                () => { Time.timeScale = 1f; SceneManager.LoadScene(menuScene); }));
            Button mute = null;
            mute = SmallButton(Agent_Audio.Muted ? "SND OFF" : "SND ON", () =>
            {
                Agent_Audio.Muted = !Agent_Audio.Muted;
                mute.text = Agent_Audio.Muted ? "SND OFF" : "SND ON";
            });
            mute.style.marginTop = 10;
            center.Add(mute);
            center.style.display = enableMatchFlow ? DisplayStyle.Flex : DisplayStyle.None;

            band.Add(_blueChips);
            band.Add(center);
            band.Add(_redChips);
            return band;
        }

        VisualElement _blueChips, _redChips;
        float _matchSeconds;

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

                var barBg = new VisualElement();
                barBg.style.width = 84; barBg.style.height = 18;
                barBg.style.marginTop = 8;
                barBg.style.backgroundColor = new Color(1f, 1f, 1f, 0.15f);
                Agent_UIStyle.Round(barBg, 9);
                var fill = new VisualElement();
                fill.style.height = 18;
                Agent_UIStyle.Round(fill, 9);
                barBg.Add(fill);
                chip.Add(barBg);

                (agent.team == Agent_Soccer.Team.Blue ? _blueChips : _redChips).Add(chip);
                _chips.Add((agent, fill));
            }
        }

        // ── Match flow ──────────────────────────────────────────────────────

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (!enableMatchFlow || _ended) return;

            if (winner == null)
            {
                // Stalemate or out-of-bounds: announce the reset so it reads as intended.
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
            if (_chips.Count == 0 && env.agents.Count > 0) BuildChips();

            if (enableMatchFlow)
            {
                if (!_ended) _matchSeconds += Time.deltaTime;
                _score.text = $"{_blueScore}  —  {_redScore}";
                _stepLabel.text = $"{(int)(_matchSeconds / 60):0}:{(int)(_matchSeconds % 60):00}";
            }
            else
            {
                // Training telemetry: raw steps + curriculum state belong here only.
                _stepLabel.text = $"step {env.StepCount}  ·  goal {env.CurrentGoalWidth:0.0}m";
            }

            if (_toast.style.display == DisplayStyle.Flex && Time.unscaledTime > _toastUntil)
                _toast.style.display = DisplayStyle.None;

            foreach (var (agent, fill) in _chips)
            {
                if (agent == null) continue;
                float ratio = agent.Stamina != null ? agent.Stamina.Ratio : 0f;
                fill.style.width = Length.Percent(ratio * 100f);
                fill.style.backgroundColor = Color.Lerp(
                    Agent_UIStyle.StaminaLow, Agent_UIStyle.StaminaHigh, ratio);
            }
        }
    }
}
