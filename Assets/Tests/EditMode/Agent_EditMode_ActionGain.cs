using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Guards the train/deploy mismatch that <see cref="Agent_EditMode_ObsContract"/>
    /// structurally cannot see.
    ///
    /// That test compares TENSOR SHAPES. <see cref="Agent_Soccer.ActionGain"/> changes
    /// none of them: it scales the three steering channels between the policy's output
    /// and the feet. Move it and every existing .onnx still declares the right inputs,
    /// still loads without a warning, and then drives at the wrong magnitude - a policy
    /// that learned "0.625 buys full force" under a 1.6 gain gets 0.625 of it under 1.0.
    ///
    /// This is the SECOND time this project has shipped a shape-identical, meaning-
    /// changed brain (the first was the 2026-08-28 world-frame -> body-frame fix). The
    /// difference in severity is real and worth keeping straight: a frame change
    /// scrambles which direction an observation points, while a gain change is a
    /// monotonic scaling that preserves direction and only costs magnitude. Degraded,
    /// not scrambled. Both are silent, which is why both need a guard rather than a
    /// paragraph in CLAUDE.md.
    ///
    /// Discovered 2026-09-07: STANDARD had been retrained at 1.0 (p22) while MATT, NICK
    /// and KIM still carried p21 checkpoints trained at 1.6, and nothing anywhere
    /// recorded that fact.
    /// </summary>
    public sealed class Agent_EditMode_ActionGain
    {
        static IEnumerable<(string path, Reward_Settings profile)> Profiles()
        {
            string[] guids = AssetDatabase.FindAssets("t:Reward_Settings");
            Assert.That(guids.Length, Is.GreaterThan(0), "No Reward_Settings assets found.");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var profile = AssetDatabase.LoadAssetAtPath<Reward_Settings>(path);
                if (profile != null) yield return (path, profile);
            }
        }

        /// <summary>
        /// A deployed brain with no recorded gain is the actual silent-drift risk: it
        /// cannot be compared against anything, so nobody can tell whether it is stale.
        /// Profiles with a null brainModel (BOT, and any personality parked back on the
        /// scripted bot) are exempt - there is no brain to mis-drive.
        /// </summary>
        [Test]
        public void EveryDeployedBrain_RecordsTheGainItWasTrainedUnder()
        {
            var unknown = new StringBuilder();
            foreach (var (path, profile) in Profiles())
            {
                if (profile.brainModel == null) continue;
                if (profile.trainedActionGain <= 0f)
                {
                    unknown.AppendLine(
                        $"  {profile.playerName} ({path}) has a brainModel but " +
                        $"trainedActionGain = {profile.trainedActionGain} (unknown provenance).");
                }
            }

            Assert.That(unknown.Length, Is.Zero,
                "A deployed brain with unrecorded ActionGain provenance cannot be checked " +
                "for staleness:\n" + unknown +
                "\nStamp it via scripts/update-model.ps1, or set the field to the gain the " +
                "run actually trained under.");
        }

        /// <summary>
        /// The substantive check. A mismatch is not cosmetic - it means the shipped
        /// brain is driving at a different magnitude than the one it was optimised for.
        /// </summary>
        [Test]
        public void EveryDeployedBrain_WasTrainedAtTheCurrentActionGain()
        {
            var stale = new StringBuilder();
            foreach (var (path, profile) in Profiles())
            {
                if (profile.brainModel == null) continue;
                if (profile.trainedActionGain <= 0f) continue;   // covered by the test above
                if (!Mathf.Approximately(profile.trainedActionGain, Agent_Soccer.ActionGain))
                {
                    stale.AppendLine(
                        $"  {profile.playerName} ({path}): trained at gain " +
                        $"{profile.trainedActionGain}, runtime is {Agent_Soccer.ActionGain} " +
                        $"(run {profile.trainingRunId}, {profile.trainingSteps} steps).");
                }
            }

            Assert.That(stale.Length, Is.Zero,
                $"Deployed brains trained at a different Agent_Soccer.ActionGain than the " +
                $"current {Agent_Soccer.ActionGain}. They load without warning and then drive " +
                $"at the wrong magnitude:\n" + stale +
                "\nFix by retraining at the current gain, or by clearing brainModel so the " +
                "profile falls back to Agent_HeuristicBot (the precedent set 2026-08-05 when " +
                "an obs change obsoleted all four brains). Do NOT silence this by editing " +
                "trainedActionGain - the field records history, it does not set policy.");
        }

        /// <summary>
        /// The gain exists to be the identity. If someone reintroduces a multiplier,
        /// this fails and points at the dead-band arithmetic that motivated 1.0, so the
        /// reasoning has to be re-made rather than rediscovered.
        /// </summary>
        [Test]
        public void ActionGain_IsTheIdentity_OrTheChangeIsDeliberate()
        {
            Assert.That(Agent_Soccer.ActionGain, Is.EqualTo(1.0f).Within(1e-6f),
                "ActionGain is no longer 1.0. A gain g multiplies THEN clamps, so the policy " +
                "loses its ability to express any magnitude between 1/g and 1.0 - at g=1.6 " +
                "that dead band was [0.625, 1.0] and p21's policy mean sat inside it. It also " +
                "scales the TURN channel, which measured 2.1x the scripted bot's heading churn. " +
                "If this change is intended, retrain every profile and update trainedActionGain.");
        }
    }
}
