using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Net trigger volume. When the ball enters, the owning team concedes.
    /// Attach to each goal with a trigger BoxCollider2D; the goal mouth spans local Y
    /// so the curriculum can scale goal width via localScale.y.
    /// </summary>
    [RequireComponent(typeof(BoxCollider2D))]
    public sealed class Reward_GoalTrigger : MonoBehaviour
    {
        [Tooltip("Team that DEFENDS this net (concedes when the ball enters).")]
        public Agent_Soccer.Team owningTeam = Agent_Soccer.Team.Blue;
        public Agent_EnvController env;

        void OnTriggerEnter2D(Collider2D other)
        {
            if (!other.CompareTag("Ball")) return;
#if UNITY_EDITOR
            // Editor only. Four headless env players x 16 pitches at time_scale 20
            // write this line thousands of times a minute into the player log during
            // training, for an event the trainer already records as a statistic.
            Debug.Log($"[Goal] Ball entered {name} - {owningTeam} concedes");
#endif
            // `env != null`, not `env?.` - Unity overrides == so a DESTROYED
            // controller compares equal to null, while ?. uses C# reference equality
            // and would happily call into it. The window is real: a ball can cross a
            // trigger on the frame a scene unload is already in progress.
            if (env != null) env.OnGoalScored(owningTeam);
        }
    }
}
