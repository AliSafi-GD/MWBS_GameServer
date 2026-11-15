using System;

namespace GameServer.Client
{
    /// <summary>
    /// The entry point of the SDK. Create it once on the main thread (events are delivered on the thread that
    /// created it) and keep it for the whole session.
    /// <code>
    /// var client = new GameClient(new GameClientOptions { BaseUrl = "https://game.example.com" });
    /// await client.Auth.LoginDeviceAsync();
    /// await client.Realtime.ConnectAsync();
    /// </code>
    /// </summary>
    public sealed class GameClient : IGameClient
    {
        private readonly ApiClient _api;

        public GameClientOptions Options { get; }

        public IAuthApi Auth { get; }
        public IAccountApi Account { get; }
        public IConfigApi Config { get; }
        public IEconomyApi Economy { get; }
        public IShopApi Shop { get; }
        public IProgressApi Progress { get; }
        public ICompetitionsApi Competitions { get; }
        public INotificationsApi Notifications { get; }
        public ISocialApi Social { get; }
        public IChatApi Chat { get; }
        public IMatchesApi Matches { get; }
        public IRpcApi Rpc { get; }

        /// <summary>The live connection (chat, matchmaker, pushed events).</summary>
        public IRealtimeClient Realtime { get; }

        public Session Session => _api.Session;

        public GameClient(GameClientOptions options = null)
        {
            Options = options ?? new GameClientOptions();

            var http = Options.Http ?? DefaultHttp(Options);
            var storage = Options.Storage ?? DefaultStorage();
            var dispatcher = new Dispatcher(Options.Dispatcher ?? new SynchronizationContextDispatcher(), Options.Log);

            _api = new ApiClient(Options, http, storage);

            Auth = new AuthApi(_api, storage);
            Account = new AccountApi(_api);
            Config = new ConfigApi(_api);
            Economy = new EconomyApi(_api);
            Shop = new ShopApi(_api);
            Progress = new ProgressApi(_api);
            Competitions = new CompetitionsApi(_api);
            Notifications = new NotificationsApi(_api);
            Social = new SocialApi(_api);
            Chat = new ChatApi(_api);
            Matches = new MatchesApi(_api, Options, dispatcher);
            Rpc = new RpcApi(_api);
            Realtime = new RealtimeClient(_api, Options, dispatcher);
        }

        private static IHttp DefaultHttp(GameClientOptions options)
        {
#if UNITY_2019_4_OR_NEWER
            return new UnityWebRequestHttp(options.RequestTimeoutSeconds);
#else
            return new HttpClientHttp(options.RequestTimeoutSeconds);
#endif
        }

        private static IClientStorage DefaultStorage()
        {
#if UNITY_2019_4_OR_NEWER
            return new PlayerPrefsStorage();
#else
            return new MemoryStorage();
#endif
        }
    }
}
