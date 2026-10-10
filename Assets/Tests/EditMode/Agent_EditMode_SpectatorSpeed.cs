using NUnit.Framework;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Agent_TimeFreeze resolves three inputs into one Time.timeScale: freeze
    /// holders, the hit-stop dip and the spectator's playback speed. This pins how
    /// they compose, because every way of getting it wrong still produces a clock
    /// that runs - just one owned by whichever input wrote last.
    ///
    /// EditMode on purpose: the class is static arithmetic over three fields, and
    /// none of this needs a scene, a frame or a match.
    /// </summary>
    public sealed class Agent_EditMode_SpectatorSpeed
    {
        [SetUp]
        public void SetUp() => Agent_TimeFreeze.ReleaseAll();

        [TearDown]
        public void TearDown() => Agent_TimeFreeze.ReleaseAll();

        [Test]
        public void DefaultsToOne_AndDrivesTheClock()
        {
            Assert.AreEqual(1f, Agent_TimeFreeze.SpectatorSpeed, 0.0001f);
            Assert.AreEqual(1f, Time.timeScale, 0.0001f);
            Assert.IsTrue(Agent_TimeFreeze.IsNormalSpeed);

            Agent_TimeFreeze.SpectatorSpeed = 2f;
            Assert.AreEqual(2f, Time.timeScale, 0.0001f);
            Assert.IsFalse(Agent_TimeFreeze.IsNormalSpeed,
                "2x is not normal speed; a flow test that reads IsNormalSpeed would pass " +
                "against a match left running fast.");
        }

        [Test]
        public void IsClampedToTheSupportedRange()
        {
            Agent_TimeFreeze.SpectatorSpeed = 100f;
            Assert.AreEqual(Agent_TimeFreeze.MAX_SPECTATOR_SPEED, Agent_TimeFreeze.SpectatorSpeed, 0.0001f);

            Agent_TimeFreeze.SpectatorSpeed = 0f;
            Assert.AreEqual(Agent_TimeFreeze.MIN_SPECTATOR_SPEED, Agent_TimeFreeze.SpectatorSpeed, 0.0001f,
                "Zero must not be reachable through the speed control: stopping the clock " +
                "is what a freeze holder is for, and a 0x speed is a pause nothing can name.");

            Agent_TimeFreeze.SpectatorSpeed = float.NaN;
            Assert.AreEqual(Agent_TimeFreeze.MIN_SPECTATOR_SPEED, Agent_TimeFreeze.SpectatorSpeed, 0.0001f,
                "NaN must be ignored, not stored.");
        }

        [Test]
        public void AFreezeAlwaysWins_AndTheSpeedComesBackWhenItLifts()
        {
            Agent_TimeFreeze.SpectatorSpeed = 4f;

            var hold = new Agent_TimeFreeze.Hold("Test.SpectatorSpeed");
            Agent_TimeFreeze.Acquire(hold);
            Assert.AreEqual(0f, Time.timeScale, 0.0001f,
                "4x un-paused a frozen game.");

            // Changing the speed while frozen must not restart the clock either.
            Agent_TimeFreeze.SpectatorSpeed = 2f;
            Assert.AreEqual(0f, Time.timeScale, 0.0001f,
                "Setting the speed during a freeze released the freeze.");

            Agent_TimeFreeze.Release(hold);
            Assert.AreEqual(2f, Time.timeScale, 0.0001f,
                "Releasing the last holder must restore the viewer's speed, not 1x.");
        }

        [Test]
        public void ComposesWithTheHitStop_RatherThanReplacingIt()
        {
            Agent_TimeFreeze.SpectatorSpeed = 2f;
            Agent_TimeFreeze.SlowMotion = 0.5f;
            Assert.AreEqual(1f, Time.timeScale, 0.0001f,
                "A hit-stop at 2x should dip to half of 2x.");

            // The hit-stop ending is the case that used to be a second writer: it
            // sets SlowMotion back to 1 and must land on the viewer's speed.
            Agent_TimeFreeze.SlowMotion = 1f;
            Assert.AreEqual(2f, Time.timeScale, 0.0001f,
                "The end of a hit-stop reset the clock to 1x over the viewer's 2x.");
        }

        [Test]
        public void ReleaseAll_ResetsTheSpeed()
        {
            Agent_TimeFreeze.SpectatorSpeed = 0.25f;
            Agent_TimeFreeze.SlowMotion = 0.5f;
            Agent_TimeFreeze.Acquire(new Agent_TimeFreeze.Hold("Test.ReleaseAll"));

            Agent_TimeFreeze.ReleaseAll();

            Assert.AreEqual(0, Agent_TimeFreeze.HolderCount);
            Assert.AreEqual(1f, Agent_TimeFreeze.SpectatorSpeed, 0.0001f,
                "ReleaseAll is the scene-entry reset. A speed that survives it follows " +
                "the viewer into the next match.");
            Assert.AreEqual(1f, Time.timeScale, 0.0001f);
        }
    }
}
