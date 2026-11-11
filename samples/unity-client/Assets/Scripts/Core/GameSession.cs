using System;
using System.Threading.Tasks;
using GameServer.Client;
using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// Owns the one <see cref="GameClient"/> and the services built on it, for the whole run. Lives across scenes.
    /// Screens talk to the services, never to the SDK directly.
    /// </summary>
    public sealed class GameSession : MonoBehaviour
    {
        private const string UrlKey = "game.serverUrl";
        public const string DefaultUrl = "http://localhost:5000";

        public static GameSession Instance { get; private set; }

        public GameClient Client { get; private set; }
        public Account Account { get; private set; }
        public string ServerUrl { get; private set; }

        /// <summary>The last startup answer (update link, maintenance text), for dialogs shown later.</summary>
        public StartupCheck Startup { get; private set; }

        public ProfileService Profile { get; private set; }
        public LoadoutService Loadout { get; private set; }
        public BackendService Backend { get; private set; }
        public WalletService Wallet { get; private set; }
        public ShopService Shop { get; private set; }
        public GoalsService Goals { get; private set; }
        public SocialService Social { get; private set; }
        public ChatService Chat { get; private set; }
        public RankService Ranks { get; private set; }
        public PlayService Play { get; private set; }

        /// <summary>The server ended this session (refresh failed): go back to the Boot screen.</summary>
        public event Action SessionEnded;

        public bool IsReady => Client != null && Client.Auth.IsLoggedIn && Account != null;

        /// <summary>
        /// The server to use: the one the player typed last, else the one baked into the build
        /// (Resources/server-url.txt, written by the Android build script), else <see cref="DefaultUrl"/>.
        /// </summary>
        public static string SavedUrl
        {
            get
            {
                var saved = PlayerPrefs.GetString(UrlKey, "");
                if (!string.IsNullOrEmpty(saved))
                    return saved;

                var baked = Resources.Load<TextAsset>("server-url");
                return baked != null && !string.IsNullOrWhiteSpace(baked.text) ? baked.text.Trim() : DefaultUrl;
            }
        }

        public static GameSession Ensure()
        {
            if (Instance != null)
                return Instance;

            var go = new GameObject("GameSession");
            DontDestroyOnLoad(go);
            return Instance = go.AddComponent<GameSession>();
        }

        private void OnDestroy() => Teardown();

        /// <summary>(Re)creates the client and services for <paramref name="url"/>. Call from the main thread.</summary>
        public void Configure(string url)
        {
            Teardown();
            ServerUrl = url.TrimEnd('/');
            PlayerPrefs.SetString(UrlKey, ServerUrl);

            Client = new GameClient(new GameClientOptions
            {
                BaseUrl = ServerUrl,
                Storage = new PlayerPrefsStorage(),
                Log = Debug.Log
            });
            Account = null;
        }

        /// <summary>
        /// Asks the server, without signing in, whether it is in maintenance and whether this build is current.
        /// Both calls are allowed during maintenance. Also warms the remote config.
        /// </summary>
        public async Task<StartupCheck> CheckStartupAsync()
        {
            if (Client == null)
                throw new InvalidOperationException("Configure() first");

            var config = await Client.Config.GetAsync();
            var check = StartupCheck.FromConfig(config);
            check.ApplyVersion(await Client.Config.VersionCheckAsync(Application.version));
            return Startup = check;
        }

        /// <summary>Continues the saved session, or signs in as a guest on this device, then builds the services.</summary>
        public async Task<Account> SignInAsync()
        {
            if (Client == null)
                throw new InvalidOperationException("Configure() first");

            if (!await Client.Auth.TryRestoreSessionAsync())
                await Client.Auth.LoginDeviceAsync();

            Account = await Client.Account.GetAsync();
            BuildServices();
            return Account;
        }

        /// <summary>Loads everything the home screen shows. Each step is reported so Boot can draw a progress bar.</summary>
        public async Task LoadHomeAsync(Action<float, string> progress)
        {
            progress?.Invoke(0.55f, "Opening your wallet");
            await Wallet.RefreshAsync();
            progress?.Invoke(0.7f, "Loading shop and quests");
            await Task.WhenAll(Shop.RefreshAsync(), Goals.RefreshAsync(), Play.LoadModesAsync());
            progress?.Invoke(0.85f, "Finding your friends");
            await Social.RefreshAsync();
            progress?.Invoke(0.95f, "Going online");
            try
            {
                await Backend.EnsureConnectedAsync();
            }
            catch (Exception ex)
            {
                // The menu works offline for REST; the realtime banner shows the problem.
                Debug.LogWarning("[Session] realtime not available: " + ex.Message);
            }
        }

        public async Task SignOutAsync()
        {
            if (Client == null)
                return;

            try
            {
                await Client.Realtime.DisconnectAsync();
                await Client.Auth.LogoutAsync();
            }
            finally
            {
                Teardown();
                Account = null;
            }
        }

        /// <summary>Deletes the account on the server (this cannot be undone) and forgets everything local.</summary>
        public async Task DeleteAccountAsync()
        {
            await Client.Realtime.DisconnectAsync();
            await Profile.DeleteAsync();
            Teardown();
            Account = null;
        }

        private void BuildServices()
        {
            DisposeServices();
            Profile = new ProfileService(Client, Account, a => Account = a);
            Backend = new BackendService(Client);
            Wallet = new WalletService(Client);
            Loadout = new LoadoutService(Wallet, Account.Id);
            Shop = new ShopService(Client, Wallet);
            Goals = new GoalsService(Client, Wallet);
            Social = new SocialService(Client);
            Chat = new ChatService(Client, Backend, Account.Id);
            Ranks = new RankService(Client);
            Play = new PlayService(Client, Backend);
            Backend.SessionEnded += OnSessionEnded;
        }

        private void OnSessionEnded()
        {
            Account = null;
            SessionEnded?.Invoke();
        }

        private void Teardown() => DisposeServices();

        private void DisposeServices()
        {
            if (Backend != null)
                Backend.SessionEnded -= OnSessionEnded;

            Backend?.Dispose();
            Social?.Dispose();
            Chat?.Dispose();
            Play?.Dispose();
            Backend = null;
            Profile = null;
            Loadout = null;
            Social = null;
            Chat = null;
            Play = null;
            Wallet = null;
            Shop = null;
            Goals = null;
            Ranks = null;
        }
    }
}
