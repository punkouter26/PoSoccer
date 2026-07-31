namespace PoSoccer
{
    /// <summary>
    /// Carries the menu's match configuration across the scene load.
    /// Applied=false (no menu visit) means the match scene uses its serialized
    /// defaults; the *2 slots only matter in 2v2.
    /// </summary>
    public static class Agent_MatchSetup
    {
        public static bool Applied;
        public static bool TwoVTwo = true;
        public static Reward_Settings BluePlayer;
        public static Reward_Settings BluePlayer2;
        public static Reward_Settings RedPlayer;
        public static Reward_Settings RedPlayer2;
    }
}
