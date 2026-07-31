using Unity.MLAgents.Policies;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Applies the menu's player selection to the match scene's two agents:
    /// reward profile (personality + color), brain name, and policy source -
    /// trained model when the profile carries one, otherwise the rule-based bot.
    /// Runs before Agent_Soccer.Awake (order -60) so the behavior contract and
    /// look pick up the selection.
    /// </summary>
    [DefaultExecutionOrder(-60)]
    public sealed class Agent_MatchLoader : MonoBehaviour
    {
        public Agent_Soccer blueAgent;
        public Agent_Soccer blueAgent2;
        public Agent_Soccer redAgent;
        public Agent_Soccer redAgent2;

        [Header("Defaults when launched without the menu")]
        public Reward_Settings defaultBlue;
        public Reward_Settings defaultBlue2;
        public Reward_Settings defaultRed;
        public Reward_Settings defaultRed2;

        void Awake()
        {
            Apply(blueAgent, Agent_MatchSetup.BluePlayer != null
                ? Agent_MatchSetup.BluePlayer : defaultBlue);
            Apply(blueAgent2, Agent_MatchSetup.BluePlayer2 != null
                ? Agent_MatchSetup.BluePlayer2 : defaultBlue2);
            Apply(redAgent, Agent_MatchSetup.RedPlayer != null
                ? Agent_MatchSetup.RedPlayer : defaultRed);
            Apply(redAgent2, Agent_MatchSetup.RedPlayer2 != null
                ? Agent_MatchSetup.RedPlayer2 : defaultRed2);
        }

        static void Apply(Agent_Soccer agent, Reward_Settings profile)
        {
            if (agent == null || profile == null) return;

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
