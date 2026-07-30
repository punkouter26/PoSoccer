using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Duplicates a single authored pitch into an N-instance grid at runtime
    /// (PRD: 16 parallel pitches per scene). Runtime-only so the authored scene
    /// stays minimal and no editor tooling is needed.
    /// </summary>
    public sealed class Agent_TrainingGrid : MonoBehaviour
    {
        [Tooltip("The fully-wired pitch root (env controller + agents + ball + goals).")]
        public GameObject pitchRoot;
        [Tooltip("Total pitch count including the original (PRD: 16 => 4x4 grid).")]
        public int instances = 16;
        public int columns = 4;
        [Tooltip("World-space spacing between pitch origins. Must exceed pitch bounds.")]
        public Vector2 spacing = new(26f, 18f);
        [Tooltip("Only duplicate when a trainer is connected; play-mode testing keeps 1 pitch.")]
        public bool onlyWhenTraining = true;

        void Awake()
        {
            if (pitchRoot == null || instances <= 1) return;
            // Clone for training (trainer connected) and for headless evaluation runs.
            if (onlyWhenTraining && !Unity.MLAgents.Academy.Instance.IsCommunicatorOn
                && !Agent_EvalStats.EvalMode) return;

            for (int i = 1; i < instances; i++)
            {
                int col = i % columns;
                int row = i / columns;
                Vector3 offset = new(col * spacing.x, row * spacing.y, 0f);
                Instantiate(pitchRoot, pitchRoot.transform.position + offset,
                    Quaternion.identity, transform);
            }
        }
    }
}
