using Unity.MLAgents.Sensors;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Configures the RayPerceptionSensor2D per PRD: 120° forward arc centered on the
    /// +Y "Eye" axis, 5 rays per side (11 total), 12 unit range, tags Ball/Wall/Goal/Agent.
    /// Runs before Agent sensor initialization so the settings are applied exactly once.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class Sensor_Vision : MonoBehaviour
    {
        // v3 (2026-08-04): 120 deg / 12 m left the agent blind behind and to the
        // sides while Agent_HeuristicBot reads every opponent's exact position from
        // the transforms, 360 deg, unlimited range. Four runs plateaued at 15-21%
        // against it. Widening the arc and extending the range costs nothing in
        // contract terms: RayPerceptionSensor.OutputSize() is
        // (DetectableTags + 2) * (2 * RaysPerDirection + 1) = 66 regardless of angle
        // or length, so existing .onnx still load - only what the rays mean changes,
        // and every model is being retrained anyway.
        //
        // The trade is angular resolution: 11 rays over 300 deg is ~27 deg apart
        // versus ~12 deg before. Acceptable here because the ball's exact position
        // already arrives through the vector observations - the rays' real job is
        // opponents and walls, where coverage beats precision.
        public const float ArcDegrees = 300f;
        public const int RaysPerDirection = 5;
        public const float RayLength = 24f;

        static readonly string[] Tags = { "Ball", "Wall", "Goal", "Agent" };

        void Awake()
        {
            var sensor = GetComponent<RayPerceptionSensorComponent2D>();
            if (sensor == null) sensor = gameObject.AddComponent<RayPerceptionSensorComponent2D>();

            sensor.SensorName = "Sensor_Vision";
            sensor.RaysPerDirection = RaysPerDirection;      // 11 rays total
            sensor.MaxRayDegrees = ArcDegrees * 0.5f;        // ±60° around +Y eye axis
            sensor.RayLength = RayLength;
            sensor.SphereCastRadius = 0.1f;
            sensor.DetectableTags = new System.Collections.Generic.List<string>(Tags);
        }
    }
}
