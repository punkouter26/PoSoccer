using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Guards the three contracts the 2026-09-06 GFX pass created.
    ///
    /// 1. TEAM COLOUR HAS ONE SOURCE. It used to have three - Agent_SoccerView's
    ///    fields, Agent_UIStyle's fields and the stylesheet's variables - and they
    ///    had already drifted. Every one of them now reads Agent_Palette, and
    ///    this fails if any grows its own copy again.
    ///
    /// 2. THE KIT SYSTEM SHIPS SWITCHED OFF. UNITY_RULES reserves how a brain
    ///    looks to its author: heuristic bot red, reference brain "green,
    ///    untextured", custom brains user-supplied textures, "never auto-assign
    ///    one". A procedural jersey is a texture by that standard, so the feature
    ///    is opt-in and every shipped profile must sit at KitPattern.None. This is
    ///    the test that stops a later session from quietly dressing the roster.
    ///
    /// 3. THE SHADER'S CBUFFER IS IDENTICAL ACROSS ITS THREE PASSES. The SRP
    ///    Batcher cannot cope with a constant buffer that differs between passes
    ///    of one shader, and the failure is not a compile error - it is a silently
    ///    broken batch, or worse, garbage read from a mismatched offset. Adding a
    ///    property to two passes out of three is a one-line mistake with no
    ///    feedback, so it is checked by reading the file.
    /// </summary>
    public sealed class Agent_EditMode_Palette
    {
        const string SHADER_PATH = "Assets/Shaders/PoSoccer_SpriteLitFX.shader";
        const string FX_INCLUDE = "Assets/Shaders/PoSoccerFX.hlsl";

        [SetUp]
        public void ResetPalette() => Agent_Palette.ResetForTests();

        [Test]
        public void EveryTeamColourConsumer_ReadsThePalette()
        {
            Assert.AreEqual(Agent_Palette.Blue, Agent_UIStyle.BlueTeam,
                "Agent_UIStyle grew its own blue again.");
            Assert.AreEqual(Agent_Palette.Red, Agent_UIStyle.RedTeam,
                "Agent_UIStyle grew its own red again.");
            Assert.AreEqual(Agent_Palette.Blue, Agent_SoccerView.TeamColor(Agent_Soccer.Team.Blue),
                "The pitch and the UI disagree about blue.");
            Assert.AreEqual(Agent_Palette.Red, Agent_SoccerView.TeamColor(Agent_Soccer.Team.Red),
                "The pitch and the UI disagree about red.");
        }

        /// <summary>
        /// The alternative palettes must actually differ from the default AND
        /// separate the two teams in LUMINANCE, which is the property that
        /// survives a colour deficiency, a greyscale screenshot and a cheap
        /// panel. A "colour safe" mode that ships the same two colours under a
        /// different name is worse than no mode at all: it tells a player who
        /// cannot use the default that they have already tried the fix.
        ///
        /// THE DEFAULT PALETTE IS EXEMPT, AND THAT IS THE FINDING. Measured
        /// here: the shipped blue (0.20, 0.50, 1.00) and red (1.00, 0.25, 0.20)
        /// have relative luminances of 0.472 and 0.406 - a separation of 0.066,
        /// well under the 0.1 this test demands of the alternatives. The two team
        /// marks are very nearly ISOLUMINANT, so a viewer who cannot separate
        /// them by hue has almost nothing left to separate them by. That is not a
        /// bug to fix by quietly restyling the shipped game - it is the reason the
        /// two other modes exist, and this test asserts the gap rather than
        /// hiding it, so nobody later concludes the modes are decorative.
        /// </summary>
        [Test]
        public void AlternativePalettes_AreDifferentAndSeparableByLuminance()
        {
            SetModeWithoutPrefs(Agent_Palette.Mode.Standard);
            Color standardBlue = Agent_Palette.Blue;
            Color standardRed = Agent_Palette.Red;

            float standardGap = LuminanceGap(standardBlue, standardRed);
            Assert.Less(standardGap, 0.1f,
                "The shipped palette now separates the teams by luminance on its own. " +
                "Good - but this test's premise (and Agent_Palette's docstring) says it " +
                "does not, so update both rather than deleting the assertion.");

            var alternatives = new[] { Agent_Palette.Mode.ColourSafe, Agent_Palette.Mode.HighContrast };
            foreach (var mode in alternatives)
            {
                SetModeWithoutPrefs(mode);
                Assert.Greater(LuminanceGap(Agent_Palette.Blue, Agent_Palette.Red), 0.1f,
                    $"{mode} does not separate the two teams by luminance, which is the " +
                    "one thing it exists to do.");
                Assert.AreNotEqual(standardBlue, Agent_Palette.Blue, $"{mode} ships the default blue.");
                Assert.AreNotEqual(standardRed, Agent_Palette.Red, $"{mode} ships the default red.");
            }

            SetModeWithoutPrefs(Agent_Palette.Mode.Standard);
            Assert.AreEqual(standardBlue, Agent_Palette.Blue);
            Assert.AreEqual(standardRed, Agent_Palette.Red);
        }

        static float LuminanceGap(Color a, Color b)
        {
            float lumaA = 0.2126f * a.r + 0.7152f * a.g + 0.0722f * a.b;
            float lumaB = 0.2126f * b.r + 0.7152f * b.g + 0.0722f * b.b;
            return Mathf.Abs(lumaA - lumaB);
        }

        [Test]
        public void EveryShippedProfile_ShipsWithNoKit()
        {
            string[] guids = AssetDatabase.FindAssets("t:Reward_Settings");
            Assert.Greater(guids.Length, 0, "No Reward_Settings assets found at all.");

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var profile = AssetDatabase.LoadAssetAtPath<Reward_Settings>(path);
                if (profile == null) continue;

                Assert.AreEqual(Reward_Settings.KitPattern.None, profile.kitPattern,
                    $"{path} carries a kit pattern. UNITY_RULES: the reference brain is " +
                    "green and UNTEXTURED, and custom brains get USER-SUPPLIED textures - " +
                    "'never auto-assign one'. If this was a deliberate authoring choice by " +
                    "the user, record it in docs/rules-exemptions.md and update this test.");
            }
        }

        /// <summary>
        /// Every property the FX include reads must be declared in all three
        /// passes' CBUFFERs, and the Properties block must expose it.
        /// </summary>
        [Test]
        public void ShaderConstantBuffer_IsIdenticalInEveryPass()
        {
            string shader = File.ReadAllText(SHADER_PATH);

            var blocks = Regex.Matches(shader, @"CBUFFER_START\(UnityPerMaterial\)(.*?)CBUFFER_END",
                RegexOptions.Singleline);
            Assert.AreEqual(3, blocks.Count,
                $"{SHADER_PATH} should have exactly three UnityPerMaterial buffers " +
                "(Universal2D, NormalsRendering, UniversalForward).");

            string first = Normalise(blocks[0].Groups[1].Value);
            for (int i = 1; i < blocks.Count; i++)
            {
                Assert.AreEqual(first, Normalise(blocks[i].Groups[1].Value),
                    "The UnityPerMaterial buffers differ between passes. The SRP Batcher " +
                    "requires them to be identical, and the failure is silent.");
            }

            // The new properties, checked by name so a partial edit is caught.
            foreach (string property in new[] { "_SpriteRect", "_KitMode", "_KitColor", "_KitScale" })
            {
                Assert.IsTrue(first.Contains(property),
                    $"{property} is missing from the constant buffer.");
                Assert.IsTrue(Regex.IsMatch(shader, $@"^\s*{property}\(", RegexOptions.Multiline),
                    $"{property} is in the CBUFFER but not in the Properties block, so a " +
                    "material cannot carry a value for it and it reads as garbage.");
            }
        }

        /// <summary>
        /// The atlas-UV fix has to be applied to every UV-space term, not just
        /// the one that prompted it.
        ///
        /// This is a text check rather than a render check on purpose: the bug it
        /// guards is invisible in a screenshot (a flat tint reads as a design
        /// choice) and only shows up as "the rim never looked like a rim". Every
        /// use of the raw `uv` inside the effect functions is what went wrong.
        /// </summary>
        [Test]
        public void EveryUvSpaceEffect_MapsThroughTheSpriteSlot()
        {
            string include = File.ReadAllText(FX_INCLUDE);

            Assert.IsTrue(include.Contains("float2 PoSoccerLocalUV(float2 uv)"),
                "The sprite-local UV helper is gone; every UV effect is back on atlas " +
                "page coordinates.");

            // Inside the two entry points, the raw parameter must be converted
            // once and never used again afterwards.
            foreach (string function in new[] { "PoSoccerApplyFX", "PoSoccerNetMask" })
            {
                // Matches the DEFINITION (name, parameter list, then a brace on
                // its own line) rather than a call, and stops at the first
                // column-0 closing brace - which is where an HLSL function ends.
                var body = Regex.Match(include, $@"{function}\([^)]*\)\s*\{{(.*?)\n\}}",
                    RegexOptions.Singleline);
                Assert.IsTrue(body.Success, $"{function} not found in {FX_INCLUDE}.");
                Assert.IsTrue(body.Groups[1].Value.Contains("PoSoccerLocalUV(uv)"),
                    $"{function} never calls PoSoccerLocalUV, so it reads atlas page UVs.");
            }
        }

        /// <summary>
        /// The 2D renderer carries NO renderer features, and that is a finding
        /// rather than an oversight.
        ///
        /// A full-screen pass was built here on 2026-09-06 - shockwave refraction,
        /// radial speed smear, tilt-shift - and MEASURED not to run on this
        /// renderer. URP's own FullScreenPassRendererFeature drew pure white at
        /// AfterRenderingPostProcessing and nothing at all before it; a
        /// hand-written feature doing the canonical blit-and-swap did nothing at
        /// either event, and neither did the same pass derived from
        /// ScriptableRenderPass2D with the 2D renderer's own injection enum. None
        /// of it changed after a full editor restart. Every attempt was confirmed
        /// with a probe compiled into the shader, not by looking for a subtle
        /// effect.
        ///
        /// So this asserts the list is EMPTY. If a later session adds a feature
        /// here, this test fails and points at the record above - which is much
        /// cheaper than rediscovering it, and stops a pass that silently does
        /// nothing from shipping while costing a colour copy per frame.
        /// See docs/gfx-audio-pass-2026-09-06.md.
        /// </summary>
        [Test]
        public void The2DRenderer_CarriesNoRendererFeatures()
        {
            var data = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.Universal.ScriptableRendererData>(
                "Assets/Settings/Renderer2D.asset");
            Assert.IsNotNull(data, "Assets/Settings/Renderer2D.asset did not load.");

            Assert.AreEqual(0, data.rendererFeatures.Count,
                "A renderer feature was added to the 2D renderer. Full-screen passes were " +
                "measured NOT to execute on it (see this test's docstring and " +
                "docs/gfx-audio-pass-2026-09-06.md); verify with a probe compiled into the " +
                "shader before trusting one, and update this test with what you find.");
        }

        static string Normalise(string block)
        {
            return Regex.Replace(block, @"\s+", " ").Trim();
        }

        /// <summary>
        /// Sets the mode through the public setter and then re-clears the loaded
        /// flag, so the test never leaves a preference on the machine running it.
        /// </summary>
        static void SetModeWithoutPrefs(Agent_Palette.Mode mode)
        {
            Agent_Palette.Current = mode;
            PlayerPrefs.DeleteKey("posoccer.palette.mode");
            PlayerPrefs.DeleteKey("posoccer.palette.typescale");
        }
    }
}
