using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PoSoccer;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Guards the two contracts the 2026-09-05 UI work created but could not
    /// express in the type system.
    ///
    /// 1. THE TYPE SCALE EXISTS TWICE. Agent_UIStyle's constants and
    ///    PoSoccerTheme.uss's --font-* vars describe one scale, and UI Toolkit
    ///    offers no way to read a USS custom property from C#, so neither can be
    ///    derived from the other. Drift between them is exactly the failure the
    ///    stylesheet's own header records - class-driven sizes rendering at UI
    ///    Toolkit's ~12 px default while inline sizes stayed correct - and it is
    ///    invisible on a desktop monitor. This parses the stylesheet and fails on
    ///    disagreement.
    ///
    /// 2. UXML NAMES ARE THE HUD'S API. Agent_HUD.BuildFromTemplate resolves
    ///    fourteen elements out of Resources/HUD.uxml by name. Renaming one in the
    ///    template compiles cleanly and disables the HUD at runtime with a log
    ///    line nobody reads during a match.
    ///
    /// Both tests read the FILES rather than the imported assets, so they run in
    /// EditMode with no scene, no panel and no play mode.
    /// </summary>
    public sealed class Agent_EditMode_Theme
    {
        const string THEME_PATH = "Assets/Resources/PoSoccerTheme.uss";
        const string HUD_PATH = "Assets/Resources/HUD.uxml";
        const string MENU_PATH = "Assets/Resources/Menu.uxml";

        [Test]
        public void FontScale_MatchesTheStylesheet()
        {
            // Comments stripped for the same reason as below: the header
            // discusses these very variables and their historical values.
            string uss = StripComments(ReadOrFail(THEME_PATH));

            var expected = new Dictionary<string, int>
            {
                { "--font-xs", Agent_UIStyle.FontXS },
                { "--font-s", Agent_UIStyle.FontS },
                { "--font-m", Agent_UIStyle.FontM },
                { "--font-l", Agent_UIStyle.FontL },
                { "--font-xl", Agent_UIStyle.FontXL },
            };

            foreach (var pair in expected)
            {
                // The `px` is part of the pattern on purpose - see below.
                var match = Regex.Match(uss, $@"{Regex.Escape(pair.Key)}\s*:\s*(\d+)px\s*;");
                Assert.IsTrue(match.Success,
                    $"{THEME_PATH} has no `{pair.Key}: <n>px;` declaration. " +
                    "Either it was renamed or its px suffix was dropped.");

                int fromUss = int.Parse(match.Groups[1].Value);
                Assert.AreEqual(pair.Value, fromUss,
                    $"Type scale drift: Agent_UIStyle says {pair.Value} for {pair.Key}, " +
                    $"{THEME_PATH} says {fromUss}. They describe one scale; change both.");
            }
        }

        /// <summary>
        /// Every length var must carry a unit. A unitless `--font-s: 38;` parses
        /// as a stylesheet but every `font-size: var(--font-s)` reading it fails
        /// silently and falls back to ~12 px - which is what shipped, and was only
        /// caught on a device on 2026-08-29. Colours are exempt: they are not
        /// lengths and have no unit.
        /// </summary>
        [Test]
        public void EveryLengthVariable_CarriesAUnit()
        {
            // Comments must go first. The stylesheet's own header QUOTES the bug
            // this test exists to catch - "these were unitless (e.g.
            // `--font-s: 38;`)" - so a scan of the raw text fails on the
            // documentation of the fix rather than on any real regression.
            string uss = StripComments(ReadOrFail(THEME_PATH));

            foreach (Match match in Regex.Matches(uss, @"(--(?:font|space|radius)-[a-z0-9-]+)\s*:\s*([^;]+);"))
            {
                string name = match.Groups[1].Value;
                string value = match.Groups[2].Value.Trim();
                Assert.IsTrue(value.EndsWith("px") || value.EndsWith("%"),
                    $"{name} is `{value}` - a bare number. UI Toolkit cannot parse it " +
                    "as a length, so every rule reading it through var() silently " +
                    "falls back to the ~12 px default.");
            }
        }

        [Test]
        public void HudTemplate_ContainsEveryElementTheHudBinds()
        {
            string uxml = ReadOrFail(HUD_PATH);

            // Exactly the set queried in Agent_HUD.BuildFromTemplate. Every one of
            // these is in the null check that disables the HUD outright, so a name
            // missing here is a blank screen in a match, not a missing widget.
            string[] required =
            {
                "safe", "score", "clock",
                "meter-blue", "meter-red",
                "chips-blue", "chips-red",
                "controls",
                "toast", "commentary", "banner", "replay-tag",
                "letterbox-top", "letterbox-bottom",
                // Broadcast telemetry lanes: win-probability strip, stat ticker,
                // and the director/vision status bug.
                "winprob", "winprob-blue", "winprob-red", "winprob-label",
                "ticker", "broadcast-tag",
            };

            foreach (string name in required)
            {
                Assert.IsTrue(uxml.Contains($"name=\"{name}\""),
                    $"{HUD_PATH} has no element named \"{name}\", which " +
                    "Agent_HUD.BuildFromTemplate resolves by name. Renaming it there " +
                    "without renaming it here disables the whole HUD at runtime.");
            }
        }

        /// <summary>
        /// Every class the template applies must be defined in the stylesheet.
        ///
        /// The reverse of the test above, and it catches the other half of the
        /// same mistake: an element can exist, bind cleanly, drive real data and
        /// still be invisible because its class was never written. UI Toolkit
        /// reports nothing for an unknown class - it is simply not styled - and
        /// for an absolutely-positioned overlay lane "not styled" means "at 0,0
        /// with no opacity rule", which reads as the feature not working at all
        /// rather than as a missing rule.
        /// </summary>
        [Test]
        public void EveryClassTheTemplateUses_ExistsInTheStylesheet()
        {
            string uxml = ReadOrFail(HUD_PATH);
            string uss = StripComments(ReadOrFail(THEME_PATH));

            foreach (Match attribute in Regex.Matches(uxml, @"class=""([^""]+)"""))
            {
                foreach (string name in attribute.Groups[1].Value.Split(' '))
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    Assert.IsTrue(Regex.IsMatch(uss, $@"\.{Regex.Escape(name)}\b"),
                        $"{HUD_PATH} applies class \"{name}\", which {THEME_PATH} does " +
                        "not define. UI Toolkit reports nothing for an unknown class; " +
                        "the element just renders unstyled.");
                }
            }
        }

        /// <summary>
        /// The menu template's names are Agent_MainMenu.Build's API, exactly as
        /// the HUD's are Agent_HUD.BuildFromTemplate's.
        ///
        /// The failure is worse here than on the HUD, which is why this exists
        /// separately rather than as more strings in the list above: a missing
        /// HUD element costs a widget, while a missing menu element costs the
        /// SCREEN - Build logs an error and returns, leaving a blank panel with
        /// no way to start a match at all.
        /// </summary>
        [Test]
        public void MenuTemplate_ContainsEveryElementTheMenuBinds()
        {
            string uxml = ReadOrFail(MENU_PATH);

            string[] required =
            {
                "safe", "header", "footer",
                "presets", "preset-1v1", "preset-2v2", "preset-5v5", "preset-clear",
                "band-blue", "name-blue", "minus-blue", "count-blue", "plus-blue",
                "clear-blue", "roster-blue", "strip-blue",
                "band-red", "name-red", "minus-red", "count-red", "plus-red",
                "clear-red", "roster-red", "strip-red",
                "pitch-note", "play", "options", "settings", "back-hint",
            };

            foreach (string name in required)
            {
                Assert.IsTrue(uxml.Contains($"name=\"{name}\""),
                    $"{MENU_PATH} has no element named \"{name}\", which " +
                    "Agent_MainMenu.Build resolves by name. Renaming it there without " +
                    "renaming it here leaves the menu blank at runtime.");
            }
        }

        /// <summary>
        /// Same contract as the HUD's, applied to the menu template and to every
        /// class Agent_MainMenu adds from code.
        ///
        /// The code-added half matters more than the template half. A class in
        /// the UXML that nobody styled is at least visible in a text diff; a
        /// class named only inside a C# string - "slot--blue", "menu__pick" -
        /// has nothing to diff it against, and an unstyled squad card renders as
        /// a bare button with no team colour and no size.
        /// </summary>
        [Test]
        public void EveryClassTheMenuUses_ExistsInTheStylesheet()
        {
            string uxml = ReadOrFail(MENU_PATH);
            string uss = StripComments(ReadOrFail(THEME_PATH));

            foreach (Match attribute in Regex.Matches(uxml, @"class=""([^""]+)"""))
            {
                foreach (string name in attribute.Groups[1].Value.Split(' '))
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    Assert.IsTrue(Regex.IsMatch(uss, $@"\.{Regex.Escape(name)}\b"),
                        $"{MENU_PATH} applies class \"{name}\", which {THEME_PATH} does not define.");
                }
            }

            // Added from C# in Agent_MainMenu, so no template scan can find them.
            string[] fromCode =
            {
                "menu__pick", "menu__pick--blue", "menu__pick--red",
                "slot", "slot--compact", "slot--blue", "slot--red",
                "slot__name", "slot__driver", "slot__steps",
                "menu__empty",
            };

            foreach (string name in fromCode)
            {
                Assert.IsTrue(Regex.IsMatch(uss, $@"\.{Regex.Escape(name)}\b"),
                    $"Agent_MainMenu adds class \"{name}\" in code and {THEME_PATH} does not " +
                    "define it. The element renders unstyled with no warning.");
            }
        }

        /// <summary>
        /// The accessibility classes exist and actually override the variables
        /// they claim to.
        ///
        /// Agent_UIStyle.ApplyAccessibility puts one of these on the panel root
        /// and nothing else happens - the whole mechanism is USS variable
        /// inheritance. A rule that exists but redefines the wrong variable name
        /// (or none) leaves every control looking exactly as before, which is
        /// indistinguishable from the setting not being wired at all.
        /// </summary>
        [Test]
        public void AccessibilityClasses_OverrideTheVariablesTheyClaim()
        {
            string uss = StripComments(ReadOrFail(THEME_PATH));

            AssertBlockDefines(uss, "palette--safe", "--color-team-blue", "--color-team-red");
            AssertBlockDefines(uss, "palette--contrast", "--color-team-blue", "--color-team-red");
            AssertBlockDefines(uss, "type--lg", "--font-xs", "--font-s", "--font-m");
            AssertBlockDefines(uss, "type--xl", "--font-xs", "--font-s", "--font-m");

            // And the C# names them, so a rename on either side is caught.
            Assert.AreEqual("palette--safe",
                Agent_Palette.RootClass(Agent_Palette.Mode.ColourSafe));
            Assert.AreEqual("palette--contrast",
                Agent_Palette.RootClass(Agent_Palette.Mode.HighContrast));
            Assert.IsNull(Agent_Palette.RootClass(Agent_Palette.Mode.Standard),
                "The default palette must add NO class, or the stylesheet's own " +
                ":root values would need duplicating in a rule that could drift.");
            Assert.AreEqual("type--lg", Agent_Palette.TypeClass(1));
            Assert.AreEqual("type--xl", Agent_Palette.TypeClass(2));
        }

        /// <summary>
        /// The two typefaces are present, are real files, and are declared in the
        /// stylesheet.
        ///
        /// The size floor is the part that matters: Git LFS routes *.ttf, and a
        /// clone made without `git lfs install` leaves every binary as a ~130
        /// byte pointer stub. Unity imports that stub as a broken font and UI
        /// Toolkit falls back to its default silently - the UI just looks the way
        /// it did before the fonts existed, which is the hardest kind of
        /// regression to notice. See the LFS landmine in CLAUDE.md.
        /// </summary>
        [Test]
        public void Typefaces_ArePresentAndDeclared()
        {
            string[] fonts = { "Assets/Resources/Fonts/Inter.ttf", "Assets/Resources/Fonts/Oswald.ttf" };
            foreach (string path in fonts)
            {
                Assert.IsTrue(File.Exists(path), $"{path} is missing.");
                long size = new FileInfo(path).Length;
                Assert.Greater(size, 20000,
                    $"{path} is {size} bytes - that is an LFS pointer stub, not a font. " +
                    "Run `git lfs install --local; git lfs pull`.");
            }

            // The OFL requires the licence to travel with the fonts.
            Assert.IsTrue(File.Exists("Assets/Resources/Fonts/OFL-Inter.txt"));
            Assert.IsTrue(File.Exists("Assets/Resources/Fonts/OFL-Oswald.txt"));

            string uss = StripComments(ReadOrFail(THEME_PATH));
            Assert.IsTrue(uss.Contains("resource(\"Fonts/Inter\")"),
                $"{THEME_PATH} never declares the body face, so every label falls " +
                "back to UI Toolkit's built-in default.");
            Assert.IsTrue(uss.Contains("resource(\"Fonts/Oswald\")"),
                $"{THEME_PATH} never declares the display face.");
        }

        static void AssertBlockDefines(string uss, string className, params string[] variables)
        {
            var block = Regex.Match(uss, $@"\.{Regex.Escape(className)}\s*\{{([^}}]*)\}}");
            Assert.IsTrue(block.Success, $"{THEME_PATH} has no `.{className}` rule.");

            foreach (string variable in variables)
            {
                Assert.IsTrue(Regex.IsMatch(block.Groups[1].Value, $@"{Regex.Escape(variable)}\s*:"),
                    $"`.{className}` does not redefine {variable}, so switching to it " +
                    "changes nothing the player can see.");
            }
        }

        static string ReadOrFail(string path)
        {
            Assert.IsTrue(File.Exists(path), $"{path} is missing.");
            return File.ReadAllText(path);
        }

        static string StripComments(string uss)
        {
            return Regex.Replace(uss, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        }
    }
}
