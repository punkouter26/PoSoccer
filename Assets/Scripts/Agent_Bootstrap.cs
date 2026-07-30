using UnityEngine;

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
        public float fixedTimestep = 0.01f;
        [Tooltip("Frame-rate lock for interactive play (mobile portrait target).")]
        public int targetFrameRate = 60;
        [Tooltip("Physics2D solver velocity iterations cap.")]
        public int velocityIterations = 8;
        [Tooltip("Physics2D solver position iterations cap.")]
        public int positionIterations = 3;

        void Awake()
        {
            // Bird's-eye view: no in-plane gravity. Real-world g applies along the
            // unmodeled Z axis; friction/drag values stand in for rolling resistance.
            Physics2D.gravity = Vector2.zero;
            Physics2D.velocityIterations = velocityIterations;
            Physics2D.positionIterations = positionIterations;

            Time.fixedDeltaTime = fixedTimestep;
            Application.targetFrameRate = targetFrameRate;
        }
    }
}
