using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PoSoccer.Tests
{
    /// <summary>
    /// The two match overlays added 2026-10-11: expected goals and skid marks.
    ///
    /// Same two questions Agent_PlayMode_Broadcast asks of its components, for the same
    /// reasons. They must exist and be alive in a match, and they must not exist in
    /// SCN_Training, where a per-frame overlay is cost on the throughput that decides
    /// how long a run takes.
    ///
    /// The third test is the one that shows the xG path is wired end to end: a ball
    /// struck at the goal by a real player has to come out the other side as one counted
    /// shot with a probability attached. Everything upstream of the model - the touch
    /// event, the settle delay, the heading test - can fail silently and leave a
    /// component that is enabled, error-free and reports zero shots forever.
    ///
    /// Drives SCN_Exhibition directly, which normal play must never do. Fine here:
    /// nothing under test depends on the lineup.
    /// </summary>
    public class Agent_PlayMode_MatchOverlays
    {
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Agent_TimeFreeze.ReleaseAll();
            Agent_MatchSetup.Clear();
            yield return null;
        }

        static IEnumerator LoadExhibition()
        {
            SceneManager.LoadScene("SCN_Exhibition");
            yield return null;   // Awake / OnEnable
            yield return null;   // Start
            yield return null;   // components added in Awake reach their own Start
        }

        [UnityTest]
        public IEnumerator TrainingScene_HasNeitherOverlay()
        {
            SceneManager.LoadScene("SCN_Training");
            yield return null;
            yield return null;
            yield return null;

            Assert.IsNull(Object.FindAnyObjectByType<Agent_ExpectedGoals>(),
                "Agent_ExpectedGoals must never be installed in SCN_Training.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_Skids>(),
                "Agent_Skids must never be installed in SCN_Training.");
            Assert.IsNull(GameObject.Find("SkidMarks"), "An overlay mesh leaked into the training scene.");
            Assert.IsNull(GameObject.Find("ThreatField"), "An overlay quad leaked into the training scene.");
        }

        [UnityTest]
        public IEnumerator ExhibitionScene_InstallsBothOverlays()
        {
            yield return LoadExhibition();

            var expectedGoals = Object.FindAnyObjectByType<Agent_ExpectedGoals>();
            var skids = Object.FindAnyObjectByType<Agent_Skids>();
            Assert.IsNotNull(expectedGoals, "Agent_ExpectedGoals was not installed");
            Assert.IsNotNull(skids, "Agent_Skids was not installed");
            Assert.IsTrue(expectedGoals.enabled, "Agent_ExpectedGoals disabled itself in a match");
            Assert.IsTrue(skids.enabled, "Agent_Skids disabled itself in a match");

            Assert.IsFalse(Agent_ExpectedGoals.FieldVisible, "the threat field starts hidden");
            var field = GameObject.Find("ThreatField");
            Assert.IsNotNull(field, "the threat field quad was never built");
            Assert.IsFalse(field.GetComponent<SpriteRenderer>().enabled);

            Agent_ExpectedGoals.FieldVisible = true;
            yield return null;
            yield return null;
            Assert.IsTrue(field.GetComponent<SpriteRenderer>().enabled,
                "showing the field must enable its renderer");
            Agent_ExpectedGoals.FieldVisible = false;

            var marks = GameObject.Find("SkidMarks");
            Assert.IsNotNull(marks, "the skid-mark mesh was never built");
            Assert.AreEqual(1, marks.GetComponent<MeshRenderer>().sharedMaterials.Length,
                "the skid marks are one draw call");
        }

        [UnityTest]
        public IEnumerator AStrikeAtGoal_IsCountedAsAShotWithAProbability()
        {
            yield return LoadExhibition();

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            var expectedGoals = Object.FindAnyObjectByType<Agent_ExpectedGoals>();
            Assert.IsNotNull(env);
            Assert.IsNotNull(expectedGoals);

            Agent_Soccer striker = null;
            for (int agentIndex = 0; agentIndex < env.agents.Count; agentIndex++)
            {
                if (env.agents[agentIndex] != null && env.agents[agentIndex].team == Agent_Soccer.Team.Blue)
                {
                    striker = env.agents[agentIndex];
                    break;
                }
            }
            Assert.IsNotNull(striker, "no blue player on the exhibition pitch");

            // The kickoff countdown holds the clock; shots are read in FixedUpdate.
            Agent_TimeFreeze.ReleaseAll();
            yield return new WaitForFixedUpdate();

            int before = expectedGoals.ShotsFor(Agent_Soccer.Team.Blue);

            // Blue attacks red's goal. Put the ball 6 m out, send it at the goal and
            // report the touch the way a collision would.
            Vector2 goal = env.GetGoalPosition(Agent_Soccer.Team.Red);
            Vector2 toward = (goal - env.GetGoalPosition(Agent_Soccer.Team.Blue)).normalized;
            env.Ball.position = goal - toward * 6f;
            env.Ball.linearVelocity = toward * 12f;
            env.NotifyBallTouch(striker);

            for (int tick = 0; tick < 12; tick++) yield return new WaitForFixedUpdate();

            Assert.AreEqual(before + 1, expectedGoals.ShotsFor(Agent_Soccer.Team.Blue),
                "a ball struck straight at the goal was not counted as a shot");
            float total = expectedGoals.TotalFor(Agent_Soccer.Team.Blue);
            Assert.That(total, Is.GreaterThan(0f).And.LessThan(1f),
                "one shot carries one probability");
        }
    }
}
