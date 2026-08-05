using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Pitch dimensions as a function of squad size, anchored on FIFA futsal.
    ///
    /// Futsal plays 5v5 on 40 x 20 m = 800 m^2, i.e. 80 m^2 per player, at a 2:1
    /// length:width ratio. Holding that density constant and keeping the ratio is
    /// what makes a 1v1 feel like a 1v1 and a 10v10 feel like indoor soccer rather
    /// than a car park.
    ///
    /// For reference, the hand-authored pitch this replaces was 36 x 54 m for 2v2 -
    /// 486 m^2 per player, roomier per head than an 11-a-side outdoor field. That
    /// spacing is a plausible contributor to the wandering and stalemates seen in
    /// training: two players on half a hectare rarely contest anything.
    /// </summary>
    public static class Agent_PitchSizing
    {
        /// <summary>Square metres of playing surface per player (FIFA futsal 5v5).</summary>
        public const float AREA_PER_PLAYER = 80f;
        /// <summary>Length divided by width. Futsal's 40 x 20 is exactly 2.</summary>
        public const float LENGTH_TO_WIDTH = 2f;

        // Sanity rails. Futsal permits 25-42 m long and 16-25 m wide; these are a
        // little wider so 1v1 stays playable and 10v10 does not become a corridor.
        const float MIN_WIDTH = 12f;
        const float MAX_WIDTH = 30f;

        /// <summary>Goal mouth as a fraction of pitch width. Preserves the ratio the
        /// trained brains learned on (7.32 m goal on a 36 m pitch).</summary>
        const float GOAL_WIDTH_FRACTION = 7.32f / 36f;

        /// <summary>
        /// Playable half extents (x = half width, y = half length) for the given
        /// squad sizes. Uses total head count, so 1v3 gets the same surface as 2v2.
        /// </summary>
        public static Vector2 HalfExtentsFor(int bluePlayers, int redPlayers)
        {
            int total = Mathf.Max(2, bluePlayers + redPlayers);
            float area = AREA_PER_PLAYER * total;
            // area = width * length = width * (width * ratio)  =>  width = sqrt(area / ratio)
            float width = Mathf.Clamp(Mathf.Sqrt(area / LENGTH_TO_WIDTH), MIN_WIDTH, MAX_WIDTH);
            float length = width * LENGTH_TO_WIDTH;
            return new Vector2(width * 0.5f, length * 0.5f);
        }

        /// <summary>Goal mouth width for a pitch of the given half extents.</summary>
        public static float GoalWidthFor(Vector2 halfExtents) =>
            halfExtents.x * 2f * GOAL_WIDTH_FRACTION;
    }
}
