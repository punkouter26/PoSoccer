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
    /// Budget (RayPerceptionSensor.OutputSize = (Tags+2) * (2*RPD+1)):
    ///   Sensor_Ball        : 2 rays/dir  tag Ball   -> (1+2)*5 -> 15
    ///   Sensor_Goal        : 1 ray /dir  tag Goal   -> (1+2)*3 ->  9
    ///   Sensor_Opponents   : 2 rays/dir  tag Agent  -> (1+2)*5 -> 15
    ///   Sensor_Walls       : 2 rays/dir  tag Wall   -> (1+2)*5 -> 15
    ///   Ray total                                             -> 54
    ///   Vector obs (BaseObservationSize 27 x 2 stacks)        -> 54
    ///   Grand total model inputs                              -> 108
    ///
    /// CORRECTED 2026-08-12. This block used to double every ray figure and
    /// claim 108 ray / 160 total, on the belief that
    /// NumStackedVectorObservations=2 stacks the ray sensors too. It does not -
    /// that setting only stacks the VectorSensor. Ray sensors stack via their
    /// own RayPerceptionSensorComponentBase.ObservationStacks, which defaults to
    /// 1 and is never set here. CLAUDE.md's "162" was wrong the same way.
    /// Nothing in the shipped code changed; only the arithmetic on this page.
    ///
    /// Previous single-sensor contract was 118 inputs (66 ray + 52 vec, when
    /// BaseObservationSize was 26). Any .onnx from before the split declares
    /// those shapes and cannot load against this runtime - see
    /// Agent_EditMode_ObsContract, which fails the build rather than letting a
    /// mismatched brain degrade silently at eval time.
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

        /// <summary>
        /// One entry per ray sensor. Declared as data rather than four inline
        /// AddSensor calls so the observation contract is computable without
        /// entering play mode - Agent_EditMode_ObsContract sums
        /// <see cref="TotalRayObservationSize"/> off this table and compares it
        /// against the assigned .onnx. Awake is the only writer; keep it in sync
        /// by adding sensors here, never by calling AddSensor directly.
        /// </summary>
        internal readonly struct SensorSpec
        {
            public readonly string Name;
            public readonly string Tag;
            public readonly int RaysPerDirection;
            public readonly float MaxRayDegrees;
            public readonly float RayLength;

            public SensorSpec(string name, string tag, int raysPerDirection,
                              float maxRayDegrees, float rayLength)
            {
                Name = name;
                Tag = tag;
                RaysPerDirection = raysPerDirection;
                MaxRayDegrees = maxRayDegrees;
                RayLength = rayLength;
            }

            // RayPerceptionSensor.OutputSize(). One tag per sensor by design.
            public int ObservationSize => (1 + 2) * (2 * RaysPerDirection + 1);
        }

        internal static readonly SensorSpec[] Battery =
        {
            new SensorSpec("Sensor_Ball",      "Ball",  2, 180f,                 StandardRayLength),
            new SensorSpec("Sensor_Goal",      "Goal",  1, GoalHalfArcDegrees,   GoalRayLength),
            new SensorSpec("Sensor_Opponents", "Agent", 2, 180f,                 StandardRayLength),
            new SensorSpec("Sensor_Walls",     "Wall",  2, 180f,                 WallRayLength),
        };

        /// <summary>
        /// Ray observations this component contributes to the model input.
        /// ObservationStacks is left at its default of 1, so this is NOT
        /// multiplied by NumStackedVectorObservations - that setting stacks the
        /// VectorSensor only.
        /// </summary>
        internal static int TotalRayObservationSize
        {
            get
            {
                int total = 0;
                for (int i = 0; i < Battery.Length; i++)
                {
                    total += Battery[i].ObservationSize;
                }
                return total;
            }
        }

        void Awake()
        {
            // Drop any pre-existing ray sensors so the contract cannot drift
            // between runs or scenes. Safe even when none exist.
            var existing = GetComponents<RayPerceptionSensorComponent2D>();
            for (int i = 0; i < existing.Length; i++)
            {
                if (existing[i] != null) Destroy(existing[i]);
            }

            for (int i = 0; i < Battery.Length; i++)
            {
                AddSensor(Battery[i]);
            }
        }

        void AddSensor(SensorSpec spec)
        {
            var s = gameObject.AddComponent<RayPerceptionSensorComponent2D>();
            s.SensorName = spec.Name;
            s.RaysPerDirection = spec.RaysPerDirection;
            // ML-Agents takes MaxRayDegrees as the HALF-arc around the +Y eye
            // axis, so 180 = full circle and 30 = a 60 deg forward wedge.
            s.MaxRayDegrees = spec.MaxRayDegrees;
            s.RayLength = spec.RayLength;
            s.SphereCastRadius = 0.1f;
            s.DetectableTags = new List<string> { spec.Tag };
        }
    }
}
