using System.Collections.Generic;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Specialized ray perception (v4, 2026-08-11).
    ///
    /// One RayPerceptionSensor2D per object class so the policy never has to
    /// disentangle "what" from "where". Each sensor is intentionally narrow
    /// (1-2 rays) but together they cover the world fully without conflating
    /// signals. Pattern lifted from "AI Learns to Play Soccer" (Hugging Face
    /// ML-Agents unit, 2023) where splitting a single 4-tag sensor into
    /// goal/own-goal pairs produced the breakthrough.
    ///
    /// Budget (RayPerceptionSensor.OutputSize = (Tags+2) * (2*RPD+1)):
    ///   Sensor_Ball        : 2 rays/dir  tag Ball   -> (1+2)*5 -> 15
    ///   Sensor_Goal        : 1 ray /dir  tag Goal   -> (1+2)*3 ->  9
    ///   Sensor_Opponents   : 2 rays/dir  tag Agent  -> (1+2)*5 -> 15
    ///   Sensor_Walls       : 2 rays/dir  tag Wall   -> (1+2)*5 -> 15
    ///   Ray total                                             -> 54
    ///   Vector obs (BaseObservationSize 27 x 2 stacks)        -> 54
    ///   Grand total model inputs                              -> 108
    ///
    /// CORRECTED 2026-08-12. This block used to double every ray figure and
    /// claim 108 ray / 160 total, on the belief that
    /// NumStackedVectorObservations=2 stacks the ray sensors too. It does not -
    /// that setting only stacks the VectorSensor. Ray sensors stack via their
    /// own RayPerceptionSensorComponentBase.ObservationStacks, which defaults to
    /// 1 and is never set here. CLAUDE.md's "162" was wrong the same way.
    /// Nothing in the shipped code changed; only the arithmetic on this page.
    ///
    /// Previous single-sensor contract was 118 inputs (66 ray + 52 vec, when
    /// BaseObservationSize was 26). Any .onnx from before the split declares
    /// those shapes and cannot load against this runtime - see
    /// Agent_EditMode_ObsContract, which fails the build rather than letting a
    /// mismatched brain degrade silently at eval time.
    ///
    /// Sensor_Goal is team-relative through rewards: +Y points "forward" for
    /// blue (toward Red's goal) and "backward" for red (toward Red's own goal),
    /// so the policy learns that the same input means "opponent goal" for blue
    /// and "own goal" for red via the reward signal.
    ///
    /// Awake wipes any pre-existing ray sensors on the GameObject first so a
    /// scene-serialized 11-ray sensor cannot silently stack with the new
    /// 4-sensor battery. DefaultExecutionOrder(-100) keeps this Awake ahead
    /// of Agent sensor initialization, so the contract is set exactly once.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class Sensor_Vision : MonoBehaviour
    {
        // Half-arc for the goal sensor. Narrow enough that the ray only fires
        // when the agent is roughly facing the goal mouth; at any other angle
        // the slot reads zero (the policy learns "ray hit = something good is
        // over there" without having to triangulate which goal).
        const float GoalHalfArcDegrees = 30f;

        // Ray lengths. Goals can sit up to ~40 m across the pitch; walls live
        // at the perimeter (max 30 m from centre on the 36x54 training pitch);
        // ball and opponents are within 24 m 99% of the time. Lengths are
        // generous on purpose - one of the v3 (300 deg / 24 m) findings was
        // that short rays left agents blind in the far half.
        const float GoalRayLength = 40f;
        const float StandardRayLength = 24f;
        const float WallRayLength = 12f;

        /// <summary>
        /// One entry per ray sensor. Declared as data rather than four inline
        /// AddSensor calls so the observation contract is computable without
        /// entering play mode - Agent_EditMode_ObsContract sums
        /// <see cref="TotalRayObservationSize"/> off this table and compares it
        /// against the assigned .onnx. Awake is the only writer; keep it in sync
        /// by adding sensors here, never by calling AddSensor directly.
        /// </summary>
        internal readonly struct SensorSpec
        {
            public readonly string Name;
            public readonly string Tag;
            public readonly int RaysPerDirection;
            public readonly float MaxRayDegrees;
            public readonly float RayLength;

            public SensorSpec(string name, string tag, int raysPerDirection,
                              float maxRayDegrees, float rayLength)
            {
                Name = name;
                Tag = tag;
                RaysPerDirection = raysPerDirection;
                MaxRayDegrees = maxRayDegrees;
                RayLength = rayLength;
            }

            // RayPerceptionSensor.OutputSize(). One tag per sensor by design.
            public int ObservationSize => (1 + 2) * (2 * RaysPerDirection + 1);
        }

        /// <summary>Placeholder replaced per-agent with the opposing team's tag.</summary>
        internal const string OPPONENT_TAG_PLACEHOLDER = "__OPPONENT__";

        /// <summary>
        /// The ray battery.
        ///
        /// RAY COUNTS. These were 2 per direction over a 180 degree half-arc,
        /// which is 5 rays across the full circle - and because -180 and +180 are
        /// the same direction, only FOUR distinct directions, 90 degrees apart.
        /// The blind gap between adjacent rays grows as d*sin(45deg) = 0.71d, and
        /// with an agent half-width of 0.4 plus the 0.1 spherecast radius that
        /// guarantees detection only within ~0.71 units. The 11-ray/300-degree
        /// sensor it replaced guaranteed ~1.9 units, so the phase 10 split made
        /// perception strictly worse while appearing to improve it - and the
        /// stale-build incident meant nobody ever measured it.
        ///
        /// 6 per direction gives 13 rays / 12 distinct directions / 30 degrees
        /// apart, so the gap is d*sin(15deg) = 0.259d and detection is guaranteed
        /// within ~1.93 units: parity with the old sensor, but now with full 360
        /// coverage AND friend/foe separation. Walls are large and continuous so
        /// they need less angular precision; the goal sensor keeps its narrow
        /// forward wedge by design.
        /// </summary>
        internal static readonly SensorSpec[] Battery =
        {
            new SensorSpec("Sensor_Ball",      "Ball",                    6, 180f,               StandardRayLength),
            new SensorSpec("Sensor_Goal",      "Goal",                    2, GoalHalfArcDegrees, GoalRayLength),
            new SensorSpec("Sensor_Opponents", OPPONENT_TAG_PLACEHOLDER,  6, 180f,               StandardRayLength),
            new SensorSpec("Sensor_Walls",     "Wall",                    4, 180f,               WallRayLength),
        };

        /// <summary>
        /// Ray observations this component contributes to the model input.
        /// ObservationStacks is left at its default of 1, so this is NOT
        /// multiplied by NumStackedVectorObservations - that setting stacks the
        /// VectorSensor only.
        /// </summary>
        internal static int TotalRayObservationSize
        {
            get
            {
                int total = 0;
                for (int i = 0; i < Battery.Length; i++)
                {
                    total += Battery[i].ObservationSize;
                }
                return total;
            }
        }

        void Awake()
        {
            // RECONFIGURE IN PLACE - do not destroy and re-add.
            //
            // This used to Destroy() the existing components and AddComponent()
            // fresh ones. Destroy is DEFERRED to the end of the frame while
            // AddComponent is immediate, so for one frame the GameObject carried
            // both sets - and Agent.OnEnable, which snapshots the sensor list,
            // runs inside that same frame. The agent therefore initialised with
            // EIGHT ray sensors instead of four.
            //
            // It only bites on cloned pitches. Sensor_Vision runs at -100, so the
            // authored pitch's agents have already added their four components by
            // the time Agent_TrainingGrid (order 0) clones the pitch; every clone
            // inherits those four, then adds four more. The original pitch ends up
            // with 4 sensors and all 15 clones with 8, so the trainer sees agents
            // that disagree about their own observation shape and rejects the
            // environment:
            //   "Observation at index=1 ... Expected shape (15,) but got (39,)"
            //
            // This has been latent since the four-sensor split (2026-08-11) and was
            // never hit, because every phase-10 run was executed against a stale
            // build that predated the split. The first run to actually execute this
            // code is the one that found it.
            var existing = GetComponents<RayPerceptionSensorComponent2D>();

            // The opponent sensor needs to know which side this agent is on, and
            // Agent_Soccer.team is a serialized field so it is readable from Awake
            // regardless of component order.
            var soccer = GetComponent<Agent_Soccer>();
            string opponentTag = soccer != null
                ? TeamTag(Agent_Soccer.Opponent(soccer.team))
                : TeamTag(Agent_Soccer.Team.Red);

            for (int i = 0; i < Battery.Length; i++)
            {
                var component = i < existing.Length && existing[i] != null
                    ? existing[i]
                    : gameObject.AddComponent<RayPerceptionSensorComponent2D>();
                Configure(component, Battery[i], opponentTag);
            }

            // Surplus can only appear if the battery shrinks in code. DestroyImmediate
            // because a deferred Destroy is exactly what caused the bug above.
            for (int i = Battery.Length; i < existing.Length; i++)
            {
                if (existing[i] != null) DestroyImmediate(existing[i]);
            }
        }

        /// <summary>
        /// Tag carried by a team's players.
        ///
        /// Until now every player was tagged "Agent", so "Sensor_Opponents"
        /// detected teammates and opponents identically - it was a
        /// Sensor_AnyPlayer, and the dedicated-opponent-ray idea that phase 10
        /// was built around never actually existed. Agent_Soccer applies these
        /// tags at Awake; nothing else in the project reads the old "Agent" tag.
        /// </summary>
        public static string TeamTag(Agent_Soccer.Team team)
        {
            return team == Agent_Soccer.Team.Blue ? "TeamBlue" : "TeamRed";
        }

        static void Configure(RayPerceptionSensorComponent2D s, SensorSpec spec, string opponentTag)
        {
            s.SensorName = spec.Name;
            s.RaysPerDirection = spec.RaysPerDirection;
            // ML-Agents takes MaxRayDegrees as the HALF-arc around the +Y eye
            // axis, so 180 = full circle and 30 = a 60 deg forward wedge.
            s.MaxRayDegrees = spec.MaxRayDegrees;
            s.RayLength = spec.RayLength;
            s.SphereCastRadius = 0.1f;
            s.DetectableTags = new List<string>
            {
                spec.Tag == OPPONENT_TAG_PLACEHOLDER ? opponentTag : spec.Tag,
            };
        }
    }
}
