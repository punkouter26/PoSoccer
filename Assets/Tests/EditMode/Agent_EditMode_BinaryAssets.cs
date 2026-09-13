using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Every Git-LFS-tracked binary in the project is a real file, not a pointer
    /// stub.
    ///
    /// WHY THIS GENERALISES AN EXISTING TEST. Agent_EditMode_Theme already
    /// carries this check for the two .ttf faces, with the right size floor and
    /// the right remediation in its message. It was written after the fonts
    /// specifically were found stubbed, and it was never widened. On 2026-09-13
    /// a checkout of this machine had ALL 125 LFS objects stubbed at ~130 bytes -
    /// every sprite, every WAV, both fonts, the three launcher icons AND all four
    /// trained .onnx brains - and the font test was the only thing in the suite
    /// that could have said so.
    ///
    /// What each class of stub actually does, none of which throws:
    ///  - .png  Unity imports a broken sprite; the pitch, ball and every player
    ///          body render wrong or not at all.
    ///  - .wav  imports as a zero-length clip; the game is silent. Agent_Audio's
    ///          whole adaptive layer runs correctly over nothing.
    ///  - .ttf  UI Toolkit falls back to its built-in face, so the UI simply
    ///          looks the way it did before the typefaces were added.
    ///  - .onnx fails to import with InvalidProtocolBufferException ("invalid
    ///          wire type"), which reads exactly like the protobuf landmine and
    ///          is not it. Every profile's brainModel resolves null, so all four
    ///          personalities silently fall back to Agent_HeuristicBot and the
    ///          benchmark grades the scripted bot against itself.
    ///
    /// That last one is the expensive case: it degrades the BENCHMARK, not just
    /// the presentation, and an eval JSON written in that state still records a
    /// plausible run id and win rate.
    ///
    /// The floor is per-extension rather than one number because the smallest
    /// legitimate file in each class differs by two orders of magnitude. Every
    /// floor is set well under the real minimum on disk so a legitimately small
    /// asset never trips it - the thing being detected is ~130 bytes.
    ///
    /// Reads the FILES, not the imported assets, so it needs no scene and no
    /// play mode. It also does not shell out to git: a stub is identifiable by
    /// its own first line, which is the spec'd LFS pointer header.
    /// </summary>
    public sealed class Agent_EditMode_BinaryAssets
    {
        const string LFS_HEADER = "version https://git-lfs.github.com/spec/v1";

        /// <summary>
        /// Extensions routed through LFS by .gitattributes, with the size below
        /// which the file cannot be a real asset of that kind.
        ///
        /// THE FLOORS ARE A BACKSTOP, NOT THE DETECTOR. LooksLikePointer is the
        /// exact test - it reads the spec'd pointer header - and these only add
        /// cover for a file that is broken some other way (truncated, half
        /// written). So every floor is set well under the smallest REAL file of
        /// its kind, because a floor that is too high fails on a legitimate asset
        /// and teaches people to ignore this test.
        ///
        /// The .png floor learned that the hard way: it was first written at 500
        /// and immediately failed on Assets/Sprites/tile.png, which is a valid
        /// 313-byte PNG - a small flat tile compresses to almost nothing. Stubs
        /// are ~130 bytes, so 200 keeps the margin on the side that matters.
        /// </summary>
        static readonly Dictionary<string, long> Floors = new()
        {
            [".onnx"] = 10000,
            [".png"] = 200,      // smallest real one on disk: tile.png at 313
            [".jpg"] = 200,
            [".wav"] = 200,
            [".mp3"] = 200,
            [".ttf"] = 20000,
            [".psd"] = 200,
            [".fbx"] = 200,
        };

        [Test]
        public void EveryLfsTrackedBinary_IsRealAndNotAPointerStub()
        {
            var offenders = new StringBuilder();
            int checkedCount = 0;
            int stubCount = 0;

            foreach (string path in Directory.EnumerateFiles("Assets", "*", SearchOption.AllDirectories))
            {
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (!Floors.TryGetValue(extension, out long floor))
                {
                    continue;
                }

                checkedCount++;
                long size = new FileInfo(path).Length;
                if (size >= floor && !LooksLikePointer(path))
                {
                    continue;
                }

                stubCount++;
                offenders.AppendLine($"  {path.Replace('\\', '/')} - {size} bytes");
            }

            Assert.Greater(checkedCount, 0,
                "Found no LFS-tracked binaries at all under Assets/. Either the " +
                "project lost its art and audio, or this test's extension table " +
                "has drifted from .gitattributes.");

            Assert.AreEqual(string.Empty, offenders.ToString(),
                $"{stubCount} of {checkedCount} binaries are Git LFS " +
                "pointer stubs, not real files:\n" + offenders +
                "\nThe project will run, render wrong, play silent, and field bots " +
                "instead of trained brains, with no error in the console.\n" +
                "Fix: git lfs install --local; git lfs pull   (then reimport in Unity)");
        }

        /// <summary>
        /// A stub is a short ASCII file whose first line is the LFS pointer
        /// header. Checked independently of size so a future stub format that
        /// happens to clear a floor is still caught.
        /// </summary>
        static bool LooksLikePointer(string path)
        {
            try
            {
                var buffer = new byte[LFS_HEADER.Length];
                using FileStream stream = File.OpenRead(path);
                int read = stream.Read(buffer, 0, buffer.Length);
                return read == buffer.Length
                    && Encoding.ASCII.GetString(buffer) == LFS_HEADER;
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>
        /// The four personality brains specifically. Separate from the sweep
        /// above because their absence is a BENCHMARK defect rather than a
        /// presentation one, and because a failure here should name the
        /// consequence rather than leave it to be inferred from a path.
        /// </summary>
        [Test]
        public void EveryDeployedBrain_IsALoadableOnnx()
        {
            string[] slots =
            {
                "Assets/Agents/Standard_v01/STANDARD.onnx",
                "Assets/Agents/Matt_v01/MATT.onnx",
                "Assets/Agents/Nick_v01/NICK.onnx",
                "Assets/Agents/Kim_v01/KIM.onnx",
            };

            foreach (string path in slots)
            {
                // A slot may legitimately be EMPTY - CLAUDE.md records long
                // stretches where a profile shipped with brainModel null on
                // purpose. What must never happen is a file that exists and is
                // not a model.
                if (!File.Exists(path))
                {
                    continue;
                }

                long size = new FileInfo(path).Length;
                Assert.IsFalse(LooksLikePointer(path),
                    $"{path} is a Git LFS pointer stub ({size} bytes). It will fail " +
                    "to import with InvalidProtocolBufferException, brainModel will " +
                    "resolve null, and this personality will silently field the " +
                    "heuristic bot while every eval JSON still records its run id.");
                Assert.Greater(size, 10000,
                    $"{path} is {size} bytes - too small to be a trained policy.");
            }
        }
    }
}
