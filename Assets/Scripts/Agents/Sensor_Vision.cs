using System.Collections.Generic;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Specialized ray perception (v4, 2026-08-11).
    ///
    /// One RayPerceptionSensor2D per object class so the policy never has to
    /// disentangle "what" from "where". Each sensor is intentionally narrow
    /// (1-2 rays) but together they cover the world fully without conflating
    /// signals. Pattern lifted from "AI Learns to Play Soccer" (Hugging Face
    /// ML-Agents unit, 2023) where splitting a single 4-tag sensor into
    /// goal/own-goal pairs produced the breakthrough.
    ///
    /// Budget per stack (RayPerceptionSensor.OutputSize = (Tags+2) * (2*RPD+1));
    /// total = x2 with NumStackedVectorObservations=2:
    ///   Sensor_Ball        : 2 rays  360   tag Ball   -> 15  ->  30
    ///   Sensor_Goal        : 1 ray   60    tag Goal   -> 9   ->  18
    ///   Sensor_Opponents   : 2 rays  360   tag Agent  -> 15  ->  30
    ///   Sensor_Walls       : 2 rays  360   tag Wall   -> 15  ->  30
    ///   Ray total                                        -> 108
    ///   Vector obs (26 x 2)                              ->  52
    ///   Grand total model inputs                         -> 160
    ///
    /// Previous single-sensor contract was 118 inputs (66 ray + 52 vec). This
    /// version obsoletes every .onnx. Fine - none are assigned as of 2026-08-05;
    /// the model input shape is reset on the next training run with no stranded
    /// assets (CLAUDE.md "No trained brain is currently assigned (2026-08-04)").
    ///
    /// Sensor_Goal is team-relative through rewards: +Y points "forward" for
    /// blue (toward Red's goal) and "backward" for red (toward Red's own goal),
    /// so the policy learns that the same input means "opponent goal" for blue
    /// and "own goal" for red via the reward signal.
    ///
    /// Awake wipes any pre-existing ray sensors on the GameObject first so a
    /// scene-serialized 11-ray sensor cannot silently stack with the new
    /// 4-sensor battery. DefaultExecutionOrder(-100) keeps this Awake ahead
    /// of Agent sensor initialization, so the contract is set exactly once.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class Sensor_Vision : MonoBehaviour
    {
        // Half-arc for the goal sensor. Narrow enough that the ray only fires
        // when the agent is roughly facing the goal mouth; at any other angle
        // the slot reads zero (the policy learns "ray hit = something good is
        // over there" without having to triangulate which goal).
        const float GoalHalfArcDegrees = 30f;

        // Ray lengths. Goals can sit up to ~40 m across the pitch; walls live
        // at the perimeter (max 30 m from centre on the 36x54 training pitch);
        // ball and opponents are within 24 m 99% of the time. Lengths are
        // generous on purpose - one of the v3 (300 deg / 24 m) findings was
        // that short rays left agents blind in the far half.
        const float GoalRayLength = 40f;
        const float StandardRayLength = 24f;
        const float WallRayLength = 12f;

        void Awake()
        {
            // Drop any pre-existing ray sensors so the contract cannot drift
            // between runs or scenes. Safe even when none exist.
            var existing = GetComponents<RayPerceptionSensorComponent2D>();
            for (int i = 0; i < existing.Length; i++)
            {
                if (existing[i] != null) Destroy(existing[i]);
            }

            AddSensor("Sensor_Ball",      new[] { "Ball" },  raysPerDirection: 2, maxRayDegrees: 180f, rayLength: StandardRayLength);
            AddSensor("Sensor_Goal",      new[] { "Goal" },  raysPerDirection: 1, maxRayDegrees: GoalHalfArcDegrees, rayLength: GoalRayLength);
            AddSensor("Sensor_Opponents", new[] { "Agent" }, raysPerDirection: 2, maxRayDegrees: 180f, rayLength: StandardRayLength);
            AddSensor("Sensor_Walls",     new[] { "Wall" },  raysPerDirection: 2, maxRayDegrees: 180f, rayLength: WallRayLength);
        }

        void AddSensor(string sensorName, string[] tags, int raysPerDirection,
                       float maxRayDegrees, float rayLength)
        {
            var s = gameObject.AddComponent<RayPerceptionSensorComponent2D>();
            s.SensorName = sensorName;
            s.RaysPerDirection = raysPerDirection;
            // ML-Agents takes MaxRayDegrees as the HALF-arc around the +Y eye
            // axis, so 180 = full circle and 30 = a 60 deg forward wedge.
            s.MaxRayDegrees = maxRayDegrees;
            s.RayLength = rayLength;
            s.SphereCastRadius = 0.1f;
            s.DetectableTags = new List<string>(tags);
        }
    }
}
