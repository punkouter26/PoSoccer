using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Phase 1 rule-based target-tracking bot (PRD Training Pipeline).
    /// Chases the ball, lines up behind it toward the opponent goal, and pushes.
    /// Includes a flanking unstick maneuver: when the ball sits still against a
    /// boundary with the bot on top of it (mirror-bot deadlock), swing to a lateral
    /// approach point instead of pressing straight in.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Agent_HeuristicBot : MonoBehaviour
    {
        [Tooltip("Angular error (deg) under which the bot drives forward at full power.")]
        public float driveAngleDeg = 25f;
        [Tooltip("Distance to ball under which the bot lines up ball->goal instead of chasing.")]
        public float controlDistance = 1.5f;
        [Range(0f, 1f)] public float boostAggression = 0.5f;
        [Tooltip("Playable half extents used to keep approach points off the walls.")]
        public Vector2 interiorHalfExtents = new(5.4f, 8.4f);
        [Tooltip("Seconds of jammed ball contact before flanking.")]
        public float unstickAfter = 1.25f;

        float _jamSince = float.PositiveInfinity;
        float _flankUntil;
        Vector2 _flankPoint;
        float _flankSide = 1f;

        /// <summary>Compute [move, turn, boost] for the given agent state.</summary>
        public Vector3 ComputeActions(Rigidbody2D self, Rigidbody2D ball, Transform opponentGoal)
        {
            if (ball == null) return Vector3.zero;

            Vector2 toBall = ball.position - self.position;
            Vector2 target = ball.position;

            // When close to the ball, aim for the point behind the ball on the
            // ball->goal line so pushes travel goalward instead of poking around.
            if (opponentGoal != null && toBall.magnitude < controlDistance)
            {
                Vector2 goalDir = ((Vector2)opponentGoal.position - ball.position).normalized;
                target = ball.position - goalDir * 0.6f;
            }

            // Deadlock detection: bot on the ball but the ball is not moving.
            bool jammed = toBall.magnitude < 1.3f && ball.linearVelocity.sqrMagnitude < 0.09f;
            if (jammed && float.IsPositiveInfinity(_jamSince)) _jamSince = Time.time;
            if (!jammed) _jamSince = float.PositiveInfinity;

            if (Time.time - _jamSince > unstickAfter && Time.time > _flankUntil)
            {
                _flankSide = -_flankSide;   // alternate sides so mirrored bots desync
                _flankPoint = ball.position
                    + Vector2.Perpendicular(toBall.normalized) * (1.8f * _flankSide);
                _flankUntil = Time.time + 0.9f;
                _jamSince = float.PositiveInfinity;
            }
            if (Time.time < _flankUntil) target = _flankPoint;

            // Never chase points inside the walls (that is how wall-jams start).
            // Pitch-local clamp so 16-grid clones each clamp around their own pitch.
            Vector2 center = opponentGoal != null && opponentGoal.parent != null
                ? (Vector2)opponentGoal.parent.position : Vector2.zero;
            target.x = Mathf.Clamp(target.x,
                center.x - interiorHalfExtents.x, center.x + interiorHalfExtents.x);
            target.y = Mathf.Clamp(target.y,
                center.y - interiorHalfExtents.y, center.y + interiorHalfExtents.y);

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
