using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// Persistent screen chrome: the four corners, identical in the menu and in
    /// a match. Nothing here occupies the centre of the screen - that belongs to
    /// Agent_HUD in a match and to the menu's own title in SCN_Menu, and a
    /// sortingOrder-90 overlay that strays into it wins over both.
    ///
    ///   upper-left    product name
    ///   upper-centre  FPS readout, parked clear of whatever owns the centre
    ///   upper-right   MENU  (back to the start menu; hidden while already there)
    ///   lower-left    DEBUG (toggles Agent_Telemetry)
    ///   lower-right   version
    ///
    /// DEBUG exists because Agent_Telemetry - a complete frame/GC/draw-call
    /// overlay - was only reachable by F3 or an undocumented three-finger tap.
    /// On a phone that is not a shortcut, it is a hidden feature: there is no
    /// keyboard and nothing on screen suggests the gesture. The button makes an
    /// already-built diagnostic surface actually usable on the target device.
    ///
    /// Everything is absolutely positioned inside a safe-area container, so the
    /// corners sit inside the notch/cutout rather than under it. Containers are
    /// PickingMode.Ignore so only the two buttons take input - a full-screen
    /// overlay that picks would swallow every tap meant for the pitch.
    /// </summary>
    [DefaultExecutionOrder(90)]
    [DisallowMultipleComponent]
    public sealed class Agent_Chrome : MonoBehaviour
    {
        [Tooltip("Scene loaded by the MENU button.")]
        [SerializeField] private string _menuScene = "SCN_Menu";
        [Tooltip("Seconds between FPS text refreshes. Samples are taken every frame.")]
        [SerializeField] private float _fpsInterval = 0.25f;
        [Tooltip("Show the frame-rate readout. Off ships a clean screen.")]
        [SerializeField] private bool _showFps = true;

        // Sorting: above the HUD (0) so the corners are never covered, below
        // Agent_Telemetry (100) so the diagnostic panel draws over the chrome
        // that opened it.
        const int SORTING_ORDER = 90;

        // Touch targets. At the 1080-wide reference resolution one UI pixel is
        // one physical pixel on a 1080p phone (~0.063 mm), so 120 px is ~7.6 mm -
        // above the ~7 mm ergonomic minimum and comfortably above Android's 48dp
        // guidance. Do not shrink these to make the layout prettier.
        const int BUTTON_HEIGHT = 120;
        const int BUTTON_MIN_WIDTH = 200;

        /// <summary>
        /// Total height one corner bar occupies: the row plus the gutter above
        /// and below it. Screens RESERVE this at top and bottom so their own
        /// content starts clear of the chrome.
        ///
        /// It has to be reserved rather than merely drawn over. This overlay is
        /// at sortingOrder 90, above the HUD and the menu, so anything it lands
        /// on it wins - the FPS readout printed through the score at 1170x2532
        /// for exactly this reason, and the DEBUG button sat on top of the
        /// menu's settings row, where a tap could plausibly have been meant for
        /// either. Giving the bar its own strip is what makes the corner map a
        /// layout rather than a collision.
        /// </summary>
        public const int BAR_HEIGHT = Agent_UIStyle.Pad + BUTTON_HEIGHT + Agent_UIStyle.Pad;

        UIDocument _doc;
        Label _fpsLabel;
        Button _menuButton;
        Agent_Telemetry _telemetry;

        float _fpsAccum;
        int _fpsFrames;
        float _fpsTimer;
        int _shownFps = -1;

        // Zero alloc in Update (performance.md): the readout changes ~4x a second,
        // and int.ToString() would allocate every time. Pre-render the plausible
        // range once instead. Anything outside it is clamped rather than formatted.
        const int FPS_CACHE_MAX = 240;
        static readonly string[] FpsText = BuildFpsText();

        static string[] BuildFpsText()
        {
            var cache = new string[FPS_CACHE_MAX + 1];
            for (int i = 0; i <= FPS_CACHE_MAX; i++) cache[i] = i + " FPS";
            return cache;
        }

        void Start()
        {
            Build();
        }

        void Build()
        {
            _doc = GetComponent<UIDocument>();
            if (_doc == null) _doc = gameObject.AddComponent<UIDocument>();

            if (_doc.panelSettings == null)
            {
                // Share the panel the HUD/menu already uses so scaling matches.
                // Scan every document rather than FindFirstObjectByType, which can
                // return the one just added to this GameObject - see the same
                // trap documented in Agent_Telemetry.BuildOverlay.
                var documents = FindObjectsByType<UIDocument>(FindObjectsSortMode.None);
                for (int i = 0; i < documents.Length; i++)
                {
                    if (documents[i] == _doc || documents[i].panelSettings == null) continue;
                    _doc.panelSettings = documents[i].panelSettings;
                    break;
                }
            }
            if (_doc.panelSettings == null)
            {
                Debug.LogWarning("Agent_Chrome: no PanelSettings available; chrome disabled.");
                return;
            }
            _doc.sortingOrder = SORTING_ORDER;

            var root = _doc.rootVisualElement;
            if (root == null) return;
            Agent_UIStyle.ApplyTheme(root);
            root.pickingMode = PickingMode.Ignore;

            // THE CORNERS ARE IN NORMAL FLOW, NOT ABSOLUTELY POSITIONED, AND THAT
            // IS THE WHOLE POINT (fixed 2026-09-13).
            //
            // BindSafeArea writes the device inset as PADDING on this element.
            // An absolutely positioned child resolves its offsets against its
            // containing block's PADDING box, not its content box - so `top: 24`
            // on an absolute row means 24 px from the outer edge and the padding
            // is simply skipped. Every corner of this overlay was therefore
            // ignoring the safe area on every device that has one, which on a
            // punch-hole phone puts the product name and the FPS readout up
            // under the status bar. Nothing logged, and the inset was being
            // computed correctly the whole time - it just was not reaching
            // anything.
            //
            // Laying the two rows out as ordinary flex children of a
            // space-between column makes them respect the padding by
            // construction, which is a property of the layout rather than an
            // arithmetic correction somebody has to remember.
            var safe = new VisualElement();
            safe.style.flexGrow = 1;
            safe.style.flexDirection = FlexDirection.Column;
            safe.style.justifyContent = Justify.SpaceBetween;
            safe.pickingMode = PickingMode.Ignore;
            Agent_UIStyle.BindSafeArea(safe);
            root.Add(safe);

            bool inMenu = SceneManager.GetActiveScene().name == _menuScene;

            // -- top row -----------------------------------------------------
            var top = Row();
            safe.Add(top);

            // Product name and FPS stack in the LEFT corner.
            //
            // The readout used to sit top-centre, pushed down by a hand-tuned
            // marginTop meant to clear the scoreboard. It never cleared it. This
            // row and Agent_HUD's #top-band are both anchored to the top of the
            // same safe area, so any constant here is a guess about a different
            // UIDocument's layout - and Chrome draws at sortingOrder 90, so when
            // the guess is wrong the diagnostic wins and the score loses.
            //
            // Measured 2026-09-07 at 1170x2532: the band runs y=141..440 and the
            // label sat at y=192, printing "60 FPS" through a score at
            // y=169..271 - a 178x61 px overprint, in every match. The old 168 was
            // derived from score + clock + padding alone; the broadcast layer
            // (2026-09-06) then added the win-probability strip and the stat
            // ticker to the same band and nobody re-derived it. Even for the
            // original two children it was short: 24 + 168 = 192 was already
            // inside the band.
            //
            // SCN_Menu was no better - the menu's own title holds x=307..770,
            // y=173..356. The centre is contested in both scenes and this
            // component owns nothing but corners, so the readout moves to one.
            // Children STRETCH to the column's width on purpose - do not switch
            // this to Align.FlexStart to make the labels hug their text. The
            // column's own width is fixed (flexGrow 1 / flexBasis 0 against the
            // MENU button), but FlexStart makes each child's width content-driven,
            // and the FPS text changes four times a second: "9 FPS" -> "10 FPS"
            // re-measures the label, dirties this panel's layout, and does it
            // again 0.25 s later, forever. Measured 2026-09-07 - with FlexStart
            // the PlayMode suite failed four consecutive runs, each time a
            // different wall-clock budget assertion (episode never ended in 90 s,
            // countdown never handed the clock back, a 180 s test timeout), while
            // the same suite on the unmodified file passed 63/63. Stretching keeps
            // the width independent of the text; unityTextAlign does the visual
            // placement instead.
            var leftCorner = new VisualElement();
            leftCorner.name = "chrome-left";
            leftCorner.style.flexGrow = 1;
            leftCorner.style.flexBasis = 0;
            leftCorner.pickingMode = PickingMode.Ignore;
            top.Add(leftCorner);

            // CornerLabel ships flexGrow 1 / flexBasis 0, which is what the corners
            // want as ROW children - they divide the row's width. Stacked in a
            // COLUMN those same two properties apply to height, and a 0 basis that
            // is not allowed to grow collapses the label to ~7 px: laid out, not
            // clipped, and reported as present by any test that only asks whether
            // the label exists. Both must go back to auto here.
            var productLabel = CornerLabel(Application.productName, Agent_UIStyle.TextPrimary, Agent_UIStyle.FontM);
            productLabel.name = "chrome-title";
            productLabel.style.flexGrow = 0;
            productLabel.style.flexBasis = StyleKeyword.Auto;
            leftCorner.Add(productLabel);

            _menuButton = ChromeButton("MENU", ReturnToMenu);
            _menuButton.name = "chrome-menu";
            // Already at the menu: keep the slot so the row keeps its shape, but
            // nothing to navigate to.
            _menuButton.style.visibility = inMenu ? Visibility.Hidden : Visibility.Visible;
            top.Add(_menuButton);

            // -- top centre: the FPS readout ---------------------------------
            //
            // Absolutely positioned INSIDE the top row, stretched edge to edge.
            // That places it dead centre horizontally and on the same baseline as
            // the product name and MENU - one top bar, three slots - and because
            // the row itself is laid out in normal flow, the safe-area inset it
            // sits inside is inherited rather than recomputed.
            //
            // AN EARLIER VERSION OF THIS MEASURED ITS OWN CLEARANCE and scanned
            // down the centre column for the first gap that would fit. It was
            // built to avoid the 2026-09-07 defect where a hand-tuned marginTop
            // of 168 printed the readout straight through the score. It failed
            // twice on device, both times by finding a "clear" slot far from the
            // top: once at mid-pitch over the roster cards, and once - after the
            // scan was corrected to walk down from the top - stepping through
            // eight consecutive menu rows and stopping, still overlapping,
            // because a dense screen has no gap that size anywhere in its centre
            // column. The lesson is that the readout does not want the first
            // clear slot in the centre; it wants the top BAR, which is a place
            // this component already owns and nothing else draws into. A fixed
            // slot in a row that is itself laid out correctly needs no
            // measurement, and there is no longer a number here to be wrong.
            _fpsLabel = CornerLabel(string.Empty, Agent_UIStyle.TextMuted, Agent_UIStyle.FontS);
            _fpsLabel.name = "chrome-fps";
            _fpsLabel.style.position = Position.Absolute;
            _fpsLabel.style.left = 0;
            _fpsLabel.style.right = 0;
            _fpsLabel.style.top = 0;
            _fpsLabel.style.bottom = 0;
            _fpsLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _fpsLabel.style.flexGrow = 0;
            _fpsLabel.style.flexBasis = StyleKeyword.Auto;
            _fpsLabel.style.display = _showFps ? DisplayStyle.Flex : DisplayStyle.None;
            // Behind the two buttons in the same row, and never picking, so a tap
            // meant for MENU is never swallowed by a full-width diagnostic label.
            _fpsLabel.pickingMode = PickingMode.Ignore;
            top.Insert(0, _fpsLabel);

            // -- bottom row --------------------------------------------------
            // Space-between puts this against the bottom of the content box; it
            // needs no offsets of its own.
            var bottom = Row();
            safe.Add(bottom);

            var debugButton = ChromeButton("DEBUG", ToggleTelemetry);
            debugButton.name = "chrome-debug";
            bottom.Add(debugButton);

            var spacer = new VisualElement { style = { flexGrow = 1 } };
            spacer.pickingMode = PickingMode.Ignore;
            bottom.Add(spacer);

            var version = CornerLabel("v" + Application.version, Agent_UIStyle.TextMuted, Agent_UIStyle.FontS);
            version.name = "chrome-version";
            version.style.unityTextAlign = TextAnchor.MiddleRight;
            bottom.Add(version);
        }

        /// <summary>
        /// One corner row, in NORMAL FLOW - see the note on the safe container
        /// for why it must not be absolutely positioned. Margins rather than
        /// offsets, so the safe-area padding and this gutter add up instead of
        /// one replacing the other.
        /// </summary>
        static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.marginLeft = Agent_UIStyle.Pad;
            row.style.marginRight = Agent_UIStyle.Pad;
            row.style.marginTop = Agent_UIStyle.Pad;
            row.style.marginBottom = Agent_UIStyle.Pad;
            row.style.flexShrink = 0;
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.alignItems = Align.Center;
            row.pickingMode = PickingMode.Ignore;
            return row;
        }

        static Label CornerLabel(string text, Color color, int fontSize)
        {
            var label = new Label(text);
            label.style.color = color;
            label.style.fontSize = fontSize;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.flexGrow = 1;
            label.style.flexBasis = 0;
            // Dark pitch, bright sky, white kit - a plain label is unreadable over
            // some of them, so every corner gets a shadow rather than a panel that
            // would box in the view.
            label.style.textShadow = new TextShadow
            {
                offset = new Vector2(0f, 2f),
                blurRadius = 6f,
                color = new Color(0f, 0f, 0f, 0.85f)
            };
            label.pickingMode = PickingMode.Ignore;
            return label;
        }

        static Button ChromeButton(string text, System.Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("btn");
            button.style.height = BUTTON_HEIGHT;
            button.style.minWidth = BUTTON_MIN_WIDTH;
            button.style.fontSize = Agent_UIStyle.FontM;
            button.style.flexGrow = 0;
            return button;
        }

        void ReturnToMenu()
        {
            // Matches Agent_HUD: a match paused through the HUD leaves a time
            // freeze in place, and loading the menu without releasing it lands
            // the player on a frozen main menu.
            Agent_TimeFreeze.ReleaseAll();
            SceneManager.LoadScene(_menuScene);
        }

        void ToggleTelemetry()
        {
            if (_telemetry == null) _telemetry = FindFirstObjectByType<Agent_Telemetry>();
            if (_telemetry == null)
            {
                Debug.LogWarning("Agent_Chrome: no Agent_Telemetry in this scene; DEBUG has nothing to show.");
                return;
            }
            _telemetry.SetVisible(!_telemetry.IsVisible);
        }

        void Update()
        {
            if (!_showFps || _fpsLabel == null) return;

            float dt = Time.unscaledDeltaTime;
            if (dt > 0f)
            {
                _fpsAccum += dt;
                _fpsFrames++;
            }

            _fpsTimer += dt;
            if (_fpsTimer < _fpsInterval) return;
            _fpsTimer = 0f;

            if (_fpsFrames == 0 || _fpsAccum <= 0f) return;
            int fps = Mathf.RoundToInt(_fpsFrames / _fpsAccum);
            _fpsAccum = 0f;
            _fpsFrames = 0;

            if (fps == _shownFps) return;
            _shownFps = fps;
            _fpsLabel.text = FpsText[Mathf.Clamp(fps, 0, FPS_CACHE_MAX)];
        }
    }
}
