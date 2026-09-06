using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// The one place team colour is decided, plus the accessibility options that
    /// change it.
    ///
    /// WHY THIS EXISTS. Before it, the blue/red pair was written down in three
    /// places that had no way to agree: Agent_SoccerView's two static readonly
    /// Colors (the eye, the outline frame and, through Agent_Surfaces, the rim
    /// on every player material), Agent_UIStyle's BlueTeam/RedTeam (chips,
    /// buttons, menu bands) and PoSoccerTheme.uss's --color-team-* (meters, the
    /// win-probability strip). They already disagreed - the sim used
    /// (0.2, 0.5, 1.0) while the stylesheet used rgb(51,128,255) = (0.2, 0.502,
    /// 1.0), close enough to look like a rounding artefact and far enough to prove
    /// nobody could keep them in step by hand.
    ///
    /// THE DEFAULT PAIR IS THE WORST CASE FOR COLOUR VISION. Red against blue is
    /// the single most common confusion axis: protanopia darkens red until it
    /// reads as the same value as a mid blue, and this game asks the viewer to
    /// tell two teams apart at phone size, in motion, on a green pitch. The
    /// ColourSafe mode swaps red for orange out of the Okabe-Ito set, which was
    /// designed to stay separable under every common form of colour blindness;
    /// HighContrast additionally pushes the two apart in LUMINANCE, so they
    /// survive greyscale, glare and a cheap panel.
    ///
    /// WHAT THIS CANNOT DO, stated plainly rather than implied: it is not a live
    /// retint. Materials, Light2D colours and the LineRenderer outlines are built
    /// once in Start and read the palette then. Changing the mode from the menu
    /// therefore applies to the UI immediately (the menu rebuilds) and to the
    /// pitch on the next match load. Changing it mid-match is not offered.
    ///
    /// The stylesheet holds the same three palettes as descendant rules on a root
    /// class (see .palette--safe / .palette--contrast in PoSoccerTheme.uss),
    /// because UI Toolkit has no API for writing a USS custom property from C#.
    /// <see cref="Agent_UIStyle.ApplyTheme"/> puts the class on the root, so the
    /// USS half and the C# half switch together from this one enum.
    /// </summary>
    public static class Agent_Palette
    {
        public enum Mode
        {
            /// <summary>The shipped look: saturated blue against saturated red.</summary>
            Standard = 0,
            /// <summary>Okabe-Ito blue/orange - separable under every common colour deficiency.</summary>
            ColourSafe = 1,
            /// <summary>Blue/amber pushed apart in luminance as well as hue, for glare and greyscale.</summary>
            HighContrast = 2,
        }

        const string MODE_KEY = "posoccer.palette.mode";
        const string TYPE_KEY = "posoccer.palette.typescale";

        // Standard. Taken from Agent_SoccerView's originals so the default build
        // is pixel-identical to what shipped before this class existed.
        static readonly Color StandardBlue = new(0.2f, 0.5f, 1f);
        static readonly Color StandardRed = new(1f, 0.25f, 0.2f);

        // Okabe-Ito "blue" #0072B2 and "orange" #E69F00. Chosen as a PAIR: they
        // hold a visible hue difference under protanopia, deuteranopia and
        // tritanopia, which a red/blue pair does not.
        static readonly Color SafeBlue = new(0f, 0.447f, 0.698f);
        static readonly Color SafeRed = new(0.902f, 0.624f, 0f);

        // High contrast. Relative luminance ~0.21 for the blue against ~0.60 for
        // the amber, a ratio near 2.4:1 between the two team marks themselves -
        // so they stay distinguishable in a greyscale screenshot, which is the
        // cheapest honest test of a colour pair.
        static readonly Color ContrastBlue = new(0.16f, 0.42f, 1f);
        static readonly Color ContrastRed = new(1f, 0.76f, 0.06f);

        static Mode _mode;
        static int _typeScale;
        static bool _loaded;

        /// <summary>
        /// Raised after <see cref="Current"/> or <see cref="TypeScale"/> changes,
        /// so open UI can rebuild. Nothing on the pitch subscribes - see the
        /// docstring on why a live retint is not offered.
        /// </summary>
        public static event System.Action Changed;

        public static Mode Current
        {
            get { Load(); return _mode; }
            set
            {
                Load();
                if (_mode == value) return;
                _mode = value;
                PlayerPrefs.SetInt(MODE_KEY, (int)value);
                PlayerPrefs.Save();
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// Type scale step: 0 = the shipped sizes, 1 = large, 2 = larger. Steps
        /// rather than a float because the stylesheet has to carry a matching
        /// rule for each one, and an arbitrary multiplier cannot be expressed in
        /// USS. See .type--lg / .type--xl in PoSoccerTheme.uss.
        /// </summary>
        public static int TypeScale
        {
            get { Load(); return _typeScale; }
            set
            {
                Load();
                int clamped = Mathf.Clamp(value, 0, 2);
                if (_typeScale == clamped) return;
                _typeScale = clamped;
                PlayerPrefs.SetInt(TYPE_KEY, clamped);
                PlayerPrefs.Save();
                Changed?.Invoke();
            }
        }

        public static Color Blue
        {
            get
            {
                Load();
                switch (_mode)
                {
                    case Mode.ColourSafe: return SafeBlue;
                    case Mode.HighContrast: return ContrastBlue;
                    default: return StandardBlue;
                }
            }
        }

        public static Color Red
        {
            get
            {
                Load();
                switch (_mode)
                {
                    case Mode.ColourSafe: return SafeRed;
                    case Mode.HighContrast: return ContrastRed;
                    default: return StandardRed;
                }
            }
        }

        public static Color Team(Agent_Soccer.Team team)
            => team == Agent_Soccer.Team.Blue ? Blue : Red;

        /// <summary>Short label for the settings button; also used in tests.</summary>
        public static string Label(Mode mode)
        {
            switch (mode)
            {
                case Mode.ColourSafe: return "COLOUR SAFE";
                case Mode.HighContrast: return "HIGH CONTRAST";
                default: return "STANDARD";
            }
        }

        /// <summary>USS class carrying this mode's palette, or null for the default.</summary>
        public static string RootClass(Mode mode)
        {
            switch (mode)
            {
                case Mode.ColourSafe: return "palette--safe";
                case Mode.HighContrast: return "palette--contrast";
                default: return null;
            }
        }

        /// <summary>USS class carrying this type step, or null for the default.</summary>
        public static string TypeClass(int step)
        {
            switch (Mathf.Clamp(step, 0, 2))
            {
                case 1: return "type--lg";
                case 2: return "type--xl";
                default: return null;
            }
        }

        /// <summary>Advances the palette one step, wrapping. Bound to the menu button.</summary>
        public static void CycleMode()
        {
            Current = (Mode)(((int)Current + 1) % 3);
        }

        /// <summary>Advances the type scale one step, wrapping.</summary>
        public static void CycleTypeScale()
        {
            TypeScale = (TypeScale + 1) % 3;
        }

        /// <summary>
        /// Resets to the shipped defaults WITHOUT touching PlayerPrefs, for tests
        /// that must not leave a preference behind on the developer's machine.
        /// </summary>
        public static void ResetForTests()
        {
            _loaded = true;
            _mode = Mode.Standard;
            _typeScale = 0;
        }

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _mode = (Mode)Mathf.Clamp(PlayerPrefs.GetInt(MODE_KEY, 0), 0, 2);
            _typeScale = Mathf.Clamp(PlayerPrefs.GetInt(TYPE_KEY, 0), 0, 2);
        }
    }
}
