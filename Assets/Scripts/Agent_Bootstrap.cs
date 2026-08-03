using UnityEngine;
using UnityEngine.Serialization;

namespace PoSoccer
{
    /// <summary>
    /// Deterministic runtime physics/display enforcement (UNITY_RULES).
    /// Top-down pitch: gravity acts along -Z conceptually, so the 2D plane sees none.
    /// SI units, 100 Hz fixed step, capped solver iterations, 60 FPS lock.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class Agent_Bootstrap : MonoBehaviour
    {
        [Tooltip("Fixed physics timestep (PRD: 0.01s / 100 Hz).")]
        [FormerlySerializedAs("fixedTimestep")]
        [SerializeField] private float _fixedTimestep = 0.01f;
        [Tooltip("Frame-rate lock for interactive play (mobile portrait target).")]
        [FormerlySerializedAs("targetFrameRate")]
        [SerializeField] private int _targetFrameRate = 60;
        [Tooltip("Physics2D solver velocity iterations cap.")]
        [FormerlySerializedAs("velocityIterations")]
        [SerializeField] private int _velocityIterations = 8;
        [Tooltip("Physics2D solver position iterations cap.")]
        [FormerlySerializedAs("positionIterations")]
        [SerializeField] private int _positionIterations = 3;

        void Awake()
        {
            // Bird's-eye view: no in-plane gravity. Real-world g applies along the
            // unmodeled Z axis; friction/drag values stand in for rolling resistance.
            // Documented exemption from the Earth-gravity rule - see docs/rules-exemptions.md.
            Physics2D.gravity = Vector2.zero;
            Physics2D.velocityIterations = _velocityIterations;
            Physics2D.positionIterations = _positionIterations;
            // Keep CCD sensitive for fast, small colliders (FIFA ball r=0.11 m
            // can hit ~22 m/s relative at sprint speeds, > the default contact
            // threshold's tunnel window at 100 Hz). 0.005 m ~ 2x the smallest
            // collider radius in the project.
            Physics2D.contactThreshold = 0.005f;

            Time.fixedDeltaTime = _fixedTimestep;
            Application.targetFrameRate = _targetFrameRate;

            // CameraFollow needs the same Bootstrap lifetime as the rest of the
            // runtime, so it gets auto-attached here. It then self-positions in
            // LateUpdate, so the scene never has to carry a serialized reference.
            if (Camera.main != null && Camera.main.GetComponent<Agent_CameraFollow>() == null)
            {
                Camera.main.gameObject.AddComponent<Agent_CameraFollow>();
            }
        }
    }
}
