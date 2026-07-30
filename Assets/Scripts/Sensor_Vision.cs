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
        public const float ArcDegrees = 120f;
        public const int RaysPerDirection = 5;
        public const float RayLength = 12f;

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
