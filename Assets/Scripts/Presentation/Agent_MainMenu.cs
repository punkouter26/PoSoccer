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

        // Panel root, kept so the roster card can be raised over the whole menu
        // rather than inside the scrolling squad column.
        VisualElement _root;
        VisualElement _cardOverlay, _cardHost;
        Label _cardCount;
        int _cardIndex;

        void OnEnable()
        {
            // Android defaults to 30 fps unless something asks for more. The menu is
            // the first scene loaded, and Agent_Bootstrap - which sets this - exists
            // only in SCN_Exhibition and SCN_Training, so the menu ran at 30 (measured
            // on device 2026-08-29). Set here rather than by adding Bootstrap to this
            // scene: Bootstrap also rewrites Physics2D and attaches a camera follow,
            // neither of which belongs on a menu with no pitch.
            Application.targetFrameRate = 60;

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

            _root = root;
            root.Clear();
            Agent_UIStyle.ApplyTheme(root);

            var safe = new VisualElement();
            // .screen--menu carries the background, centring and padding. The
            // SpaceEvenly choice is deliberate and documented in the stylesheet:
            // the squads change height as players are added, so a centred block
            // leaves a dead band above and below at small squad sizes.
            safe.AddToClassList("screen");
            safe.AddToClassList("screen--menu");
            // Safe area stays in code: a device measurement, not a design token.
            Agent_UIStyle.BindSafeArea(safe);
            root.Add(safe);

            var header = new VisualElement();
            header.AddToClassList("stack-center");
            header.AddToClassList("enter");

            var title = new Label("PoSoccer");
            title.AddToClassList("title");
            header.Add(title);

            var subtitle = new Label("tap a name to add · tap a card to remove");
            subtitle.AddToClassList("text-muted");
            header.Add(subtitle);
            safe.Add(header);

            safe.Add(BuildPresetRow());

            _blueStrip = new VisualElement();
            _redStrip = new VisualElement();
            safe.Add(BuildTeamSection("BLUE", Agent_UIStyle.BlueTeam, _blue, _blueStrip, out _blueCount));
            safe.Add(BuildTeamSection("RED", Agent_UIStyle.RedTeam, _red, _redStrip, out _redCount));

            // Pitch note rides just above PLAY so the size readout reads as a
            // caption on the button rather than a floating third element.
            var footer = new VisualElement();
            footer.AddToClassList("stack-center");
            footer.AddToClassList("enter");

            _pitchNote = new Label(string.Empty);
            _pitchNote.AddToClassList("text-muted");
            _pitchNote.style.marginBottom = 16;
            footer.Add(_pitchNote);

            _play = new Button(StartMatch) { text = "PLAY" };
            _play.AddToClassList("btn");
            _play.AddToClassList("btn--play");
            footer.Add(_play);

            // Sound could previously only be muted from inside a match, which is
            // the one place you cannot reach without starting one first.
            var options = new VisualElement();
            options.AddToClassList("row");
            options.style.marginTop = 18;
            options.Add(CardsButton());
            options.Add(Agent_UIStyle.SoundToggleButton());
            footer.Add(options);

            _backHint = new Label("press back again to exit");
            _backHint.AddToClassList("text-muted");
            _backHint.style.marginTop = 12;
            _backHint.style.opacity = 0f;
            footer.Add(_backHint);

            safe.Add(footer);

            RefreshAll();

            // Staged entrance: header first, footer last. Each element animates
            // from the offset in .enter-from once the class is cleared a frame
            // later, so the menu assembles itself instead of snapping into place.
            StageEntrance(header, 0);
            StageEntrance(footer, 90);
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
        /// <summary>
        /// Applies the entrance offset now and clears it after a delay, letting
        /// the USS transition animate the element in. Delay is in milliseconds
        /// and staggers elements down the screen.
        /// </summary>
        static void StageEntrance(VisualElement element, long delayMs)
        {
            if (element == null) return;
            element.AddToClassList("enter-from");
            element.schedule
                .Execute(() => element.RemoveFromClassList("enter-from"))
                .ExecuteLater(delayMs + 16);
        }

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
            section.style.width = 1000;

            var band = new VisualElement();
            band.style.height = 8; band.style.width = 340;
            band.style.backgroundColor = teamColor;
            band.style.marginBottom = 12;
            Agent_UIStyle.Round(band, 4);
            section.Add(band);

            // Header row: team name, [- N +] stepper, per-side CLEAR.
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.justifyContent = Justify.Center;
            header.style.marginBottom = 8;

            var name = new Label(teamLabel);
            name.style.fontSize = 52;
            name.style.color = teamColor;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.marginRight = 26;
            header.Add(name);

            header.Add(StepperButton("−", () => Resize(squad, squad.Count - 1)));

            countLabel = new Label();
            countLabel.style.fontSize = 48;
            countLabel.style.color = Agent_UIStyle.TextPrimary;
            countLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            countLabel.style.width = 120;
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
            strip.style.minHeight = 190;
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
                b.AddToClassList("btn");
                b.AddToClassList("btn--roster");
                b.style.unityFontStyleAndWeight = FontStyle.Bold;
                b.style.paddingLeft = 26; b.style.paddingRight = 26;
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
            b.style.width = 86; b.style.height = 86;
            b.style.fontSize = 54;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.style.color = Agent_UIStyle.TextPrimary;
            b.style.backgroundColor = Agent_UIStyle.PanelBg;
            Agent_UIStyle.Round(b, 14);
            return b;
        }

        static Button SmallButton(string text, Color background, System.Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.height = 74;
            b.style.fontSize = Agent_UIStyle.FontS;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.style.paddingLeft = 30; b.style.paddingRight = 30;
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
                empty.style.fontSize = Agent_UIStyle.FontXS;
                empty.style.color = Agent_UIStyle.TextMuted;
                empty.style.unityTextAlign = TextAnchor.MiddleCenter;
                empty.style.marginTop = 34;
                strip.Add(empty);
                return;
            }

            // Cards shrink as the squad grows so ten still fit two ranks of five.
            // Compact widened 170 -> 182 to buy room for legible type; five cards
            // plus margins is 5 * 192 = 960 px, still inside the 1080 panel.
            bool compact = squad.Count > 5;
            // 210 could not hold "STANDARD" at 44 px - verified clipped on device
            // 2026-08-29. The longest roster name sets this width, not the average.
            float width = compact ? 182f : 250f;

            for (int slot = 0; slot < squad.Count; slot++)
            {
                int captured = slot;
                var profile = squad[slot];
                var card = new Button(() => RemoveSlot(squad, captured));
                card.style.width = width;
                card.style.height = compact ? 150f : 174f;
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

                // 16 px and 21 px were roughly 0.7 mm and 0.9 mm of cap height on a
                // phone - present, but not readable. At 5v5 the card cannot hold
                // three lines at a legible size, so the step count (the least
                // useful of the three while picking a squad) is dropped instead of
                // shrinking everything below the floor.
                card.Add(CardLine(profile.playerName, compact ? 34 : 44, FontStyle.Bold, 1f));
                card.Add(CardLine(DriverLine(profile), compact ? 30 : 34, FontStyle.Normal, 0.8f));
                if (!compact)
                {
                    card.Add(CardLine(StepsLine(profile), 34, FontStyle.Bold, 0.85f));
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

        static string FormatSteps(int steps)
        {
            if (steps >= 1_000_000) return $"{steps / 1_000_000f:0.#}M";
            if (steps >= 1_000) return $"{steps / 1_000f:0}k";
            return steps.ToString();
        }

        // ── Player cards ────────────────────────────────────────────────────

        Button CardsButton()
        {
            var b = new Button(() => OpenCards(0)) { text = "PLAYER CARDS" };
            b.AddToClassList("btn");
            return b;
        }

        /// <summary>
        /// Raises the roster card over the whole menu.
        ///
        /// It is a modal layer rather than a fourth column on the menu because
        /// the card is dense - three tiles, seven attribute bars, a provenance
        /// line - and the menu is already a full portrait screen of squad
        /// controls. It also stays open while you add players, with the squad
        /// counts echoed in the nav row, so browsing the roster and picking
        /// from it are the same activity instead of two round trips.
        /// </summary>
        void OpenCards(int index)
        {
            if (_root == null || _roster == null || _roster.Length == 0) return;
            if (_cardOverlay != null) CloseCards();

            _cardOverlay = new VisualElement();
            _cardOverlay.AddToClassList("panel--scrim");
            // A tap on the scrim itself dismisses. The target check matters:
            // without it, every tap inside the card bubbles up here and closes
            // the panel the moment you press an attribute row.
            _cardOverlay.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (ReferenceEquals(evt.target, _cardOverlay)) CloseCards();
            });

            // The card scrolls. personalityNotes is a free-text design note and
            // KIM's and NICK's run to several lines, so card height is content
            // driven and cannot be assumed to fit: without this the provenance
            // line and the buttons below it walk off the bottom of the screen
            // on exactly the two profiles that have the most to say.
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.width = 1000;
            scroll.style.maxHeight = 1250;
            _cardOverlay.Add(scroll);
            _cardHost = scroll.contentContainer;
            // The card is 980 inside a 1000 viewport; without this it hugs the
            // left edge and the 20 px of slack all lands on one side.
            _cardHost.style.alignItems = Align.Center;

            var nav = new VisualElement();
            nav.AddToClassList("card-nav");
            nav.Add(NavArrow("<", () => ShowCard(_cardIndex - 1)));
            _cardCount = new Label();
            _cardCount.AddToClassList("card-nav__count");
            nav.Add(_cardCount);
            nav.Add(NavArrow(">", () => ShowCard(_cardIndex + 1)));
            _cardOverlay.Add(nav);

            var add = new VisualElement();
            add.AddToClassList("card-add");
            add.Add(AddButton("ADD TO BLUE", Agent_UIStyle.BlueTeam, _blue));
            add.Add(AddButton("ADD TO RED", Agent_UIStyle.RedTeam, _red));
            _cardOverlay.Add(add);

            var close = new Button(CloseCards) { text = "CLOSE" };
            close.AddToClassList("btn");
            close.style.marginTop = 18;
            _cardOverlay.Add(close);

            _root.Add(_cardOverlay);
            Agent_UIStyle.PlayEntrance(_cardOverlay);
            ShowCard(index);
        }

        Button NavArrow(string glyph, System.Action onClick)
        {
            var b = new Button(onClick) { text = glyph };
            b.AddToClassList("btn");
            b.AddToClassList("card-nav__arrow");
            return b;
        }

        Button AddButton(string text, Color teamColor, List<Reward_Settings> squad)
        {
            var b = new Button(() =>
            {
                AddToSquad(squad, _roster[_cardIndex]);
                UpdateCardCounts();
            })
            { text = text };
            b.AddToClassList("btn");
            b.AddToClassList("card-add__btn");
            b.style.backgroundColor = teamColor;
            return b;
        }

        /// <summary>Renders the profile at <paramref name="index"/>, wrapping both ways.</summary>
        void ShowCard(int index)
        {
            if (_cardHost == null || _roster == null || _roster.Length == 0) return;

            // Wrap rather than clamp so the arrows never dead-end, and take the
            // positive modulus so stepping back from the first entry lands on
            // the last instead of a negative index.
            int count = _roster.Length;
            _cardIndex = ((index % count) + count) % count;

            _cardHost.Clear();
            _cardHost.Add(Agent_PlayerCard.Build(_roster[_cardIndex], _roster, ruleBot));
            UpdateCardCounts();
        }

        void UpdateCardCounts()
        {
            if (_cardCount == null) return;
            _cardCount.text = $"{_cardIndex + 1}/{_roster.Length}\nBLUE {_blue.Count} · RED {_red.Count}";
        }

        void CloseCards()
        {
            if (_cardOverlay == null) return;
            if (_root != null) _root.Remove(_cardOverlay);
            _cardOverlay = null;
            _cardHost = null;
            _cardCount = null;
            // The squads changed underneath while the card was up.
            RefreshAll();
        }

        Label _backHint;
        float _backArmedUntil;

        /// <summary>
        /// Android's back button arrives as Escape through the Input System. The
        /// menu is the root of the navigation stack, so back means exit - but a
        /// single tap quitting the game outright is hostile, hence the standard
        /// press-twice confirmation rather than a modal nobody reads.
        /// </summary>
        void Update()
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null || !keyboard.escapeKey.wasPressedThisFrame) return;

            // The card is a modal layer, so back dismisses it before back means
            // anything to the menu underneath - and long before it means quit.
            if (_cardOverlay != null)
            {
                CloseCards();
                return;
            }

            if (Time.unscaledTime < _backArmedUntil)
            {
                Application.Quit();
                return;
            }

            _backArmedUntil = Time.unscaledTime + 2f;
            if (_backHint == null) return;
            _backHint.style.opacity = 1f;
            _backHint.schedule.Execute(() => _backHint.style.opacity = 0f).ExecuteLater(2000);
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
