namespace PoSoccer
{
    /// <summary>
    /// Carries the menu's player selection across the scene load into the match.
    /// Null entries mean "use the match scene's serialized defaults".
    /// </summary>
    public static class Agent_MatchSetup
    {
        public static Reward_Settings BluePlayer;
        public static Reward_Settings RedPlayer;
    }
}
