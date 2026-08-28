using System.Collections;
using NUnit.Framework;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Guards the sensor lifecycle on CLONED pitches, which is where it broke.
    ///
    /// Sensor_Vision used to Destroy() any pre-existing ray components and
    /// AddComponent() replacements. Destroy is deferred to end of frame while
    /// AddComponent is immediate, and Agent.OnEnable snapshots the sensor list
    /// inside that same frame - so an agent could initialise with eight ray
    /// sensors instead of four.
    ///
    /// It only showed on clones: Sensor_Vision runs at -100, so the authored
    /// pitch's agents already carry four components when Agent_TrainingGrid
    /// (order 0) clones the pitch, and every clone inherits them and then adds
    /// four more. The authored pitch had 4, the clones 8, and the trainer
    /// rejected the environment outright with a shape mismatch.
    ///
    /// A test that only ever looked at the authored pitch would pass. This one
    /// clones a pitch the way the training grid does.
    /// </summary>
    public class Agent_PlayMode_SensorProbe
    {
        static IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        [UnityTest]
        public IEnumerator EveryAgent_HasExactlyOneSensorPerBatteryEntry()
        {
            SceneManager.LoadScene("SCN_Training");
            yield return Frames(3);

            var agents = Object.FindObjectsByType<Agent_Soccer>(FindObjectsSortMode.None);
            Assert.Greater(agents.Length, 0, "No agents in SCN_Training");

            foreach (var agent in agents)
            {
                var rays = agent.GetComponents<RayPerceptionSensorComponent2D>();
                Assert.AreEqual(Sensor_Vision.Battery.Length, rays.Length,
                    $"{agent.name} has {rays.Length} ray sensors, expected " +
                    $"{Sensor_Vision.Battery.Length}");
            }
        }

        /// <summary>
        /// The case that actually failed: a pitch cloned after its agents have
        /// already built their sensors, exactly as Agent_TrainingGrid does.
        /// </summary>
        [UnityTest]
        public IEnumerator ClonedPitch_DoesNotDoubleUpSensors()
        {
            SceneManager.LoadScene("SCN_Training");
            yield return Frames(3);

            var source = Object.FindAnyObjectByType<Agent_EnvController>();
            Assert.IsNotNull(source, "No pitch to clone");

            var clone = Object.Instantiate(source.gameObject,
                source.transform.position + new Vector3(200f, 0f, 0f), Quaternion.identity);
            yield return Frames(2);

            var cloned = clone.GetComponentsInChildren<Agent_Soccer>();
            Assert.Greater(cloned.Length, 0, "Clone has no agents");

            foreach (var agent in cloned)
            {
                var rays = agent.GetComponents<RayPerceptionSensorComponent2D>();
                Assert.AreEqual(Sensor_Vision.Battery.Length, rays.Length,
                    $"Cloned {agent.name} has {rays.Length} ray sensors. Deferred Destroy " +
                    "left the inherited components alive alongside the new ones - this is " +
                    "the bug that made the trainer reject the environment.");

                // Names must be unique too: duplicates pass silently in a release
                // build (the uniqueness assert is DEBUG-only) and simply corrupt
                // the observation layout.
                var seen = new System.Collections.Generic.HashSet<string>();
                foreach (var ray in rays)
                    Assert.IsTrue(seen.Add(ray.SensorName),
                        $"Duplicate sensor name '{ray.SensorName}' on cloned {agent.name}");
            }

            Object.Destroy(clone);
        }
    }
}
