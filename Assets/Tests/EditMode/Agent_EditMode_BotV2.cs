using System;
using NUnit.Framework;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the two skills bot v2 adds (2026-10-11) and the switch that keeps them away
    /// from the benchmark.
    ///
    /// The switch is the part that protects other people's numbers. Every grade in
    /// docs/training-history.md was taken against bot v1, so v1 has to stay what an
    /// unset POSOCCER_BOT_VERSION means; a default that drifted to 2 would move the
    /// benchmark's opponent with no eval JSON able to show it until someone read the
    /// new botVersion stamp.
    ///
    /// The bank shot is checked by playing it out: aim where TryBankAim says, reflect
    /// off the wall the way Agent_PitchGuard's walls do, and see where the ball crosses
    /// the goal line.
    /// </summary>
    public sealed class Agent_EditMode_BotV2
    {
        static readonly Vector2 Centre = Vector2.zero;
        static readonly Vector2 WallHalf = new(17.89f, 26.89f);
        static readonly Vector2 Goal = new(0f, 27f);
        const float Bounce = 0.85f;

        [Test]
        public void TheBenchmarkBot_IsWhatAnUnsetVariableMeans()
        {
            string before = Environment.GetEnvironmentVariable(Agent_RunConfig.BOT_VERSION);
            try
            {
                Environment.SetEnvironmentVariable(Agent_RunConfig.BOT_VERSION, null);
                Assert.AreEqual(1, Agent_HeuristicBot.BENCHMARK_VERSION);
                Assert.AreEqual(1, Agent_HeuristicBot.VersionFor(Agent_Soccer.Team.Red));

                Environment.SetEnvironmentVariable(Agent_RunConfig.BOT_VERSION, "2");
                Assert.AreEqual(2, Agent_HeuristicBot.VersionFor(Agent_Soccer.Team.Red));
                Assert.AreEqual(1, Agent_HeuristicBot.VersionFor(Agent_Soccer.Team.Blue),
                    "red's variable must not reach blue");

                Environment.SetEnvironmentVariable(Agent_RunConfig.BOT_VERSION, "9");
                Assert.AreEqual(1, Agent_HeuristicBot.VersionFor(Agent_Soccer.Team.Red),
                    "an unknown version falls back to the benchmark bot");
            }
            finally
            {
                Environment.SetEnvironmentVariable(Agent_RunConfig.BOT_VERSION, before);
            }
        }

        [Test]
        public void ABankShot_LandsOnTheGoal()
        {
            Vector2 ball = new(2f, 8f);
            Vector2 foe = ball + (Goal - ball).normalized * 2f;   // standing in the lane

            Assert.IsTrue(Agent_HeuristicBot.TryBankAim(ball, Goal, foe, Centre, WallHalf, Bounce,
                out Vector2 aim));

            // Play it out. The wall keeps the speed along it and returns Bounce of the
            // speed into it.
            Vector2 direction = (aim - ball).normalized;
            float wallX = Mathf.Sign(direction.x) * WallHalf.x;
            float toWall = (wallX - ball.x) / direction.x;
            Vector2 hit = ball + direction * toWall;
            Vector2 after = new(-direction.x * Bounce, direction.y);
            float toGoalLine = (Goal.y - hit.y) / after.y;
            Vector2 arrival = hit + after * toGoalLine;

            Assert.AreEqual(Goal.x, arrival.x, 0.05f, "the ball crosses the goal line at the goal");
            Assert.Less(Mathf.Abs(hit.y), WallHalf.y - Agent_HeuristicBot.BANK_CORNER_MARGIN,
                "the bounce is on a flat wall, clear of the corner arcs");
        }

        [Test]
        public void NoBankShot_WhenTheLaneIsOpen()
        {
            Vector2 ball = new(2f, 8f);
            Vector2 foeBehind = ball + (ball - Goal).normalized * 3f;
            Assert.IsFalse(Agent_HeuristicBot.TryBankAim(ball, Goal, foeBehind, Centre, WallHalf,
                Bounce, out Vector2 aim));
            Assert.AreEqual(Goal, aim, "with no bank on, the aim is left on the goal");
        }

        [Test]
        public void NoBankShot_FromPointBlankOrFromTheOtherEnd()
        {
            Vector2 near = new(0f, 23f);
            Assert.IsFalse(Agent_HeuristicBot.TryBankAim(near, Goal, near + Vector2.up, Centre,
                WallHalf, Bounce, out _), "inside the minimum range the bot shoots");

            Vector2 far = new(0f, -20f);
            Assert.IsFalse(Agent_HeuristicBot.TryBankAim(far, Goal, far + Vector2.up, Centre,
                WallHalf, Bounce, out _), "beyond the maximum range the ball would die");
        }

        [Test]
        public void AStillBall_IsInterceptedWhereItLies()
        {
            Vector2 ball = new(6f, 3f);
            Vector2 point = Agent_HeuristicBot.InterceptPoint(Vector2.zero,
                Agent_HeuristicBot.LEAD_CHASE_SPEED, ball, Vector2.zero, 0.1f, Centre, WallHalf, Bounce);
            Assert.AreEqual(ball.x, point.x, 1e-3f);
            Assert.AreEqual(ball.y, point.y, 1e-3f);
        }

        [Test]
        public void AMovingBall_IsLed()
        {
            // Ball 10 m to the right, crossing upfield at 5 m/s.
            Vector2 ball = new(10f, 0f);
            Vector2 point = Agent_HeuristicBot.InterceptPoint(Vector2.zero,
                Agent_HeuristicBot.LEAD_CHASE_SPEED, ball, new Vector2(0f, 5f), 0.1f, Centre,
                WallHalf, Bounce);

            Assert.AreEqual(10f, point.x, 1e-3f);
            Assert.Greater(point.y, 2f, "the meeting point is ahead of the ball, not on it");
        }

        [Test]
        public void TheIntercept_StaysInsideTheWalls()
        {
            // Ball about to hit the right wall at 10 m/s; the chaser is far away, so the
            // search runs to its horizon through the bounce.
            Vector2 point = Agent_HeuristicBot.InterceptPoint(new Vector2(-15f, -20f),
                Agent_HeuristicBot.LEAD_CHASE_SPEED, new Vector2(17f, 0f), new Vector2(10f, 0f),
                0.1f, Centre, WallHalf, Bounce);

            Assert.LessOrEqual(point.x, WallHalf.x + 1e-3f);
            Assert.Less(point.x, 17f, "after the bounce the ball is travelling back infield");
        }

        [Test]
        public void LeagueSeating_PutsTheBotOnBothColours()
        {
            Assert.IsNull(Agent_EnvController.LeagueSeatFor(0));
            Assert.AreEqual(Agent_Soccer.Team.Red, Agent_EnvController.LeagueSeatFor(1));
            Assert.IsNull(Agent_EnvController.LeagueSeatFor(2));
            Assert.AreEqual(Agent_Soccer.Team.Blue, Agent_EnvController.LeagueSeatFor(3));
            Assert.IsNull(Agent_EnvController.LeagueSeatFor(4));
            Assert.AreEqual(Agent_Soccer.Team.Red, Agent_EnvController.LeagueSeatFor(5));
        }
    }
}
