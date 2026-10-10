using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Physics layers (ProjectSettings/TagManager.asset slots 6-11) and the code that
    /// stamps them. Layers are applied at Awake/Start rather than serialized in the
    /// scenes, for the same reason the team tags are: a scene cannot drift from code,
    /// and pitches cloned at runtime (Agent_TrainingGrid, Agent_Gallery, the
    /// POSOCCER_SQUAD player clones) stamp themselves exactly like the authored one.
    ///
    /// WHY THE RAY SENSORS NEED THIS. Until 2026-10-10 every collider sat on Default
    /// and every ray sensor cast against the default mask. The rays start at the
    /// agent's own centre, i.e. INSIDE its own BoxCollider2D, and a Physics2D shape
    /// cast reports a collider it starts overlapped with (fraction 0). With a mask
    /// that contains the caster's own layer, a ray therefore has nothing to say but
    /// "I hit myself at distance zero". (That is Physics2D.CircleCast's documented
    /// behaviour; it was NOT measured in an editor on the day this was written.
    /// Agent_PlayMode_RayLayers is the measurement.) Each sensor now casts only against the layer
    /// its class of object lives on, and no sensor's mask contains the caster:
    ///   Sensor_Ball      -> Ball
    ///   Sensor_Walls     -> Pitch
    ///   Sensor_Opponents -> the OPPOSING team's layer
    ///
    /// WHY TWO TEAM LAYERS. A single "Player" mask on the opponent sensor would put
    /// the caster back inside its own mask - the exact defect above. PlayerBlue /
    /// PlayerRed are what make "opponents only, never myself" expressible. The plain
    /// Player layer is declared but nothing is stamped with it; use
    /// <see cref="AllPlayersMask"/> for "any player".
    ///
    /// FX collides with nothing (Physics2DSettings m_LayerCollisionMatrix) and is in no
    /// sensor's mask. Nothing in Assets/Scripts/Agents stamps it; it is there for
    /// presentation objects that must never touch the simulation.
    /// </summary>
    public static class Agent_Layers
    {
        public const string PLAYER = "Player";
        public const string BALL = "Ball";
        public const string PITCH = "Pitch";
        public const string FX = "FX";
        public const string PLAYER_BLUE = "PlayerBlue";
        public const string PLAYER_RED = "PlayerRed";

        /// <summary>
        /// ML-Agents' own default ray mask (everything but Ignore Raycast). Only used
        /// as the fallback when a layer is missing from TagManager.asset.
        /// </summary>
        const int DEFAULT_RAY_MASK = -5;

        static bool _warnedMissing;

        public static string TeamLayerName(Agent_Soccer.Team team) =>
            team == Agent_Soccer.Team.Blue ? PLAYER_BLUE : PLAYER_RED;

        /// <summary>Both team layers: "any player".</summary>
        public static int AllPlayersMask => MaskOrZero(PLAYER_BLUE) | MaskOrZero(PLAYER_RED);

        /// <summary>
        /// Ray mask containing exactly <paramref name="layerName"/>. If the layer does
        /// not exist (a TagManager.asset older than this file) it falls back to the
        /// pre-layer default mask and says so once, rather than returning 0 - a zero
        /// mask is a sensor that sees nothing and reports nothing wrong.
        /// </summary>
        public static int RayMask(string layerName)
        {
            int layer = LayerMask.NameToLayer(layerName);
            if (layer >= 0) return 1 << layer;
            WarnMissing(layerName);
            return DEFAULT_RAY_MASK;
        }

        /// <summary>Moves <paramref name="target"/> onto the named layer. No-op when it is already there.</summary>
        public static void Stamp(GameObject target, string layerName)
        {
            if (target == null) return;
            int layer = LayerMask.NameToLayer(layerName);
            if (layer < 0)
            {
                WarnMissing(layerName);
                return;
            }
            if (target.layer != layer) target.layer = layer;
        }

        /// <summary>
        /// Ball -> Ball; every other collider under the pitch root that is not part of
        /// a player (walls, corner cushions, goal triggers, Agent_PitchGuard's arcs) ->
        /// Pitch. Players stamp themselves in Agent_Soccer.Awake, because their layer
        /// depends on their team.
        ///
        /// Idempotent, and called twice per pitch: from Agent_EnvController.Awake for
        /// the authored colliders and again from Start after Agent_PitchGuard.Build has
        /// added its own. Start/Awake only - GetComponentsInChildren allocates.
        /// </summary>
        public static void StampPitch(Agent_EnvController env)
        {
            if (env == null) return;

            Transform ballRoot = env.ball != null ? env.ball.transform : null;
            if (ballRoot != null) Stamp(ballRoot.gameObject, BALL);

            var colliders = env.GetComponentsInChildren<Collider2D>(true);
            for (int colliderIndex = 0; colliderIndex < colliders.Length; colliderIndex++)
            {
                var collider = colliders[colliderIndex];
                if (collider == null) continue;
                if (ballRoot != null && collider.transform.IsChildOf(ballRoot)) continue;
                if (collider.GetComponentInParent<Agent_Soccer>(true) != null) continue;
                Stamp(collider.gameObject, PITCH);
            }
        }

        static int MaskOrZero(string layerName)
        {
            int layer = LayerMask.NameToLayer(layerName);
            return layer >= 0 ? 1 << layer : 0;
        }

        static void WarnMissing(string layerName)
        {
            if (_warnedMissing) return;
            _warnedMissing = true;
            Debug.LogError(
                $"[Agent_Layers] Physics layer '{layerName}' is not defined in " +
                "ProjectSettings/TagManager.asset. Objects stay on their current layer and " +
                "ray sensors fall back to the default mask, in which every ray starts inside " +
                "the caster's own collider. Restore the layer slots (6-11).");
        }
    }
}
