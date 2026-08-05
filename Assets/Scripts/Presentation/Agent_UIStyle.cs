using UnityEngine;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// Single source of truth for runtime UI styling. All values are in the
    /// 1170x2532 reference-resolution units the shared PanelSettings scales from,
    /// so menu and HUD stay visually consistent.
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

        public static void ApplySafeArea(VisualElement element)
        {
            Rect safe = Screen.safeArea;
            element.style.paddingTop = Screen.height - safe.yMax;
            element.style.paddingBottom = safe.yMin;
            element.style.paddingLeft = safe.xMin;
            element.style.paddingRight = Screen.width - safe.xMax;
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
            b.style.fontSize = FontS;
            b.style.color = TextPrimary;
            b.style.backgroundColor = PanelBg;
            Round(b);
            b.style.paddingLeft = 28; b.style.paddingRight = 28;
            b.style.paddingTop = 14; b.style.paddingBottom = 14;
            return b;
        }
    }
}
