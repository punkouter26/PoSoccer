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
            Agent_Log.Info($"[Goal] Ball entered {name} - {owningTeam} concedes");
            env?.OnGoalScored(owningTeam);
        }
    }
}
