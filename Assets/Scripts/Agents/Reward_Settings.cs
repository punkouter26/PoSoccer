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

        /// <summary>
        /// Jersey patterns the body shader can draw. Procedural, so a kit costs
        /// no texture memory and no extra draw call - the pattern is a few ALU
        /// inside the material this player already shares with its team.
        /// </summary>
        public enum KitPattern
        {
            None = 0,
            Stripes = 1,
            Hoops = 2,
            Sash = 3,
            Halves = 4,
        }

        [Header("Kit")]
        // DEFAULT IS None ON EVERY SHIPPED PROFILE, DELIBERATELY. UNITY_RULES
        // reserves the look of a brain to its author: the heuristic bot is red,
        // the reference brain is "green, untextured", and custom brains get
        // USER-SUPPLIED textures - "never auto-assign one". A procedural kit is
        // still a texture by that standard, so this ships as an opt-in switch
        // rather than as four personalities that quietly grew shirts.
        [Tooltip("Procedural jersey pattern drawn on the body. None = flat playerColor, " +
                 "which is what every shipped profile uses (see UNITY_RULES on brain colour).")]
        public KitPattern kitPattern = KitPattern.None;
        [Tooltip("Secondary kit colour. Alpha is the blend strength, so alpha 0 = no kit " +
                 "however kitPattern is set.")]
        public Color kitColor = new Color(1f, 1f, 1f, 0f);
        [Tooltip("Bands across the body for Stripes and Hoops. Ignored by Sash and Halves.")]
        [Range(2f, 16f)]
        public float kitBands = 6f;
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

        /// <summary>
        /// The <see cref="Agent_Soccer.ActionGain"/> in force when this brainModel was
        /// TRAINED. -1 means unknown provenance, which for a populated brainModel is
        /// itself the defect this field exists to surface.
        ///
        /// WHY THIS EXISTS. On 2026-09-07 the gain went 1.6 -> 1.0 (p22) while MATT,
        /// NICK and KIM stayed on p21 checkpoints trained at 1.6. The tensor shapes are
        /// identical, so those .onnx load without a single warning and then under-drive:
        /// a policy that learned "0.625 buys full force" now gets 0.625 of it. That is
        /// milder than the 2026-08-28 frame change - a monotonic magnitude scaling
        /// preserves direction where a frame change scrambles meaning - but it is still
        /// a silent train/deploy mismatch, and Agent_EditMode_ObsContract cannot see it
        /// because every number that contract checks is unchanged.
        ///
        /// Stamped by scripts/update-model.ps1; pinned by Agent_EditMode_ActionGain.
        /// </summary>
        [Tooltip("Agent_Soccer.ActionGain in force when this brainModel was trained. " +
                 "-1 = unknown provenance. A value differing from the current constant " +
                 "means the brain under- or over-drives relative to what it learned.")]
        public float trainedActionGain = -1f;

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
        // v5 (2026-08-11): bonus shaping borrowed from the "AI Learns to Play Soccer"
        // "score with style" tip - small positive gradients for fast/central shots so
        // the policy converges faster than under pure terminal reward alone. Both
        // default to 0 so existing profiles train identically until they opt in.
        // goalSpeedBonus adds (stepsLeftAtGoal * fixedDt * scale) to every member of
        // the scoring side; default 0.05 means a 60% remaining episode adds 0.015, a
        // 10% remaining adds 0.0025 - small enough not to compete with goalScorer
        // (1.2) but visible enough to bias toward fast counters.
        [Tooltip("Per-second-remaining bonus added to every scoring-side member when a " +
                 "goal is scored. 0 = off (matches p7 behavior).")]
        public float goalSpeedBonus = 0.05f;
        // crossbarProximity rewards shots launched from inside the attacking third.
        // Dense per-step reward while the ball is in the opponent's goal mouth and
        // moving toward the net (so it isn't a free pass for parking the ball there).
        // Default 0.0005 / step caps at ~0.5 over an 1000-step possession, well under
        // a single goal so it shapes without dominating.
        [Tooltip("Per-step reward when the ball is in the opponent's goal mouth AND " +
                 "moving toward the net (close-range shot gradient). 0 = off.")]
        // v5 (2026-09-07): 0.0005 -> 0. Retired. Ceiling was +4.5 per episode, 3.75x a
        // goal, for a term that is nearly pure redundancy: a ball in the opponent's goal
        // mouth moving toward the net is already paid by ballToGoalVelocityScale, and
        // moments later by goalScorer itself. Two shaping terms and a terminal all paying
        // for the same event is how a shaping term becomes the objective.
        public float crossbarProximity = 0f;

        [Header("Dense rewards (per decision step)")]
        [Tooltip("Per-step time cost. v2 zeroed the old -0.0001 because it washed out the " +
                 "entire reward gradient; v4 restores it at -0.00005, which is half the " +
                 "magnitude and lands well under the stalemate terminal. See below.")]
        // v4 (2026-08-29): 0 -> -0.00005. The v2 objection was about MAGNITUDE, not the
        // mechanism: the episode cap is 9000 steps, so the old -0.0001 cost -0.9 per
        // episode - near-identical to goalConceded (-1.0) - and drowned every other term
        // while the reward table around it was still miscalibrated. At -0.00005 a full
        // 9000-step episode costs -0.45, below stalemateTimeout (-0.6) and well under
        // goalScorer (+1.2), so it biases toward finishing without dominating the outcome.
        //
        // WHY A PER-STEP COST SPECIFICALLY. After the body-frame fix the probe showed a
        // policy that drives at a deliberate magnitude of 0.320 (sign-stable, 19 flips in
        // 400 steps - that is the policy mean, not exploration), giving ~1.5 m/s against a
        // 9.54 m/s chassis. Nothing in the table paid for hurrying: the proximity term is
        // differential, so it telescopes to ballProximityScale * (dStart - dEnd) and pays
        // the same whether the ball is reached in 2 seconds or 20. The only existing time
        // pressure was the stalemateTimeout terminal, and at gamma 0.99 the horizon is
        // 1/(1-0.99) = 100 decisions ~ 8 s of game time, so a terminal 560 decisions away
        // arrives multiplied by ~0.004. A per-step cost is charged immediately at every
        // step and therefore does not depend on that propagation, which is exactly why it
        // is the right instrument at a short horizon.
        public float stepPenalty = -0.00005f;
        [Tooltip("Differential proximity reward scale. v2: replaces absolute proximity " +
                 "(which rewarded idling near the ball) with (prevDist - curDist) * scale " +
                 "so an agent approaching faster than its teammate earns positive reward.")]
        // v3 (2026-08-28): 0.002 -> 0.02. Differential proximity TELESCOPES - the
        // per-step deltas over a chase sum to scale * (startDist - endDist), so the
        // whole 10.44 m sprint in the movement probe paid 0.002 * 10.44 = 0.021,
        // while facingAlignment paid up to 0.0002 * 400 steps = 0.080. Aiming at the
        // ball out-earned arriving at it ~4:1, and the measured policy did exactly
        // that: it circle-strafed (lateral 0.314 vs forward 0.285) to hold the nose
        // on the ball, never boosted, and covered 13% of the distance. See
        // Agent_PlayMode_MovementProbe RAW ACTIONS output.
        public float ballProximityScale = 0.02f;
        [Tooltip("Use the v2 differential-proximity mode. Set false for the legacy absolute-proximity mode.")]
        public bool useDifferentialProximity = true;
        [Tooltip("Per-step reward for pointing the eye axis at the ball. Deliberately " +
                 "small: facing is INSTRUMENTAL, not a goal, and the ray sensors plus " +
                 "the vector obs already supply direction. Paid every step, so it " +
                 "outgrows the telescoping proximity term easily - at 0.0002 it was " +
                 "the dominant dense term and taught strafing over running.")]
        // v5 (2026-09-07): 0.00005 -> 0. Retired, not merely shrunk. The signed bearing
        // to the ball became an OBSERVATION on 2026-08-28 (it replaced the world eye
        // axis, which the body frame had made constant), so facing is now something the
        // policy can read directly and steer on. Paying for it as well is paying for an
        // input. Its ceiling was 0.45 per episode - under a goal, so it never tripped the
        // budget test, but it is the term that historically taught strafe-over-run.
        public float facingAlignmentScale = 0f;
        [Tooltip("Reward per step for ball velocity toward the opponent goal (the 'shoot goalward' gradient).")]
        // v5 (2026-09-07): 0.001 -> 0.0001. BUDGET FIX, and the largest one in the table.
        // This is charged every physics step against a 9000-step cap, so 0.001 ceilings at
        // +9.0 per episode - SEVEN AND A HALF TIMES what scoring pays (+1.2). A policy
        // maximising return under that table should farm ball-goal velocity and never
        // bother finishing, which is a more direct account of this project's signature
        // "learns not to lose, never learns to win" plateau than the curriculum story it
        // was attributed to. At 0.0001 the ceiling is 0.9, comfortably under a goal.
        // See Agent_EditMode_RewardBudget, which now fails if any term regains a ceiling
        // above the largest terminal reward.
        public float ballToGoalVelocityScale = 0.0001f;

        [Tooltip("Use the v6 POTENTIAL-BASED ball->goal term (reward the decrease in the " +
                 "ball's distance to the attacking goal) instead of the legacy velocity " +
                 "rate. Telescopes, so it cannot be farmed and cannot outgrow the pitch.")]
        public bool useDifferentialBallToGoal = true;

        /// <summary>
        /// Scale for the potential-based ball->goal term. Telescopes to
        /// `scale * (startDist - endDist)`, so its episode ceiling is set by the PITCH
        /// (54 units long), not by the step count: 0.01 * 54 = 0.54, comfortably under a
        /// goal, and it stays there no matter how long the episode runs.
        ///
        /// It can safely be ~100x the velocity scale it replaces because potential-based
        /// shaping provably preserves the optimal policy at any magnitude (Ng, Harada &amp;
        /// Russell 1999). That property is what lets this restore a real learning gradient
        /// - which matters because at gamma 0.99 a terminal reward is 1125 decisions from
        /// episode start and arrives discounted by ~1e-5.
        /// </summary>
        [Tooltip("Scale for the potential-based ball->goal term. Telescopes to " +
                 "scale * distanceClosed, so the ceiling is bounded by pitch length.")]
        public float ballToGoalProgressScale = 0.02f;
        [Tooltip("Penalty scale on per-step action change (anti-twitch; smooth, deliberate movement). " +
                 "v2 halved from 0.001: hard cuts are *correct* for soccer (cutting inside the box) " +
                 "and the old penalty was teaching the brain to be smooth and idle.")]
        public float actionJitterScale = 0.0004f;
        [Tooltip("Penalty scale for lingering within 0.8m of a wall (cures wall-hugging).")]
        // v5 (2026-09-07): 0.0005 -> 0.00005. Budget fix: at 0.0005 the ceiling is -4.5
        // per episode, 4.5x the cost of CONCEDING A GOAL (-1.0). Wall-hugging is a real
        // pathology worth a nudge, but at the old scale a policy should rationally let
        // the opponent score rather than spend an episode near a boundary.
        public float wallProximityPenalty = 0.00005f;
        [Tooltip("Penalty per step (only for the team that last touched the ball) while the ball sits " +
                 "inside a corner zone. v2: team-aware - used to bleed both teams regardless of fault, " +
                 "which let a corner-creator escape penalty while the defender suffered.")]
        // v5 (2026-09-07): 0.0006 -> 0.00006. Budget fix: ceiling was -5.4 per episode,
        // 5.4x conceding. Same reasoning as wallProximityPenalty above.
        public float cornerBallPenalty = 0.00006f;
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
