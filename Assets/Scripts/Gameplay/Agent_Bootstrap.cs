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

            bool gallery = Agent_Presentation.IsGalleryScene();

            // CameraFollow needs the same Bootstrap lifetime as the rest of the
            // runtime, so it gets auto-attached here. It then self-positions in
            // LateUpdate, so the scene never has to carry a serialized reference.
            //
            // Not in the gallery: there is no ball to follow when there are six,
            // and Agent_Gallery frames the whole grid once instead. Attaching it
            // anyway would mean two components writing the camera transform in the
            // same LateUpdate, which resolves to whichever ran last.
            if (!gallery && Camera.main != null
                && Camera.main.GetComponent<Agent_CameraFollow>() == null)
            {
                Camera.main.gameObject.AddComponent<Agent_CameraFollow>();
            }

            // Spectator layer (replay, match flow, crowd, commentary), attached
            // the same way and for the same reason: no scene asset has to carry a
            // reference, so a scene cannot drift out of sync with the code.
            // Installed only in a scene meant for an audience - this runs at -200,
            // ahead of Agent_TrainingGrid cloning the pitch, so a training run
            // never even allocates these components, let alone ticks them.
            // Telemetry goes in EVERY scene, training included - a diagnostic you
            // have to switch scenes to reach is a diagnostic nobody uses. It costs
            // nothing while hidden: no profiler recorders are allocated until the
            // overlay is opened, and Update early-outs.
            if (GetComponent<Agent_Telemetry>() == null) gameObject.AddComponent<Agent_Telemetry>();

            var hud = FindFirstObjectByType<Agent_HUD>();
            var env = FindFirstObjectByType<Agent_EnvController>();

            if (gallery)
            {
                // Visual layer only, installed BEFORE Agent_Gallery clones the
                // pitch so every clone inherits the same set. Nothing here owns
                // the clock, the camera or the scoreboard - six copies of any of
                // those would fight over one of each.
                Agent_Presentation.InstallGallery(env);
            }
            else if (Agent_Presentation.IsMatchScene(hud))
            {
                Agent_Presentation.Install(env);
            }
        }
    }
}
