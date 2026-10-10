using System.Collections;
using NUnit.Framework;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PoSoccer.Tests
{
    /// <summary>
    /// MEASURES the 2026-10-10 physics-layer change rather than asserting it.
    ///
    /// The layer masks were written without a running editor, on a reading of the
    /// Physics2D documentation: a ray sensor casts from the agent's centre, inside
    /// its own collider, and a shape cast reports a collider it starts overlapped
    /// with - so a mask containing the caster's own layer makes every ray return
    /// "hit myself at distance zero". Each sensor's mask now excludes the caster.
    /// This fixture is where that claim meets the engine: it casts the real sensors
    /// against a staged pitch and fails if any ray lands on its own caster, or if a
    /// sensor cannot see the one class of object it exists to see.
    ///
    /// The casts are driven directly (RayPerceptionSensor.Update) after an explicit
    /// Physics2D.SyncTransforms, so nothing here depends on the editor running
    /// play-mode frames or on where the scripted bot happens to wander.
    /// </summary>
    public class Agent_PlayMode_RayLayers
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

        static void AssertPitchLayers(Agent_EnvController env, string label)
        {
            int ballLayer = LayerMask.NameToLayer(Agent_Layers.BALL);
            int pitchLayer = LayerMask.NameToLayer(Agent_Layers.PITCH);
            Assert.GreaterOrEqual(ballLayer, 0, "Layer 'Ball' missing from TagManager.asset");
            Assert.GreaterOrEqual(pitchLayer, 0, "Layer 'Pitch' missing from TagManager.asset");

            Assert.IsNotNull(env.Ball, $"{label}: no ball");
            Assert.AreEqual(ballLayer, env.Ball.gameObject.layer, $"{label}: ball is not on the Ball layer");

            var agents = env.GetComponentsInChildren<Agent_Soccer>();
            Assert.GreaterOrEqual(agents.Length, 2, $"{label}: need both teams present");
            foreach (var agent in agents)
            {
                int expected = LayerMask.NameToLayer(Agent_Layers.TeamLayerName(agent.team));
                Assert.GreaterOrEqual(expected, 0, "Team layer missing from TagManager.asset");
                Assert.AreEqual(expected, agent.gameObject.layer,
                    $"{label}: {agent.name} ({agent.team}) is not on its team layer, so the " +
                    "opposing side's opponent sensor cannot see it");
            }

            int pitchColliders = 0;
            foreach (var collider in env.GetComponentsInChildren<Collider2D>())
            {
                if (collider.transform.IsChildOf(env.Ball.transform)) continue;
                if (collider.GetComponentInParent<Agent_Soccer>() != null) continue;
                pitchColliders++;
                Assert.AreEqual(pitchLayer, collider.gameObject.layer,
                    $"{label}: collider '{collider.name}' is not on the Pitch layer - the wall " +
                    "sensor cannot see it");
            }
            Assert.Greater(pitchColliders, 0, $"{label}: found no wall/goal colliders at all");
        }

        [UnityTest]
        public IEnumerator Layers_AreStamped_OnTheAuthoredPitchAndOnAClone()
        {
            SceneManager.LoadScene("SCN_Training");
            yield return Frames(3);

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            Assert.IsNotNull(env);
            AssertPitchLayers(env, "authored pitch");

            // The training grid and the gallery both clone the pitch root.
            var clone = Object.Instantiate(env.gameObject,
                env.transform.position + new Vector3(200f, 0f, 0f), Quaternion.identity);
            yield return Frames(2);
            AssertPitchLayers(clone.GetComponent<Agent_EnvController>(), "cloned pitch");
            Object.Destroy(clone);
        }

        [UnityTest]
        public IEnumerator EverySensor_CastsAgainstOneLayer_ThatIsNotItsOwn()
        {
            SceneManager.LoadScene("SCN_Training");
            yield return Frames(3);

            foreach (var agent in Object.FindObjectsByType<Agent_Soccer>())
            {
                foreach (var sensor in agent.GetComponents<RayPerceptionSensorComponent2D>())
                {
                    int mask = sensor.RayLayerMask.value;
                    Assert.AreNotEqual(0, mask, $"{agent.name}/{sensor.SensorName} casts against nothing");
                    Assert.AreEqual(0, mask & (mask - 1),
                        $"{agent.name}/{sensor.SensorName} casts against more than one layer (mask {mask})");
                    Assert.AreEqual(0, mask & (1 << agent.gameObject.layer),
                        $"{agent.name}/{sensor.SensorName} can hit its own caster's layer");
                }
            }
        }

        static RayPerceptionOutput.RayOutput[] Cast(Agent_Soccer agent, string sensorName)
        {
            foreach (var component in agent.GetComponents<RayPerceptionSensorComponent2D>())
            {
                if (component.SensorName != sensorName) continue;
                Assert.IsNotNull(component.RaySensor,
                    $"{agent.name}/{sensorName} has no sensor yet - the agent did not initialise");
                component.RaySensor.Update();
                return component.RaySensor.RayPerceptionOutput.RayOutputs;
            }
            Assert.Fail($"{agent.name} has no {sensorName}");
            return null;
        }

        static bool AnyTaggedHit(RayPerceptionOutput.RayOutput[] rays, GameObject target)
        {
            for (int i = 0; i < rays.Length; i++)
            {
                if (rays[i].HitTaggedObject && (target == null || rays[i].HitGameObject == target))
                {
                    return true;
                }
            }
            return false;
        }

        static void AssertNoSelfHit(RayPerceptionOutput.RayOutput[] rays, Agent_Soccer agent, string sensorName)
        {
            for (int i = 0; i < rays.Length; i++)
            {
                if (!rays[i].HasHit || rays[i].HitGameObject == null) continue;
                Assert.IsFalse(rays[i].HitGameObject.transform.IsChildOf(agent.transform),
                    $"{agent.name}/{sensorName} ray {i} hit its own caster at fraction " +
                    $"{rays[i].HitFraction:0.000}. A ray that starts inside its own collider " +
                    "and can hit it reports nothing else.");
            }
        }

        /// <summary>
        /// Stage: blue faces +Y with the ball 0.6 m ahead and red 2 m ahead (directly
        /// behind the ball), then faces the east wall from 3 m. The centre ray of every
        /// sensor points along the eye axis, so each target is on a ray by construction.
        /// </summary>
        [UnityTest]
        public IEnumerator Sensors_SeeTheirOwnClass_AndNeverTheirCaster()
        {
            SceneManager.LoadScene("SCN_Training");
            yield return Frames(3);

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            Assert.IsNotNull(env);
            Agent_Soccer blue = null, red = null;
            foreach (var agent in env.GetComponentsInChildren<Agent_Soccer>())
            {
                if (agent.team == Agent_Soccer.Team.Blue && blue == null) blue = agent;
                if (agent.team == Agent_Soccer.Team.Red && red == null) red = agent;
            }
            Assert.IsNotNull(blue);
            Assert.IsNotNull(red);
            Assert.Greater(blue.GetComponents<RayPerceptionSensorComponent2D>().Length, 0,
                "Blue carries no ray sensors in a plain editor run");

            Vector3 origin = env.transform.position;
            blue.transform.SetPositionAndRotation(origin, Quaternion.identity);
            env.Ball.transform.position = origin + new Vector3(0f, 0.6f, 0f);
            red.transform.SetPositionAndRotation(origin + new Vector3(0f, 2f, 0f), Quaternion.identity);
            Physics2D.SyncTransforms();

            var ballRays = Cast(blue, "Sensor_Ball");
            AssertNoSelfHit(ballRays, blue, "Sensor_Ball");
            Assert.IsTrue(AnyTaggedHit(ballRays, env.Ball.gameObject),
                "Ball sensor did not see a ball 0.6 m dead ahead");

            var opponentRays = Cast(blue, "Sensor_Opponents");
            AssertNoSelfHit(opponentRays, blue, "Sensor_Opponents");
            Assert.IsTrue(AnyTaggedHit(opponentRays, red.gameObject),
                "Opponent sensor did not see an opponent 2 m dead ahead (with the ball in " +
                "between - the ball is on another layer and must not occlude it)");

            // Face the east wall from 3 m, at mid-height so no corner arc is involved.
            blue.transform.SetPositionAndRotation(
                origin + new Vector3(env.PitchHalfExtents.x - 3f, 0f, 0f),
                Quaternion.Euler(0f, 0f, -90f));
            Physics2D.SyncTransforms();

            var wallRays = Cast(blue, "Sensor_Walls");
            AssertNoSelfHit(wallRays, blue, "Sensor_Walls");
            Assert.IsTrue(AnyTaggedHit(wallRays, null),
                "Wall sensor did not see a wall 3 m dead ahead");
            int pitchLayer = LayerMask.NameToLayer(Agent_Layers.PITCH);
            for (int i = 0; i < wallRays.Length; i++)
            {
                if (!wallRays[i].HasHit) continue;
                Assert.AreEqual(pitchLayer, wallRays[i].HitGameObject.layer,
                    $"Wall ray {i} stopped on '{wallRays[i].HitGameObject.name}', which is not pitch geometry");
            }
        }
    }
}
