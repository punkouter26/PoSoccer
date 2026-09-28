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
    /// lineup), the team-role split and post geometry (Agent_TeamRoles), the spacing
    /// penalty's shape, and that the spacing budget any shipped config asks for
    /// cannot outrank conceding.
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

        // ── Team roles ──────────────────────────────────────────────────────
        // Pitch-local frame used below: the authored 36 x 54 training pitch, BLUE
        // defending the goal at y = -27 and attacking the one at y = +27.
        static readonly Vector2 Half = new(18f, 27f);
        static readonly Vector2 OwnGoal = new(0f, -27f);
        static readonly Vector2 OppGoal = new(0f, 27f);

        [TestCase(1, false, 0)]
        [TestCase(2, false, 1)]   // attacker + defender: no keeper with only two
        [TestCase(3, true, 1)]    // goalie + defender + attacker
        [TestCase(4, true, 1)]    // goalie + defender + 2 attackers
        [TestCase(5, true, 2)]    // goalie + 2 defenders + 2 attackers
        public void SquadSize_DecidesGoalieAndDefenderCount(int size, bool goalie, int defenders)
        {
            Assert.AreEqual(goalie, Agent_TeamRoles.HasGoalie(size));
            Assert.AreEqual(defenders, Agent_TeamRoles.DefenderCount(size));
        }

        [Test]
        public void GoalieSpot_StaysInFrontOfTheLine_AndInsideTheMouth()
        {
            const float goalWidth = 6f;
            // Ball far out on the right wing: the goalie shades right, but never past the post.
            Vector2 spot = Agent_TeamRoles.GoalieSpot(new Vector2(17f, -20f), OwnGoal, goalWidth, Half);
            Assert.Greater(spot.x, 0f, "The goalie should shade toward the ball's side.");
            Assert.LessOrEqual(spot.x, goalWidth * 0.5f, "The goalie must not leave the goal mouth.");
            Assert.Greater(spot.y, OwnGoal.y, "The goalie stands in front of its line, not behind it.");
            Assert.Less(spot.y, OwnGoal.y + 2f, "The goalie stays near its line until it rushes.");

            // Ball dead level with the goal line: still in front of it.
            Vector2 flat = Agent_TeamRoles.GoalieSpot(new Vector2(10f, -27f), OwnGoal, goalWidth, Half);
            Assert.Greater(flat.y, OwnGoal.y);
        }

        [Test]
        public void Goalie_RushesOnlyForABallNearItsGoal()
        {
            Assert.IsTrue(Agent_TeamRoles.GoalieShouldRush(new Vector2(0f, -24f), OwnGoal));
            Assert.IsFalse(Agent_TeamRoles.GoalieShouldRush(new Vector2(0f, 0f), OwnGoal));
        }

        [Test]
        public void DefenderSpot_SitsBetweenOwnGoalAndBall()
        {
            var ball = new Vector2(0f, 10f);
            Vector2 spot = Agent_TeamRoles.DefenderSpot(ball, OwnGoal, 0, 1, Half);
            Assert.AreEqual(0f, spot.x, 1e-4f, "A lone defender sits on the goal-ball line.");
            Assert.Greater(spot.y, OwnGoal.y + Agent_TeamRoles.DEFENDER_MIN_DEPTH - 0.01f,
                "The defender leaves the goal line to the goalie.");
            Assert.Less(spot.y, ball.y - Agent_TeamRoles.DEFENDER_BALL_GAP + 0.01f,
                "The defender leaves the ball to the presser.");

            // Two defenders split across the line rather than stacking.
            Vector2 left = Agent_TeamRoles.DefenderSpot(ball, OwnGoal, 0, 2, Half);
            Vector2 right = Agent_TeamRoles.DefenderSpot(ball, OwnGoal, 1, 2, Half);
            Assert.AreEqual(Agent_TeamRoles.DEFENDER_SPREAD, Vector2.Distance(left, right), 1e-3f);
        }

        [Test]
        public void SupportSpot_IsAheadOfTheBall_OnTheOpenFlank()
        {
            var ball = new Vector2(6f, 0f);   // ball on the right
            Vector2 spot = Agent_TeamRoles.SupportSpot(ball, OppGoal, 0, Half);
            Assert.Greater(spot.y, ball.y, "Support attackers go ahead of the ball, toward goal.");
            Assert.Less(spot.x, ball.x, "The first support attacker takes the open (left) flank.");

            Vector2 second = Agent_TeamRoles.SupportSpot(ball, OppGoal, 1, Half);
            Assert.Greater(second.x, ball.x, "A second support attacker takes the other flank.");
        }

        [Test]
        public void EverySpot_StaysInsideThePitch()
        {
            var corner = new Vector2(17.5f, 26.5f);
            foreach (Vector2 spot in new[]
            {
                Agent_TeamRoles.DefenderSpot(corner, OwnGoal, 0, 3, Half),
                Agent_TeamRoles.DefenderSpot(corner, OwnGoal, 2, 3, Half),
                Agent_TeamRoles.SupportSpot(corner, OppGoal, 0, Half),
                Agent_TeamRoles.SupportSpot(corner, OppGoal, 3, Half),
                Agent_TeamRoles.GoalieSpot(corner, OwnGoal, 6f, Half),
            })
            {
                Assert.LessOrEqual(Mathf.Abs(spot.x), Half.x, $"{spot} is outside the pitch.");
                Assert.LessOrEqual(Mathf.Abs(spot.y), Half.y, $"{spot} is outside the pitch.");
            }
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
