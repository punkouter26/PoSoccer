using System.Collections.Generic;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Specialized ray perception (v5, 2026-10-10).
    ///
    /// One RayPerceptionSensor2D per object class so the policy never has to
    /// disentangle "what" from "where": one detectable tag AND one physics layer
    /// per sensor (see <see cref="Agent_Layers"/> for why the layer matters).
    ///
    /// Budget (RayPerceptionSensor.OutputSize = (Tags+2) * (2*RPD+1)):
    ///   Sensor_Ball      : 6 rays/dir  tag Ball   layer Ball          -> (1+2)*13 ->  39
    ///   Sensor_Opponents : 6 rays/dir  tag Team*  layer Player(opp.)  -> (1+2)*13 ->  39
    ///   Sensor_Walls     : 4 rays/dir  tag Wall   layer Pitch         -> (1+2)* 9 ->  27
    ///   Ray total                                                                 -> 105
    ///   Vector obs (BaseObservationSize 34 x 2 stacks)                            ->  68
    ///   Grand total model inputs                                                  -> 173
    ///
    /// 2026-10-10: 188 -> 173. Sensor_Goal (2 rays/dir, a 60 degree forward wedge,
    /// (1+2)*5 = 15 inputs) was REMOVED. Both goals are already in the vector
    /// observation in the body frame, at any range and any heading
    /// (Agent_Soccer.CollectObservations, "Goals (5)"), so the wedge re-reported a
    /// subset of that through a coarser instrument. It was also not team-relative:
    /// both goals carry the one "Goal" tag, so the same ray meant "attack here" or
    /// "defend here" depending on which way the agent happened to face.
    /// Same day: each sensor gained a single-layer RayLayerMask. That changes NO
    /// tensor shape and changes what every ray means - the dangerous direction; see
    /// Agent_Layers. The previous table on this page (15/9/15/15 = 54, 108 total) had
    /// also been stale since the 2026-08-27 ray-count change.
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
    /// Awake reconfigures any pre-existing ray sensors on the GameObject in place
    /// (and destroys a surplus) so a scene-serialized sensor cannot silently stack
    /// with the battery. DefaultExecutionOrder(-100) keeps this Awake ahead of
    /// Agent sensor initialization, so the contract is set exactly once.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class Sensor_Vision : MonoBehaviour
    {
        // Ray lengths. Walls live at the perimeter (max 30 m from centre on the
        // 36x54 training pitch); ball and opponents are within 24 m 99% of the
        // time. Lengths are generous on purpose - one of the v3 (300 deg / 24 m)
        // findings was that short rays left agents blind in the far half.
        const float StandardRayLength = 24f;
        const float WallRayLength = 12f;

        /// <summary>
        /// One entry per ray sensor. Declared as data rather than inline
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
            /// <summary>The ONE physics layer this sensor casts against (Agent_Layers).</summary>
            public readonly string Layer;
            public readonly int RaysPerDirection;
            public readonly float MaxRayDegrees;
            public readonly float RayLength;

            public SensorSpec(string name, string tag, string layer, int raysPerDirection,
                              float maxRayDegrees, float rayLength)
            {
                Name = name;
                Tag = tag;
                Layer = layer;
                RaysPerDirection = raysPerDirection;
                MaxRayDegrees = maxRayDegrees;
                RayLength = rayLength;
            }

            // RayPerceptionSensor.OutputSize(). One tag per sensor by design.
            public int ObservationSize => (1 + 2) * (2 * RaysPerDirection + 1);
        }

        /// <summary>Placeholder replaced per-agent with the opposing team's tag (and layer).</summary>
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
        /// they need less angular precision.
        ///
        /// LAYERS (2026-10-10). Each sensor casts against exactly one layer, so
        /// nothing occludes another class's sensor: the ball is seen through
        /// players, an opponent through a teammate. The wall sensor's layer (Pitch)
        /// also holds the two goal TRIGGERS, and Physics2D queries hit triggers
        /// (m_QueriesHitTriggers: 1), so a wall ray into a goal mouth stops on the
        /// trigger and reads "hit, not a wall" at that distance.
        /// </summary>
        internal static readonly SensorSpec[] Battery =
        {
            new SensorSpec("Sensor_Ball",      "Ball",                   Agent_Layers.BALL,        6, 180f, StandardRayLength),
            new SensorSpec("Sensor_Opponents", OPPONENT_TAG_PLACEHOLDER, OPPONENT_TAG_PLACEHOLDER, 6, 180f, StandardRayLength),
            new SensorSpec("Sensor_Walls",     "Wall",                   Agent_Layers.PITCH,       4, 180f, WallRayLength),
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
            // twice the battery (eight ray sensors, when the battery was four).
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

            // A scripted opponent whose observations are discarded gets NO rays.
            // See Agent_Soccer.IsSensorlessScriptedSide for why they are discarded
            // (HeuristicPolicy writes every sensor into a NullList) and for the three
            // cases where they are not. The predicate is a pure function of the
            // serialized team plus the launcher's env vars, which is what lets it be
            // evaluated HERE, at execution order -100, before Agent_Soccer.Awake has
            // run - the same reason `team` itself is readable this early.
            //
            // Any already-serialized sensors are destroyed rather than left enabled:
            // Agent.OnEnable snapshots whatever is on the GameObject, so a component
            // that merely has `enabled = false` would still be in that list.
            if (soccer != null && Agent_Soccer.IsSensorlessScriptedSide(soccer.team))
            {
                for (int i = 0; i < existing.Length; i++)
                {
                    if (existing[i] != null) DestroyImmediate(existing[i]);
                }
                return;
            }
            Agent_Soccer.Team opposing = soccer != null
                ? Agent_Soccer.Opponent(soccer.team)
                : Agent_Soccer.Team.Red;
            string opponentTag = TeamTag(opposing);
            string opponentLayer = Agent_Layers.TeamLayerName(opposing);

            for (int i = 0; i < Battery.Length; i++)
            {
                var component = i < existing.Length && existing[i] != null
                    ? existing[i]
                    : gameObject.AddComponent<RayPerceptionSensorComponent2D>();
                Configure(component, Battery[i], opponentTag, opponentLayer);
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

        static void Configure(RayPerceptionSensorComponent2D s, SensorSpec spec,
                              string opponentTag, string opponentLayer)
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
            // Exactly one layer, and never the caster's own: a ray starts at the
            // agent's centre, inside its own collider, and a mask containing that
            // collider's layer returns "hit myself at distance 0" for every ray.
            s.RayLayerMask = Agent_Layers.RayMask(
                spec.Layer == OPPONENT_TAG_PLACEHOLDER ? opponentLayer : spec.Layer);
        }
    }
}
