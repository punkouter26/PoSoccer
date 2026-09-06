using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Carries the menu's match configuration across the scene load.
    /// Applied=false (no menu visit) means the match scene uses its serialized
    /// defaults. Squad sizes are independent per side (1-10 each), so handicap
    /// matches like 1v3 are expressible.
    /// </summary>
    public static class Agent_MatchSetup
    {
        /// <summary>Hard cap per side. The menu's stepper clamps to this.</summary>
        public const int MAX_SQUAD = 10;

        public static bool Applied;

        /// <summary>Profile per slot, index 0 first. Null/empty = fall back to scene defaults.</summary>
        public static Reward_Settings[] BlueSquad;
        public static Reward_Settings[] RedSquad;

        public static int BlueSize => BlueSquad != null ? BlueSquad.Length : 0;
        public static int RedSize => RedSquad != null ? RedSquad.Length : 0;

        /// <summary>
        /// Gallery mode: the match scene runs a grid of pitches, one archived
        /// brain per pitch against the bot, instead of one exhibition match.
        ///
        /// A static rather than a separate scene because scene authoring is
        /// MCP-only under UNITY_RULES and the gallery needs the exhibition scene's
        /// entire wiring - pitch, goals, ball, agents, profiles - identically. A
        /// second scene would be a copy that drifts, which is the failure this
        /// project already documents for serialized references.
        /// </summary>
        public static bool GalleryMode;

        /// <summary>
        /// Brains to exhibit, one per pitch. Null or empty means "use the live
        /// roster", which Agent_Gallery resolves from the profiles the menu
        /// already knows about - so the feature works before anybody has archived
        /// a single checkpoint.
        /// </summary>
        public static Agent_Checkpoint[] GalleryEntries;

        /// <summary>Profiles to exhibit when no checkpoints have been authored yet.</summary>
        public static Reward_Settings[] GalleryProfiles;

        /// <summary>The rule-based benchmark opponent every gallery pitch plays against.</summary>
        public static Reward_Settings GalleryOpponent;

        /// <summary>Profile for a slot, or null when the slot is past the squad size.</summary>
        public static Reward_Settings Get(Agent_Soccer.Team team, int slot)
        {
            var squad = team == Agent_Soccer.Team.Blue ? BlueSquad : RedSquad;
            if (squad == null || slot < 0 || slot >= squad.Length) return null;
            return squad[slot];
        }

        /// <summary>Clears the selection so a direct scene load uses serialized defaults.</summary>
        public static void Clear()
        {
            Applied = false;
            BlueSquad = null;
            RedSquad = null;
            GalleryMode = false;
            GalleryEntries = null;
            GalleryProfiles = null;
            GalleryOpponent = null;
        }
    }
}
