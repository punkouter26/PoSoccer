using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// One archived brain: a model file plus the provenance needed to say what it
    /// is and what it was worth.
    ///
    /// WHY THIS IS NOT JUST Reward_Settings. A Reward_Settings is a SLOT - the
    /// live brain a personality is currently fielding - and update-model.ps1
    /// overwrites its .onnx in place by design (GUID-stable slot per personality).
    /// That is exactly right for shipping and exactly wrong for comparison: the
    /// moment p22 lands, p21 is gone and the two can never be put side by side.
    ///
    /// A checkpoint is the other thing: an immutable exhibit. Point it at a copy
    /// of the model that will not be overwritten, record the run id, the step
    /// count and the eval result THAT MODEL actually scored, and it stays
    /// comparable forever.
    ///
    /// THE DATE FIELD IS LOAD-BEARING, NOT DECORATION. CLAUDE.md's hardest-won
    /// landmine is that the body-frame observation fix on 2026-08-28 kept the
    /// tensor shape and changed the MEANING, so every earlier .onnx loads without
    /// a single warning and silently reads a different world. A pre-2026-08-28
    /// checkpoint is not comparable to a later one no matter what its eval JSON
    /// says. <see cref="IsPreBodyFrame"/> answers that question from this asset,
    /// and Agent_Gallery labels such an exhibit rather than quietly racing it.
    /// </summary>
    [CreateAssetMenu(fileName = "Checkpoint", menuName = "PoSoccer/Checkpoint")]
    public sealed class Agent_Checkpoint : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Short caption, e.g. \"p21 · curriculum\". Shown under the pitch.")]
        public string label = "checkpoint";
        [Tooltip("One line on what changed in this run. Shown under the label.")]
        public string notes;

        [Header("Brain")]
        [Tooltip("The archived model. Leave null to exhibit whatever brain the base " +
                 "profile is currently fielding - which is how the gallery works out of " +
                 "the box, before anybody has archived anything.")]
        public Unity.InferenceEngine.ModelAsset brainModel;
        [Tooltip("Reward DNA, colour and physique this brain was trained with. Required: " +
                 "a brain exhibited against the wrong reward profile is a different agent.")]
        public Reward_Settings baseProfile;

        [Header("Provenance")]
        [Tooltip("Trainer steps behind this model.")]
        public int trainingSteps;
        [Tooltip("Run that produced it, e.g. soccer_p21curric_standard.")]
        public string trainingRunId;
        [Tooltip("Date this model was exported (yyyy-MM-dd). Compared against the " +
                 "2026-08-28 body-frame boundary - see the class docstring.")]
        public string trainedOn;
        [Tooltip("Share of eval episodes this exact model won against the full-strength " +
                 "bot. -1 = never graded.")]
        [Range(-1f, 1f)]
        public float evalWinRate = -1f;
        [Tooltip("Episode count behind evalWinRate. Below ~350 a gap under 10 points is " +
                 "noise - see the variance landmine in CLAUDE.md.")]
        public int evalEpisodes;

        /// <summary>
        /// The date on which observations moved from world frame to body frame.
        /// A model exported before this reads its inputs in a coordinate system
        /// this runtime no longer produces, at an identical tensor shape.
        /// </summary>
        public const string BODY_FRAME_BOUNDARY = "2026-08-28";

        /// <summary>The model to actually load: this asset's, or the base profile's.</summary>
        public Unity.InferenceEngine.ModelAsset ResolvedModel =>
            brainModel != null ? brainModel
            : baseProfile != null ? baseProfile.brainModel
            : null;

        /// <summary>Step count to display, falling back to the base profile's.</summary>
        public int ResolvedSteps =>
            trainingSteps > 0 ? trainingSteps
            : baseProfile != null ? baseProfile.trainingSteps
            : 0;

        /// <summary>Run id to display, falling back to the base profile's.</summary>
        public string ResolvedRunId =>
            !string.IsNullOrEmpty(trainingRunId) ? trainingRunId
            : baseProfile != null ? baseProfile.trainingRunId
            : string.Empty;

        /// <summary>Win rate to display, falling back to the base profile's. -1 = ungraded.</summary>
        public float ResolvedWinRate =>
            evalWinRate >= 0f ? evalWinRate
            : baseProfile != null ? baseProfile.evalWinRate
            : -1f;

        /// <summary>Episode count behind <see cref="ResolvedWinRate"/>.</summary>
        public int ResolvedEpisodes =>
            evalWinRate >= 0f ? evalEpisodes
            : baseProfile != null ? baseProfile.evalEpisodes
            : 0;

        /// <summary>Caption text, falling back to the base profile's player name.</summary>
        public string ResolvedLabel =>
            !string.IsNullOrEmpty(label) && label != "checkpoint" ? label
            : baseProfile != null ? baseProfile.playerName
            : name;

        /// <summary>
        /// True when this model predates the body-frame observation change and so
        /// cannot be honestly raced against a later one. Unknown dates return
        /// false: an unlabelled exhibit is not evidence of a problem, and marking
        /// every undated checkpoint as suspect would make the warning meaningless.
        /// </summary>
        public bool IsPreBodyFrame =>
            !string.IsNullOrEmpty(trainedOn)
            && string.CompareOrdinal(trainedOn, BODY_FRAME_BOUNDARY) < 0;
    }
}
