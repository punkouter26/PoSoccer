namespace PoSoccer
{
    /// <summary>
    /// Carries the menu's player selection across the scene load into the match.
    /// Null entries mean "use the match scene's serialized defaults". The *2 slots
    /// only apply in scenes that field two agents per team.
    /// </summary>
    public static class Agent_MatchSetup
    {
        public static Reward_Settings BluePlayer;
        public static Reward_Settings BluePlayer2;
        public static Reward_Settings RedPlayer;
        public static Reward_Settings RedPlayer2;
    }
}
