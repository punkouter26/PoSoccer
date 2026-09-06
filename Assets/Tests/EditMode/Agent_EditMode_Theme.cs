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
