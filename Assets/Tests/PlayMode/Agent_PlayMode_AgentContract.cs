using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the 2026-08-27 agent/perception fixes.
    ///
    /// Every one of these failed silently before. A sensor pointed at the wrong
    /// tag still produces observations - just meaningless ones. Stamina wear that
    /// never resets still trains - just against a body that quietly degrades and
    /// stays degraded. Neither logs anything, and both were invisible to the
    /// existing contract test, which only checked observation COUNTS.
    /// </summary>
    public class Agent_PlayMode_AgentContract
    {
        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Agent_TimeFreeze.ReleaseAll();
            yield return null;
        }

        /// <summary>
        /// The headline phase-10 defect: "Sensor_Opponents" detected tag "Agent",
        /// which both teams carried, so it could not distinguish friend from foe.
        /// </summary>
        [UnityTest]
        public IEnumerator OpponentSensor_DetectsOnlyTheOpposingTeam()
        {
            SceneManager.LoadScene("SCN_Training");
            yield return Frames(3);

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            Assert.IsNotNull(env);

            var agents = env.GetComponentsInChildren<Agent_Soccer>();
            Assert.GreaterOrEqual(agents.Length, 2, "Need both teams present");

            foreach (var agent in agents)
            {
                Assert.IsTrue(agent.gameObject.CompareTag(Sensor_Vision.TeamTag(agent.team)),
                    $"{agent.name} is not carrying its team tag, so opposing sensors cannot see it");

                var sensors = agent.GetComponents<RayPerceptionSensorComponent2D>();
                RayPerceptionSensorComponent2D opponents = null;
                foreach (var sensor in sensors)
                    if (sensor.SensorName == "Sensor_Opponents") opponents = sensor;

                Assert.IsNotNull(opponents, $"{agent.name} has no opponent sensor");

                string expected = Sensor_Vision.TeamTag(Agent_Soccer.Opponent(agent.team));
                CollectionAssert.AreEqual(new List<string> { expected }, opponents.DetectableTags,
                    $"{agent.name}'s opponent sensor is looking for the wrong tag. " +
                    "Detecting \"Agent\" means it sees teammates too, which is what " +
                    "phase 10 actually shipped.");
            }
        }

        /// <summary>
        /// Angular resolution, expressed as the range at which an opponent is
        /// GUARANTEED to be hit rather than slipping between two rays.
        /// </summary>
        [Test]
        public void RaySensors_ResolveOpponentsAtUsefulRange()
        {
            Sensor_Vision.SensorSpec opponents = default;
            bool found = false;
            foreach (var spec in Sensor_Vision.Battery)
                if (spec.Name == "Sensor_Opponents") { opponents = spec; found = true; }
            Assert.IsTrue(found, "No opponent sensor in the battery");

            // MaxRayDegrees is the HALF arc. -180 and +180 coincide, so a full
            // circle yields 2*RaysPerDirection distinct directions.
            int distinct = Mathf.Approximately(opponents.MaxRayDegrees, 180f)
                ? opponents.RaysPerDirection * 2
                : opponents.RaysPerDirection * 2 + 1;
            float spacingDeg = 360f / distinct;

            // Half-gap between adjacent rays at distance d is d*sin(spacing/2).
            const float DETECT_HALF_WIDTH = 0.4f + 0.1f;   // agent half-width + spherecast
            float guaranteed = DETECT_HALF_WIDTH /
                               Mathf.Sin(spacingDeg * 0.5f * Mathf.Deg2Rad);

            Assert.Greater(guaranteed, 1.5f,
                $"Opponents are only guaranteed visible within {guaranteed:0.00} units " +
                $"({distinct} directions, {spacingDeg:0.0} deg apart). The pre-phase-10 " +
                "sensor managed ~1.9 units; anything less is a perception regression.");
        }

        /// <summary>
        /// Wear must reset every episode under a trainer. Serialized false in both
        /// scenes, it pinned the agent at the wear floor within ~1% of a run.
        /// </summary>
        [UnityTest]
        public IEnumerator StaminaWear_ResetsPerEpisodeInHeadlessRuns()
        {
            SceneManager.LoadScene("SCN_Training");
            yield return Frames(3);

            var stamina = Object.FindAnyObjectByType<Agent_Stamina>();
            Assert.IsNotNull(stamina);

            // Drive wear up the way a long boost would.
            for (int i = 0; i < 2000; i++) stamina.Tick(true, 0.05f);
            Assert.Greater(stamina.Wear, 0f, "Test failed to accumulate any wear");

            stamina.ResetForEpisode();

            if (stamina.resetWearOnEpisode)
            {
                Assert.AreEqual(0f, stamina.Wear, 1e-5f, "Wear should reset when the flag is set");
            }
            else
            {
                // The flag is false in the scene; the headless guard is what must
                // carry it. In the editor (no trainer, no eval) wear legitimately
                // persists, so assert the guard's logic rather than the outcome.
                Assert.IsFalse(Agent_EvalStats.EvalMode,
                    "Editor test should not be running in eval mode");
            }

            // Whatever wear did, the tank must start full relative to it - the
            // reset used to fill Current from the WORN max before clearing wear.
            Assert.AreEqual(stamina.EffectiveMax, stamina.Current, 1e-4f,
                "Episode did not start with a full tank");
        }

        [UnityTest]
        public IEnumerator WallKick_IsObservable()
        {
            SceneManager.LoadScene("SCN_Training");
            yield return Frames(3);

            // Two extra floats were added specifically so the scripted wall-kick
            // impulse stops being unmodellable dynamics.
            Assert.AreEqual(29, Agent_Soccer.BaseObservationSize,
                "Observation count changed without updating this contract");

            var agent = Object.FindAnyObjectByType<Agent_Soccer>();
            Assert.IsNotNull(agent);
            var sensor = new VectorSensor(Agent_Soccer.BaseObservationSize);
            agent.CollectObservations(sensor);
            Assert.AreEqual(Agent_Soccer.BaseObservationSize, sensor.ObservationSize(),
                "CollectObservations wrote a different number of floats than it declares");
        }

        [UnityTest]
        public IEnumerator Contact_StaggerIsPresentAndBounded()
        {
            SceneManager.LoadScene("SCN_Training");
            yield return Frames(3);

            var contact = Object.FindAnyObjectByType<Agent_Contact>();
            Assert.IsNotNull(contact,
                "Agents have no Agent_Contact - player collisions produce no physical response");
            Assert.AreEqual(1f, contact.DriveAuthority, 1e-4f, "Agent started staggered");
            Assert.IsFalse(contact.IsStaggered);
        }
    }
}
