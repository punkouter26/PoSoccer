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
        float _unstickJitter;

        // v2: strength knob for curriculum training. POSOCCER_BOT_STRENGTH
        // env-var scales drive power, boost aggression, and turns off the
        // support-positioning logic at low values so the brain gets a soft
        // curriculum instead of a single deterministic strong opponent.
        float _strength = 1f;

        void Awake()
        {
            // Desynchronize unstick timing between bots so they don't re-jam together.
            _unstickJitter = (GetHashCode() & 3) * 0.3f;

            // Curriculum strength (v2): scale everything by POSOCCER_BOT_STRENGTH.
            // Clamped to [0, 1]; default 1.0 = full strength (v1 behaviour).
            // Training scripts set this via env var before launching the player.
            string raw = System.Environment.GetEnvironmentVariable("POSOCCER_BOT_STRENGTH");
            if (!string.IsNullOrEmpty(raw) && float.TryParse(raw, out float parsed))
                _strength = Mathf.Clamp01(parsed);
        }

        /// <summary>Compute [move, turn, boost] for the given agent state.</summary>
        public Vector4 ComputeActions(Rigidbody2D self, Rigidbody2D ball, Transform opponentGoal,
            Rigidbody2D teammate = null, Rigidbody2D nearestOpponent = null)
        {
            if (ball == null) return Vector4.zero;

            Vector2 toBall = ball.position - self.position;
            Vector2 target = ball.position;

            // Closest man presses: if the teammate is clearly nearer the ball, take a
            // support position between the ball and our own goal instead of piling in.
            // v2: disabled when strength < 0.5 so a weak bot double-teams with its
            // teammate, leaving the brain's chosen attacker one-on-one with the ball.
            if (_strength >= 0.5f && teammate != null && opponentGoal != null)
            {
                float myDist = toBall.magnitude;
                float mateDist = Vector2.Distance(teammate.position, ball.position);
                if (mateDist + 0.5f < myDist)
                {
                    Vector2 c = opponentGoal.parent != null
                        ? (Vector2)opponentGoal.parent.position : Vector2.zero;
                    Vector2 ownGoal = c * 2f - (Vector2)opponentGoal.position;
                    Vector2 toOwnGoal = (ownGoal - ball.position).normalized;
                    Vector2 lateral = Vector2.Perpendicular(toOwnGoal) * (1.8f * _flankSide);
                    target = ball.position + toOwnGoal * 3f + lateral;
                    return Steer(self, target, opponentGoal, allowBoost: false);
                }
            }

            // Corner-craft: never press straight into a corner pocket (that is what
            // wedges the ball). Approach from behind the ball ALONG the wall and
            // sweep it toward open field, preferring the exit that also moves play
            // toward the goal we attack.
            // v2: disabled at low strength - weak bot just chases.
            if (opponentGoal != null && _strength >= 0.5f)
            {
                Vector2 center = opponentGoal.parent != null
                    ? (Vector2)opponentGoal.parent.position : Vector2.zero;
                Vector2 halfExt = interiorHalfExtents + new Vector2(0.6f, 0.6f);
                Vector2 ballLocal = ball.position - center;
                float cornerDist = Mathf.Max(
                    halfExt.x - Mathf.Abs(ballLocal.x),
                    halfExt.y - Mathf.Abs(ballLocal.y));
                if (cornerDist < 2.4f)
                {
                    Vector2 escapeX = new(-Mathf.Sign(ballLocal.x), 0f);
                    Vector2 escapeY = new(0f, -Mathf.Sign(ballLocal.y));
                    Vector2 toGoal = ((Vector2)opponentGoal.position - ball.position).normalized;
                    Vector2 escape = Vector2.Dot(escapeX, toGoal) > Vector2.Dot(escapeY, toGoal)
                        ? escapeX : escapeY;
                    target = ball.position - escape * 0.7f;
                    return Steer(self, target, opponentGoal, allowBoost: false);
                }
            }

            // Boost-shot: already behind the ball on the ball->goal axis and facing
            // it - burst through the contact so body speed becomes shot speed.
            // v2: requires full strength - weak bot can't execute the timing.
            if (opponentGoal != null && toBall.magnitude < 1.1f && _strength >= 0.8f)
            {
                Vector2 goalDir = ((Vector2)opponentGoal.position - ball.position).normalized;
                bool behindBall = Vector2.Dot(toBall.normalized, goalDir) > 0.75f;
                float facingErr = Vector2.SignedAngle(self.transform.up, toBall);
                if (behindBall && Mathf.Abs(facingErr) < 20f)
                    return new Vector4(1f, 0f, Mathf.Clamp(facingErr / 45f, -1f, 1f), 1f);
            }

            // Shoulder-charge: an opponent parked between us and a slow ball gets
            // bumped off it - momentum is the only tackle this game has.
            if (nearestOpponent != null && ball.linearVelocity.sqrMagnitude < 1f)
            {
                Vector2 toFoe = nearestOpponent.position - self.position;
                bool foeBetween = toBall.magnitude < 3f
                    && toFoe.magnitude < toBall.magnitude
                    && Vector2.Dot(toFoe.normalized, toBall.normalized) > 0.85f;
                float chargeErr = Vector2.SignedAngle(self.transform.up, toFoe);
                if (foeBetween && toFoe.magnitude < 2f && Mathf.Abs(chargeErr) < 30f)
                    return new Vector4(1f, 0f, Mathf.Clamp(chargeErr / 45f, -1f, 1f), 0.8f);
            }

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

            if (Time.time - _jamSince > unstickAfter + _unstickJitter && Time.time > _flankUntil)
            {
                _flankSide = -_flankSide;   // alternate sides so mirrored bots desync
                _flankPoint = ball.position
                    + Vector2.Perpendicular(toBall.normalized) * (1.8f * _flankSide);
                _flankUntil = Time.time + 0.9f;
                _jamSince = float.PositiveInfinity;
            }
            if (Time.time < _flankUntil) target = _flankPoint;

            return Steer(self, target, opponentGoal, allowBoost: true);
        }

        Vector4 Steer(Rigidbody2D self, Vector2 target, Transform opponentGoal, bool allowBoost)
        {
            // Never chase points inside the walls (that is how wall-jams start).
            // Pitch-local clamp so 16-grid clones each clamp around their own pitch.
            Vector2 center = opponentGoal != null && opponentGoal.parent != null
                ? (Vector2)opponentGoal.parent.position : Vector2.zero;
            target.x = Mathf.Clamp(target.x,
                center.x - interiorHalfExtents.x, center.x + interiorHalfExtents.x);
            target.y = Mathf.Clamp(target.y,
                center.y - interiorHalfExtents.y, center.y + interiorHalfExtents.y);

            Vector2 toTarget = target - self.position;
            float dist = toTarget.magnitude;
            float signedAngle = Vector2.SignedAngle(self.transform.up, toTarget);

            // v2: at low strength, the bot's turning is sloppy and imprecise.
            // Turn authority scales with strength so a 0.3-strength bot is beatable
            // even when the brain has converged on its v1 plateau strategy.
            float turn = Mathf.Clamp(signedAngle / 45f * _strength, -1f, 1f);

            // Drive is still primarily forward - sideways travel is genuinely slower
            // for a real body, so strafing is a correction, not a travel mode.
            Vector2 dir = dist > 0.001f ? toTarget / dist : Vector2.zero;
            float align = Vector2.Dot(dir, self.transform.up);
            float forward = Mathf.Abs(signedAngle) < 90f ? Mathf.Max(0.4f, align) : 0.2f;
            float lateral = Mathf.Clamp(Vector2.Dot(dir, self.transform.right), -0.6f, 0.6f);

            // Decelerate into the target instead of orbiting it. The probe measured
            // an 18 m path for a 10.4 m approach; braking early fixes that.
            float approach = Mathf.Max(0.3f, Mathf.Clamp01(dist / 1.5f));
            forward *= approach;
            lateral *= approach;

            // v2: drive authority scales with strength. A 0.3-strength bot coasts
            // around at half speed, easy for the brain to push past.
            forward *= _strength;
            lateral *= _strength;

            float boost = allowBoost && Mathf.Abs(signedAngle) < driveAngleDeg && dist > 2f
                ? boostAggression * _strength : 0f;

            return new Vector4(forward, lateral, turn, boost);
        }
    }
}
