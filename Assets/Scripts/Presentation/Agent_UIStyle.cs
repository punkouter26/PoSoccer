using UnityEngine;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// Bridge between C# and the USS design system in
    /// Assets/Resources/PoSoccerTheme.uss, which is now the single source of
    /// truth for spacing, type and colour.
    ///
    /// Values below are in the shared PanelSettings reference space: 1080x1920,
    /// ScaleWithScreenSize matching WIDTH. This docstring previously claimed
    /// 1170x2532, which was never the configured resolution.
    ///
    /// The constants remain because dynamic styling legitimately needs them from
    /// code - a per-profile player colour or a computed meter width is data, not
    /// design. Static styling belongs in the stylesheet.
    /// </summary>
    public static class Agent_UIStyle
    {
        // Palette
        public static readonly Color Background = new(0.05f, 0.09f, 0.07f);
        public static readonly Color PanelBg = new(0f, 0f, 0f, 0.45f);
        public static readonly Color TextPrimary = Color.white;
        public static readonly Color TextMuted = new(0.7f, 0.75f, 0.72f);
        // Team colour is NOT a constant here any more: Agent_Palette owns it, so
        // the accessibility modes reach the HUD chips and menu bands through the
        // same switch that reaches the pitch. Kept as properties with the old
        // names so every existing call site is unchanged.
        public static Color BlueTeam => Agent_Palette.Blue;
        public static Color RedTeam => Agent_Palette.Red;
        public static readonly Color Accent = new(0.16f, 0.55f, 0.28f);
        public static readonly Color StaminaLow = new(0.9f, 0.3f, 0.2f);
        public static readonly Color StaminaHigh = new(0.3f, 0.9f, 0.4f);

        // Rhythm (reference px)
        public const int Pad = 24;
        public const int Radius = 16;
        // These MUST match --font-xs .. --font-xl in Resources/PoSoccerTheme.uss.
        // Two sources of truth for one scale is how half the UI ends up a size
        // nobody chose. See the USS for the sp arithmetic behind these numbers.
        //
        // 2026-09-05: that requirement used to be enforced by this comment and
        // nothing else, which is not enforcement. UI Toolkit exposes no public
        // API for reading a USS custom property's value from C#, so the scale
        // cannot literally live in one place - but it CAN be made impossible to
        // drift: Agent_EditMode_Theme parses the stylesheet and fails if any of
        // these five constants disagrees with its var. Change one, change both,
        // and the test tells you when you did not.
        public const int FontXS = 34;
        public const int FontS = 38;
        public const int FontM = 44;
        public const int FontL = 64;
        public const int FontXL = 120;

        /// <summary>USS class marking a transient element as currently visible.</summary>
        public const string SHOWN = "is-shown";

        /// <summary>USS class supplying a one-frame entrance offset.</summary>
        public const string ENTERING = "is-entering";

        const string THEME_RESOURCE = "PoSoccerTheme";
        static StyleSheet _theme;
        static bool _themeMissingLogged;

        /// <summary>
        /// Attaches the shared stylesheet to a panel root. Safe to call more than
        /// once per root. Loaded from Resources rather than a serialized field so
        /// no scene has to carry a reference to it.
        /// </summary>
        public static void ApplyTheme(VisualElement root)
        {
            if (root == null) return;
            if (_theme == null) _theme = Resources.Load<StyleSheet>(THEME_RESOURCE);
            if (_theme == null)
            {
                if (!_themeMissingLogged)
                {
                    _themeMissingLogged = true;
                    Debug.LogWarning($"Agent_UIStyle: '{THEME_RESOURCE}' not found in Resources; " +
                                     "UI falls back to inline styling.");
                }
                return;
            }
            if (!root.styleSheets.Contains(_theme)) root.styleSheets.Add(_theme);
            ApplyAccessibility(root);
        }

        /// <summary>
        /// Stamps the accessibility choices onto a panel root as USS classes.
        ///
        /// Both the palette and the type scale are expressed as OVERRIDES OF THE
        /// STYLESHEET'S OWN VARIABLES (.palette--safe redefines --color-team-*,
        /// .type--lg redefines --font-*), which is why this is one class on the
        /// root rather than a sweep over every element: USS custom properties
        /// inherit, so every rule already reading var(--font-s) picks the new
        /// value up with no C# involvement at all.
        ///
        /// Idempotent - every class it can add, it first removes - so it is safe
        /// to call again on a root that is already themed, which is what happens
        /// when the menu rebuilds after a settings change.
        /// </summary>
        public static void ApplyAccessibility(VisualElement root)
        {
            if (root == null) return;

            root.RemoveFromClassList("palette--safe");
            root.RemoveFromClassList("palette--contrast");
            root.RemoveFromClassList("type--lg");
            root.RemoveFromClassList("type--xl");

            string palette = Agent_Palette.RootClass(Agent_Palette.Current);
            if (palette != null) root.AddToClassList(palette);

            string type = Agent_Palette.TypeClass(Agent_Palette.TypeScale);
            if (type != null) root.AddToClassList(type);
        }

        /// <summary>Toggles the shared visibility class used by every transient lane.</summary>
        public static void SetShown(VisualElement element, bool shown)
        {
            if (element == null) return;
            element.EnableInClassList(SHOWN, shown);
        }

        /// <summary>
        /// Plays an element's entrance: applies the offset class, then clears it
        /// on the next frame so the USS transition animates from it.
        /// </summary>
        public static void PlayEntrance(VisualElement element, string fromClass = ENTERING)
        {
            if (element == null) return;
            element.AddToClassList(fromClass);
            element.schedule.Execute(() => element.RemoveFromClassList(fromClass)).ExecuteLater(16);
        }

        public static void Round(VisualElement e, int radius = Radius)
        {
            e.style.borderTopLeftRadius = radius;
            e.style.borderTopRightRadius = radius;
            e.style.borderBottomLeftRadius = radius;
            e.style.borderBottomRightRadius = radius;
        }

        public static void PadAll(VisualElement e, int pad = Pad)
        {
            e.style.paddingTop = pad;
            e.style.paddingBottom = pad;
            e.style.paddingLeft = pad;
            e.style.paddingRight = pad;
        }

        /// <summary>Safe-area insets as (top, bottom, left, right) in SCREEN pixels.</summary>
        static Vector4 SafeAreaPadding()
        {
            Rect safe = Screen.safeArea;
            return new Vector4(
                Screen.height - safe.yMax,
                safe.yMin,
                safe.xMin,
                Screen.width - safe.xMax);
        }

        /// <summary>
        /// Screen pixels per panel unit, read from the live panel.
        ///
        /// THE TWO SPACES ARE NOT THE SAME AND THIS FILE USED TO ASSUME THEY WERE.
        /// The shared PanelSettings is ScaleWithScreenSize, match-WIDTH, reference
        /// 1080x1920, so a panel unit is 1080/Screen.width screen pixels - 1.0 on a
        /// 1080-wide phone, 1.083 at 1170, 1.333 at 1440. Screen.safeArea is in
        /// screen pixels, and ApplySafeArea was writing those numbers straight into
        /// `style.padding*`, which UI Toolkit reads as PANEL units: every notch inset
        /// came out oversized by exactly that factor, up to a third too deep on a
        /// 1440-wide device. Always over-inset, so nothing ever clipped - it just
        /// quietly ate screen on the tall phones this game ships to.
        ///
        /// Derived from the panel rather than from the PanelSettings asset's
        /// reference resolution: the panel's own root spans it by definition, so this
        /// stays correct if the reference or the match mode is ever changed, and
        /// there is no second copy of 1080 to drift.
        /// </summary>
        static float PanelScale(VisualElement element)
        {
            var panel = element?.panel;
            if (panel == null) return 1f;
            float panelWidth = panel.visualTree.layout.width;
            if (float.IsNaN(panelWidth) || panelWidth < 1f) return 1f;
            return Screen.width / panelWidth;
        }

        /// <summary>
        /// Applies the safe-area inset, converted into panel units. Returns what was
        /// written so the caller can tell whether anything actually changed.
        /// </summary>
        public static Vector4 ApplySafeArea(VisualElement element)
        {
            if (element == null) return Vector4.zero;
            Vector4 padding = SafeAreaPadding() / PanelScale(element);
            element.style.paddingTop = padding.x;
            element.style.paddingBottom = padding.y;
            element.style.paddingLeft = padding.z;
            element.style.paddingRight = padding.w;
            return padding;
        }

        /// <summary>
        /// Applies the safe area now AND keeps it correct afterwards.
        ///
        /// ApplySafeArea on its own runs once in OnEnable and never again, so any
        /// later change to the reported insets - a resolution change, split
        /// screen, or just resizing the Game View - left stale padding baked in.
        /// The guard against re-entry matters: writing padding inside a
        /// GeometryChangedEvent handler causes another geometry change, so this
        /// only re-applies when the computed insets actually differ.
        /// </summary>
        public static void BindSafeArea(VisualElement element)
        {
            if (element == null) return;
            // The first call runs before the element is attached to a panel, so the
            // scale is not knowable yet and this lands in screen pixels. The
            // GeometryChanged pass below corrects it on the first layout - which is
            // also why the change detector compares what was WRITTEN (panel units)
            // rather than the raw Screen.safeArea: the inset can be unchanged while
            // the scale that converts it is not, and comparing the input would then
            // never re-apply.
            Vector4 applied = ApplySafeArea(element);
            element.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                Vector4 current = SafeAreaPadding() / PanelScale(element);
                if (current == applied) return;
                applied = current;
                ApplySafeArea(element);
            });
        }

        /// <summary>
        /// Builds a small "SND ON" / "SND OFF" toggle button bound to
        /// <see cref="Agent_Audio.Muted"/>. Both the main menu and the match HUD
        /// use this so the toggle stays visually consistent.
        /// </summary>
        public static Button SoundToggleButton()
        {
            Button b = null;
            b = new Button(() =>
            {
                Agent_Audio.Muted = !Agent_Audio.Muted;
                b.text = Agent_Audio.Muted ? "SND OFF" : "SND ON";
            })
            { text = Agent_Audio.Muted ? "SND OFF" : "SND ON" };
            b.AddToClassList("btn");
            return b;
        }

        /// <summary>
        /// The four output-level sliders, bound to <see cref="Agent_Audio"/>'s
        /// persisted player volumes.
        ///
        /// These sit alongside <see cref="SoundToggleButton"/> rather than
        /// replacing it. The toggle is the one control someone reaches for
        /// without looking - the room just got quiet, kill it now - and a slider
        /// is a poor substitute for that. What the toggle could never express is
        /// the ordinary case: crowd and music down, impacts and whistle up,
        /// because those are the sounds that carry information about the match.
        ///
        /// Built in code and shared, for the same reason the toggle is: the menu
        /// and the match HUD both present them, and a second copy would drift.
        ///
        /// CHANGES APPLY LIVE AND PERSIST IMMEDIATELY. There is no OK button, so
        /// there is no state to lose, no way to leave the panel in a half-applied
        /// condition, and the player hears what they are setting while they set
        /// it - which for a volume control is the only way to set it at all.
        /// </summary>
        public static VisualElement VolumeSliders()
        {
            var panel = new VisualElement();
            panel.AddToClassList("volumes");

            panel.Add(VolumeRow("MASTER",
                () => Agent_Audio.MasterVolume, v => Agent_Audio.MasterVolume = v));
            panel.Add(VolumeRow("EFFECTS",
                () => Agent_Audio.SfxVolume, v => Agent_Audio.SfxVolume = v));
            panel.Add(VolumeRow("CROWD",
                () => Agent_Audio.CrowdVolume, v => Agent_Audio.CrowdVolume = v));
            panel.Add(VolumeRow("MUSIC",
                () => Agent_Audio.MusicVolume, v => Agent_Audio.MusicVolume = v));

            return panel;
        }

        /// <summary>
        /// One labelled slider with a live percentage readout.
        ///
        /// The readout is not decoration. A bare slider on a phone is set by a
        /// thumb that covers the handle, so the only feedback while dragging is
        /// the audio itself - and the crowd bed in particular changes slowly
        /// enough that a small move sounds like nothing happened.
        /// </summary>
        static VisualElement VolumeRow(string label, System.Func<float> get, System.Action<float> set)
        {
            var row = new VisualElement();
            row.AddToClassList("volumes__row");

            var name = new Label(label);
            name.AddToClassList("volumes__label");
            row.Add(name);

            var slider = new Slider(0f, 1f) { value = get() };
            slider.AddToClassList("volumes__slider");
            row.Add(slider);

            var readout = new Label($"{Mathf.RoundToInt(get() * 100f)}%");
            readout.AddToClassList("volumes__value");
            row.Add(readout);

            slider.RegisterValueChangedCallback(evt =>
            {
                set(evt.newValue);
                readout.text = $"{Mathf.RoundToInt(evt.newValue * 100f)}%";
            });

            return row;
        }
    }
}
