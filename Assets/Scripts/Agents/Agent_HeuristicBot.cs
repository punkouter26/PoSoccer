using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Phase 1 rule-based target-tracking bot (PRD Training Pipeline).
    /// Chases the ball, lines up behind it toward the opponent goal, and pushes.
    /// Includes a flanking unstick maneuver: when the ball sits still against a
    /// boundary with the bot on top of it (mirror-bot deadlock), swing to a lateral
    /// approach point instead of pressing straight in.
    ///
    /// In a squad (2026-09-28) it plays a team role from Agent_TeamRoles: goalie,
    /// defender or support attacker hold their posts and only the presser chases.
    /// Alone on its side it has no role and plays exactly as it always did.
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
        [Tooltip("Max distance at which the bot perceives an opponent. 0 = unlimited, " +
                 "which is the historical benchmark behaviour every result to date was " +
                 "measured against - leave it at 0 unless you are deliberately running " +
                 "a fair-information experiment. Seeded from POSOCCER_BOT_VISION.")]
        public float perceptionRadius = 0f;
        [Tooltip("1 = the benchmark bot every grade in docs/training-history.md was taken " +
                 "against. 2 adds lead pursuit (run to where the ball will be) and bank shots " +
                 "off the side walls. A grade against 2 is a DIFFERENT measurement and is never " +
                 "comparable with one against 1. Seeded from POSOCCER_BOT_VERSION (red) or " +
                 "POSOCCER_BOT_VERSION_BLUE (blue).")]
        [Range(1, 2)] public int version = BENCHMARK_VERSION;

        /// <summary>The version the 80% benchmark bar is defined against. Do not change.</summary>
        public const int BENCHMARK_VERSION = 1;

        float _jamSince = float.PositiveInfinity;
        float _flankUntil;
        Vector2 _flankPoint;
        float _flankSide = 1f;
        float _unstickJitter;

        // v2: strength knob for curriculum training. Scales drive power, boost
        // aggression, and turns off the support-positioning logic at low values
        // so the brain gets a soft curriculum instead of a single deterministic
        // strong opponent. Seeded from POSOCCER_BOT_STRENGTH; during training
        // Agent_EnvController overwrites it every episode from the trainer's
        // 'bot_strength' environment parameter (see SetStrength).
        float _strength = 1f;

        /// <summary>Current opponent difficulty in [0, 1]. 1 = full-strength v1 behaviour.</summary>
        public float Strength => _strength;

        /// <summary>
        /// Curriculum hook: set the bot's difficulty for the coming episode.
        /// Called from <see cref="Agent_EnvController.ResetPitch"/> so a lesson change
        /// takes effect on the next kickoff rather than mid-play.
        /// </summary>
        public void SetStrength(float value) => _strength = Mathf.Clamp01(value);

        void Awake()
        {
            // Desynchronize unstick timing between bots so they don't re-jam together.
            _unstickJitter = (GetHashCode() & 3) * 0.3f;

            // Curriculum strength (v2): scale everything by POSOCCER_BOT_STRENGTH.
            // Clamped to [0, 1]; default 1.0 = full strength (v1 behaviour).
            // Training scripts set this via env var before launching the player.
            if (Agent_RunConfig.TryGetFloat(Agent_RunConfig.BOT_STRENGTH, out float parsed))
                _strength = Mathf.Clamp01(parsed);

            // Optional fair-information handicap (v3). The bot reads opponents straight
            // off the transforms at unlimited range, while the brain's ray sensor only
            // guarantees detection within ~1.9 units. Capping this radius is how you ask
            // "is the 80% bar reachable, or is it just the perfect-information gap?".
            // Default 0 (unlimited) keeps every historical result comparable.
            if (Agent_RunConfig.TryGetFloat(Agent_RunConfig.BOT_VISION, out float radius))
                perceptionRadius = Mathf.Max(0f, radius);

            // Bot v2 (2026-10-11). Per side, so a bot-vs-bot run can field v2 against v1
            // and measure what the new skills are worth. Unset = the benchmark bot.
            var owner = GetComponent<Agent_Soccer>();
            if (owner != null) version = VersionFor(owner.team, version);
        }

        /// <summary>
        /// The bot version the launcher asked for on <paramref name="team"/>'s side, or
        /// <paramref name="fallback"/> when the variable is unset or out of range. Static
        /// so Agent_EvalStats stamps the same answer the bots acted on.
        /// </summary>
        public static int VersionFor(Agent_Soccer.Team team, int fallback = BENCHMARK_VERSION)
        {
            string variable = team == Agent_Soccer.Team.Blue
                ? Agent_RunConfig.BOT_VERSION_BLUE : Agent_RunConfig.BOT_VERSION;
            if (!Agent_RunConfig.TryGetInt(variable, out int parsed)) return fallback;
            return parsed >= 1 && parsed <= 2 ? parsed : fallback;
        }

        [Tooltip("How close (m) to its post a goalie, defender or support attacker has to be " +
                 "before it stops moving and just turns to watch the ball.")]
        public float postTolerance = 0.8f;

        /// <summary>
        /// Compute [forward, lateral, turn, boost] for the given agent state.
        ///
        /// <paramref name="role"/>, <paramref name="post"/> and <paramref name="ballDuty"/>
        /// come from Agent_EnvController's team-role assignment (Agent_TeamRoles). When the
        /// player has a role and its job is NOT the ball, it holds its post instead of
        /// chasing. Role None - a player alone on its side, or a caller that passes no role -
        /// leaves the bot exactly as it was before roles existed.
        /// </summary>
        public Vector4 ComputeActions(Rigidbody2D self, Rigidbody2D ball, Transform opponentGoal,
            Rigidbody2D teammate = null, Rigidbody2D nearestOpponent = null,
            Agent_TeamRoles.Role role = Agent_TeamRoles.Role.None, Vector2 post = default,
            bool ballDuty = true)
        {
            if (ball == null) return Vector4.zero;

            // Fair-information handicap: drop an opponent the bot should not be able to
            // see. Only the shoulder-charge consults nearestOpponent, so this narrows
            // the bot's perfect-information edge without touching its ball skills.
            if (perceptionRadius > 0f && nearestOpponent != null &&
                (nearestOpponent.position - self.position).sqrMagnitude >
                    perceptionRadius * perceptionRadius)
            {
                nearestOpponent = null;
            }

            Vector2 toBall = ball.position - self.position;
            Vector2 target = ball.position;

            // Team roles (2026-09-28): goalie, defender or support attacker whose job right
            // now is not the ball - hold the post. Same strength gate as the support logic
            // below: a weak curriculum bot still swarms, so the learner's first lessons
            // keep the easy opponent they were designed around.
            if (_strength >= 0.5f && role != Agent_TeamRoles.Role.None && !ballDuty)
            {
                // Off the ball, so any jam timer is about a contact that has ended. Left
                // running, a bot handed the ball back while still next to it would flank
                // on the very first tick.
                _jamSince = float.PositiveInfinity;
                return HoldPost(self, post, ball, opponentGoal);
            }

            // Closest man presses: if the teammate is clearly nearer the ball, take a
            // support position between the ball and our own goal instead of piling in.
            // v2: disabled when strength < 0.5 so a weak bot double-teams with its
            // teammate, leaving the brain's chosen attacker one-on-one with the ball.
            // Superseded by team roles whenever the caller supplies them; kept for any
            // caller that does not.
            if (_strength >= 0.5f && role == Agent_TeamRoles.Role.None
                && teammate != null && opponentGoal != null)
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

            // Where the ball should go. Normally the goal; a teammate instead when the lane
            // to goal is blocked and that teammate is open and better placed (2026-10-11).
            // Everything below that used to aim at the goal aims here, so a pass is struck
            // exactly the way a shot is. Same strength gate as the roles: a weak bot
            // neither holds a shape nor passes. Never in 1v1 - there is no teammate.
            Vector2 aim = opponentGoal != null ? (Vector2)opponentGoal.position : ball.position;
            if (_strength >= 0.5f && role != Agent_TeamRoles.Role.None && opponentGoal != null
                && ShouldPass(ball.position, aim, teammate, nearestOpponent, out Vector2 passTarget))
            {
                aim = passTarget;
            }
            // v2: no pass on, and an opponent in the lane to goal - go round it off a side
            // wall. Full strength only, like the boost-shot that has to strike it.
            else if (version >= 2 && _strength >= 0.8f && opponentGoal != null
                && nearestOpponent != null)
            {
                Vector2 pitchCentre = opponentGoal.parent != null
                    ? (Vector2)opponentGoal.parent.position : Vector2.zero;
                if (TryBankAim(ball.position, aim, nearestOpponent.position, pitchCentre,
                        BallWallHalfExtents, Agent_PitchGuard.SideBounciness, out Vector2 bankAim))
                {
                    aim = bankAim;
                }
            }

            // Boost-shot: already behind the ball on the ball->goal axis and facing
            // it - burst through the contact so body speed becomes shot speed.
            // v2: requires full strength - weak bot can't execute the timing.
            if (opponentGoal != null && toBall.magnitude < 1.1f && _strength >= 0.8f)
            {
                Vector2 goalDir = (aim - ball.position).normalized;
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

            // v2 lead pursuit: a moving ball is chased to where it will be, walls
            // included, not to where it is. Only the long chase - close control below
            // still works off the ball itself.
            if (version >= 2 && _strength >= 0.8f && toBall.magnitude >= controlDistance
                && ball.linearVelocity.sqrMagnitude > LEAD_MIN_BALL_SPEED * LEAD_MIN_BALL_SPEED)
            {
                Vector2 pitchCentre = opponentGoal != null && opponentGoal.parent != null
                    ? (Vector2)opponentGoal.parent.position : Vector2.zero;
                target = InterceptPoint(self.position, LEAD_CHASE_SPEED, ball.position,
                    ball.linearVelocity, ball.linearDamping, pitchCentre, BallWallHalfExtents,
                    Agent_PitchGuard.SideBounciness);
            }

            // When close to the ball, aim for the point behind the ball on the
            // ball->goal line so pushes travel goalward instead of poking around.
            if (opponentGoal != null && toBall.magnitude < controlDistance)
            {
                Vector2 goalDir = (aim - ball.position).normalized;
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

        // ── v2 skills ───────────────────────────────────────────────────────

        /// <summary>Ball speed (m/s) under which leading it is not worth the arithmetic.</summary>
        internal const float LEAD_MIN_BALL_SPEED = 2f;
        /// <summary>Speed (m/s) the bot is assumed to chase at. Its measured top speed is 4.74.</summary>
        internal const float LEAD_CHASE_SPEED = 4.5f;
        /// <summary>Seconds of ball flight the intercept search looks ahead.</summary>
        internal const float LEAD_HORIZON = 2f;
        /// <summary>A bank shot is tried only from this far out (m) ...</summary>
        internal const float BANK_MIN_RANGE = 6f;
        /// <summary>... and no farther than this: beyond it the ball dies on the second leg.</summary>
        internal const float BANK_MAX_RANGE = 22f;
        /// <summary>The bounce must land this far (m) from a corner; the corner arcs are not flat walls.</summary>
        internal const float BANK_CORNER_MARGIN = 3f;
        const float BALL_RADIUS = 0.11f;

        /// <summary>
        /// Half extents of the box the BALL'S CENTRE bounces inside. interiorHalfExtents is
        /// the pitch pulled in by 1 m for steering (Agent_EnvController.ApplyBotBounds).
        /// </summary>
        Vector2 BallWallHalfExtents =>
            interiorHalfExtents + new Vector2(1f - BALL_RADIUS, 1f - BALL_RADIUS);

        /// <summary>
        /// Where a chaser moving at <paramref name="chaseSpeed"/> first meets the ball: the
        /// ball is stepped forward under its own damping and reflected off the four walls
        /// (the same terms Agent_ShotTracer integrates), and the first point the chaser can
        /// reach in time is the answer. Past the horizon it returns the ball's last
        /// projected position. Static and pure so it can be pinned without a scene.
        /// </summary>
        public static Vector2 InterceptPoint(Vector2 self, float chaseSpeed, Vector2 ballPos,
            Vector2 ballVelocity, float ballDamping, Vector2 pitchCentre, Vector2 wallHalfExtents,
            float bounce, float horizon = LEAD_HORIZON)
        {
            const float step = 0.05f;
            Vector2 position = ballPos;
            Vector2 velocity = ballVelocity;

            for (float elapsed = step; elapsed <= horizon + 0.0001f; elapsed += step)
            {
                velocity /= 1f + ballDamping * step;
                Vector2 local = position + velocity * step - pitchCentre;
                if (Mathf.Abs(local.x) > wallHalfExtents.x)
                {
                    local.x = Mathf.Sign(local.x) * wallHalfExtents.x;
                    velocity.x = -velocity.x * bounce;
                }
                if (Mathf.Abs(local.y) > wallHalfExtents.y)
                {
                    local.y = Mathf.Sign(local.y) * wallHalfExtents.y;
                    velocity.y = -velocity.y * bounce;
                }
                position = pitchCentre + local;

                if ((position - self).magnitude <= chaseSpeed * elapsed) return position;
            }
            return position;
        }

        /// <summary>
        /// A shot off a side wall, for when an opponent stands in the direct lane.
        ///
        /// The walls keep the ball's speed along the wall and return
        /// <paramref name="bounce"/> of its speed into it (Agent_PitchGuard), so the angle
        /// out is steeper than the angle in and the textbook mirror image is wrong. The
        /// ball lands on the goal when it is struck at the goal's reflection pushed
        /// 1/bounce times farther behind the wall; that point is <paramref name="aim"/>.
        /// False when the direct lane is open, the range is wrong, the bounce would land in
        /// a corner arc, or the opponent also covers the way to both walls.
        /// </summary>
        public static bool TryBankAim(Vector2 ballPos, Vector2 goal, Vector2 foePos,
            Vector2 pitchCentre, Vector2 wallHalfExtents, float bounce, out Vector2 aim)
        {
            aim = goal;
            if (bounce <= 0.01f) return false;

            Vector2 toGoal = goal - ballPos;
            float goalDist = toGoal.magnitude;
            if (goalDist < BANK_MIN_RANGE || goalDist > BANK_MAX_RANGE) return false;

            Vector2 toFoe = foePos - ballPos;
            float foeDist = toFoe.magnitude;
            if (foeDist < 0.001f) return false;
            Vector2 foeDir = toFoe / foeDist;
            bool laneBlocked = foeDist < PASS_PRESSURE_RANGE
                && Vector2.Dot(foeDir, toGoal / goalDist) > 0.8f;
            if (!laneBlocked) return false;

            bool found = false;
            float bestPath = float.PositiveInfinity;
            for (int side = -1; side <= 1; side += 2)
            {
                float wallX = pitchCentre.x + side * wallHalfExtents.x;
                float toWall = Mathf.Abs(wallX - ballPos.x);
                float wallToGoal = Mathf.Abs(wallX - goal.x) / bounce;
                if (toWall < 0.5f) continue;   // already on this wall: nothing to bank off

                float hitY = ballPos.y + (goal.y - ballPos.y) * toWall / (toWall + wallToGoal);
                if (Mathf.Abs(hitY - pitchCentre.y) > wallHalfExtents.y - BANK_CORNER_MARGIN) continue;

                Vector2 hit = new(wallX, hitY);
                Vector2 firstLeg = hit - ballPos;
                float firstLegLength = firstLeg.magnitude;
                if (foeDist < firstLegLength && Vector2.Dot(foeDir, firstLeg / firstLegLength) > 0.8f)
                {
                    continue;   // the opponent covers this wall too
                }

                float path = firstLegLength + (goal - hit).magnitude;
                if (path >= bestPath) continue;
                bestPath = path;
                aim = new Vector2(wallX + side * wallToGoal, goal.y);
                found = true;
            }
            return found;
        }

        /// <summary>Teammate nearer than this (m) is not worth a pass - just keep going.</summary>
        internal const float PASS_MIN_RANGE = 4f;
        /// <summary>Teammate farther than this (m) is out of reach: the ball dies on the way.</summary>
        internal const float PASS_MAX_RANGE = 14f;
        /// <summary>The teammate must be this much (m) nearer the goal than the ball is.</summary>
        internal const float PASS_ADVANTAGE = 1.5f;
        /// <summary>Inside this range (m) of goal, shoot instead.</summary>
        internal const float PASS_SHOOT_RANGE = 7f;
        /// <summary>An opponent this close (m) and in the way counts as blocking the lane to goal.</summary>
        internal const float PASS_PRESSURE_RANGE = 5f;

        /// <summary>
        /// Pass or carry on? Pass only when all of it holds: we are not already in shooting
        /// range, an opponent stands in the lane to goal, the teammate is in range and
        /// clearly better placed, and that same opponent is not also sitting on the line
        /// to the teammate. The target leads the teammate by 0.4 s of its own movement.
        /// Static and pure so the rule can be pinned without a scene.
        /// </summary>
        internal static bool ShouldPass(Vector2 ballPos, Vector2 goal, Rigidbody2D teammate,
            Rigidbody2D nearestOpponent, out Vector2 passTarget)
        {
            passTarget = goal;
            if (teammate == null || nearestOpponent == null) return false;
            return ShouldPass(ballPos, goal, teammate.position, teammate.linearVelocity,
                nearestOpponent.position, out passTarget);
        }

        public static bool ShouldPass(Vector2 ballPos, Vector2 goal, Vector2 matePos,
            Vector2 mateVelocity, Vector2 foePos, out Vector2 passTarget)
        {
            passTarget = goal;

            Vector2 toGoal = goal - ballPos;
            float goalDist = toGoal.magnitude;
            if (goalDist < PASS_SHOOT_RANGE) return false;

            Vector2 toMate = matePos - ballPos;
            float mateDist = toMate.magnitude;
            if (mateDist < PASS_MIN_RANGE || mateDist > PASS_MAX_RANGE) return false;
            if ((goal - matePos).magnitude > goalDist - PASS_ADVANTAGE) return false;

            Vector2 toFoe = foePos - ballPos;
            float foeDist = toFoe.magnitude;
            if (foeDist < 0.001f) return false;
            Vector2 foeDir = toFoe / foeDist;

            bool laneBlocked = foeDist < PASS_PRESSURE_RANGE
                && Vector2.Dot(foeDir, toGoal / goalDist) > 0.8f;
            if (!laneBlocked) return false;

            bool passBlocked = foeDist < mateDist && Vector2.Dot(foeDir, toMate / mateDist) > 0.9f;
            if (passBlocked) return false;

            passTarget = matePos + mateVelocity * 0.4f;
            return true;
        }

        /// <summary>
        /// Walk to the post, then stop there and turn to face the ball. Steer alone would
        /// keep creeping at its 0.3 minimum approach speed and orbit the spot.
        /// </summary>
        Vector4 HoldPost(Rigidbody2D self, Vector2 post, Rigidbody2D ball, Transform opponentGoal)
        {
            if ((post - self.position).sqrMagnitude > postTolerance * postTolerance)
            {
                return Steer(self, post, opponentGoal, allowBoost: false);
            }
            float facingErr = Vector2.SignedAngle(self.transform.up, ball.position - self.position);
            return new Vector4(0f, 0f, Mathf.Clamp(facingErr / 45f * _strength, -1f, 1f), 0f);
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
