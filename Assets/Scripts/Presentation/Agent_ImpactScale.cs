using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// One definition of "how hard was that", shared by everything that reacts
    /// to a contact: Agent_Audio's impact voices, Agent_ParticleFX's debris and
    /// Agent_ImpactFX's shocks.
    ///
    /// WHY IMPULSE AND NOT SPEED. Relative speed says how fast two things closed;
    /// it says nothing about how much of that the contact actually absorbed. A
    /// ball skimming a wall at 12 m/s and a ball driven square into it at 12 m/s
    /// have the same relativeVelocity and very different normal impulses, and
    /// only the second one should thump. The 2D solver already reports the
    /// impulse per contact point (Agent_Contact has summed it since it was
    /// written); this reads the same number so the sound, the debris and the
    /// stagger all peak together.
    ///
    /// WHY IT IS ITS OWN STATIC CLASS. Three components needed the same
    /// normalisation and the same saturation points. Written out three times,
    /// the loudest hit and the biggest burst drift apart the first time one of
    /// them is retuned.
    ///
    /// Pure arithmetic: no state, no allocation, safe from any callback.
    /// </summary>
    public static class Agent_ImpactScale
    {
        /// <summary>
        /// Summed normal impulse (N*s) at which a BALL contact is at full
        /// strength. The ball is 0.43 kg, so a square 14 m/s strike off a wall
        /// delivers roughly 0.43 * 14 * (1 + restitution) = 9-10 N*s.
        /// </summary>
        public const float BALL_IMPULSE_FULL = 9f;

        /// <summary>
        /// Closing speed (m/s) used as a FLOOR under the impulse reading for ball
        /// contacts. The solver occasionally reports a zero normal impulse on the
        /// Enter callback of a very fast, very brief contact; without a floor
        /// that hit would be silent. Deliberately generous (18, against the old
        /// speed-only saturation of 14) so the impulse decides whenever it is
        /// present and the speed only rescues the cases where it is not.
        /// </summary>
        public const float BALL_SPEED_FULL = 18f;

        /// <summary>Player-on-player impulse below which a contact is a jostle. Matches Agent_ImpactFX.</summary>
        public const float PLAYER_IMPULSE_FLOOR = 6f;

        /// <summary>Player-on-player impulse at full strength. Matches Agent_Contact's stagger saturation.</summary>
        public const float PLAYER_IMPULSE_FULL = 45f;

        /// <summary>
        /// Collision2D has no `impulse` (that is the 3D API); the 2D solver
        /// reports one normal impulse per contact point, so they are summed.
        /// GetContact returns a struct, so this allocates nothing.
        /// </summary>
        public static float SummedNormalImpulse(Collision2D collision)
        {
            if (collision == null) return 0f;

            float impulse = 0f;
            int count = collision.contactCount;
            for (int contactIndex = 0; contactIndex < count; contactIndex++)
                impulse += collision.GetContact(contactIndex).normalImpulse;
            return impulse;
        }

        /// <summary>Strength of a ball contact, 0..1.</summary>
        public static float Ball01(Collision2D collision)
        {
            if (collision == null) return 0f;

            float byImpulse = SummedNormalImpulse(collision) / BALL_IMPULSE_FULL;
            float bySpeed = collision.relativeVelocity.magnitude / BALL_SPEED_FULL;
            return Mathf.Clamp01(Mathf.Max(byImpulse, bySpeed));
        }

        /// <summary>
        /// Strength of a player-on-player contact, 0..1, from the summed impulse
        /// Agent_Contact.PlayerContact already carries. 0 at or below the floor.
        /// </summary>
        public static float Player01(float impulse,
            float floor = PLAYER_IMPULSE_FLOOR, float full = PLAYER_IMPULSE_FULL)
        {
            if (impulse <= floor) return 0f;
            return Mathf.Clamp01(Mathf.InverseLerp(floor, Mathf.Max(floor + 0.01f, full), impulse));
        }

        /// <summary>
        /// Strength to perceived level. Loudness is roughly a power law of
        /// physical magnitude, so a linear map makes every ordinary touch
        /// near-silent and only the hardest hits audible; the square root spreads
        /// the middle of the range out where most contacts actually land.
        /// </summary>
        public static float Loudness(float strength) => Mathf.Sqrt(Mathf.Clamp01(strength));
    }
}
