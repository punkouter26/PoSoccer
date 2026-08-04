using NUnit.Framework;
using UnityEditor;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the rule-based benchmark opponent's contract. The BOT roster entry only
    /// works because its profile carries no brainModel — Agent_MatchLoader.Apply reads
    /// exactly that field to decide between inference and Agent_HeuristicBot. Assigning
    /// a brain to this asset would silently turn the benchmark into another AI player.
    /// </summary>
    public class Agent_EditMode_BotProfile
    {
        const string BOT_PROFILE_PATH = "Assets/Agents/Bot_v01/Reward_BOT.asset";

        [Test]
        public void BotProfile_ExistsAtTheGuidStableSlot()
        {
            var profile = AssetDatabase.LoadAssetAtPath<Reward_Settings>(BOT_PROFILE_PATH);
            Assert.IsNotNull(profile, $"missing {BOT_PROFILE_PATH} — the menu's BOT pick resolves to null");
            Assert.AreEqual("BOT", profile.playerName);
        }

        [Test]
        public void BotProfile_NeverCarriesABrainModel()
        {
            var profile = AssetDatabase.LoadAssetAtPath<Reward_Settings>(BOT_PROFILE_PATH);
            Assert.IsNotNull(profile);
            Assert.IsNull(profile.brainModel,
                "Reward_BOT must stay brainless: it is the rule-based baseline trained brains are measured against");
        }
    }
}
