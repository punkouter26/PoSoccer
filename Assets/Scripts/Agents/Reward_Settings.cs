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
        [Tooltip("Body tint applied at runtime. Team is shown by the eye color instead.")]
        public Color playerColor = Color.white;
        [Tooltip("This player's trained brain. Null = plays with the rule-based bot until trained.")]
        public Unity.InferenceEngine.ModelAsset brainModel;

        [Header("Training provenance (stamped by scripts/update-model.ps1 and evaluate.ps1)")]
        [Tooltip("Trainer steps behind the deployed brainModel. 0 = never trained (rule-based bot).")]
        public int trainingSteps;
        [Tooltip("Run that produced the deployed brainModel, e.g. soccer_p3_botcurric_00.")]
        public string trainingRunId;
        [Tooltip("Date the brainModel was deployed into this slot (yyyy-MM-dd).")]
        public string trainedOn;
        [Tooltip("Share of eval episodes this brain won against the full-strength bot. " +
                 "-1 = never measured. Bot-vs-bot baseline is ~0.425, so anything under that " +
                 "is losing to the scripted opponent.")]
        [Range(-1f, 1f)]
        public float evalWinRate = -1f;
        [Tooltip("Episode count behind evalWinRate (sample size for the number above).")]
        public int evalEpisodes;

        [Header("Physique")]
        [Tooltip("Body size multiplier on the scene's base scale. Big bodies block and shield more of the pitch.")]
        public float bodyScale = 1f;
        [Tooltip("Body mass (kg). Drive force is shared, so heavier = slower but harder to shove; " +
                 "top-speed momentum (mass x max speed) is identical for every mass - big-slow and " +
                 "small-fast are equally strong, just different.")]
        public float bodyMass = 75f;

        [Header("Terminal rewards")]
        // v4 (2026-08-04): goalScorer 0.7 -> 1.2 and stalemateTimeout -0.1 -> -0.6.
        // MEASURED: with 0.7/-1.0/-0.1, stalling was the OPTIMAL policy. Expected value
        // of stalling every episode was -0.1; of attacking and trading goals 50/50,
        // (0.7-1.0)/2 = -0.15. A policy had to win >53% of contested games before
        // attacking beat parking the bus - and it wins ~17%. So it learned not to lose
        // instead of to score, which is exactly what this table paid for.
        // The proof: halving bot_strength (1.0 -> 0.5) left the win rate flat at ~17%
        // and converted 31 points of losses into stalemates (17% -> 48%). Scoring rate
        // was pinned regardless of opponent - an offense problem, not a perception one.
        // Now goalScorer must exceed |goalConceded| so attacking beats stalling at any
        // competitive rate, and the stalemate penalty removes the safe harbour.
        public float goalScorer = 1.2f;
        public float assist = 0.3f;
        public float teamBaselineVictory = 0.1f;
        public float goalConceded = -1.0f;
        public float stalemateTimeout = -0.6f;

        [Header("Dense rewards (per decision step)")]
        [Tooltip("Per-step time cost. v2 changed from -0.0001 to 0 because stepPenalty " +
                 "washed out the entire reward gradient (O(1) of the episode budget). " +
                 "Terminal reward already provides temporal credit.")]
        public float stepPenalty = 0f;
        [Tooltip("Differential proximity reward scale. v2: replaces absolute proximity " +
                 "(which rewarded idling near the ball) with (prevDist - curDist) * scale " +
                 "so an agent approaching faster than its teammate earns positive reward.")]
        public float ballProximityScale = 0.002f;
        [Tooltip("Use the v2 differential-proximity mode. Set false for the legacy absolute-proximity mode.")]
        public bool useDifferentialProximity = true;
        public float facingAlignmentScale = 0.0002f;
        [Tooltip("Reward per step for ball velocity toward the opponent goal (the 'shoot goalward' gradient).")]
        public float ballToGoalVelocityScale = 0.001f;
        [Tooltip("Penalty scale on per-step action change (anti-twitch; smooth, deliberate movement). " +
                 "v2 halved from 0.001: hard cuts are *correct* for soccer (cutting inside the box) " +
                 "and the old penalty was teaching the brain to be smooth and idle.")]
        public float actionJitterScale = 0.0004f;
        [Tooltip("Penalty scale for lingering within 0.8m of a wall (cures wall-hugging).")]
        public float wallProximityPenalty = 0.0005f;
        [Tooltip("Penalty per step (only for the team that last touched the ball) while the ball sits " +
                 "inside a corner zone. v2: team-aware - used to bleed both teams regardless of fault, " +
                 "which let a corner-creator escape penalty while the defender suffered.")]
        public float cornerBallPenalty = 0.0006f;
        [Tooltip("Defender trait: reward for positioning between the ball and own goal (0 = off).")]
        public float defensivePositionScale = 0f;
        [Tooltip("Midfielder trait: reward per step while keeping the ball within 1.2m (0 = off).")]
        public float possessionScale = 0f;

        [Header("Sparse rewards")]
        [Tooltip("Reward per fresh ball collision. v3 (2026-08-04): 0.05 -> 0.005. At 0.05 " +
                 "just 14 touches outscored a goal (0.7), so over a ~4400-step episode the " +
                 "optimal policy was to poke the ball repeatedly rather than finish - which " +
                 "matches the observed 12-18% stalemate rate. Contact should nudge a cold-start " +
                 "policy toward the ball, not compete with scoring.")]
        public float ballContact = 0.005f;

        [Header("Episode limits")]
        [Tooltip("Env steps before stalemate timeout is applied.")]
        public int maxEnvironmentSteps = 5000;
    }
}
