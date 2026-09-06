using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the roster card's arithmetic and its two honesty properties.
    ///
    /// The card exists to show numbers a player will act on - who finishes
    /// best, which brain has actually been graded - so the failure mode that
    /// matters is not a crash but a plausible-looking wrong number. These tests
    /// target exactly that: the comparative scale, the sign handling on
    /// conceding, and the distinction between "never evaluated" and "evaluated
    /// at zero" that evalWinRate's -1 default exists to preserve.
    /// </summary>
    public class Agent_EditMode_PlayerCard
    {
        readonly List<Reward_Settings> _created = new();

        Reward_Settings Profile(string name)
        {
            var profile = ScriptableObject.CreateInstance<Reward_Settings>();
            profile.playerName = name;
            _created.Add(profile);
            return profile;
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _created.Count; i++)
            {
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            }
            _created.Clear();
        }

        static float ShareOf(Agent_PlayerCard.Attribute[] attributes, string label)
        {
            for (int i = 0; i < attributes.Length; i++)
            {
                if (attributes[i].Label == label) return attributes[i].Share;
            }
            Assert.Fail($"no attribute named {label}");
            return 0f;
        }

        [Test]
        public void Attributes_ProducesEveryRow()
        {
            var only = Profile("ONLY");
            var attributes = Agent_PlayerCard.Attributes(only, new[] { only });
            Assert.AreEqual(Agent_PlayerCard.ATTRIBUTE_COUNT, attributes.Length);
        }

        [Test]
        public void Attributes_RosterLeaderReadsFull()
        {
            var weak = Profile("WEAK");
            weak.goalScorer = 0.7f;
            var strong = Profile("STRONG");
            strong.goalScorer = 1.4f;

            var roster = new[] { weak, strong };

            Assert.AreEqual(1f, ShareOf(Agent_PlayerCard.Attributes(strong, roster), "FINISHING"), 0.001f,
                "the roster's best finisher must read a full bar");
            Assert.AreEqual(0.5f, ShareOf(Agent_PlayerCard.Attributes(weak, roster), "FINISHING"), 0.001f,
                "half the leader's value must read half a bar");
        }

        [Test]
        public void Attributes_DisciplineUsesTheMagnitudeOfANegativeReward()
        {
            // goalConceded is stored negative. Read naively, the most fearful
            // defender would have the SMALLEST value and rank last on the trait
            // that describes her.
            var cautious = Profile("CAUTIOUS");
            cautious.goalConceded = -1.2f;
            var reckless = Profile("RECKLESS");
            reckless.goalConceded = -0.6f;

            var roster = new[] { cautious, reckless };

            Assert.AreEqual(1f, ShareOf(Agent_PlayerCard.Attributes(cautious, roster), "DISCIPLINE"), 0.001f);
            Assert.AreEqual(0.5f, ShareOf(Agent_PlayerCard.Attributes(reckless, roster), "DISCIPLINE"), 0.001f);
        }

        [Test]
        public void Attributes_UnspentTraitReadsEmptyForEveryone()
        {
            // Nobody spends on possession here, so CONTROL must read empty
            // rather than dividing by zero or defaulting to a full bar.
            var a = Profile("A");
            var b = Profile("B");
            a.possessionScale = 0f;
            b.possessionScale = 0f;

            var roster = new[] { a, b };
            Assert.AreEqual(0f, ShareOf(Agent_PlayerCard.Attributes(a, roster), "CONTROL"), 0.0001f);
        }

        [Test]
        public void Attributes_SignatureTraitBelongsToItsSoleOwner()
        {
            var wall = Profile("WALL");
            wall.defensivePositionScale = 0.0006f;
            var striker = Profile("STRIKER");
            striker.defensivePositionScale = 0f;

            var roster = new[] { wall, striker };

            Assert.AreEqual(1f, ShareOf(Agent_PlayerCard.Attributes(wall, roster), "COVER"), 0.001f);
            Assert.AreEqual(0f, ShareOf(Agent_PlayerCard.Attributes(striker, roster), "COVER"), 0.001f);
        }

        [Test]
        public void Attributes_NullProfileIsEmptyNotAThrow()
        {
            Assert.AreEqual(0, Agent_PlayerCard.Attributes(null, null).Length);
        }

        [Test]
        public void Attributes_NullRosterMeasuresTheProfileAgainstItself()
        {
            var lone = Profile("LONE");
            lone.goalScorer = 1.1f;
            Assert.AreEqual(1f, ShareOf(Agent_PlayerCard.Attributes(lone, null), "FINISHING"), 0.001f);
        }

        [Test]
        public void RecordText_KeepsUngradedDistinctFromGradedAtZero()
        {
            var never = Profile("NEVER");
            never.evalWinRate = -1f;

            var lost = Profile("LOST");
            lost.evalWinRate = 0f;
            lost.evalEpisodes = 350;

            Assert.AreEqual("UNGRADED", Agent_PlayerCard.RecordText(never, null),
                "a brain that was never evaluated must not read as a measured result");
            Assert.AreEqual("0%", Agent_PlayerCard.RecordText(lost, null),
                "a brain measured at zero must read as a measured result");
        }

        [Test]
        public void RecordCaption_CarriesTheSampleSize()
        {
            var graded = Profile("GRADED");
            graded.evalWinRate = 0.2571f;
            graded.evalEpisodes = 350;

            StringAssert.Contains("350", Agent_PlayerCard.RecordCaption(graded, null),
                "a win rate shown without its episode count is the exact number this project's " +
                "notes warn against over-reading");
        }

        [Test]
        public void DriverText_SeparatesTheBenchmarkFromAnUntrainedBrain()
        {
            var bot = Profile("BOT");
            var untrained = Profile("UNTRAINED");

            Assert.AreEqual("RULE-BASED BOT", Agent_PlayerCard.DriverText(bot, bot));
            Assert.AreNotEqual(Agent_PlayerCard.DriverText(bot, bot),
                Agent_PlayerCard.DriverText(untrained, bot),
                "the benchmark and a brainless personality both field the bot, but the card " +
                "must not present them as the same thing");
        }

        [Test]
        public void FormatSteps_ScalesTheUnit()
        {
            Assert.AreEqual("10M", Agent_PlayerCard.FormatSteps(10_000_034));
            Assert.AreEqual("2M", Agent_PlayerCard.FormatSteps(1_999_868));
            Assert.AreEqual("500k", Agent_PlayerCard.FormatSteps(500_000));
            Assert.AreEqual("42", Agent_PlayerCard.FormatSteps(42));
            Assert.AreEqual("NONE", Agent_PlayerCard.StepsText(Profile("RAW"), null));
        }

        [Test]
        public void ShippedRoster_RendersWithoutAnEmptyAttributeSet()
        {
            // The live assets, not fixtures: the card is only worth anything if
            // it works on the roster the menu actually loads.
            string[] paths =
            {
                "Assets/Agents/Standard_v01/Reward_STANDARD.asset",
                "Assets/Agents/Matt_v01/Reward_MATT.asset",
                "Assets/Agents/Kim_v01/Reward_KIM.asset",
                "Assets/Agents/Nick_v01/Reward_NICK.asset",
                "Assets/Agents/Bot_v01/Reward_BOT.asset",
            };

            var roster = new Reward_Settings[paths.Length];
            for (int i = 0; i < paths.Length; i++)
            {
                roster[i] = AssetDatabase.LoadAssetAtPath<Reward_Settings>(paths[i]);
                Assert.IsNotNull(roster[i], $"missing {paths[i]}");
            }

            for (int i = 0; i < roster.Length; i++)
            {
                var attributes = Agent_PlayerCard.Attributes(roster[i], roster);
                Assert.AreEqual(Agent_PlayerCard.ATTRIBUTE_COUNT, attributes.Length,
                    $"{roster[i].playerName} produced no attribute rows");

                for (int a = 0; a < attributes.Length; a++)
                {
                    Assert.GreaterOrEqual(attributes[a].Share, 0f,
                        $"{roster[i].playerName}/{attributes[a].Label} below zero");
                    Assert.LessOrEqual(attributes[a].Share, 1f,
                        $"{roster[i].playerName}/{attributes[a].Label} above a full bar");
                }
            }
        }
    }
}
