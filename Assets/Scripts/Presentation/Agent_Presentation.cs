using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Gate and installer for the spectator layer (replay, match flow, crowd,
    /// commentary).
    ///
    /// THE GATE MATTERS MORE THAN THE INSTALLER. SCN_Training and SCN_Exhibition
    /// carry an identical object set, so nothing about the hierarchy distinguishes
    /// them at runtime. The one honest discriminator is Agent_HUD.enableMatchFlow,
    /// which is serialized 0 in SCN_Training and 1 in SCN_Exhibition. Everything
    /// here also refuses to run when a trainer is attached or an evaluation is in
    /// progress, so a headless run can never pay for a light show - and, more
    /// importantly, can never have its timing perturbed by one. A replay freezing
    /// the clock during an eval would corrupt the very numbers the benchmark
    /// exists to produce.
    ///
    /// Components are attached in code rather than serialized into the scene, in
    /// the same spirit as Agent_Bootstrap attaching Agent_CameraFollow and
    /// Agent_Soccer auto-adding Sensor_Vision: the scene assets stay untouched, so
    /// there is no way for a scene to drift out of sync with the code.
    /// </summary>
    public static class Agent_Presentation
    {
        /// <summary>
        /// True only in a scene meant for a human audience. False in training, in
        /// evaluation, and whenever a trainer is connected.
        /// </summary>
        public static bool IsMatchScene(Agent_HUD hud)
        {
            if (hud == null || !hud.enableMatchFlow) return false;
            if (Agent_EvalStats.EvalMode) return false;
            return !Unity.MLAgents.Academy.Instance.IsCommunicatorOn;
        }

        /// <summary>
        /// True in the checkpoint gallery: a grid of pitches, no match flow. Same
        /// training/eval exclusions as <see cref="IsMatchScene"/> and for the same
        /// reason - a headless run must never pay for, or be perturbed by, a light
        /// show.
        /// </summary>
        public static bool IsGalleryScene()
        {
            if (!Agent_MatchSetup.GalleryMode) return false;
            if (Agent_EvalStats.EvalMode) return false;
            return !Unity.MLAgents.Academy.Instance.IsCommunicatorOn;
        }

        /// <summary>
        /// True wherever a human is watching, match or gallery.
        ///
        /// THE DISTINCTION FROM IsMatchScene IS THE WHOLE POINT. Components that
        /// own GLOBAL state - the clock (Agent_Replay, Agent_Hitstop, the
        /// countdown), the camera (Agent_Director), the scoreboard
        /// (Agent_WinProbability, Agent_MatchStats) - must gate on IsMatchScene,
        /// because the gallery clones the pitch and six copies of any of them
        /// would fight over one clock and one camera. Components that only draw
        /// their OWN pitch gate on this instead, so the gallery still looks like
        /// the game rather than like a debug scene.
        /// </summary>
        public static bool IsVisualScene(Agent_HUD hud) => IsMatchScene(hud) || IsGalleryScene();

        /// <summary>
        /// Adds the spectator components to the pitch root, once. Safe to call
        /// when the gate is closed - each component checks the gate itself in
        /// Start and disables, which keeps the decision in one place.
        /// </summary>
        public static void Install(Agent_EnvController env)
        {
            if (env == null) return;
            var go = env.gameObject;

            InstallVisuals(go);

            // Global-state owners: exactly one pitch may hold these.
            if (go.GetComponent<Agent_Replay>() == null) go.AddComponent<Agent_Replay>();
            if (go.GetComponent<Agent_MatchFlow>() == null) go.AddComponent<Agent_MatchFlow>();
            if (go.GetComponent<Agent_Commentary>() == null) go.AddComponent<Agent_Commentary>();
            if (go.GetComponent<Agent_Crowd>() == null) go.AddComponent<Agent_Crowd>();
            if (go.GetComponent<Agent_Hitstop>() == null) go.AddComponent<Agent_Hitstop>();

            // Broadcast layer. Order matters once: the director reads the win
            // probability's threat, so the probability component has to exist by
            // the time the director's Start runs GetComponent for it.
            if (go.GetComponent<Agent_WinProbability>() == null) go.AddComponent<Agent_WinProbability>();
            if (go.GetComponent<Agent_Director>() == null) go.AddComponent<Agent_Director>();
            if (go.GetComponent<Agent_MatchStats>() == null) go.AddComponent<Agent_MatchStats>();
        }

        /// <summary>
        /// The gallery's component set: everything that draws one pitch, and
        /// nothing that touches the clock, the camera or the scoreboard.
        ///
        /// Added to the pitch root BEFORE Agent_Gallery clones it, so every clone
        /// inherits the same set - which is why this is a separate entry point
        /// rather than Install with a flag.
        /// </summary>
        public static void InstallGallery(Agent_EnvController env)
        {
            if (env == null) return;
            InstallVisuals(env.gameObject);
            if (env.GetComponent<Agent_Gallery>() == null) env.gameObject.AddComponent<Agent_Gallery>();
        }

        /// <summary>Per-pitch visuals, shared by both modes.</summary>
        static void InstallVisuals(GameObject go)
        {
            if (go.GetComponent<Agent_Surfaces>() == null) go.AddComponent<Agent_Surfaces>();
            if (go.GetComponent<Agent_ParticleFX>() == null) go.AddComponent<Agent_ParticleFX>();
            if (go.GetComponent<Agent_ImpactFX>() == null) go.AddComponent<Agent_ImpactFX>();
            if (go.GetComponent<Agent_Shadows>() == null) go.AddComponent<Agent_Shadows>();
            if (go.GetComponent<Agent_Limbs>() == null) go.AddComponent<Agent_Limbs>();
            if (go.GetComponent<Agent_Intent>() == null) go.AddComponent<Agent_Intent>();
            if (go.GetComponent<Agent_VisionView>() == null) go.AddComponent<Agent_VisionView>();
        }
    }
}
