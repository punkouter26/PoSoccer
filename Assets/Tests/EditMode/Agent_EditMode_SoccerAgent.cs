using NUnit.Framework;
using UnityEngine;

namespace PoSoccer.Tests
{
    public class Agent_EditMode_SoccerAgent
    {
        GameObject _go;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
        }

        Agent_Stamina NewStamina()
        {
            _go = new GameObject("stamina");
            var s = _go.AddComponent<Agent_Stamina>();
            s.ResetForEpisode();
            return s;
        }

        [Test]
        public void Stamina_DrainsAt60PerSecondWhileBoosting()
        {
            var s = NewStamina();
            s.Tick(boosting: true, deltaTime: 1f);
            Assert.AreEqual(s.EffectiveMax - 60f, s.Current, 0.5f);
        }

        [Test]
        public void Stamina_RechargesAt25PerSecondWhenIdle()
        {
            var s = NewStamina();
            s.Tick(true, 1f);              // drain 60
            float drained = s.Current;
            s.Tick(false, 1f);             // recharge 25
            Assert.AreEqual(drained + 25f, s.Current, 0.5f);
        }

        [Test]
        public void Stamina_WearNeverDropsEffectiveMaxBelowFloor()
        {
            var s = NewStamina();
            for (int i = 0; i < 5000; i++) s.Tick(true, 1f); // extreme sustained exertion
            Assert.GreaterOrEqual(s.EffectiveMax, s.maxStamina * s.wearFloor - 0.01f);
            Assert.Less(s.EffectiveMax, s.maxStamina); // wear did accumulate
        }

        [Test]
        public void RewardSettings_DefaultsMatchPrd()
        {
            var r = ScriptableObject.CreateInstance<Reward_Settings>();
            Assert.AreEqual(0.7f, r.goalScorer);
            Assert.AreEqual(0.3f, r.assist);
            Assert.AreEqual(0.2f, r.teamBaselineVictory);
            Assert.AreEqual(-1.0f, r.goalConceded);
            Assert.AreEqual(-0.0001f, r.stepPenalty, 1e-6f);
            Assert.AreEqual(0.0004f, r.ballProximityScale, 1e-6f);
            Assert.AreEqual(0.0002f, r.facingAlignmentScale, 1e-6f);
            Assert.AreEqual(0.05f, r.ballContact);
            Assert.AreEqual(-0.10f, r.stalemateTimeout, 1e-6f);
            Assert.AreEqual(5000, r.maxEnvironmentSteps);
            Object.DestroyImmediate(r);
        }

        [Test]
        public void HeuristicBot_TurnsTowardBallOnTheLeft()
        {
            _go = new GameObject("bot");
            var selfBody = _go.AddComponent<Rigidbody2D>();
            var bot = _go.AddComponent<Agent_HeuristicBot>();

            var ballGo = new GameObject("ball");
            ballGo.transform.position = new Vector3(-5f, 0f, 0f);
            var ballBody = ballGo.AddComponent<Rigidbody2D>();

            // Facing +Y, ball to the left (-X) => positive (CCW) turn expected.
            Vector3 actions = bot.ComputeActions(selfBody, ballBody, null);
            Assert.Greater(actions.y, 0f, "turn should be CCW toward a ball on the left");

            Object.DestroyImmediate(ballGo);
        }

        [Test]
        public void Opponent_MappingIsSymmetric()
        {
            Assert.AreEqual(Agent_Soccer.Team.Red, Agent_Soccer.Opponent(Agent_Soccer.Team.Blue));
            Assert.AreEqual(Agent_Soccer.Team.Blue, Agent_Soccer.Opponent(Agent_Soccer.Team.Red));
        }
    }
}
