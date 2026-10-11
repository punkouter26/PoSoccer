using NUnit.Framework;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the passing rules added 2026-10-11: when the scripted bot passes instead of
    /// carrying on, what a completed pass is worth, and the one role change that makes a
    /// forward pass possible for a pair.
    ///
    /// The value rule is the one that matters most. The pass reward pays for ground
    /// gained toward goal, and a reward that pays per pass is farmed the moment two
    /// players discover they can roll the ball back and forth. PassProgress is what
    /// stops that, so its "back and forth is worth nothing" case is asserted directly.
    /// </summary>
    public sealed class Agent_EditMode_Passing
    {
        // Ball at the centre spot, attacking the goal at y = +27.
        static readonly Vector2 Ball = Vector2.zero;
        static readonly Vector2 Goal = new(0f, 27f);

        [Test]
        public void APair_SendsItsSparePlayerForward_OnlyWhenItHasTheBall()
        {
            Assert.AreEqual(1, Agent_TeamRoles.DefenderCount(2, false), "out of possession: attacker + defender");
            Assert.AreEqual(0, Agent_TeamRoles.DefenderCount(2, true), "in possession: the spare player supports");
        }

        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        public void LargerSquads_KeepTheirShape_WithOrWithoutTheBall(int size)
        {
            Assert.AreEqual(Agent_TeamRoles.DefenderCount(size), Agent_TeamRoles.DefenderCount(size, true));
            Assert.AreEqual(Agent_TeamRoles.DefenderCount(size), Agent_TeamRoles.DefenderCount(size, false));
        }

        [Test]
        public void AForwardPass_IsWorthTheGroundItGains()
        {
            Assert.AreEqual(6f, Agent_TeamRoles.PassProgress(20f, 14f, 20f), 1e-4f);
        }

        [Test]
        public void ASquareOrBackwardPass_IsWorthNothing()
        {
            Assert.AreEqual(0f, Agent_TeamRoles.PassProgress(20f, 20f, 20f));
            Assert.AreEqual(0f, Agent_TeamRoles.PassProgress(14f, 20f, 14f));
        }

        [Test]
        public void PassingBackAndForth_PaysOnlyOnce()
        {
            // Forward 20 -> 14 (frontier becomes 14), back 14 -> 20, forward again 20 -> 14.
            float frontier = 20f;
            float first = Agent_TeamRoles.PassProgress(20f, 14f, frontier);
            frontier = 14f;
            float back = Agent_TeamRoles.PassProgress(14f, 20f, frontier);
            float again = Agent_TeamRoles.PassProgress(20f, 14f, frontier);
            Assert.AreEqual(6f, first, 1e-4f);
            Assert.AreEqual(0f, back);
            Assert.AreEqual(0f, again, "the same stretch of pitch must not pay twice");
        }

        [Test]
        public void ADribbleThenAShortPass_PaysOnlyForThePass()
        {
            // Possession began 20 m out; the passer carried it to 12 m, then passed to 11 m.
            Assert.AreEqual(1f, Agent_TeamRoles.PassProgress(12f, 11f, 20f), 1e-4f);
        }

        [Test]
        public void Bot_Passes_WhenTheLaneIsBlockedAndTheTeammateIsOpenAndAhead()
        {
            var mate = new Vector2(6f, 8f);
            var foe = new Vector2(0f, 3f);   // straight between ball and goal
            Assert.IsTrue(Agent_HeuristicBot.ShouldPass(Ball, Goal, mate, Vector2.zero, foe, out var target));
            Assert.AreEqual(mate, target);
        }

        [Test]
        public void Bot_LeadsAMovingTeammate()
        {
            var mate = new Vector2(6f, 8f);
            Agent_HeuristicBot.ShouldPass(Ball, Goal, mate, new Vector2(0f, 5f), new Vector2(0f, 3f), out var target);
            Assert.AreEqual(new Vector2(6f, 10f), target);
        }

        [Test]
        public void Bot_KeepsTheBall_WhenTheLaneToGoalIsOpen()
        {
            var foe = new Vector2(-8f, -2f);   // behind and to the side
            Assert.IsFalse(Agent_HeuristicBot.ShouldPass(Ball, Goal, new Vector2(6f, 8f), Vector2.zero, foe, out _));
        }

        [Test]
        public void Bot_DoesNotPassBackwards_OrIntoTheSameDefender()
        {
            var blocker = new Vector2(0f, 3f);
            Assert.IsFalse(Agent_HeuristicBot.ShouldPass(Ball, Goal, new Vector2(4f, -6f), Vector2.zero, blocker, out _),
                "a teammate behind the ball is not better placed");
            Assert.IsFalse(Agent_HeuristicBot.ShouldPass(Ball, Goal, new Vector2(0f, 9f), Vector2.zero, blocker, out _),
                "the defender blocking the goal also blocks this pass");
        }

        [Test]
        public void Bot_ShootsInsteadOfPassing_NearGoal()
        {
            var ball = new Vector2(0f, 22f);   // 5 m out
            Assert.IsFalse(Agent_HeuristicBot.ShouldPass(ball, Goal, new Vector2(5f, 25f), Vector2.zero,
                new Vector2(0f, 24f), out _));
        }

        [Test]
        public void Bot_IgnoresATeammateTooCloseOrTooFar()
        {
            var foe = new Vector2(0f, 3f);
            Assert.IsFalse(Agent_HeuristicBot.ShouldPass(Ball, Goal, new Vector2(1f, 3f), Vector2.zero, foe, out _));
            Assert.IsFalse(Agent_HeuristicBot.ShouldPass(Ball, Goal, new Vector2(10f, 14f), Vector2.zero, foe, out _));
        }
    }
}
