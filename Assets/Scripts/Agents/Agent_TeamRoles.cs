using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Team roles for squads of two or more: who keeps goal, who defends and who
    /// attacks, and WHERE each of them should stand. Pure geometry - no state, no
    /// Unity lifecycle - so every rule here is pinned by an EditMode test.
    /// <see cref="Agent_EnvController"/> owns the assignment (it knows the squad) and
    /// calls these for the spots.
    ///
    /// WHY (2026-09-28, user request). Every player in this project chased the ball. The
    /// scripted bot had one exception - a teammate clearly nearer the ball made it drop
    /// 3 m behind - and trained brains had none, because training was 1v1. A squad of
    /// N was N strikers.
    ///
    /// THE SPLIT, by squad size (outfield = everyone but the goalie):
    ///
    ///   1  None                          - 1v1 is untouched by all of this
    ///   2  attacker + defender           - no goalie: keeping one of two players on the
    ///                                      line hands the other team a 2v1 everywhere
    ///   3  goalie + defender + attacker
    ///   4  goalie + 1 defender + 2 attackers
    ///   5  goalie + 2 defenders + 2 attackers   (defenders = outfield / 2)
    ///
    /// The GOALIE is sticky for a whole episode: whoever starts deepest at kickoff.
    /// Swapping keepers mid-play is how two players end up both leaving the goal.
    /// Among the outfield, the player nearest the ball is the ATTACKER ON THE BALL (the
    /// presser) and only hands over when a teammate is nearer by
    /// <see cref="PRESS_HANDOFF_MARGIN"/> - without that, two players at similar range
    /// swap every tick and neither commits. The remaining outfield players fill the
    /// defender slots deepest-first, and whoever is left is a SUPPORT attacker.
    ///
    /// All spots are PITCH-LOCAL (world minus the pitch root; pitches are never rotated,
    /// only translated in the training grid) with the pitch running along Y.
    /// </summary>
    public static class Agent_TeamRoles
    {
        public enum Role
        {
            /// <summary>Alone on the side: plays everything, exactly as before roles existed.</summary>
            None = 0,
            Goalie = 1,
            Defender = 2,
            Attacker = 3,
        }

        /// <summary>A teammate must be this much nearer the ball (m) to take over pressing.</summary>
        internal const float PRESS_HANDOFF_MARGIN = 0.5f;
        /// <summary>The goalie leaves its line for a ball this close (m) to its goal.</summary>
        internal const float GOALIE_RUSH_RADIUS = 4f;
        /// <summary>How far off the goal line (m), along the line to the ball, the goalie stands.</summary>
        internal const float GOALIE_DEPTH = 1.2f;
        /// <summary>Defender stands this fraction of the way from its goal to the ball...</summary>
        internal const float DEFENDER_FRACTION = 0.4f;
        /// <summary>...but never closer than this (m) to its own goal (that is the goalie's)...</summary>
        internal const float DEFENDER_MIN_DEPTH = 4f;
        /// <summary>...and never closer than this (m) to the ball (that is the presser's).</summary>
        internal const float DEFENDER_BALL_GAP = 2f;
        /// <summary>Lateral spacing (m) between defenders when there are several.</summary>
        internal const float DEFENDER_SPREAD = 3f;
        /// <summary>Support attackers stand this far (m) ahead of the ball toward the goal they attack...</summary>
        internal const float SUPPORT_AHEAD = 5f;
        /// <summary>...and this far (m) to the side, on the open flank.</summary>
        internal const float SUPPORT_WIDTH = 4f;
        /// <summary>Spots are kept this far (m) inside the walls.</summary>
        internal const float WALL_MARGIN = 1.5f;

        public static bool HasGoalie(int squadSize) => squadSize >= 3;

        /// <summary>How many defenders a squad of this size fields.</summary>
        public static int DefenderCount(int squadSize)
        {
            if (squadSize <= 1) return 0;
            int outfield = squadSize - (HasGoalie(squadSize) ? 1 : 0);
            return outfield / 2;
        }

        /// <summary>
        /// The goalie's post: <see cref="GOALIE_DEPTH"/> off its own goal on the line to
        /// the ball, so it narrows the shooting angle, and never wider than the goal mouth.
        /// </summary>
        public static Vector2 GoalieSpot(Vector2 ballLocal, Vector2 ownGoalLocal, float goalWidth,
            Vector2 halfExtents)
        {
            Vector2 toBall = ballLocal - ownGoalLocal;
            Vector2 spot = toBall.sqrMagnitude > 0.0001f
                ? ownGoalLocal + toBall.normalized * GOALIE_DEPTH
                : ownGoalLocal;

            // Always in FRONT of the line: the direction into the pitch is toward y = 0.
            float inward = ownGoalLocal.y > 0f ? -1f : 1f;
            spot.y = ownGoalLocal.y + inward * Mathf.Max(GOALIE_DEPTH * 0.5f,
                Mathf.Abs(spot.y - ownGoalLocal.y));

            float mouth = Mathf.Max(0f, goalWidth * 0.5f - 0.3f);
            spot.x = Mathf.Clamp(spot.x, ownGoalLocal.x - mouth, ownGoalLocal.x + mouth);
            spot.y = Mathf.Clamp(spot.y, -(halfExtents.y - 0.6f), halfExtents.y - 0.6f);
            return spot;
        }

        /// <summary>True when the ball is close enough to the goal for the goalie to come for it.</summary>
        public static bool GoalieShouldRush(Vector2 ballLocal, Vector2 ownGoalLocal) =>
            (ballLocal - ownGoalLocal).sqrMagnitude < GOALIE_RUSH_RADIUS * GOALIE_RUSH_RADIUS;

        /// <summary>
        /// A defender's post: on the line from its goal to the ball, between the two -
        /// the lane a shot or a dribble has to come through. Several defenders spread
        /// across that line, <see cref="DEFENDER_SPREAD"/> apart.
        /// </summary>
        public static Vector2 DefenderSpot(Vector2 ballLocal, Vector2 ownGoalLocal, int index,
            int count, Vector2 halfExtents)
        {
            Vector2 toBall = ballLocal - ownGoalLocal;
            float dist = toBall.magnitude;
            if (dist < 0.0001f) return Clamp(ownGoalLocal, halfExtents);
            Vector2 dir = toBall / dist;

            float depth = Mathf.Max(DEFENDER_MIN_DEPTH, dist * DEFENDER_FRACTION);
            depth = Mathf.Min(depth, Mathf.Max(dist * 0.5f, dist - DEFENDER_BALL_GAP));

            Vector2 spot = ownGoalLocal + dir * depth;
            if (count > 1)
            {
                float offset = (index - (count - 1) * 0.5f) * DEFENDER_SPREAD;
                spot += new Vector2(-dir.y, dir.x) * offset;
            }
            return Clamp(spot, halfExtents);
        }

        /// <summary>
        /// A support attacker's post: ahead of the ball toward the goal it attacks, out on
        /// the flank away from the ball, where a pass or a rebound can find it. A second
        /// support attacker takes the other flank, a third goes wider, and so on.
        /// </summary>
        public static Vector2 SupportSpot(Vector2 ballLocal, Vector2 oppGoalLocal, int index,
            Vector2 halfExtents)
        {
            Vector2 toGoal = oppGoalLocal - ballLocal;
            Vector2 ahead = toGoal.sqrMagnitude > 0.0001f
                ? toGoal.normalized * Mathf.Min(SUPPORT_AHEAD, toGoal.magnitude)
                : Vector2.zero;

            float openSide = ballLocal.x >= 0f ? -1f : 1f;
            float side = index % 2 == 0 ? openSide : -openSide;
            float width = SUPPORT_WIDTH * (1 + index / 2);

            Vector2 spot = ballLocal + ahead;
            spot.x += side * width;
            return Clamp(spot, halfExtents);
        }

        static Vector2 Clamp(Vector2 spot, Vector2 halfExtents)
        {
            float x = Mathf.Max(0f, halfExtents.x - WALL_MARGIN);
            float y = Mathf.Max(0f, halfExtents.y - WALL_MARGIN);
            return new Vector2(Mathf.Clamp(spot.x, -x, x), Mathf.Clamp(spot.y, -y, y));
        }
    }
}
