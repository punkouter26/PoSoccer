using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// Opening menu (UI Toolkit, mobile portrait, safe-area): set a squad size per
    /// side (1-10, independent, so 1v3 is expressible) and choose who fills each
    /// slot, then launch the match scene.
    ///
    /// The old layout gave every slot its own five-button row, which does not
    /// survive twenty slots. Instead each side gets a stepper and a wrapping strip
    /// of compact slot cards; tapping a card cycles it through the roster. New
    /// slots default to BOT, so a 10v10 against the benchmark is two taps.
    /// The whole tree is built in code so no UXML wiring is needed.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Agent_MainMenu : MonoBehaviour
    {
        [Header("Roster (profile assets)")]
        public Reward_Settings standard;
        public Reward_Settings matt;
        public Reward_Settings kim;
        public Reward_Settings nick;
        [Tooltip("The rule-based benchmark opponent (Reward_BOT). Never carries a brainModel, " +
                 "so picking it always fields Agent_HeuristicBot - that is how a trained brain " +
                 "gets measured against the bot inside a normal match.")]
        public Reward_Settings ruleBot;

        [Header("Flow")]
        public string matchScene = "SCN_Exhibition";

        Reward_Settings[] _roster;
        readonly List<Reward_Settings> _blue = new();
        readonly List<Reward_Settings> _red = new();

        VisualElement _blueStrip, _redStrip;
        Label _blueCount, _redCount, _pitchNote;

        void OnEnable()
        {
            var doc = GetComponent<UIDocument>();
            var root = doc.rootVisualElement;
            if (doc.panelSettings == null || root == null)
            {
                Debug.LogWarning("Agent_MainMenu: UIDocument needs a PanelSettings asset.");
                return;
            }

            // BOT sits last so the four personalities keep their familiar order.
            _roster = Compact(standard, matt, kim, nick, ruleBot);
            if (_roster.Length == 0)
            {
                Debug.LogWarning("Agent_MainMenu: no roster profiles wired; menu disabled.");
                return;
            }

            // Default matchup: the trained baseline pair against the benchmark bot.
            _blue.Clear();
            _blue.Add(standard != null ? standard : _roster[0]);
            _blue.Add(nick != null ? nick : _roster[0]);
            _red.Clear();
            _red.Add(Bot());
            _red.Add(Bot());

            root.Clear();
            var safe = new VisualElement();
            safe.style.flexGrow = 1;
            Agent_UIStyle.ApplySafeArea(safe);
            safe.style.backgroundColor = Agent_UIStyle.Background;
            safe.style.alignItems = Align.Center;
            safe.style.justifyContent = Justify.Center;
            root.Add(safe);

            // All sizes are in 1080x1920 reference-resolution units (9:16 per
            // UNITY_RULES; PanelSettings scales the panel to the actual screen,
            // match-width).
            var title = new Label("PoSoccer");
            title.style.fontSize = 104;
            title.style.color = Color.white;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            safe.Add(title);

            var subtitle = new Label("set the squads, tap a slot to swap the player");
            subtitle.style.fontSize = 30;
            subtitle.style.color = Agent_UIStyle.TextMuted;
            subtitle.style.marginBottom = 34;
            safe.Add(subtitle);

            _blueStrip = new VisualElement();
            _redStrip = new VisualElement();
            safe.Add(BuildTeamSection("BLUE", Agent_UIStyle.BlueTeam, _blue, _blueStrip, out _blueCount));
            safe.Add(BuildTeamSection("RED", Agent_UIStyle.RedTeam, _red, _redStrip, out _redCount));

            _pitchNote = new Label(string.Empty);
            _pitchNote.style.fontSize = 24;
            _pitchNote.style.color = Agent_UIStyle.TextMuted;
            _pitchNote.style.marginTop = 18;
            safe.Add(_pitchNote);

            var play = new Button(StartMatch) { text = "PLAY" };
            play.style.fontSize = 64;
            play.style.unityFontStyleAndWeight = FontStyle.Bold;
            play.style.marginTop = 22;
            play.style.paddingLeft = 110; play.style.paddingRight = 110;
            play.style.paddingTop = 22; play.style.paddingBottom = 22;
            play.style.backgroundColor = Agent_UIStyle.Accent;
            play.style.color = Agent_UIStyle.TextPrimary;
            Agent_UIStyle.Round(play);
            safe.Add(play);

            var sound = Agent_UIStyle.SoundToggleButton();
            sound.style.color = Agent_UIStyle.TextMuted;
            sound.style.marginTop = 14;
            safe.Add(sound);

            RefreshAll();
        }

        static Reward_Settings[] Compact(params Reward_Settings[] entries)
        {
            var list = new List<Reward_Settings>(entries.Length);
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i] != null) list.Add(entries[i]);
            }
            return list.ToArray();
        }

        /// <summary>The benchmark bot when it is wired, else the last roster entry.</summary>
        Reward_Settings Bot() => ruleBot != null ? ruleBot : _roster[_roster.Length - 1];

        // ── Team section ────────────────────────────────────────────────────

        VisualElement BuildTeamSection(string teamLabel, Color teamColor,
            List<Reward_Settings> squad, VisualElement strip, out Label countLabel)
        {
            var section = new VisualElement();
            section.style.alignItems = Align.Center;
            section.style.marginBottom = 26;
            section.style.width = 1000;

            var band = new VisualElement();
            band.style.height = 6; band.style.width = 240;
            band.style.backgroundColor = teamColor;
            band.style.marginBottom = 10;
            Agent_UIStyle.Round(band, 3);
            section.Add(band);

            // Header row: team name on the left, [-  N  +] stepper on the right.
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.justifyContent = Justify.Center;
            header.style.marginBottom = 12;

            var name = new Label(teamLabel);
            name.style.fontSize = 44;
            name.style.color = teamColor;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.marginRight = 28;
            header.Add(name);

            header.Add(StepperButton("−", () => Resize(squad, squad.Count - 1)));

            countLabel = new Label();
            countLabel.style.fontSize = 40;
            countLabel.style.color = Agent_UIStyle.TextPrimary;
            countLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            countLabel.style.width = 120;
            countLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            header.Add(countLabel);

            header.Add(StepperButton("+", () => Resize(squad, squad.Count + 1)));
            section.Add(header);

            // Slot strip: wraps to a second line once a squad passes five.
            strip.style.flexDirection = FlexDirection.Row;
            strip.style.flexWrap = Wrap.Wrap;
            strip.style.justifyContent = Justify.Center;
            strip.style.width = 1000;
            section.Add(strip);

            return section;
        }

        static Button StepperButton(string glyph, System.Action onClick)
        {
            var b = new Button(onClick) { text = glyph };
            b.style.width = 76; b.style.height = 76;
            b.style.fontSize = 46;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.style.color = Agent_UIStyle.TextPrimary;
            b.style.backgroundColor = Agent_UIStyle.PanelBg;
            Agent_UIStyle.Round(b, 14);
            return b;
        }

        void Resize(List<Reward_Settings> squad, int size)
        {
            size = Mathf.Clamp(size, 1, Agent_MatchSetup.MAX_SQUAD);
            // New slots default to the benchmark bot - the common case is "my brain
            // against N bots", and it keeps a 10-a-side setup to two taps.
            while (squad.Count < size) squad.Add(Bot());
            while (squad.Count > size) squad.RemoveAt(squad.Count - 1);
            RefreshAll();
        }

        void Cycle(List<Reward_Settings> squad, int slot)
        {
            int current = System.Array.IndexOf(_roster, squad[slot]);
            squad[slot] = _roster[(current + 1 + _roster.Length) % _roster.Length];
            RefreshAll();
        }

        // ── Rendering ───────────────────────────────────────────────────────

        void RefreshAll()
        {
            RenderStrip(_blueStrip, _blue, Agent_UIStyle.BlueTeam);
            RenderStrip(_redStrip, _red, Agent_UIStyle.RedTeam);
            if (_blueCount != null) _blueCount.text = $"{_blue.Count}";
            if (_redCount != null) _redCount.text = $"{_red.Count}";

            if (_pitchNote != null)
            {
                Vector2 half = Agent_PitchSizing.HalfExtentsFor(_blue.Count, _red.Count);
                _pitchNote.text =
                    $"{_blue.Count}v{_red.Count}  ·  pitch {half.x * 2f:0}m x {half.y * 2f:0}m";
            }
        }

        void RenderStrip(VisualElement strip, List<Reward_Settings> squad, Color teamColor)
        {
            if (strip == null) return;
            strip.Clear();

            // Cards shrink as the squad grows so ten still fit two ranks of five.
            bool compact = squad.Count > 5;
            float width = compact ? 150f : 178f;

            for (int slot = 0; slot < squad.Count; slot++)
            {
                int captured = slot;
                var profile = squad[slot];
                var card = new Button(() => Cycle(squad, captured));
                card.style.width = width;
                card.style.height = compact ? 108f : 136f;
                card.style.marginLeft = 5; card.style.marginRight = 5;
                card.style.marginBottom = 8;
                card.style.paddingTop = 4; card.style.paddingBottom = 4;
                card.style.paddingLeft = 2; card.style.paddingRight = 2;
                card.style.backgroundColor = profile.playerColor;
                card.style.alignItems = Align.Center;
                card.style.justifyContent = Justify.Center;
                card.style.borderTopWidth = 3; card.style.borderBottomWidth = 3;
                card.style.borderLeftWidth = 3; card.style.borderRightWidth = 3;
                card.style.borderTopColor = teamColor; card.style.borderBottomColor = teamColor;
                card.style.borderLeftColor = teamColor; card.style.borderRightColor = teamColor;

                card.Add(CardLine(profile.playerName, compact ? 22 : 28, FontStyle.Bold, 1f));
                card.Add(CardLine(DriverLine(profile), compact ? 15 : 18, FontStyle.Normal, 0.75f));
                card.Add(CardLine(StepsLine(profile), compact ? 15 : 18, FontStyle.Bold, 0.85f));
                if (!compact)
                {
                    card.Add(CardLine(EvalLine(profile), 16, FontStyle.Normal, 0.7f));
                }
                strip.Add(card);
            }
        }

        // ── Roster card text ────────────────────────────────────────────────

        static Label CardLine(string text, int fontSize, FontStyle weight, float opacity)
        {
            var label = new Label(text);
            label.style.fontSize = fontSize;
            label.style.unityFontStyleAndWeight = weight;
            label.style.color = Color.black;
            label.style.opacity = opacity;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            return label;
        }

        /// <summary>Who actually drives this body: a trained brain or the scripted bot.</summary>
        string DriverLine(Reward_Settings profile)
        {
            if (ReferenceEquals(profile, ruleBot)) return "rule-based";
            return profile.brainModel != null ? "trained AI" : "(bot)";
        }

        /// <summary>How much training is behind this player's brain.</summary>
        string StepsLine(Reward_Settings profile)
        {
            if (ReferenceEquals(profile, ruleBot)) return "scripted";
            if (profile.brainModel == null) return "no brain";
            if (profile.trainingSteps <= 0) return "steps unknown";
            return $"{FormatSteps(profile.trainingSteps)} steps";
        }

        /// <summary>
        /// Measured win rate against the full-strength bot. Bot-vs-bot is ~43%, so
        /// the number is only meaningful next to that - it is shown raw rather than
        /// dressed up as a grade.
        /// </summary>
        static string EvalLine(Reward_Settings profile)
        {
            if (profile.brainModel == null) return string.Empty;
            if (profile.evalWinRate < 0f) return "unrated";
            return $"{profile.evalWinRate * 100f:0}% vs bot";
        }

        static string FormatSteps(int steps)
        {
            if (steps >= 1_000_000) return $"{steps / 1_000_000f:0.#}M";
            if (steps >= 1_000) return $"{steps / 1_000f:0}k";
            return steps.ToString();
        }

        void StartMatch()
        {
            Agent_MatchSetup.Applied = true;
            Agent_MatchSetup.BlueSquad = _blue.ToArray();
            Agent_MatchSetup.RedSquad = _red.ToArray();
            SceneManager.LoadScene(matchScene);
        }
    }
}
