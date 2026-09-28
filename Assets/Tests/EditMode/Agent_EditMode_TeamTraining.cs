using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the team-training switches added 2026-09-28.
    ///
    /// Every run before them trained 1v1, so the teammate observation block had never
    /// held anything but zeros. POSOCCER_SQUAD brings the training pitches to N-a-side,
    /// and two trainer-driven shaping terms (`team_roles`, `team_spacing`) aim at the
    /// first thing every 2v2 policy does wrong: both teammates chasing one ball.
    ///
    /// What is pinned here is what can silently go wrong without a player build:
    /// the squad pattern parser (a typo must be REJECTED, never read as a different
    /// lineup), the cover-spot geometry, the spacing penalty's shape, and that the
    /// spacing budget any shipped config asks for cannot outrank conceding.
    /// </summary>
    public sealed class Agent_EditMode_TeamTraining
    {
        [Test]
        public void SquadPattern_ParsesSingleAndCycledSizes()
        {
            CollectionAssert.AreEqual(new[] { 2 }, Agent_EnvController.ParseSquadPattern("2"));
            CollectionAssert.AreEqual(new[] { 1, 2 }, Agent_EnvController.ParseSquadPattern("1,2"));
            CollectionAssert.AreEqual(new[] { 1, 2 }, Agent_EnvController.ParseSquadPattern(" 1 , 2 "));
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("two")]
        [TestCase("0")]
        [TestCase("1,,2")]
        [TestCase("-1")]
        [TestCase("11")]
        public void SquadPattern_RejectsAnythingMalformed(string raw)
        {
            Assert.IsNull(Agent_EnvController.ParseSquadPattern(raw),
                "A malformed POSOCCER_SQUAD must be rejected, not reinterpreted - a " +
                "multi-hour run on a lineup nobody asked for reads exactly like a real result.");
        }

        [Test]
        public void SquadForPitch_CyclesThePatternOverTheGrid()
        {
            int[] pattern = { 1, 2 };
            Assert.AreEqual(1, Agent_EnvController.SquadForPitch(pattern, 0));
            Assert.AreEqual(2, Agent_EnvController.SquadForPitch(pattern, 1));
            Assert.AreEqual(1, Agent_EnvController.SquadForPitch(pattern, 14));
            Assert.AreEqual(2, Agent_EnvController.SquadForPitch(pattern, 15));
            Assert.AreEqual(1, Agent_EnvController.SquadForPitch(null, 3),
                "No pattern means the authored 1v1.");
        }

        [Test]
        public void SupportSpot_SitsBehindTheBall_TowardOwnGoal()
        {
            var ball = new Vector2(0f, 5f);
            var ownGoal = new Vector2(0f, -27f);
            Vector2 spot = Agent_EnvController.SupportSpot(ball, ownGoal);

            Assert.AreEqual(0f, spot.x, 1e-4f);
            Assert.AreEqual(5f - Agent_EnvController.SUPPORT_DEPTH, spot.y, 1e-4f,
                "The cover spot is SUPPORT_DEPTH back from the ball on the line to our own goal.");

            // Ball almost on our own goal line: the spot must not overshoot past the goal.
            Vector2 close = Agent_EnvController.SupportSpot(new Vector2(0f, -26f), ownGoal);
            Assert.AreEqual(-27f, close.y, 1e-4f);
        }

        [Test]
        public void SpacingPenalty_IsZeroOutsideTheRadius_AndLinearInside()
        {
            const float scale = 0.00005f;
            float r = Agent_EnvController.TEAM_SPACING_RADIUS;

            Assert.AreEqual(0f, Agent_EnvController.SpacingPenalty(r, scale));
            Assert.AreEqual(0f, Agent_EnvController.SpacingPenalty(r * 2f, scale));
            Assert.AreEqual(-scale, Agent_EnvController.SpacingPenalty(0f, scale), 1e-9f);
            Assert.AreEqual(-scale * 0.5f, Agent_EnvController.SpacingPenalty(r * 0.5f, scale), 1e-9f);
            Assert.AreEqual(0f, Agent_EnvController.SpacingPenalty(0f, 0f),
                "Scale 0 is the default for every run that does not ask for the term.");
        }

        [Test]
        public void RewardAccounting_NamesEveryTerm_IncludingSpacing()
        {
            Assert.AreEqual(Agent_Soccer.TermCount, Agent_Soccer.TermNames.Length);
            CollectionAssert.Contains(Agent_Soccer.TermNames, "spacing");
        }

        /// <summary>
        /// Same ordering property Agent_EditMode_RewardBudget enforces on the profile
        /// terms, applied to the team term, which lives in trainer configs instead: a
        /// pair standing together for a whole episode must not lose more to spacing
        /// than the team loses to conceding.
        /// </summary>
        [Test]
        public void EveryConfigsSpacingCeiling_StaysUnderConceding()
        {
            var standard = AssetDatabase.LoadAssetAtPath<Reward_Settings>(
                "Assets/Agents/Standard_v01/Reward_STANDARD.asset");
            Assert.IsNotNull(standard, "Reward_STANDARD.asset not found.");
            float conceding = Mathf.Abs(standard.goalConceded);
            int cap = standard.maxEnvironmentSteps;

            string configDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "config"));
            if (!Directory.Exists(configDir)) Assert.Ignore("No config/ folder in this checkout.");

            var line = new Regex(@"^\s*team_spacing:\s*([0-9.eE+-]+)\s*$", RegexOptions.Multiline);
            foreach (string path in Directory.GetFiles(configDir, "*.yaml"))
            {
                foreach (Match match in line.Matches(File.ReadAllText(path)))
                {
                    float scale = float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    float ceiling = scale * cap;
                    Assert.Less(ceiling, conceding,
                        $"{Path.GetFileName(path)}: team_spacing {scale} x {cap} steps = -{ceiling:F3}, " +
                        $"which outweighs conceding (-{conceding}). Lower it.");
                }
            }
        }
    }
}
