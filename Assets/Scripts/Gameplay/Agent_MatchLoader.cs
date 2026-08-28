using System.Collections.Generic;
using Unity.MLAgents.Policies;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Builds the match scene from the menu's selection: squad size per side (1-10),
    /// a reward profile per slot, the policy that drives it (trained model when the
    /// profile carries one, otherwise the rule-based bot), and a pitch scaled to the
    /// head count.
    ///
    /// Runs at order -60: after Sensor_Vision (-100) but before Agent_Soccer.Awake
    /// and Agent_EnvController.Start (-50), so cloned players are configured before
    /// their policy initializes and the pitch is already resized when spawn
    /// positions are captured.
    /// </summary>
    [DefaultExecutionOrder(-60)]
    public sealed class Agent_MatchLoader : MonoBehaviour
    {
        [Header("Slot 0 and 1 per side (authored in the scene; extra slots clone these)")]
        public Agent_Soccer blueAgent;
        public Agent_Soccer blueAgent2;
        public Agent_Soccer redAgent;
        public Agent_Soccer redAgent2;

        [Header("Defaults when launched without the menu")]
        public Reward_Settings defaultBlue;
        public Reward_Settings defaultBlue2;
        public Reward_Settings defaultRed;
        public Reward_Settings defaultRed2;

        [Tooltip("Rescale the pitch to the squad size (futsal density). Off keeps the " +
                 "authored pitch, which is what trained brains learned on.")]
        [SerializeField] private bool _scalePitchToSquad = true;

        /// <summary>
        /// Loading the match scene without going through the menu is a documented
        /// trap: Agent_MatchSetup is empty, so the lineup silently falls back to
        /// whatever happens to be serialized and you are testing a squad nobody
        /// chose. It used to fail silently; now it says so.
        /// </summary>
        void WarnIfLineupWasNeverChosen()
        {
            if (Agent_MatchSetup.Applied) return;
            if (Agent_EvalStats.EvalMode) return;
            if (Unity.MLAgents.Academy.Instance.IsCommunicatorOn) return;

            Debug.LogWarning(
                "Agent_MatchLoader: no menu selection found (Agent_MatchSetup.Applied is false). " +
                "Falling back to the lineup serialized in the scene. Start from SCN_Menu to " +
                "choose a squad - see CLAUDE.md.");
        }

        void Awake()
        {
            WarnIfLineupWasNeverChosen();
            var env = GetComponent<Agent_EnvController>();

            int blueSize = Mathf.Clamp(
                Agent_MatchSetup.BlueSize > 0 ? Agent_MatchSetup.BlueSize : CountAuthored(blueAgent, blueAgent2),
                1, Agent_MatchSetup.MAX_SQUAD);
            int redSize = Mathf.Clamp(
                Agent_MatchSetup.RedSize > 0 ? Agent_MatchSetup.RedSize : CountAuthored(redAgent, redAgent2),
                1, Agent_MatchSetup.MAX_SQUAD);

            // Pitch first: spawn positions are derived from the final extents.
            if (_scalePitchToSquad && env != null)
            {
                env.ResizePitch(Agent_PitchSizing.HalfExtentsFor(blueSize, redSize));
            }
            Vector2 half = env != null ? env.PitchHalfExtents : new Vector2(18f, 27f);

            BuildSquad(Agent_Soccer.Team.Blue, blueSize, blueAgent, blueAgent2,
                defaultBlue, defaultBlue2, half);
            BuildSquad(Agent_Soccer.Team.Red, redSize, redAgent, redAgent2,
                defaultRed, defaultRed2, half);
        }

        static int CountAuthored(Agent_Soccer first, Agent_Soccer second) =>
            (first != null ? 1 : 0) + (second != null ? 1 : 0);

        /// <summary>
        /// Bring one side to the requested size. Written to be idempotent: it starts
        /// from whatever players are already under the pitch, reuses them, clones the
        /// deficit and destroys the surplus. Building from "the two authored slots
        /// plus N clones" instead would double up if this ever ran twice on the same
        /// scene, which is exactly the accumulation bug it replaces.
        /// </summary>
        void BuildSquad(Agent_Soccer.Team team, int size, Agent_Soccer slot0, Agent_Soccer slot1,
            Reward_Settings default0, Reward_Settings default1, Vector2 half)
        {
            // Existing players of this side, authored ones first so slot order is stable.
            var squad = new List<Agent_Soccer>();
            if (slot0 != null && slot0.team == team) squad.Add(slot0);
            if (slot1 != null && slot1.team == team) squad.Add(slot1);
            var present = GetComponentsInChildren<Agent_Soccer>(true);
            for (int i = 0; i < present.Length; i++)
            {
                if (present[i].team == team && !squad.Contains(present[i])) squad.Add(present[i]);
            }
            if (squad.Count == 0) return;

            // Surplus first, so the clone template is never a player being destroyed.
            //
            // Deactivate BEFORE Destroy. Destroy is deferred to the end of the frame,
            // so a surplus player is still returned by GetComponentsInChildren for the
            // rest of this frame - including by Agent_EnvController.Start, which runs
            // at order -50, after this Awake at -60, in the SAME frame. It would then
            // hold a reference that turns into a MissingReferenceException once the
            // deferred destroy lands. SetActive(false) closes the window: the default
            // GetComponentsInChildren<T>() skips inactive objects, so the doomed player
            // is invisible to discovery immediately rather than at end of frame.
            // (Symptom this fixes: Match_TwoStandardsVsTwoBots failing intermittently
            // with "The object of type 'PoSoccer.Agent_Soccer' has been destroyed".)
            for (int slot = squad.Count - 1; slot >= size; slot--)
            {
                squad[slot].gameObject.SetActive(false);
                Destroy(squad[slot].gameObject);
                squad.RemoveAt(slot);
            }

            var template = squad[0];
            while (squad.Count < size)
            {
                var clone = CloneFrom(template, team, squad.Count);
                if (clone == null) break;
                squad.Add(clone);
            }

            for (int slot = 0; slot < squad.Count; slot++)
            {
                var profile = Agent_MatchSetup.Get(team, slot)
                    ?? (slot == 1 ? default1 : default0);
                Apply(squad[slot], profile);
                Place(squad[slot], team, slot, size, half);
                // Clones are built inactive so their policy initializes only once
                // the profile, brain and team are all in place.
                if (!squad[slot].gameObject.activeSelf) squad[slot].gameObject.SetActive(true);
            }
        }

        /// <summary>
        /// Clone a player while it is inactive, so the clone's Awake/OnEnable - which
        /// locks in the behavior name and policy - does not run until the caller has
        /// finished configuring it.
        /// </summary>
        static Agent_Soccer CloneFrom(Agent_Soccer template, Agent_Soccer.Team team, int slot)
        {
            bool wasActive = template.gameObject.activeSelf;
            template.gameObject.SetActive(false);
            var go = Instantiate(template.gameObject, template.transform.parent);
            template.gameObject.SetActive(wasActive);

            go.name = $"Agent{team}{slot + 1}";
            var agent = go.GetComponent<Agent_Soccer>();
            if (agent != null) agent.team = team;
            return agent;
        }

        /// <summary>
        /// Kickoff formation: players fan out across their own half in up to two
        /// ranks, so a 10-a-side squad does not stack on one spot. Domain
        /// randomization in ResetPitch scatters them from here during training.
        /// </summary>
        static void Place(Agent_Soccer agent, Agent_Soccer.Team team, int slot, int size, Vector2 half)
        {
            float sign = team == Agent_Soccer.Team.Blue ? -1f : 1f;
            int perRank = Mathf.CeilToInt(size / 2f);
            int rank = slot < perRank ? 0 : 1;
            int indexInRank = rank == 0 ? slot : slot - perRank;
            int rankCount = rank == 0 ? perRank : size - perRank;

            // Evenly spaced across the width, inset from the walls.
            float usable = (half.x - 1.5f) * 2f;
            float x = rankCount <= 1
                ? 0f
                : -usable * 0.5f + usable * (indexInRank / (float)(rankCount - 1));
            // Front rank near the halfway line, back rank deeper toward own goal.
            float y = sign * (rank == 0 ? half.y * 0.35f : half.y * 0.7f);

            var t = agent.transform;
            t.localPosition = new Vector3(x, y, t.localPosition.z);
            // Face the opponent half.
            t.localRotation = Quaternion.Euler(0f, 0f, team == Agent_Soccer.Team.Blue ? 0f : 180f);
        }

        static void Apply(Agent_Soccer agent, Reward_Settings profile)
        {
            if (agent == null) return;

            // v2: when called from a direct scene load (no menu), the serialized
            // default* slots may be null on the loader. Build a runtime fallback
            // so the agent still gets a valid Reward_Settings reference rather
            // than silently running with rewards=null (which disables dense
            // rewards and the HUD identity letters).
            if (profile == null)
            {
                Debug.LogWarning($"[Agent_MatchLoader] No profile for {agent.name}; using runtime fallback. " +
                                 "Fix: wire the default* slots on this scene's loader, or visit SCN_Menu first.");
                profile = ScriptableObject.CreateInstance<Reward_Settings>();
                profile.playerName = agent.brainName ?? "FALLBACK";
            }

            agent.rewards = profile;
            agent.brainName = profile.playerName;

            var behavior = agent.GetComponent<BehaviorParameters>();
            if (behavior == null) return;

            if (profile.brainModel != null)
            {
                behavior.Model = profile.brainModel;
                behavior.BehaviorType = BehaviorType.InferenceOnly;
            }
            else
            {
                // Untrained player: the heuristic bot stands in.
                behavior.BehaviorType = BehaviorType.HeuristicOnly;
                var bot = agent.GetComponent<Agent_HeuristicBot>();
                if (bot != null) bot.enabled = true;
            }
        }
    }
}
