using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace PoSoccer.Tests
{
    /// <summary>
    /// The shader's variant and constant-buffer budget.
    ///
    /// PoSoccer_SpriteLitFX's own header states the invariant this guards:
    /// "No shader_feature keywords on purpose: four toggles would mean sixteen
    /// variants for effects that cost a few ALU each, and keyword-free branches
    /// keep the SRP Batcher's constant layout identical across every material."
    /// That is a design decision with a cost attached, written in a comment, and
    /// a comment cannot fail a build.
    ///
    /// WHY A KEYWORD IS THE THING TO WATCH. Adding `#pragma shader_feature
    /// _STRIPES_ON` to save a handful of ALU is a natural-looking optimisation
    /// that passes every existing test, renders identically in the editor, and
    /// multiplies the compiled variant count of a THREE-PASS shader. Variants are
    /// paid for at build time and in player load time and memory, neither of
    /// which any test in this project measures. It also risks the SRP Batcher:
    /// batching requires a byte-identical UnityPerMaterial layout, and
    /// Agent_EditMode_Palette.ShaderConstantBuffer_IsIdenticalInEveryPass already
    /// pins the layout ACROSS passes - this pins its SIZE, so growth is a
    /// deliberate act rather than a side effect.
    ///
    /// The allowed list is exactly what URP's Sprite-Lit-Default declares and
    /// this shader inherited. Anything else is new, and new is what needs a
    /// decision.
    ///
    /// Reads the file rather than the compiled ShaderVariantCollection, so it
    /// runs in EditMode with no import and no play mode.
    /// </summary>
    public sealed class Agent_EditMode_ShaderBudget
    {
        const string SHADER_PATH = "Assets/Shaders/PoSoccer_SpriteLitFX.shader";
        const string INCLUDE_PATH = "Assets/Shaders/PoSoccerFX.hlsl";

        /// <summary>
        /// Keyword pragmas inherited from URP's Sprite-Lit-Default. Every one of
        /// these is required by the lighting path; none was added by PoSoccer.
        /// </summary>
        static readonly HashSet<string> InheritedPragmas = new()
        {
            "multi_compile_instancing",
            "multi_compile _ SKINNED_SPRITE",
            "multi_compile _ DEBUG_DISPLAY",
            "multi_compile_fragment _ LINEAR_TO_SRGB_CONVERSION",
        };

        /// <summary>
        /// The UnityPerMaterial block is allowed to hold this many float4-equivalent
        /// entries. Raising it is fine; doing it without noticing is not.
        /// </summary>
        const int MAX_CBUFFER_ENTRIES = 24;

        [Test]
        public void TheShader_AddsNoVariantKeywordsOfItsOwn()
        {
            string source = ReadOrFail(SHADER_PATH);
            var offenders = new StringBuilder();

            // Horizontal whitespace only. `\s` matches newlines, so `\s*` after the
            // keyword swallowed the line break and the capture ran on into the NEXT
            // pragma - which reported two real lines as one bogus offender.
            foreach (Match match in Regex.Matches(source,
                @"^[ \t]*#pragma[ \t]+((?:multi_compile|shader_feature)[^\r\n]*)",
                RegexOptions.Multiline))
            {
                string pragma = match.Groups[1].Value.Trim();
                if (InheritedPragmas.Contains(pragma))
                {
                    continue;
                }

                offenders.AppendLine($"  #pragma {pragma}");
            }

            Assert.AreEqual(string.Empty, offenders.ToString(),
                "PoSoccer_SpriteLitFX declares variant keywords beyond the ones it " +
                "inherited from Sprite-Lit-Default:\n" + offenders +
                "\nThis shader has THREE passes, so each toggle multiplies the " +
                "compiled variant count across all of them, and every effect it " +
                "would gate costs a few ALU. The shader's header records this as a " +
                "deliberate choice. If the trade has genuinely changed, measure the " +
                "variant count and the ALU saving, update the header, and add the " +
                "pragma to InheritedPragmas here with the measurement in the commit.");
        }

        [Test]
        public void TheConstantBuffer_StaysWithinBudget()
        {
            string source = ReadOrFail(SHADER_PATH);

            var block = Regex.Match(source,
                @"CBUFFER_START\(UnityPerMaterial\)(.*?)CBUFFER_END",
                RegexOptions.Singleline);
            Assert.IsTrue(block.Success,
                $"{SHADER_PATH} has no UnityPerMaterial block - without one the SRP " +
                "Batcher cannot batch this shader at all.");

            int entries = 0;
            foreach (string raw in block.Groups[1].Value.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#"))
                {
                    continue;
                }

                if (line.EndsWith(";"))
                {
                    entries++;
                }
            }

            Assert.LessOrEqual(entries, MAX_CBUFFER_ENTRIES,
                $"UnityPerMaterial declares {entries} entries against a budget of " +
                $"{MAX_CBUFFER_ENTRIES}. Every material using this shader pays for the " +
                "whole buffer whether or not it uses the feature. Raise the budget " +
                "deliberately, not as a side effect of adding a property.");
        }

        /// <summary>
        /// Every UV-space term lives in the include, and the include is what
        /// Agent_EditMode_Palette.EveryUvSpaceEffect_MapsThroughTheSpriteSlot
        /// reads. This asserts the two files stay split that way, because moving
        /// a term inline into the .shader would move it out from under that
        /// test's nose while still compiling and rendering.
        /// </summary>
        [Test]
        public void EveryEffectTerm_StaysInTheIncludeWhereTheAtlasGuardCanSeeIt()
        {
            Assert.IsTrue(File.Exists(INCLUDE_PATH),
                $"{INCLUDE_PATH} is missing. The atlas-slot guard reads it; without " +
                "it that guard silently checks nothing.");

            string source = ReadOrFail(SHADER_PATH);
            Assert.IsTrue(source.Contains("PoSoccerFX.hlsl"),
                $"{SHADER_PATH} no longer includes {INCLUDE_PATH}. If the effect terms " +
                "were inlined, EveryUvSpaceEffect_MapsThroughTheSpriteSlot is now " +
                "reading a file the shader does not use - and the atlas landmine " +
                "(every UV effect measuring the atlas PAGE, not the sprite) is " +
                "unguarded again.");
        }

        static string ReadOrFail(string path)
        {
            Assert.IsTrue(File.Exists(path), $"{path} is missing.");
            return File.ReadAllText(path);
        }
    }
}
