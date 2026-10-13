using NUnit.Framework;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the ORDERING the expected-goals model has to respect, not its numbers.
    ///
    /// The weights are hand-set and are meant to be replaced by a fit (see the class
    /// docstring on Agent_ExpectedGoals), so a test that asserted 0.52 from five metres
    /// would break the day somebody does the right thing. What must survive any refit is
    /// the shape: nearer is better, a wider view of the goal is better, a defender in the
    /// way is worse, and the answer is a probability.
    /// </summary>
    public sealed class Agent_EditMode_ExpectedGoals
    {
        static readonly Vector2 Goal = new(0f, 27f);
        static readonly Vector2 GoalLine = Vector2.right;
        const float Width = 6f;
        static readonly Vector2[] Nobody = new Vector2[1];

        static float Estimate(Vector2 ball, Vector2[] defenders, int count) =>
            Agent_ExpectedGoals.Estimate(ball, Goal, GoalLine, Width, defenders, count,
                -2.2f, 2.4f, 0.06f, 1.1f, out _, out _, out _);

        [Test]
        public void TheAnswer_IsAProbability()
        {
            float pointBlank = Estimate(new Vector2(0f, 26.5f), Nobody, 0);
            float ownHalf = Estimate(new Vector2(0f, -25f), Nobody, 0);
            Assert.That(pointBlank, Is.GreaterThan(0f).And.LessThan(1f));
            Assert.That(ownHalf, Is.GreaterThan(0f).And.LessThan(1f));
        }

        [Test]
        public void NearerTheGoal_IsWorthMore()
        {
            Assert.Greater(Estimate(new Vector2(0f, 22f), Nobody, 0),
                Estimate(new Vector2(0f, 10f), Nobody, 0));
        }

        [Test]
        public void ATightAngle_IsWorthLess_AtTheSameDistance()
        {
            // Both 8 m from the centre of the goal: one straight out, one along the line.
            float central = Estimate(new Vector2(0f, 19f), Nobody, 0);
            float wide = Estimate(new Vector2(7.9f, 25.75f), Nobody, 0);
            Assert.Greater(central, wide);
        }

        [Test]
        public void ADefenderInTheTriangle_CostsTheShot()
        {
            Vector2 ball = new(0f, 17f);
            var inTheWay = new[] { new Vector2(0f, 22f) };
            var besideIt = new[] { new Vector2(12f, 22f) };

            float open = Estimate(ball, Nobody, 0);
            Assert.Less(Estimate(ball, inTheWay, 1), open);
            Assert.AreEqual(open, Estimate(ball, besideIt, 1), 1e-5f,
                "a defender outside ball-post-post is not blocking anything");
        }

        [Test]
        public void TheFeatures_AreReportedForTheCalibrationLog()
        {
            Vector2 ball = new(0f, 17f);
            Agent_ExpectedGoals.Estimate(ball, Goal, GoalLine, Width,
                new[] { new Vector2(0f, 22f) }, 1, -2.2f, 2.4f, 0.06f, 1.1f,
                out float angle, out float distance, out int blockers);

            Assert.AreEqual(10f, distance, 1e-4f);
            Assert.AreEqual(2f * Mathf.Atan(3f / 10f), angle, 1e-3f);
            Assert.AreEqual(1, blockers);
        }
    }
}
