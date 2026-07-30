using System.Collections;
using System.Linq;
using NUnit.Framework;
using Unity.MLAgents.Actuators;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PoSoccer.Tests
{
    public class PlayMode_GoalFlow
    {
        Agent_EnvController _env;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            SceneManager.LoadScene("SCN_Training");
            yield return null;              // scene Awake/OnEnable
            yield return null;              // Start (env self-discovers agents)

            _env = Object.FindFirstObjectByType<Agent_EnvController>();
            Assert.IsNotNull(_env, "Pitch env controller not found in SCN_Training");
            Assert.IsTrue(_env.agents.Count >= 2, "env did not discover its agents");
        }

        [UnityTest]
        public IEnumerator BallIntoBlueNet_RedWins_RewardsApplied()
        {
            var red = _env.agents.First(a => a.team == Agent_Soccer.Team.Red);
            var blue = _env.agents.First(a => a.team == Agent_Soccer.Team.Blue);

            Agent_Soccer.Team? winner = null;
            float redAtEnd = 0f, blueAtEnd = 0f;
            bool ended = false;
            _env.EpisodeEnded += w =>
            {
                winner = w;
                redAtEnd = red.GetCumulativeReward();
                blueAtEnd = blue.GetCumulativeReward();
                ended = true;
            };

            // Without a registered last touch the scorer path pays only +0.2.
            _env.NotifyBallTouch(red);

            // Shoot the ball into the blue net from an open lane (x=2.5 avoids the
            // blue agent at x~0; goal mouth spans +/-3 at the default 6m width).
            var ball = _env.Ball;
            ball.position = new Vector2(2.5f, 0f);
            ball.linearVelocity = new Vector2(0f, -30f);
            ball.WakeUp();

            for (int i = 0; i < 200 && !ended; i++)
                yield return new WaitForFixedUpdate();

            Assert.IsTrue(ended, "EpisodeEnded never fired after ball entered the blue net");
            Assert.AreEqual(Agent_Soccer.Team.Red, winner, "red (last toucher) should win");
            Assert.GreaterOrEqual(redAtEnd, 0.6f, "scorer should carry ~+0.7 at episode end");
            Assert.LessOrEqual(blueAtEnd, -0.9f, "conceding side should carry ~-1.0");

            // Pitch reset: ball back near the center spot (kickoff jitter <= 1).
            yield return new WaitForFixedUpdate();
            Assert.Less(Vector2.Distance(ball.position, Vector2.zero), 2.5f,
                "ball should respawn near center after the goal");
        }

        [UnityTest]
        public IEnumerator HeuristicBots_ActuallyMove()
        {
            var red = _env.agents.First(a => a.team == Agent_Soccer.Team.Red);
            Vector2 start = red.Body.position;

            for (int i = 0; i < 300; i++)          // 3 simulated seconds @ 100 Hz
                yield return new WaitForFixedUpdate();

            Assert.Greater(Vector2.Distance(red.Body.position, start), 0.5f,
                "the bot-driven red agent should displace within 3s of kickoff " +
                "(decision pipeline or actuation is broken if it does not)");
        }

        [UnityTest]
        public IEnumerator Boost_WithZeroStamina_DoesNotActivate()
        {
            var blue = _env.agents.First(a => a.team == Agent_Soccer.Team.Blue);
            yield return new WaitForFixedUpdate();   // let Agent initialize fully

            blue.Stamina.Tick(boosting: true, deltaTime: 10f);   // force-drain to 0
            Assert.IsFalse(blue.Stamina.HasStamina);

            blue.OnActionReceived(new ActionBuffers(
                new[] { 0f, 0f, 1f }, System.Array.Empty<int>()));

            Assert.IsFalse(blue.IsBoosting,
                "boost must not activate at zero stamina (force stays 1x, not 2.2x)");
        }
    }
}
