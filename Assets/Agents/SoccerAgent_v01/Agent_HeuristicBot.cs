using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Phase 1 rule-based target-tracking bot (PRD Training Pipeline).
    /// Chases the ball, turns toward it, and pushes it at the opponent goal.
    /// Used as the heuristic policy when BehaviorType = Heuristic Only, giving the
    /// PPO learner a non-trivial opponent for the initial policy bootstrap.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Agent_HeuristicBot : MonoBehaviour
    {
        [Tooltip("Angular error (deg) under which the bot drives forward at full power.")]
        public float driveAngleDeg = 25f;
        [Tooltip("Distance to ball under which the bot lines up ball->goal instead of chasing.")]
        public float controlDistance = 1.5f;
        [Range(0f, 1f)] public float boostAggression = 0.5f;

        /// <summary>Compute [move, turn, boost] for the given agent state.</summary>
        public Vector3 ComputeActions(Rigidbody2D self, Rigidbody2D ball, Transform opponentGoal)
        {
            if (ball == null) return Vector3.zero;

            Vector2 toBall = ball.position - self.position;
            Vector2 target = ball.position;

            // When close to the ball, aim for the point behind the ball on the ball->goal line
            // so pushes travel goalward instead of just poking the ball around.
            if (opponentGoal != null && toBall.magnitude < controlDistance)
            {
                Vector2 goalDir = ((Vector2)opponentGoal.position - ball.position).normalized;
                target = ball.position - goalDir * 0.6f;
            }

            Vector2 toTarget = target - self.position;
            float signedAngle = Vector2.SignedAngle(self.transform.up, toTarget);

            float turn = Mathf.Clamp(signedAngle / 45f, -1f, 1f);
            float move = Mathf.Abs(signedAngle) < driveAngleDeg ? 1f : 0.25f;
            float boost = (Mathf.Abs(signedAngle) < driveAngleDeg && toTarget.magnitude > 2f)
                ? boostAggression : 0f;

            return new Vector3(move, turn, boost);
        }
    }
}
