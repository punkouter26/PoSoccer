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
    ///   upper-left    product name, with the FPS readout beneath it
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

            var safe = new VisualElement();
            safe.style.flexGrow = 1;
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
            productLabel.style.flexGrow = 0;
            productLabel.style.flexBasis = StyleKeyword.Auto;
            leftCorner.Add(productLabel);

            _fpsLabel = CornerLabel(string.Empty, Agent_UIStyle.TextMuted, Agent_UIStyle.FontS);
            _fpsLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            _fpsLabel.style.flexGrow = 0;
            _fpsLabel.style.flexBasis = StyleKeyword.Auto;
            _fpsLabel.style.display = _showFps ? DisplayStyle.Flex : DisplayStyle.None;
            leftCorner.Add(_fpsLabel);

            _menuButton = ChromeButton("MENU", ReturnToMenu);
            // Already at the menu: keep the slot so the row keeps its shape, but
            // nothing to navigate to.
            _menuButton.style.visibility = inMenu ? Visibility.Hidden : Visibility.Visible;
            top.Add(_menuButton);

            // -- bottom row --------------------------------------------------
            var bottom = Row();
            bottom.style.top = StyleKeyword.Null;
            bottom.style.bottom = Agent_UIStyle.Pad;
            safe.Add(bottom);

            bottom.Add(ChromeButton("DEBUG", ToggleTelemetry));

            var spacer = new VisualElement { style = { flexGrow = 1 } };
            spacer.pickingMode = PickingMode.Ignore;
            bottom.Add(spacer);

            var version = CornerLabel("v" + Application.version, Agent_UIStyle.TextMuted, Agent_UIStyle.FontS);
            version.style.unityTextAlign = TextAnchor.MiddleRight;
            bottom.Add(version);
        }

        static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.position = Position.Absolute;
            row.style.left = Agent_UIStyle.Pad;
            row.style.right = Agent_UIStyle.Pad;
            row.style.top = Agent_UIStyle.Pad;
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
