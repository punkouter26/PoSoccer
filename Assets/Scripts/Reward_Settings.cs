using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Central reward constants for SoccerAgent_v01 (PRD §Reward Engineering).
    /// One asset instance is shared by every pitch so training tweaks happen in one place.
    /// </summary>
    [CreateAssetMenu(fileName = "Reward_Settings", menuName = "PoSoccer/Reward Settings")]
    public sealed class Reward_Settings : ScriptableObject
    {
        [Header("Personality")]
        [Tooltip("Player this profile belongs to (STANDARD / MATT / KIM / NICK ...).")]
        public string playerName = "STANDARD";
        [TextArea]
        [Tooltip("Design notes: how this personality attacks, scores, defends.")]
        public string personalityNotes;

        [Header("Terminal rewards")]
        public float goalScorer = 0.7f;
        public float assist = 0.3f;
        public float teamBaselineVictory = 0.2f;
        public float goalConceded = -1.0f;
        public float stalemateTimeout = -0.10f;

        [Header("Dense rewards (per decision step)")]
        public float stepPenalty = -0.0001f;
        public float ballProximityScale = 0.0004f;
        public float facingAlignmentScale = 0.0002f;
        [Tooltip("Reward per step for ball velocity toward the opponent goal (the 'shoot goalward' gradient).")]
        public float ballToGoalVelocityScale = 0.001f;
        [Tooltip("Penalty scale on per-step action change (anti-twitch; smooth, deliberate movement).")]
        public float actionJitterScale = 0.001f;
        [Tooltip("Penalty scale for lingering within 0.8m of a wall (cures wall-hugging).")]
        public float wallProximityPenalty = 0.0005f;
        [Tooltip("Defender trait: reward for positioning between the ball and own goal (0 = off).")]
        public float defensivePositionScale = 0f;
        [Tooltip("Midfielder trait: reward per step while keeping the ball within 1.2m (0 = off).")]
        public float possessionScale = 0f;

        [Header("Sparse rewards")]
        public float ballContact = 0.05f;

        [Header("Episode limits")]
        [Tooltip("Env steps before stalemate timeout is applied.")]
        public int maxEnvironmentSteps = 5000;
    }
}
