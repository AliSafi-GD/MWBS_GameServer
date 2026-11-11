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
    public sealed class GameClient
    {
        private readonly ApiClient _api;

        public GameClientOptions Options { get; }

        public AuthApi Auth { get; }
        public AccountApi Account { get; }
        public ConfigApi Config { get; }
        public EconomyApi Economy { get; }
        public ShopApi Shop { get; }
        public ProgressApi Progress { get; }
        public CompetitionsApi Competitions { get; }
        public NotificationsApi Notifications { get; }
        public SocialApi Social { get; }
        public ChatApi Chat { get; }
        public MatchesApi Matches { get; }
        public RpcApi Rpc { get; }

        /// <summary>The live connection (chat, matchmaker, pushed events).</summary>
        public RealtimeClient Realtime { get; }

        public Session Session => _api.Session;

        public GameClient(GameClientOptions options = null)
        {
            Options = options ?? new GameClientOptions();

            var http = Options.Http ?? DefaultHttp(Options);
            var storage = Options.Storage ?? DefaultStorage();
            var dispatcher = new Dispatcher(Options.Log);

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
