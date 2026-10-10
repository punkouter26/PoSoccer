using System.Collections.Generic;
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
    ///
    /// 2026-10-10 - WHAT A MATCH SHOWS BY DEFAULT. Scoreboard, clock, identity
    /// chips, and the sound / pause / broadcast controls. Everything that is
    /// commentary ON the match rather than the match - the win-probability strip,
    /// the stat ticker, the director's status bug, the ball-control meter - sits
    /// behind ONE switch (<see cref="BroadcastEnabled"/>, the STATS button, also in
    /// the settings sheet), persisted and OFF by default. The components behind
    /// those readouts keep running either way: Agent_Director reads
    /// Agent_WinProbability's threat signal whether or not its strip is painted.
    /// Hidden elements are display:none, so the bands genuinely shrink and
    /// <see cref="TopBandFraction"/> / <see cref="BottomBandFraction"/> report the
    /// smaller figure to the camera with no second bookkeeping.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Agent_HUD : MonoBehaviour
    {
        public Agent_EnvController env;
        [Tooltip("Enable goal toasts, scoreboard persistence and the end panel. Off in the training scene.")]
        public bool enableMatchFlow = true;
        [Tooltip("First team to this many goals ends the match (0 = endless).")]
        public int matchGoals = 5;
        [Tooltip("Hard match length in seconds; the leader wins, level is a draw. " +
                 "0 = no limit. A bound, not a pace: it exists so a match always ends.")]
        public float matchTimeLimit = 480f;
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
        // Whether the broadcast readouts are painted right now. Seeded from the
        // persisted BroadcastEnabled when the HUD builds. This was a serialized
        // field until 2026-10-10 - a switch only reachable from the Inspector,
        // which on a phone is no switch at all.
        bool _showBroadcastStrip;

        const string BROADCAST_KEY = "posoccer_broadcast";

        /// <summary>
        /// The player's persisted choice for the broadcast readouts (win-probability
        /// strip, stat ticker, status bug, ball-control meter). Default OFF.
        ///
        /// Static because it is a preference, not scene state: the settings sheet
        /// writes it from the MENU, where no HUD exists, and the next match reads it
        /// when its HUD builds.
        /// </summary>
        public static bool BroadcastEnabled
        {
            get => PlayerPrefs.GetInt(BROADCAST_KEY, 0) == 1;
            set
            {
                PlayerPrefs.SetInt(BROADCAST_KEY, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        VisualElement _root, _endPanel, _blueChips, _redChips;
        VisualElement _meter;
        Button _broadcastButton, _soundButton;
        VisualElement _ballControlBlue, _ballControlRed;   // halves of the meter
        VisualElement _topBand, _bottomBand;
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

        // Broadcast telemetry lanes, added with the director / win-probability /
        // stat-ticker layer. Each is driven by exactly one component and no-ops
        // when that component is absent, so the HUD never has to know which of
        // them a given scene installed.
        VisualElement _winProbBar, _winProbBlue, _winProbRed;
        Label _winProbLabel, _ticker, _broadcastTag;
        int _shownWinPercent = -1;
        string _shownTicker, _shownBroadcastTag;

        /// <summary>
        /// Pause holds the clock through its own token, so it composes with the
        /// replay, the countdown and the end panel instead of fighting them - the
        /// clock resumes only when every holder has let go.
        /// </summary>
        readonly object _pauseToken = new Agent_TimeFreeze.Hold("HUD.Pause");

        /// <summary>
        /// Shows or hides the broadcast strip (win-probability bar + label, stat ticker,
        /// broadcast tag) at runtime. Hidden by default - see the note in
        /// BuildFromTemplate for why it is off and why the components behind it keep
        /// running.
        ///
        /// Public because the floor logic in <see cref="SetWinProbability"/> still has to
        /// be testable: Agent_PlayMode_Broadcast asserts the bar never collapses to zero
        /// width, which is a statement about the FLOOR, not about whether the strip is
        /// currently on screen. Without this the test would only be able to observe the
        /// hidden state and would fail for the wrong reason.
        /// </summary>
        public void SetBroadcastStripVisible(bool visible)
        {
            _showBroadcastStrip = visible;
            ApplyBroadcastVisibility();
        }

        /// <summary>
        /// The single place that decides which broadcast readouts are laid out.
        ///
        /// display:none, never opacity: a hidden strip must give its height back to
        /// the band, because the band's measured height is what the camera frames
        /// the pitch against (see <see cref="TopBandFraction"/>). An invisible strip
        /// that still occupied the band would keep the camera pulled back for a
        /// readout nobody can see.
        ///
        /// The win-probability pair additionally needs a match: it reads the score,
        /// and a training scene has none. The meter is the reverse - outside a match
        /// there is no STATS button to bring it back, so there it always shows.
        /// </summary>
        void ApplyBroadcastVisibility()
        {
            var broadcast = _showBroadcastStrip ? DisplayStyle.Flex : DisplayStyle.None;
            var probability = _showBroadcastStrip && enableMatchFlow ? DisplayStyle.Flex : DisplayStyle.None;
            var meter = _showBroadcastStrip || !enableMatchFlow ? DisplayStyle.Flex : DisplayStyle.None;

            if (_winProbBar != null) _winProbBar.style.display = probability;
            if (_winProbLabel != null) _winProbLabel.style.display = probability;
            if (_ticker != null) _ticker.style.display = broadcast;
            if (_broadcastTag != null) _broadcastTag.style.display = broadcast;
            if (_meter != null) _meter.style.display = meter;
            if (_broadcastButton != null)
                _broadcastButton.EnableInClassList(Agent_UIStyle.ON, _showBroadcastStrip);
        }

        /// <summary>The STATS button: flips the readouts and remembers the choice.</summary>
        void ToggleBroadcast()
        {
            BroadcastEnabled = !_showBroadcastStrip;
            SetBroadcastStripVisible(BroadcastEnabled);
        }

        public bool IsPaused => _pausePanel != null;

        public int BlueScore => _blueScore;
        public int RedScore => _redScore;

        /// <summary>
        /// The panel root, for overlays that must sit in the same UI Toolkit panel
        /// as the HUD - currently the gallery's pitch captions, which project world
        /// positions into panel space and so have to live in this panel to use its
        /// scale factor. Null before OnEnable and when showHud is false.
        /// </summary>
        public VisualElement OverlayRoot => _root;

        /// <summary>
        /// Fraction of the screen's HEIGHT that the top band covers, 0..1. Zero
        /// when the HUD is off or the band has not been laid out yet.
        ///
        /// THIS EXISTS BECAUSE THE CAMERA HAS TO KNOW. The class docstring above
        /// claims the bands sit "above and below the pitch" so that "nothing ever
        /// covers the play area". That was true of the authored 36 x 54 letterboxed
        /// pitch and has been false since Agent_CameraFollow started filling the
        /// portrait viewport top to bottom: the bands are an OVERLAY on a full-screen
        /// pitch, and at 2v2 they cover roughly the top 1.9 m and bottom 3.9 m of the
        /// view - which is where both goal mouths are. Agent_CameraFollow reads these
        /// two numbers and frames the pitch into what is left.
        ///
        /// Reported as a fraction rather than in pixels on purpose: worldBound is in
        /// PANEL units, which are screen pixels divided by the panel's scale factor,
        /// and the ratio of two panel-unit measurements is the one form the caller can
        /// use without knowing that scale. The same mistake in absolute units is what
        /// the safe-area inset was making (see Agent_UIStyle.SafeAreaPadding).
        /// </summary>
        public float TopBandFraction => BandFraction(_topBand);

        /// <summary>Fraction of the screen's height covered by the bottom band, 0..1.</summary>
        public float BottomBandFraction => BandFraction(_bottomBand);

        float BandFraction(VisualElement band)
        {
            if (!showHud || band == null || _root == null) return 0f;
            if (band.resolvedStyle.display == DisplayStyle.None) return 0f;

            float panelHeight = _root.worldBound.height;
            float bandHeight = band.worldBound.height;
            // NaN until the first layout pass, and a zero panel is a panel that has
            // not been measured - both mean "no inset yet" rather than "no band".
            if (float.IsNaN(panelHeight) || float.IsNaN(bandHeight) || panelHeight < 1f) return 0f;
            return Mathf.Clamp01(bandHeight / panelHeight);
        }

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
        /// Optional extra content appended to the end panel above the buttons, in
        /// registration order: Agent_MatchFlow supplies the man-of-the-match card,
        /// Agent_MatchStats the match telemetry table. Callbacks rather than
        /// direct references so the HUD never has to know either exists.
        ///
        /// This was a single Func field until the stats table needed a slot too.
        /// A second contributor assigning over the first is a silent loss of the
        /// first one's card, and which card survived would have depended on Start
        /// order - so it is a list, and every contributor removes its own.
        /// </summary>
        readonly List<System.Func<VisualElement>> _endPanelSections = new();
        readonly List<EndTab> _endPanelTabs = new();

        /// <summary>
        /// The three views of the end panel. The panel is one docked screen with a
        /// tab strip; every registered section lives on exactly one tab.
        /// </summary>
        public enum EndTab
        {
            /// <summary>The result and the man of the match.</summary>
            Summary = 0,
            /// <summary>The measured match table (Agent_MatchStats).</summary>
            Stats = 1,
            /// <summary>Per-player material (Agent_Dossier's occupancy maps).</summary>
            Players = 2,
        }

        const int END_TAB_COUNT = 3;
        static readonly string[] EndTabNames = { "SUMMARY", "STATS", "PLAYERS" };

        /// <summary>
        /// Register a builder for a section of the end-of-match panel, on the tab
        /// its owner implies. Contributors that predate the tabs call this
        /// one-argument form, so the routing is done here by WHO is registering
        /// rather than by editing each of them to say where it belongs.
        /// </summary>
        public void AddEndPanelSection(System.Func<VisualElement> section)
        {
            AddEndPanelSection(section, TabFor(section));
        }

        /// <summary>Register a builder for a section on a specific tab.</summary>
        public void AddEndPanelSection(System.Func<VisualElement> section, EndTab tab)
        {
            if (section == null || _endPanelSections.Contains(section)) return;
            _endPanelSections.Add(section);
            _endPanelTabs.Add(tab);
        }

        /// <summary>Unregister a builder. Call from OnDestroy; safe if never added.</summary>
        public void RemoveEndPanelSection(System.Func<VisualElement> section)
        {
            if (section == null) return;
            int index = _endPanelSections.IndexOf(section);
            if (index < 0) return;
            _endPanelSections.RemoveAt(index);
            _endPanelTabs.RemoveAt(index);
        }

        static EndTab TabFor(System.Func<VisualElement> section)
        {
            object owner = section != null ? section.Target : null;
            if (owner is Agent_MatchStats) return EndTab.Stats;
            if (owner is Agent_Dossier) return EndTab.Players;
            return EndTab.Summary;
        }

        void OnEnable()
        {
            // The gallery is not a match: no score, no clock, no first-to-5, and
            // no pause button for a grid of six independent pitches. Read here
            // rather than set by Agent_Bootstrap so it cannot depend on whether
            // that Awake happens to beat this OnEnable during scene load.
            if (Agent_MatchSetup.GalleryMode) enableMatchFlow = false;

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
            //
            // IsHeadless takes the same exit, and that is the point of routing it
            // here rather than disabling the component (2026-09-13). SCN_Training
            // serializes showHud: 1, so every headless env process was building the
            // full scoreboard - bands, chips, clock, toast, win-probability strip -
            // and then running the Update below on it every frame, in all four
            // processes, for a screen that does not exist. Taking the existing
            // no-visuals path leaves _score null, which Update already treats as
            // "nothing to draw", and leaves the component ENABLED so the serialized
            // enableMatchFlow / showHud fields that Agent_Presentation.IsMatchScene
            // and Agent_CameraFollow read stay reachable exactly as before.
            if (!showHud || Agent_Presentation.IsHeadless)
            {
                _root.Clear();
                return;
            }

            _root.Clear();
            Agent_UIStyle.ApplyTheme(_root);

            if (!BuildFromTemplate())
            {
                enabled = false;
                return;
            }

            // NO Agent_TimeFreeze.ReleaseAll() HERE - deliberately removed.
            //
            // It used to sit at the end of this method with the note "statics
            // outlive a scene load, so a REMATCH from the frozen end panel would
            // resume into a still-frozen clock". The intent was right; the owner
            // was wrong. ReleaseAll drops EVERY holder, so a scoreboard rebuilding
            // its own visual tree would silently void Agent_MatchFlow's countdown,
            // halftime and full-time holds while those phases still believed they
            // owned the clock - and their later Release would then be a no-op on an
            // already-empty set. It was harmless only because nothing ever
            // re-enables this component mid-match, which is a property of today's
            // scenes rather than of this class.
            //
            // The clock is released by the things that actually own scene entry:
            // Agent_MatchFlow.Start (runs in every match scene, after this) and
            // every navigation button (REMATCH / MENU / Agent_Chrome.ReturnToMenu)
            // before it calls LoadScene. The REMATCH path is covered twice over.
        }

        /// <summary>
        /// Instantiate Resources/HUD.uxml and resolve every element this class
        /// drives. Structure lives in the template; only binding lives here.
        ///
        /// Loaded from Resources rather than assigned to UIDocument.visualTreeAsset
        /// in the scene, for the same reason every other asset in this layer is:
        /// a serialized reference means a scene edit per scene, scene authoring is
        /// MCP-only per UNITY_RULES, and a scene that missed the edit would show a
        /// blank HUD with no error.
        ///
        /// Returns false and says exactly what is missing if the template cannot
        /// be found or has been renamed underneath the code. Names are the
        /// contract between the two files, and a silent null here surfaces later
        /// as an NRE in Update with nothing pointing at the cause.
        /// </summary>
        bool BuildFromTemplate()
        {
            var template = Resources.Load<VisualTreeAsset>("HUD");
            if (template == null)
            {
                Debug.LogError("Agent_HUD: Resources/HUD.uxml not found; HUD disabled.");
                return false;
            }
            template.CloneTree(_root);

            var safe = _root.Q<VisualElement>("safe");
            _topBand = _root.Q<VisualElement>("top-band");
            _bottomBand = _root.Q<VisualElement>("bottom-band");
            _score = _root.Q<Label>("score");
            _stepLabel = _root.Q<Label>("clock");
            _ballControlBlue = _root.Q<VisualElement>("meter-blue");
            _ballControlRed = _root.Q<VisualElement>("meter-red");
            _blueChips = _root.Q<VisualElement>("chips-blue");
            _redChips = _root.Q<VisualElement>("chips-red");
            _toast = _root.Q<Label>("toast");
            _commentary = _root.Q<Label>("commentary");
            _banner = _root.Q<Label>("banner");
            _replayTag = _root.Q<Label>("replay-tag");
            _letterboxTop = _root.Q<VisualElement>("letterbox-top");
            _letterboxBottom = _root.Q<VisualElement>("letterbox-bottom");
            _winProbBar = _root.Q<VisualElement>("winprob");
            _winProbBlue = _root.Q<VisualElement>("winprob-blue");
            _winProbRed = _root.Q<VisualElement>("winprob-red");
            _winProbLabel = _root.Q<Label>("winprob-label");
            _ticker = _root.Q<Label>("ticker");
            _broadcastTag = _root.Q<Label>("broadcast-tag");
            // Optional, unlike the rest: the meter is hidden with the broadcast
            // readouts, and a template without it just has nothing to hide.
            _meter = _root.Q<VisualElement>("meter");
            var controls = _root.Q<VisualElement>("controls");

            if (safe == null || _topBand == null || _bottomBand == null ||
                _score == null || _stepLabel == null ||
                _ballControlBlue == null || _ballControlRed == null ||
                _blueChips == null || _redChips == null || _toast == null ||
                _commentary == null || _banner == null || _replayTag == null ||
                _letterboxTop == null || _letterboxBottom == null || controls == null ||
                _winProbBar == null || _winProbBlue == null || _winProbRed == null ||
                _winProbLabel == null || _ticker == null || _broadcastTag == null)
            {
                Debug.LogError(
                    "Agent_HUD: Resources/HUD.uxml is missing a named element this " +
                    "class binds to. The names are the contract between the two " +
                    "files - check HUD.uxml against the queries in BuildFromTemplate.");
                return false;
            }

            // Safe area stays in code: a device measurement, not a design token.
            // Bound rather than applied once, so a resolution change re-insets.
            Agent_UIStyle.BindSafeArea(safe);

            // RESERVE AGENT_CHROME'S TOP BAR ON THE BAND, NOT ON ITS PARENT.
            //
            // The reservation keeps the score and the clock out of the chrome
            // bar: measured on a Pixel 9 Pro 2026-09-13, without it the chrome
            // row resolved to y=132..253 and the score to y=132..236 - the same
            // strip, with the higher-sorted overlay winning.
            //
            // It has to be padding on the BAND. Agent_CameraFollow frames the
            // pitch into whatever TopBandFraction leaves clear, and that
            // fraction is the band's own measured HEIGHT. Padding the safe
            // container instead moves the band down without making it taller,
            // so the camera keeps framing against the old figure and the top
            // goal mouth ends up under the bar - which is the 2026-09-13
            // landmine returning by a different route. Tried exactly that way
            // first and the goal was behind the chrome on device.
            //
            // Agent_UIStyle.Pad is the .band rule's own padding-top
            // (--space-5: 24px), added back because assigning style.paddingTop
            // replaces the stylesheet value rather than adding to it.
            _topBand.style.paddingTop = Agent_UIStyle.Pad + Agent_Chrome.BAR_HEIGHT;

            // The training scene has no match, so a frozen 0-0 would only mislead.
            _score.style.display = enableMatchFlow ? DisplayStyle.Flex : DisplayStyle.None;

            _ballControlBlue.style.width = Length.Percent(50);
            _ballControlRed.style.width = Length.Percent(50);

            SetWinProbability(0.5f);

            // BROADCAST READOUTS OFF BY DEFAULT (2026-09-07, user request; made a
            // persisted player choice 2026-10-10).
            //
            // The win-probability bar and the rotating stat ticker sit directly above
            // the play area and were reported as interfering with watching the match.
            // They are hidden here rather than by disabling Agent_WinProbability /
            // Agent_MatchStats, deliberately: Agent_PlayMode_Broadcast asserts those
            // components ARE installed in a match scene and are NOT in SCN_Training,
            // and Agent_Director reads the win-probability THREAT signal to choose
            // its shots. The components keep running - the end-of-match table and
            // the health panel are unaffected - their output simply is not painted
            // over the pitch until the player asks for it.
            //
            // The win-probability bar is the one number on screen that needs a MODEL
            // qualifier next to it, because it is an uncalibrated hand-tuned logistic
            // rather than a measurement. SetWinProbability writes that word into the
            // label every time, so it comes back with the strip.
            _showBroadcastStrip = BroadcastEnabled;
            ApplyBroadcastVisibility();

            // The gallery turns match flow off, which drops the clock into its
            // training readout ("step N · goal 6.0m · bot 1.00"). That is honest
            // in SCN_Training and pure noise over a grid of exhibition pitches -
            // it reports one pitch's episode counter while six are playing.
            if (Agent_MatchSetup.GalleryMode) _stepLabel.style.display = DisplayStyle.None;

            if (enableMatchFlow)
            {
                // MENU used to live here. It now sits in the upper-right corner,
                // owned by Agent_Chrome, so that the corner layout is identical in
                // the menu and in a match. Two MENU buttons on one screen is worse
                // than either position on its own. Agent_Chrome.ReturnToMenu keeps
                // the Agent_TimeFreeze.ReleaseAll() that used to happen here.
                _soundButton = Agent_UIStyle.SoundToggleButton();
                _soundButton.AddToClassList("hud__control");
                controls.Add(_soundButton);

                var pause = SmallButton("II", TogglePause);
                pause.name = "pause";
                pause.AddToClassList("hud__control");
                controls.Add(pause);

                // ONE switch for every broadcast readout. Short label on purpose:
                // this row is centred between Agent_Chrome's DEBUG button and the
                // version label, and three wide buttons would reach both.
                _broadcastButton = Agent_UIStyle.ToggleButton("STATS", _showBroadcastStrip, ToggleBroadcast);
                _broadcastButton.name = "broadcast-toggle";
                _broadcastButton.AddToClassList("hud__control");
                controls.Add(_broadcastButton);
            }
            else
            {
                controls.style.display = DisplayStyle.None;
            }

            return true;
        }

        void Start()
        {
            if (env != null) env.EpisodeEnded += OnEpisodeEnded;
        }

        void OnDestroy()
        {
            if (env != null) env.EpisodeEnded -= OnEpisodeEnded;

            // A PAUSED HUD THAT IS DESTROYED USED TO FREEZE THE CLOCK FOREVER.
            // Agent_TimeFreeze.Holders is a static HashSet, so _pauseToken outlives
            // the component that owns it: once this object is gone nothing holds a
            // reference to that token, nothing can ever call Release with it, and
            // IsFrozen stays true for the rest of the process. Every subsequent
            // match then starts with a stopped clock and no visible cause - the
            // holder is named "HUD.Pause" and belongs to a HUD that no longer exists.
            //
            // The two exits from the pause panel (its MENU button and the hardware
            // back button on the result screen) both call ReleaseAll and so were
            // safe, which is why this survived: the leak needs the HUD to be
            // destroyed while paused by any OTHER route - a scene load from
            // elsewhere, a test fixture tearing down, a rematch. Releasing here
            // covers all of them, and Release is idempotent so the ordinary
            // Resume-then-destroy path is unaffected.
            //
            // Found 2026-09-13 by Agent_PlayMode_SmokeRun: adding that fixture
            // reordered the suite so a pausing test ran before
            // Agent_PlayMode_GameFlow.MatchScene_NeverLeavesTheClockStopped, which
            // then failed with "Held by: HUD.Pause". The ordering was the trigger,
            // not the defect.
            Agent_TimeFreeze.Release(_pauseToken);

            // The settings sheet lives on this HUD's panel when it was opened from
            // the pause screen. Its state is static, so drop it with the panel.
            if (_settingsOpenedHere) Agent_SettingsSheet.Close();
        }

        /// <summary>
        /// The app went to the background mid-match: pause, through the ordinary
        /// pause panel and its ordinary token.
        ///
        /// Android suspends the player on its own, so nothing is lost while away -
        /// the cost is on the way BACK. Without this the match resumes at full
        /// speed the instant the app is foregrounded, before the player has found
        /// the ball again; with it they return to PAUSED and choose when to go.
        ///
        /// Only the `true` edge is handled. Unity also sends `false` at startup
        /// and on return, and resuming there would un-pause a match the player
        /// paused by hand before switching away. No second token: Pause() is
        /// skipped when the panel is already up, and it is the same _pauseToken
        /// either way, which Agent_TimeFreeze holds at most once.
        /// </summary>
        void OnApplicationPause(bool paused)
        {
            if (!paused) return;
            if (!enableMatchFlow || MatchOver || _root == null || _score == null) return;
            if (!Agent_Presentation.IsMatchScene(this)) return;
            if (!IsPaused) Pause();
        }

        bool _settingsOpenedHere;

        void OpenSettings()
        {
            if (_root == null) return;
            _settingsOpenedHere = true;
            Agent_SettingsSheet.Open(_root, OnSettingsChanged);
        }

        /// <summary>
        /// Runs after any change in the settings sheet. The sheet has already
        /// re-stamped the palette / type-scale classes on this panel's root; what
        /// is left is what this HUD has baked in or mirrors: the broadcast switch,
        /// the mute label, and the chips, whose team tint is an inline colour read
        /// from Agent_Palette at build time.
        /// </summary>
        void OnSettingsChanged()
        {
            SetBroadcastStripVisible(BroadcastEnabled);
            if (_soundButton != null) _soundButton.text = Agent_Audio.Muted ? "SND OFF" : "SND ON";
            _chipsBuilt = false;
        }

        float _matchSeconds;
        bool _chipsBuilt;

        /// <summary>
        /// Ends the match when the clock runs out.
        ///
        /// WHY A BOUND EXISTS AT ALL. first-to-<see cref="matchGoals"/> was the only
        /// terminal condition, so a match between two evenly-matched sides that both
        /// stall - which, per the project's own eval record, is 27% of episodes - had
        /// no end state to reach. Agent_Overtime forces GOALLESS stretches to resolve,
        /// but a 4-4 match that keeps trading goals never arms it and never finishes.
        ///
        /// SAFE TO RUN UNGUARDED AGAINST THE REPLAY. _matchSeconds accumulates
        /// Time.deltaTime, which is zero whenever Agent_TimeFreeze holds the clock, so
        /// the threshold can only be crossed during live play - never underneath a
        /// goal replay, a countdown or the pause panel. It therefore raises the panel
        /// directly instead of going through DeferEndPanel: that deferral is answered
        /// by Agent_MatchFlow's GOAL sequence, and a timeout is not a goal, so a
        /// deferred timeout would wait for a sequence that is never going to run.
        /// </summary>
        void CheckTimeLimit()
        {
            if (matchTimeLimit <= 0f || _ended || _matchSeconds < matchTimeLimit) return;
            ShowEndPanel();
        }

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

        // The toast, letterbox bars, REPLAY tag, banner and commentary strip all
        // live in Resources/HUD.uxml now. They are absolute-positioned overlay
        // lanes, each with its own name, so a callout in one lane can never
        // overwrite a callout in another - and their pick-ignore is declared in
        // the template rather than reapplied in five places here.

        // ── Public broadcast API ────────────────────────────────────────────

        /// <summary>
        /// Drives the win-probability strip. Takes blue's share, 0..1.
        ///
        /// The label always carries the word MODEL. That is not decoration: the
        /// estimate behind it is an uncalibrated hand-tuned logistic (see
        /// Agent_WinProbability), and a percentage on a scoreboard reads as
        /// measured unless it says otherwise. This project has already published
        /// one retraction over a number that looked measured and was not.
        ///
        /// THE DRAWN WIDTH IS FLOORED; THE NUMBER IS NOT. Measured live on
        /// 2026-09-06 at 1-5 down: winprob-blue resolved to 0.00 px wide. A bar
        /// with one side literally absent does not read as "blue is at 0%", it
        /// reads as a broken widget - and it sits beside a stat ticker that IS
        /// measured, so the failure mode is a viewer discounting the honest half
        /// of the band too. A 2% sliver keeps the losing side visible as a side.
        /// The label and _shownWinPercent keep the true value, so nothing that
        /// reads this back is told a rounded-up story.
        /// </summary>
        public void SetWinProbability(float blueShare01)
        {
            if (_winProbBlue == null || _winProbRed == null || _winProbLabel == null) return;

            float blue = Mathf.Clamp01(blueShare01);
            int percent = Mathf.RoundToInt(blue * 100f);
            if (percent == _shownWinPercent) return;
            _shownWinPercent = percent;

            int drawn = Mathf.Clamp(percent, WINPROB_MIN_VISIBLE, 100 - WINPROB_MIN_VISIBLE);
            _winProbBlue.style.width = Length.Percent(drawn);
            _winProbRed.style.width = Length.Percent(100 - drawn);
            _winProbLabel.text = $"BLUE {percent}%  ·  MODEL  ·  RED {100 - percent}%";
        }

        /// <summary>
        /// Narrowest either half of the win-probability bar is ever drawn, in
        /// percent. Pinned by Agent_PlayMode_Broadcast so the floor cannot be
        /// tuned back to zero without the test saying what that costs.
        /// </summary>
        public const int WINPROB_MIN_VISIBLE = 2;

        /// <summary>
        /// One line of live match telemetry in the bottom band. Pass null or an
        /// empty string to clear it. Cheap to call every frame - the string is
        /// only assigned when it actually changed.
        /// </summary>
        public void SetTicker(string text)
        {
            if (_ticker == null) return;
            if (text == _shownTicker) return;
            _shownTicker = text;

            bool show = !string.IsNullOrEmpty(text);
            if (show) _ticker.text = text;
            Agent_UIStyle.SetShown(_ticker, show);
        }

        /// <summary>
        /// The corner status bug: which shot the director is holding and whether
        /// the vision overlay is up. Null or empty hides it.
        /// </summary>
        public void SetBroadcastTag(string text)
        {
            if (_broadcastTag == null) return;
            if (text == _shownBroadcastTag) return;
            _shownBroadcastTag = text;

            bool show = !string.IsNullOrEmpty(text);
            if (show) _broadcastTag.text = text;
            Agent_UIStyle.SetShown(_broadcastTag, show);
        }

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

            _pausePanel = new VisualElement { name = "pause-panel" };
            _pausePanel.AddToClassList("panel--scrim");
            // Absolute on the panel root, so it carries its own safe-area inset.
            Agent_UIStyle.BindSafeArea(_pausePanel, Agent_Chrome.BAR_HEIGHT, Agent_Chrome.BAR_HEIGHT);

            var heading = new Label("PAUSED");
            heading.AddToClassList("score");
            heading.style.marginBottom = Agent_UIStyle.Space2;
            _pausePanel.Add(heading);

            var score = new Label($"{_blueScore}  —  {_redScore}");
            score.AddToClassList("clock");
            score.style.marginBottom = Agent_UIStyle.Pad;
            _pausePanel.Add(score);

            // THE VOLUME SLIDERS USED TO BE INLINE HERE. They are the Audio tab of
            // the settings sheet now, one tap away, alongside every other setting -
            // so the mix, the palette and the text size can all be changed while
            // looking at the paused match they apply to, and this panel is three
            // buttons instead of a form.
            _pausePanel.Add(PauseButton("RESUME", true, Resume));
            _pausePanel.Add(PauseButton("SETTINGS", false, OpenSettings));
            _pausePanel.Add(PauseButton("MENU", false,
                () => { Agent_TimeFreeze.ReleaseAll(); SceneManager.LoadScene(menuScene); }));

            _root.Add(_pausePanel);
            Agent_UIStyle.PlayEntrance(_pausePanel);
        }

        static Button PauseButton(string text, bool primary, System.Action onClick)
        {
            var button = EndButton(text, primary, onClick);
            button.AddToClassList("pause__btn");
            return button;
        }

        void Resume()
        {
            // The sheet is a child of the pause flow; it cannot outlive it.
            if (_settingsOpenedHere && Agent_SettingsSheet.IsOpen) Agent_SettingsSheet.Close();

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

                var square = new Label(agent.rewards.Initial.ToString());
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

        VisualElement _endBody;
        readonly VisualElement[] _endPanes = new VisualElement[END_TAB_COUNT];
        readonly Button[] _endTabButtons = new Button[END_TAB_COUNT];

        /// <summary>
        /// The result screen: ONE docked view, nothing scrolls.
        ///
        ///     headline + score      what happened
        ///     SUMMARY | STATS | PLAYERS
        ///     one content pane      switched in place by the tabs
        ///     REMATCH | MENU        fixed, inside thumb reach
        ///
        /// It used to be a centred column that appended every registered section -
        /// man of the match, the telemetry table, the occupancy maps - one under
        /// the other. On a 2v2 that is already taller than a 16:9 screen, and the
        /// two buttons were the LAST children, so the first thing to fall off the
        /// bottom was the only way out. Tabs bound the height; the pane is the
        /// only flexible child and clips, so nothing a contributor builds can move
        /// the action row.
        ///
        /// Panes are built lazily, on the first visit to a tab, and kept: the
        /// dossier bakes a texture per player, which is not worth doing for a tab
        /// nobody opens and must not be done twice for one they revisit.
        /// </summary>
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

            _endPanel = new VisualElement { name = "end-panel" };
            _endPanel.AddToClassList("panel--scrim");
            _endPanel.AddToClassList("endpanel");
            // Absolute on the panel root, so it insets itself - and reserves both
            // of Agent_Chrome's bars, which draw above this panel.
            Agent_UIStyle.BindSafeArea(_endPanel, Agent_Chrome.BAR_HEIGHT, Agent_Chrome.BAR_HEIGHT);

            var head = new VisualElement();
            head.AddToClassList("endpanel__head");
            _endPanel.Add(head);

            // A draw was unreachable while first-to-N was the only terminal
            // condition, so the headline was a two-way branch that filed every
            // level scoreline as a RED win. matchTimeLimit makes level scores
            // reachable, so the third case has to exist.
            bool drawn = _blueScore == _redScore;
            var headline = new Label(drawn ? "DRAW" : _blueScore > _redScore ? "BLUE WINS" : "RED WINS")
            {
                name = "end-headline",
            };
            headline.AddToClassList("endpanel__headline");
            // Whose win it was is data; the size and face are the stylesheet's.
            headline.style.color = drawn
                ? Agent_UIStyle.TextPrimary
                : _blueScore > _redScore ? Agent_UIStyle.BlueTeam : Agent_UIStyle.RedTeam;
            head.Add(headline);

            var score = new Label($"{_blueScore}  —  {_redScore}") { name = "end-score" };
            score.AddToClassList("endpanel__score");
            head.Add(score);

            var tabs = new VisualElement { name = "end-tabs" };
            tabs.AddToClassList("tabs");
            for (int tabIndex = 0; tabIndex < END_TAB_COUNT; tabIndex++)
            {
                var tab = (EndTab)tabIndex;
                var button = new Button(() => ShowEndTab(tab)) { text = EndTabNames[tabIndex] };
                button.AddToClassList("btn");
                button.AddToClassList("tab");
                _endTabButtons[tabIndex] = button;
                _endPanes[tabIndex] = null;
                tabs.Add(button);
            }
            _endPanel.Add(tabs);

            _endBody = new VisualElement { name = "end-body" };
            _endBody.AddToClassList("endpanel__body");
            _endPanel.Add(_endBody);

            var actions = new VisualElement { name = "end-actions" };
            actions.AddToClassList("endpanel__actions");
            // REMATCH = the same lineup again. Agent_MatchSetup's statics are only
            // cleared on arrival at the MENU, so reloading this scene hands
            // Agent_MatchLoader the squads it was given the first time; the score
            // and the first-to-N state die with this component; and ReleaseAll
            // drops this panel's hold, every phase token and the spectator speed
            // before the load, so the new kickoff countdown starts on a clean clock.
            var rematch = EndButton("REMATCH", true,
                () => { Agent_TimeFreeze.ReleaseAll(); SceneManager.LoadScene(SceneManager.GetActiveScene().name); });
            rematch.AddToClassList("btn--dock");
            actions.Add(rematch);
            var menu = EndButton("MENU", false,
                () => { Agent_TimeFreeze.ReleaseAll(); SceneManager.LoadScene(menuScene); });
            menu.AddToClassList("btn--dock");
            actions.Add(menu);
            _endPanel.Add(actions);

            _root.Add(_endPanel);
            Agent_UIStyle.PlayEntrance(_endPanel);
            ShowEndTab(EndTab.Summary);
        }

        /// <summary>Switches the end panel's content pane in place.</summary>
        public void ShowEndTab(EndTab tab)
        {
            if (_endBody == null) return;
            int wanted = Mathf.Clamp((int)tab, 0, END_TAB_COUNT - 1);

            if (_endPanes[wanted] == null)
            {
                _endPanes[wanted] = BuildEndPane((EndTab)wanted);
                _endBody.Add(_endPanes[wanted]);
            }

            for (int tabIndex = 0; tabIndex < END_TAB_COUNT; tabIndex++)
            {
                _endTabButtons[tabIndex].EnableInClassList(Agent_UIStyle.ON, tabIndex == wanted);
                if (_endPanes[tabIndex] != null)
                {
                    _endPanes[tabIndex].style.display =
                        tabIndex == wanted ? DisplayStyle.Flex : DisplayStyle.None;
                }
            }
        }

        VisualElement BuildEndPane(EndTab tab)
        {
            var pane = new VisualElement();
            pane.style.alignItems = Align.Center;
            // Natural height, CLIPPED by the body - never shrunk to fit it. A
            // shrinking pane squeezes its rows into each other, which reads as a
            // rendering bug; a clipped one reads as a list that continues.
            pane.style.flexShrink = 0;

            int contributed = 0;
            for (int sectionIndex = 0; sectionIndex < _endPanelSections.Count; sectionIndex++)
            {
                if (_endPanelTabs[sectionIndex] != tab) continue;
                var builder = _endPanelSections[sectionIndex];
                var section = builder != null ? builder() : null;
                if (section == null) continue;
                pane.Add(section);
                contributed++;
            }

            switch (tab)
            {
                case EndTab.Summary:
                    pane.Add(EndNote(SummaryLine()));
                    break;
                case EndTab.Stats:
                    if (contributed == 0) pane.Add(EndNote("No match statistics were recorded."));
                    break;
                default:
                    // Agent_Dossier declines to draw a map from under ten seconds
                    // of play. The roster itself is always known, so the tab is
                    // never empty.
                    if (contributed == 0) BuildPlayerList(pane);
                    break;
            }
            return pane;
        }

        string SummaryLine()
        {
            int seconds = Mathf.Max(0, Mathf.RoundToInt(_matchSeconds));
            string length = $"{seconds / 60:0}:{seconds % 60:00} played";
            bool timedOut = matchTimeLimit > 0f && _matchSeconds >= matchTimeLimit
                            && (matchGoals <= 0 || (_blueScore < matchGoals && _redScore < matchGoals));
            if (timedOut) return $"time expired  ·  {length}";
            return matchGoals > 0 ? $"first to {matchGoals}  ·  {length}" : length;
        }

        static Label EndNote(string text)
        {
            var note = new Label(text);
            note.AddToClassList("endpanel__note");
            return note;
        }

        /// <summary>Rows drawn before the list collapses into a "+N more" line.</summary>
        const int MAX_END_PLAYER_ROWS = 10;

        /// <summary>
        /// Who played and who drove them - what this class can say about the
        /// roster on its own, used when no per-player section registered or the one
        /// that did had nothing to draw.
        /// </summary>
        void BuildPlayerList(VisualElement pane)
        {
            if (env == null) return;

            int drawn = 0, skipped = 0;
            for (int agentIndex = 0; agentIndex < env.agents.Count; agentIndex++)
            {
                var agent = env.agents[agentIndex];
                if (agent == null || agent.rewards == null) continue;
                if (drawn >= MAX_END_PLAYER_ROWS) { skipped++; continue; }
                drawn++;

                var row = new VisualElement();
                row.AddToClassList("endpanel__player");

                var name = new Label(agent.rewards.playerName);
                name.AddToClassList("endpanel__player-name");
                name.style.color = agent.team == Agent_Soccer.Team.Blue
                    ? Agent_UIStyle.BlueTeam : Agent_UIStyle.RedTeam;
                row.Add(name);

                var driver = new Label(agent.RuleBased ? "BOT" : "AI");
                driver.AddToClassList("endpanel__player-driver");
                row.Add(driver);

                string run = agent.RuleBased
                    ? "scripted"
                    : string.IsNullOrEmpty(agent.rewards.trainingRunId) ? "run unknown" : agent.rewards.trainingRunId;
                var provenance = new Label(run);
                provenance.AddToClassList("endpanel__player-run");
                row.Add(provenance);

                pane.Add(row);
            }

            if (skipped > 0) pane.Add(EndNote($"+{skipped} more"));
            if (drawn == 0) pane.Add(EndNote("No players to list."));
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

            // Built once per lineup, not "whenever the row happens to be empty".
            // The old condition was `childCount == 0 && env.agents.Count > 0`, which
            // is a rebuild request that the rebuild itself cannot satisfy: BuildChips
            // skips any agent whose rewards are null, so a lineup that produces no
            // blue chip left the condition true and re-ran the whole builder - with
            // its VisualElement and Label allocations - EVERY FRAME, for the life of
            // the match. A flag records that the attempt was made.
            if (!_chipsBuilt && _blueChips != null && env.agents.Count > 0)
            {
                _chipsBuilt = true;
                BuildChips();
            }

            if (enableMatchFlow)
            {
                if (!_ended)
                {
                    _matchSeconds += Time.deltaTime;
                    CheckTimeLimit();
                }

                if (_blueScore != _shownBlue || _redScore != _shownRed)
                {
                    _score.text = $"{_blueScore}  —  {_redScore}";
                    _shownBlue = _blueScore;
                    _shownRed = _redScore;
                }

                // Counts DOWN when the match is bounded. A timer that ends the game
                // has to be the timer on screen - a stopwatch climbing past a limit
                // the player cannot see is how a match appears to stop for no reason.
                int second = matchTimeLimit > 0f
                    ? Mathf.Max(0, Mathf.CeilToInt(matchTimeLimit - _matchSeconds))
                    : (int)_matchSeconds;
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

            // Back goes up ONE level, and the settings sheet is the topmost one:
            // it closes back to the pause panel it was opened from, rather than
            // back resuming the match out from under an open sheet.
            if (_settingsOpenedHere && Agent_SettingsSheet.IsOpen)
            {
                Agent_SettingsSheet.Close();
                return;
            }

            // ON THE RESULT SCREEN, BACK MEANS LEAVE. It used to mean nothing:
            // TogglePause refuses once MatchOver is true (correctly - a pause layered
            // under the result card is a dead end with two sets of buttons), so the
            // hardware back button was inert on the ONE screen a player most wants out
            // of, with the only exits two on-screen buttons. Android's contract is that
            // back always goes up a level, and from a finished match that is the menu.
            if (MatchOver)
            {
                Agent_TimeFreeze.ReleaseAll();
                SceneManager.LoadScene(menuScene);
                return;
            }

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