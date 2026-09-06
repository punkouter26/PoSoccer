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
    /// have at least one player.
    ///
    /// 2026-09-06 - THE TREE IS NOW A TEMPLATE. Structure lives in
    /// Resources/Menu.uxml and appearance in Resources/PoSoccerTheme.uss; this
    /// class binds them and builds only what is data-shaped (a roster button per
    /// profile, a card per squad slot). It also owns the accessibility shelf,
    /// which is on this screen rather than behind a settings modal because a
    /// player who cannot separate the two team colours cannot navigate three
    /// taps into a game they cannot read.
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

        const string TEMPLATE_RESOURCE = "Menu";

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
            Build();
        }

        /// <summary>
        /// Instantiates Resources/Menu.uxml into the panel and binds it.
        ///
        /// THE TREE USED TO BE BUILT ENTIRELY IN CODE, and this was the last
        /// screen where that was still true - roughly forty style assignments
        /// restating rules the stylesheet already carried, which is precisely how
        /// the menu and the HUD had drifted apart before the design system
        /// existed. Structure now lives in the template; only the parts whose
        /// SHAPE is data (a roster button per profile, a card per squad slot) are
        /// still built here, into named containers.
        ///
        /// Separate from OnEnable because a settings change rebuilds the screen:
        /// the palette and type scale are USS variables on the root, and the
        /// squad strips carry team colours baked in at build time, so re-running
        /// this is both the simplest and the most honest way to apply them.
        /// </summary>
        void Build()
        {
            _root.Clear();
            Agent_UIStyle.ApplyTheme(_root);

            var template = Resources.Load<VisualTreeAsset>(TEMPLATE_RESOURCE);
            if (template == null)
            {
                // Unlike the HUD, the menu IS the screen - failing quietly here
                // means an unusable game, so this is an error, not a warning.
                Debug.LogError($"Agent_MainMenu: '{TEMPLATE_RESOURCE}' not found in Resources; " +
                               "the menu cannot be built.");
                return;
            }

            template.CloneTree(_root);

            var safe = _root.Q<VisualElement>("safe");
            var header = _root.Q<VisualElement>("header");
            var footer = _root.Q<VisualElement>("footer");
            _blueStrip = _root.Q<VisualElement>("strip-blue");
            _redStrip = _root.Q<VisualElement>("strip-red");
            _blueCount = _root.Q<Label>("count-blue");
            _redCount = _root.Q<Label>("count-red");
            _pitchNote = _root.Q<Label>("pitch-note");
            _play = _root.Q<Button>("play");
            _backHint = _root.Q<Label>("back-hint");
            var blueRoster = _root.Q<VisualElement>("roster-blue");
            var redRoster = _root.Q<VisualElement>("roster-red");
            var options = _root.Q<VisualElement>("options");
            var settings = _root.Q<VisualElement>("settings");

            if (safe == null || _blueStrip == null || _redStrip == null || _play == null ||
                blueRoster == null || redRoster == null || options == null || settings == null)
            {
                Debug.LogError($"Agent_MainMenu: '{TEMPLATE_RESOURCE}' is missing a bound element. " +
                               "Agent_EditMode_Theme pins the full name list.");
                return;
            }

            // Safe area stays in code: a device measurement, not a design token.
            Agent_UIStyle.BindSafeArea(safe);

            Click("preset-1v1", () => ApplyPreset(1));
            Click("preset-2v2", () => ApplyPreset(2));
            Click("preset-5v5", () => ApplyPreset(5));
            Click("preset-clear", ClearAll);

            Click("minus-blue", () => Resize(_blue, _blue.Count - 1));
            Click("plus-blue", () => Resize(_blue, _blue.Count + 1));
            Click("clear-blue", () => ClearSide(_blue));
            Click("minus-red", () => Resize(_red, _red.Count - 1));
            Click("plus-red", () => Resize(_red, _red.Count + 1));
            Click("clear-red", () => ClearSide(_red));

            _play.clicked += StartMatch;

            FillRoster(blueRoster, _blue, "menu__pick--blue");
            FillRoster(redRoster, _red, "menu__pick--red");

            // Sound could previously only be muted from inside a match, which is
            // the one place you cannot reach without starting one first.
            options.Add(CardsButton());
            options.Add(GalleryButton());
            options.Add(Agent_UIStyle.SoundToggleButton());

            BuildSettings(settings);

            RefreshAll();

            // Staged entrance: header first, footer last. Each element animates
            // from the offset in .enter-from once the class is cleared a frame
            // later, so the menu assembles itself instead of snapping into place.
            StageEntrance(header, 0);
            StageEntrance(footer, 90);
        }

        /// <summary>Binds a click handler to a named button, tolerating absence.</summary>
        void Click(string name, System.Action onClick)
        {
            var button = _root.Q<Button>(name);
            if (button != null) button.clicked += onClick;
        }

        /// <summary>
        /// The accessibility shelf: colour-vision palette, type scale, haptics.
        ///
        /// Each button says what it is currently SET TO, not what it would switch
        /// to. A control labelled with its next state reads as an instruction and
        /// leaves the player with no way to know where they are - which matters
        /// most for exactly the people this row exists for.
        /// </summary>
        void BuildSettings(VisualElement host)
        {
            var palette = new Button { text = $"COLOURS: {Agent_Palette.Label(Agent_Palette.Current)}" };
            palette.AddToClassList("btn");
            palette.clicked += () =>
            {
                Agent_Palette.CycleMode();
                // A full rebuild, not a restyle: the squad cards and roster
                // buttons carry team colour in their classes, and the USS
                // variables only re-cascade from the root.
                Build();
            };
            host.Add(palette);

            var type = new Button { text = $"TEXT: {TypeLabel(Agent_Palette.TypeScale)}" };
            type.AddToClassList("btn");
            type.clicked += () =>
            {
                Agent_Palette.CycleTypeScale();
                Build();
            };
            host.Add(type);

            var haptics = new Button();
            haptics.AddToClassList("btn");
            haptics.text = Agent_Haptics.Enabled ? "BUZZ ON" : "BUZZ OFF";
            haptics.clicked += () =>
            {
                Agent_Haptics.Enabled = !Agent_Haptics.Enabled;
                haptics.text = Agent_Haptics.Enabled ? "BUZZ ON" : "BUZZ OFF";
            };
            host.Add(haptics);
        }

        static string TypeLabel(int step)
        {
            switch (step)
            {
                case 1: return "LARGE";
                case 2: return "LARGER";
                default: return "NORMAL";
            }
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

        // ── Roster picker ───────────────────────────────────────────────────

        /// <summary>
        /// One button per roster entry, filled into the template's container.
        ///
        /// The background is the PROFILE's colour - that is identity data, not
        /// design - while the border is the TEAM's, and comes from a class so the
        /// colour-vision palettes reach it. That division is the rule for every
        /// dynamic element in this file: data inline, design in USS.
        /// </summary>
        void FillRoster(VisualElement host, List<Reward_Settings> squad, string teamClass)
        {
            host.Clear();
            for (int i = 0; i < _roster.Length; i++)
            {
                var profile = _roster[i];
                var button = new Button(() => AddToSquad(squad, profile)) { text = profile.playerName };
                button.AddToClassList("btn");
                button.AddToClassList("menu__pick");
                button.AddToClassList(teamClass);
                button.style.backgroundColor = profile.playerColor;
                host.Add(button);
            }
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
            RenderStrip(_blueStrip, _blue, "slot--blue");
            RenderStrip(_redStrip, _red, "slot--red");
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

            // .btn:disabled already carries the 0.4 opacity, so setting it inline
            // here as well would be the stylesheet and the code disagreeing about
            // one rule - the exact drift the design system exists to end.
            if (_play != null) _play.SetEnabled(playable);
        }

        void RenderStrip(VisualElement strip, List<Reward_Settings> squad, string teamClass)
        {
            if (strip == null) return;
            strip.Clear();

            if (squad.Count == 0)
            {
                var empty = new Label("empty — tap a name above");
                empty.AddToClassList("menu__empty");
                strip.Add(empty);
                return;
            }

            // Cards shrink as the squad grows so ten still fit two ranks of five.
            // The two sizes, and the decision to DROP the step-count line rather
            // than shrink every line below the 12sp legibility floor, now live in
            // .slot / .slot--compact. Only the branch survives here, because how
            // many players a squad has is data.
            bool compact = squad.Count > 5;

            for (int slot = 0; slot < squad.Count; slot++)
            {
                int captured = slot;
                var profile = squad[slot];
                var card = new Button(() => RemoveSlot(squad, captured));
                card.AddToClassList("slot");
                card.AddToClassList(teamClass);
                if (compact) card.AddToClassList("slot--compact");
                // The profile's own colour: identity, not design.
                card.style.backgroundColor = profile.playerColor;

                card.Add(SlotLine(profile.playerName, "slot__name"));
                card.Add(SlotLine(DriverLine(profile), "slot__driver"));
                if (!compact) card.Add(SlotLine(StepsLine(profile), "slot__steps"));

                strip.Add(card);
            }
        }

        // ── Roster card text ────────────────────────────────────────────────

        static Label SlotLine(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
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
        /// Opens the checkpoint gallery: every trained brain on its own pitch,
        /// against the same bot, side by side. Disabled with a reason when the
        /// roster has nothing trained to exhibit, rather than loading a scene
        /// that would show four empty pitches.
        /// </summary>
        Button GalleryButton()
        {
            var b = new Button(OpenGallery) { text = "GALLERY" };
            b.AddToClassList("btn");

            if (TrainedRoster().Length == 0)
            {
                b.SetEnabled(false);
                b.tooltip = "No trained brains to exhibit yet - run update-model.ps1 first.";
            }
            return b;
        }

        void OpenGallery()
        {
            var roster = TrainedRoster();
            if (roster.Length == 0) return;

            Agent_MatchSetup.Clear();
            Agent_MatchSetup.GalleryMode = true;
            Agent_MatchSetup.GalleryProfiles = roster;
            Agent_MatchSetup.GalleryOpponent = ruleBot;

            // The gallery still needs a lineup for the pitch it starts from:
            // Agent_MatchLoader runs before Agent_Gallery and would otherwise fall
            // back to the scene's serialized defaults, which are not what was asked
            // for. One brain a side; Agent_Gallery reassigns per pitch after that.
            Agent_MatchSetup.Applied = true;
            Agent_MatchSetup.BlueSquad = new[] { roster[0] };
            Agent_MatchSetup.RedSquad = new[] { ruleBot != null ? ruleBot : roster[0] };

            SceneManager.LoadScene(matchScene);
        }

        /// <summary>Roster entries that actually carry a trained brain, in menu order.</summary>
        Reward_Settings[] TrainedRoster()
        {
            var all = Compact(standard, matt, kim, nick);
            var trained = new List<Reward_Settings>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].brainModel != null) trained.Add(all[i]);
            }
            return trained.ToArray();
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
            Agent_UIStyle.SetShown(_backHint, true);
            _backHint.schedule.Execute(() => Agent_UIStyle.SetShown(_backHint, false)).ExecuteLater(2000);
        }

        void StartMatch()
        {
            if (_blue.Count == 0 || _red.Count == 0) return;
            // Statics outlive a scene load in a player build, so a PLAY taken
            // after a visit to the gallery would otherwise load the grid again.
            Agent_MatchSetup.Clear();
            Agent_MatchSetup.Applied = true;
            Agent_MatchSetup.BlueSquad = _blue.ToArray();
            Agent_MatchSetup.RedSquad = _red.ToArray();
            SceneManager.LoadScene(matchScene);
        }
    }
}
