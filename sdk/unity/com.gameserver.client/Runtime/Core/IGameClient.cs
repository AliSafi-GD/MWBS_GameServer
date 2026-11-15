namespace GameServer.Client
{
    /// <summary>
    /// Everything <see cref="GameClient"/> offers, as interfaces. Write game code against this (or against the single API
    /// interfaces like <see cref="IShopApi"/>) so a test can hand it a fake instead of a live server:
    /// <code>
    /// public sealed class ShopScreen
    /// {
    ///     private readonly IShopApi _shop;
    ///     public ShopScreen(IShopApi shop) => _shop = shop;   // new ShopScreen(client.Shop) in the game, a fake in tests
    /// }
    /// </code>
    /// </summary>
    public interface IGameClient
    {
        GameClientOptions Options { get; }

        IAuthApi Auth { get; }
        IAccountApi Account { get; }
        IConfigApi Config { get; }
        IEconomyApi Economy { get; }
        IShopApi Shop { get; }
        IProgressApi Progress { get; }
        ICompetitionsApi Competitions { get; }
        INotificationsApi Notifications { get; }
        ISocialApi Social { get; }
        IChatApi Chat { get; }
        IMatchesApi Matches { get; }
        IRpcApi Rpc { get; }

        /// <summary>The live connection (chat, matchmaker, pushed events).</summary>
        IRealtimeClient Realtime { get; }

        Session Session { get; }
    }
}
