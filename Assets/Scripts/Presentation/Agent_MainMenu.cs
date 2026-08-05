using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// Opening menu (UI Toolkit, mobile portrait, safe-area): build a squad per
    /// side (0-10, independent, so 1v3 is expressible) and launch the match scene.
    ///
    /// Each side owns a roster picker and a strip of slot cards. Tapping a roster
    /// button *appends* that player to that side; tapping a filled slot *removes*
    /// it. That replaces the old tap-to-cycle card, which needed up to five taps
    /// to reach one player and gave no way to drop a slot from the middle. The
    /// [- N +] stepper is kept because it bulk-adds bots, so 10-a-side against the
    /// benchmark is still two taps. Presets cover the common matchups outright.
    ///
    /// A side may be empty while editing; PLAY stays disabled until both sides
    /// have at least one player. The whole tree is built in code, no UXML wiring.
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
        Button _play;

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

            ApplyPreset(2);

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
            title.style.fontSize = 92;
            title.style.color = Color.white;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            safe.Add(title);

            var subtitle = new Label("tap a name to add · tap a card to remove");
            subtitle.style.fontSize = 28;
            subtitle.style.color = Agent_UIStyle.TextMuted;
            subtitle.style.marginBottom = 18;
            safe.Add(subtitle);

            safe.Add(BuildPresetRow());

            _blueStrip = new VisualElement();
            _redStrip = new VisualElement();
            safe.Add(BuildTeamSection("BLUE", Agent_UIStyle.BlueTeam, _blue, _blueStrip, out _blueCount));
            safe.Add(BuildTeamSection("RED", Agent_UIStyle.RedTeam, _red, _redStrip, out _redCount));

            _pitchNote = new Label(string.Empty);
            _pitchNote.style.fontSize = 24;
            _pitchNote.style.color = Agent_UIStyle.TextMuted;
            _pitchNote.style.marginTop = 12;
            safe.Add(_pitchNote);

            _play = new Button(StartMatch) { text = "PLAY" };
            _play.style.fontSize = 60;
            _play.style.unityFontStyleAndWeight = FontStyle.Bold;
            _play.style.marginTop = 18;
            _play.style.paddingLeft = 110; _play.style.paddingRight = 110;
            _play.style.paddingTop = 20; _play.style.paddingBottom = 20;
            _play.style.backgroundColor = Agent_UIStyle.Accent;
            _play.style.color = Agent_UIStyle.TextPrimary;
            Agent_UIStyle.Round(_play);
            safe.Add(_play);

            var sound = Agent_UIStyle.SoundToggleButton();
            sound.style.color = Agent_UIStyle.TextMuted;
            sound.style.marginTop = 12;
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

        /// <summary>A wired profile, or the first roster entry when it is missing.</summary>
        Reward_Settings Or(Reward_Settings profile) => profile != null ? profile : _roster[0];

        // ── Presets ─────────────────────────────────────────────────────────

        VisualElement BuildPresetRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.Center;
            row.style.marginBottom = 20;

            row.Add(SmallButton("1v1", Agent_UIStyle.PanelBg, () => ApplyPreset(1)));
            row.Add(SmallButton("2v2", Agent_UIStyle.PanelBg, () => ApplyPreset(2)));
            row.Add(SmallButton("5v5", Agent_UIStyle.PanelBg, () => ApplyPreset(5)));
            row.Add(SmallButton("CLEAR ALL", Agent_UIStyle.PanelBg, ClearAll));
            return row;
        }

        /// <summary>
        /// Blue fields personalities (cycling the roster order, skipping the bot),
        /// red fields the benchmark bot. That is the matchup the benchmark grades,
        /// so the presets and the eval harness agree on what "NvN vs bot" means.
        /// </summary>
        void ApplyPreset(int perSide)
        {
            _blue.Clear();
            _red.Clear();

            var picks = Compact(standard, matt, kim, nick);
            for (int i = 0; i < perSide; i++)
            {
                _blue.Add(picks.Length > 0 ? picks[i % picks.Length] : Or(null));
                _red.Add(Bot());
            }
            RefreshAll();
        }

        void ClearAll()
        {
            _blue.Clear();
            _red.Clear();
            RefreshAll();
        }

        // ── Team section ────────────────────────────────────────────────────

        VisualElement BuildTeamSection(string teamLabel, Color teamColor,
            List<Reward_Settings> squad, VisualElement strip, out Label countLabel)
        {
            var section = new VisualElement();
            section.style.alignItems = Align.Center;
            section.style.marginBottom = 20;
            section.style.width = 1000;

            var band = new VisualElement();
            band.style.height = 6; band.style.width = 240;
            band.style.backgroundColor = teamColor;
            band.style.marginBottom = 8;
            Agent_UIStyle.Round(band, 3);
            section.Add(band);

            // Header row: team name, [- N +] stepper, per-side CLEAR.
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.justifyContent = Justify.Center;
            header.style.marginBottom = 8;

            var name = new Label(teamLabel);
            name.style.fontSize = 40;
            name.style.color = teamColor;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.marginRight = 22;
            header.Add(name);

            header.Add(StepperButton("−", () => Resize(squad, squad.Count - 1)));

            countLabel = new Label();
            countLabel.style.fontSize = 38;
            countLabel.style.color = Agent_UIStyle.TextPrimary;
            countLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            countLabel.style.width = 100;
            countLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            header.Add(countLabel);

            header.Add(StepperButton("+", () => Resize(squad, squad.Count + 1)));

            var clear = SmallButton("CLEAR", Agent_UIStyle.PanelBg, () => ClearSide(squad));
            clear.style.marginLeft = 22;
            header.Add(clear);
            section.Add(header);

            // Roster picker: one tap appends that player to this side.
            section.Add(BuildRosterPicker(squad, teamColor));

            // Slot strip: wraps to a second line once a squad passes five.
            strip.style.flexDirection = FlexDirection.Row;
            strip.style.flexWrap = Wrap.Wrap;
            strip.style.justifyContent = Justify.Center;
            strip.style.width = 1000;
            strip.style.minHeight = 116;
            section.Add(strip);

            return section;
        }

        VisualElement BuildRosterPicker(List<Reward_Settings> squad, Color teamColor)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.justifyContent = Justify.Center;
            row.style.width = 1000;
            row.style.marginBottom = 8;

            for (int i = 0; i < _roster.Length; i++)
            {
                var profile = _roster[i];
                var b = new Button(() => AddToSquad(squad, profile)) { text = profile.playerName };
                b.style.height = 56;
                b.style.fontSize = 24;
                b.style.unityFontStyleAndWeight = FontStyle.Bold;
                b.style.paddingLeft = 20; b.style.paddingRight = 20;
                b.style.marginLeft = 4; b.style.marginRight = 4;
                b.style.backgroundColor = profile.playerColor;
                b.style.color = Color.black;
                b.style.borderTopWidth = 2; b.style.borderBottomWidth = 2;
                b.style.borderLeftWidth = 2; b.style.borderRightWidth = 2;
                b.style.borderTopColor = teamColor; b.style.borderBottomColor = teamColor;
                b.style.borderLeftColor = teamColor; b.style.borderRightColor = teamColor;
                Agent_UIStyle.Round(b, 12);
                row.Add(b);
            }
            return row;
        }

        static Button StepperButton(string glyph, System.Action onClick)
        {
            var b = new Button(onClick) { text = glyph };
            b.style.width = 70; b.style.height = 70;
            b.style.fontSize = 44;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.style.color = Agent_UIStyle.TextPrimary;
            b.style.backgroundColor = Agent_UIStyle.PanelBg;
            Agent_UIStyle.Round(b, 14);
            return b;
        }

        static Button SmallButton(string text, Color background, System.Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.height = 56;
            b.style.fontSize = 24;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.style.paddingLeft = 22; b.style.paddingRight = 22;
            b.style.marginLeft = 5; b.style.marginRight = 5;
            b.style.color = Agent_UIStyle.TextPrimary;
            b.style.backgroundColor = background;
            Agent_UIStyle.Round(b, 12);
            return b;
        }

        // ── Squad edits ─────────────────────────────────────────────────────

        void AddToSquad(List<Reward_Settings> squad, Reward_Settings profile)
        {
            if (squad.Count >= Agent_MatchSetup.MAX_SQUAD) return;
            squad.Add(profile);
            RefreshAll();
        }

        void RemoveSlot(List<Reward_Settings> squad, int slot)
        {
            if (slot < 0 || slot >= squad.Count) return;
            squad.RemoveAt(slot);
            RefreshAll();
        }

        void ClearSide(List<Reward_Settings> squad)
        {
            squad.Clear();
            RefreshAll();
        }

        void Resize(List<Reward_Settings> squad, int size)
        {
            // Floor is 0, not 1: a side may be emptied while building a lineup.
            // PLAY is gated in RefreshAll instead, so an empty side can never launch.
            size = Mathf.Clamp(size, 0, Agent_MatchSetup.MAX_SQUAD);
            // New slots default to the benchmark bot - the common case is "my brain
            // against N bots", and it keeps a 10-a-side setup to two taps.
            while (squad.Count < size) squad.Add(Bot());
            while (squad.Count > size) squad.RemoveAt(squad.Count - 1);
            RefreshAll();
        }

        // ── Rendering ───────────────────────────────────────────────────────

        void RefreshAll()
        {
            RenderStrip(_blueStrip, _blue, Agent_UIStyle.BlueTeam);
            RenderStrip(_redStrip, _red, Agent_UIStyle.RedTeam);
            if (_blueCount != null) _blueCount.text = $"{_blue.Count}";
            if (_redCount != null) _redCount.text = $"{_red.Count}";

            bool playable = _blue.Count > 0 && _red.Count > 0;

            if (_pitchNote != null)
            {
                if (!playable)
                {
                    _pitchNote.text = "add at least one player to each side";
                }
                else
                {
                    Vector2 half = Agent_PitchSizing.HalfExtentsFor(_blue.Count, _red.Count);
                    _pitchNote.text =
                        $"{_blue.Count}v{_red.Count}  ·  pitch {half.x * 2f:0}m x {half.y * 2f:0}m";
                }
            }

            if (_play != null)
            {
                _play.SetEnabled(playable);
                _play.style.opacity = playable ? 1f : 0.4f;
            }
        }

        void RenderStrip(VisualElement strip, List<Reward_Settings> squad, Color teamColor)
        {
            if (strip == null) return;
            strip.Clear();

            if (squad.Count == 0)
            {
                var empty = new Label("empty — tap a name above");
                empty.style.fontSize = 22;
                empty.style.color = Agent_UIStyle.TextMuted;
                empty.style.unityTextAlign = TextAnchor.MiddleCenter;
                empty.style.marginTop = 34;
                strip.Add(empty);
                return;
            }

            // Cards shrink as the squad grows so ten still fit two ranks of five.
            bool compact = squad.Count > 5;
            float width = compact ? 150f : 178f;

            for (int slot = 0; slot < squad.Count; slot++)
            {
                int captured = slot;
                var profile = squad[slot];
                var card = new Button(() => RemoveSlot(squad, captured));
                card.style.width = width;
                card.style.height = compact ? 100f : 116f;
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

                card.Add(CardLine(profile.playerName, compact ? 22 : 26, FontStyle.Bold, 1f));
                card.Add(CardLine(DriverLine(profile), compact ? 15 : 17, FontStyle.Normal, 0.75f));
                card.Add(CardLine(StepsLine(profile), compact ? 15 : 17, FontStyle.Bold, 0.85f));
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

        static string FormatSteps(int steps)
        {
            if (steps >= 1_000_000) return $"{steps / 1_000_000f:0.#}M";
            if (steps >= 1_000) return $"{steps / 1_000f:0}k";
            return steps.ToString();
        }

        void StartMatch()
        {
            if (_blue.Count == 0 || _red.Count == 0) return;
            Agent_MatchSetup.Applied = true;
            Agent_MatchSetup.BlueSquad = _blue.ToArray();
            Agent_MatchSetup.RedSquad = _red.ToArray();
            SceneManager.LoadScene(matchScene);
        }
    }
}
