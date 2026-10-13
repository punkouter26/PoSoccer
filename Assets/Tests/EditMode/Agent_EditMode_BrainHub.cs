using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the community-brain loader against the two ways it can go wrong quietly.
    ///
    /// THE ROUND TRIP is the load-bearing test. Agent_BrainHub rebuilds a ModelAsset
    /// from a .sentis file by writing two INTERNAL fields of an Inference Engine type
    /// through reflection. A package update that renames either field would otherwise be
    /// found by a player whose download does nothing. Exporting the shipped STANDARD
    /// brain and loading it back through the same code a download takes exercises every
    /// byte of that path against a real model.
    ///
    /// THE REFUSALS are the security half. A file from the internet has to be rejected
    /// when it is not a model, and a name from an index must never address anything
    /// outside the brains folder.
    /// </summary>
    public sealed class Agent_EditMode_BrainHub
    {
        const string STANDARD_PROFILE = "Assets/Agents/Standard_v01/Reward_STANDARD.asset";

        static Reward_Settings Standard()
        {
            var profile = AssetDatabase.LoadAssetAtPath<Reward_Settings>(STANDARD_PROFILE);
            Assert.IsNotNull(profile, STANDARD_PROFILE + " is missing");
            if (profile.brainModel == null)
            {
                Assert.Ignore("STANDARD has no brain in this checkout (git lfs pull?) - nothing to round trip.");
            }
            return profile;
        }

        [Test]
        public void AnExportedBrain_LoadsBackAndFitsTheContract()
        {
            Reward_Settings standard = Standard();
            string folder = Path.Combine(Path.GetTempPath(), "posoccer-hub-" + Guid.NewGuid().ToString("N"));
            Reward_Settings loaded = null;
            try
            {
                var index = Agent_BrainHub.Export(new List<Reward_Settings> { standard }, folder, "test");
                Assert.AreEqual(1, index.brains.Length, "the shipped brain was not exported");
                Assert.AreEqual(Agent_Soccer.ExpectedModelInputSize, index.modelInputs);

                var entry = index.brains[0];
                Assert.IsTrue(Agent_BrainHub.IsSafeFileName(entry.file));
                byte[] bytes = File.ReadAllBytes(Path.Combine(folder, entry.file));

                entry.name = "Visitor";
                Assert.IsTrue(Agent_BrainHub.TryBuildProfile(entry, bytes, standard, out loaded,
                    out string error), "refused its own export: " + error);

                Assert.AreEqual("VISITOR", loaded.playerName);
                Assert.IsNotNull(loaded.brainModel);
                Assert.AreNotSame(standard.brainModel, loaded.brainModel);
                Assert.IsTrue(Agent_Soccer.ModelFitsContract(loaded.brainModel));
                Assert.AreEqual(Reward_Settings.KitPattern.None, loaded.kitPattern,
                    "a community brain is never given a kit");
                Assert.AreEqual(-1f, loaded.trainedActionGain, "the file does not say; neither may the profile");

                // The index on disk is the one a hub would serve.
                var parsed = Agent_BrainHub.ParseIndex(
                    File.ReadAllText(Path.Combine(folder, Agent_BrainHub.INDEX_FILE)));
                Assert.IsNotNull(parsed);
                Assert.AreEqual(1, parsed.brains.Length);
            }
            finally
            {
                if (loaded != null) UnityEngine.Object.DestroyImmediate(loaded);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        [Test]
        public void AFileThatIsNotAModel_IsRefusedWithAReason()
        {
            Assert.IsNull(Agent_BrainHub.LoadModel(null, "x", out string none));
            StringAssert.Contains("truncated", none);

            Assert.IsNull(Agent_BrainHub.LoadModel(new byte[] { 1, 2, 3 }, "x", out _));

            // A length prefix that claims more bytes than the file holds.
            byte[] lying = new byte[16];
            BitConverter.GetBytes(1_000_000).CopyTo(lying, 0);
            Assert.IsNull(Agent_BrainHub.LoadModel(lying, "x", out string doesNotFit));
            StringAssert.Contains("does not fit", doesNotFit);
        }

        [Test]
        public void GarbageOfTheRightShape_IsRefusedNotFielded()
        {
            // One well-formed block of noise: passes the structure check, is not a model.
            // The loader reports through the package's own error log on the way out.
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            try
            {
                byte[] noise = new byte[4 + 64];
                BitConverter.GetBytes(64).CopyTo(noise, 0);
                for (int byteIndex = 4; byteIndex < noise.Length; byteIndex++)
                {
                    noise[byteIndex] = (byte)(byteIndex * 37);
                }
                Assert.IsNull(Agent_BrainHub.LoadModel(noise, "x", out string error));
                Assert.IsNotNull(error);
            }
            finally
            {
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
            }
        }

        [TestCase("STANDARD.sentis", true)]
        [TestCase("my-brain_02.sentis", true)]
        [TestCase("../STANDARD.sentis", false)]
        [TestCase("a/b.sentis", false)]
        [TestCase("a\\b.sentis", false)]
        [TestCase("..sentis", false)]
        [TestCase(".sentis", false)]
        [TestCase("brain.onnx", false)]
        [TestCase("brain.sentis.exe", false)]
        [TestCase("", false)]
        public void OnlyAPlainSentisName_MayBeWritten(string file, bool expected)
        {
            Assert.AreEqual(expected, Agent_BrainHub.IsSafeFileName(file));
        }

        [TestCase("punkouter26/posoccer-brains", true)]
        [TestCase("owner/name.v2", true)]
        [TestCase("owner", false)]
        [TestCase("owner/name/extra", false)]
        [TestCase("owner/../name", false)]
        [TestCase("owner/name?x=1", false)]
        [TestCase("/name", false)]
        public void OnlyAnOwnerSlashName_IsARepo(string repo, bool expected)
        {
            Assert.AreEqual(expected, Agent_BrainHub.IsSafeRepo(repo));
        }

        [Test]
        public void ARosterName_IsNeverEmptyAndNeverLong()
        {
            Assert.AreEqual("BRAIN", Agent_BrainHub.RosterName(null));
            Assert.AreEqual("BRAIN", Agent_BrainHub.RosterName("!!!"));
            Assert.AreEqual("MYBRAIN2", Agent_BrainHub.RosterName("my brain 2"));
            Assert.AreEqual(Agent_BrainHub.MAX_NAME_LENGTH,
                Agent_BrainHub.RosterName("AVeryLongNameIndeedForAPlayer").Length);
        }

        [Test]
        public void TextThatIsNotAnIndex_ParsesToNull()
        {
            Assert.IsNull(Agent_BrainHub.ParseIndex(""));
            Assert.IsNull(Agent_BrainHub.ParseIndex("<html>404</html>"));
            Assert.IsNotNull(Agent_BrainHub.ParseIndex("{\"formatVersion\":1,\"brains\":[]}"));
        }
    }
}
