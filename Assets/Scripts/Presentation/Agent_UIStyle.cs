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
        public static readonly Color BlueTeam = new(0.2f, 0.5f, 1f);
        public static readonly Color RedTeam = new(1f, 0.25f, 0.2f);
        public static readonly Color Accent = new(0.16f, 0.55f, 0.28f);
        public static readonly Color StaminaLow = new(0.9f, 0.3f, 0.2f);
        public static readonly Color StaminaHigh = new(0.3f, 0.9f, 0.4f);

        // Rhythm (reference px)
        public const int Pad = 24;
        public const int Radius = 16;
        public const int FontS = 30;
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

        /// <summary>Safe-area insets as (top, bottom, left, right) in screen pixels.</summary>
        static Vector4 SafeAreaPadding()
        {
            Rect safe = Screen.safeArea;
            return new Vector4(
                Screen.height - safe.yMax,
                safe.yMin,
                safe.xMin,
                Screen.width - safe.xMax);
        }

        public static void ApplySafeArea(VisualElement element)
        {
            if (element == null) return;
            Vector4 padding = SafeAreaPadding();
            element.style.paddingTop = padding.x;
            element.style.paddingBottom = padding.y;
            element.style.paddingLeft = padding.z;
            element.style.paddingRight = padding.w;
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
            Vector4 applied = SafeAreaPadding();
            ApplySafeArea(element);
            element.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                Vector4 current = SafeAreaPadding();
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
    }
}
