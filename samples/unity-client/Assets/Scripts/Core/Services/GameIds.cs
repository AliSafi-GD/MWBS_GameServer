namespace Game.Core
{
    /// <summary>
    /// Ids of the content this game defines on the server (see wwwroot/arena/arena-config.json). Currencies, shop products,
    /// quests and achievements are read from the server and never named here.
    /// </summary>
    public static class GameIds
    {
        public const string RankedMode = "arena";
        public const string FriendlyMode = "arena_friendly";
        public const string LobbyRoom = "lobby";

        public const string Coins = "coins";
        public const string Gems = "gems";

        public const string StatWins = "matches_won";
        public const string StatPlayed = "matches_played";

        public static readonly string[] BoardIds = { "arena_wins", "arena_weekly", "arena_best_hits" };
        public static readonly string[] BoardNames = { "Wins", "Weekly", "Best hits" };
    }
}
