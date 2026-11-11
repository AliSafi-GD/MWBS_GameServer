using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    /// <summary>Signing in and out. A successful login stores the session, so every other call is authenticated.</summary>
    public sealed class AuthApi
    {
        private readonly ApiClient _api;
        private readonly IClientStorage _storage;
        private const string DeviceKey = "gs.deviceId";

        internal AuthApi(ApiClient api, IClientStorage storage)
        {
            _api = api;
            _storage = storage;
        }

        public Session Session => _api.Session;
        public bool IsLoggedIn => _api.Session.IsLoggedIn;
        public string UserId => _api.Session.UserId;

        /// <summary>Raised when the server ended the session (refresh token rejected): show the login screen.</summary>
        public event Action SessionEnded
        {
            add { _api.SessionEnded += value; }
            remove { _api.SessionEnded -= value; }
        }

        /// <summary>The id this install uses to sign in as a guest. Created once and kept.</summary>
        public string DeviceId
        {
            get
            {
                var id = _storage.Get(DeviceKey);
                if (string.IsNullOrEmpty(id))
                {
                    id = Guid.NewGuid().ToString("N");
                    _storage.Set(DeviceKey, id);
                }

                return id;
            }
        }

        /// <summary>
        /// Guest login with this install's device id. The account is created the first time (unless
        /// <paramref name="create"/> is false, which only signs in to an existing one).
        /// </summary>
        public async Task<LoginResult> LoginDeviceAsync(string username = null, bool create = true, string deviceId = null, CancellationToken ct = default(CancellationToken))
        {
            var result = await _api.SendAsync<LoginResult>("POST", "/v1/auth/device",
                new { deviceId = deviceId ?? DeviceId, username, create }, false, ct);
            _api.SetSession(result);
            return result;
        }

        /// <summary>Which login methods the server offers besides the device id (for example "phone", "google").</summary>
        public async Task<List<string>> ProvidersAsync(CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.SendAsync<JObject>("GET", "/v1/auth/providers", null, false, ct);
            return root.Get<List<string>>("providers");
        }

        /// <summary>First step of providers that need one, e.g. sending the SMS code. <paramref name="credential"/> is provider specific.</summary>
        public Task BeginAsync(string provider, object credential, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync("POST", "/v1/auth/" + ApiClient.Seg(provider) + "/begin", new { credential }, false, ct);

        public async Task<LoginResult> LoginAsync(string provider, object credential, string username = null, bool create = true, CancellationToken ct = default(CancellationToken))
        {
            var result = await _api.SendAsync<LoginResult>("POST", "/v1/auth/" + ApiClient.Seg(provider),
                new { credential, username, create }, false, ct);
            _api.SetSession(result);
            return result;
        }

        /// <summary>
        /// Continues with the session saved on this device: refreshes it and returns true, or returns false when there is
        /// none or it has ended (then call <see cref="LoginDeviceAsync"/>). Network errors are thrown.
        /// </summary>
        public async Task<bool> TryRestoreSessionAsync(CancellationToken ct = default(CancellationToken))
        {
            _api.LoadSession();
            if (!_api.Session.IsLoggedIn || string.IsNullOrEmpty(_api.Session.RefreshToken))
                return false;

            return await _api.RefreshAsync(_api.Session.Token, ct);
        }

        /// <summary>Ends this device's session.</summary>
        public async Task LogoutAsync(CancellationToken ct = default(CancellationToken))
        {
            var refreshToken = _api.Session.RefreshToken;
            try
            {
                if (!string.IsNullOrEmpty(refreshToken))
                    await _api.SendAsync("POST", "/v1/auth/logout", new { refreshToken }, false, ct);
            }
            finally
            {
                _api.ClearSession();
            }
        }

        /// <summary>Ends the sessions on every device.</summary>
        public async Task LogoutEverywhereAsync(CancellationToken ct = default(CancellationToken))
        {
            await _api.SendAsync("POST", "/v1/account/logout-all", null, true, ct);
            _api.ClearSession();
        }
    }

    /// <summary>The signed-in player's account and other players' public profiles.</summary>
    public sealed class AccountApi
    {
        private readonly ApiClient _api;

        internal AccountApi(ApiClient api)
        {
            _api = api;
        }

        public Task<Account> GetAsync(CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync<Account>("GET", "/v1/account", null, true, ct);

        public Task<Account> UpdateAsync(ProfileUpdate update, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync<Account>("PUT", "/v1/account", update, true, ct);

        /// <summary>Deletes the account and everything stored for it. This cannot be undone.</summary>
        public async Task DeleteAsync(CancellationToken ct = default(CancellationToken))
        {
            await _api.SendAsync("DELETE", "/v1/account", null, true, ct);
            _api.ClearSession();
        }

        /// <summary>Adds a login method (for example Google) so progress survives a lost device.</summary>
        public async Task<List<IdentityInfo>> LinkAsync(string provider, object credential, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.SendAsync<JObject>("POST", "/v1/account/link/" + ApiClient.Seg(provider), new { credential }, true, ct);
            return root.Get<List<IdentityInfo>>("identities");
        }

        public Task UnlinkAsync(string provider, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync("DELETE", "/v1/account/link/" + ApiClient.Seg(provider), null, true, ct);

        /// <summary>Public profiles of other players (up to 100 ids).</summary>
        public async Task<List<PublicProfile>> GetUsersAsync(IEnumerable<string> ids, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync(ApiClient.Query("/v1/users", "ids", string.Join(",", ids)), ct);
            return root.Get<List<PublicProfile>>("users");
        }
    }

    /// <summary>Public settings the game can read before login.</summary>
    public sealed class ConfigApi
    {
        private readonly ApiClient _api;
        private string _etag;
        private Dictionary<string, JToken> _cached;

        internal ConfigApi(ApiClient api)
        {
            _api = api;
        }

        /// <summary>
        /// The server's public config keys. Remembers the last answer and sends its ETag, so an unchanged config
        /// costs almost nothing.
        /// </summary>
        public async Task<Dictionary<string, JToken>> GetAsync(CancellationToken ct = default(CancellationToken))
        {
            var request = new HttpRequest { Method = "GET", Url = _api.BaseUrl + "/v1/config" };
            if (_etag != null && _cached != null)
                request.Headers["If-None-Match"] = _etag;

            var response = await _api.RawAsync(request, ct);
            if (response.Status == 304 && _cached != null)
                return _cached;
            if (response.Status >= 400)
                throw ApiClient.ToException(response);

            var root = JObject.Parse(response.Body);
            _cached = root.Get<Dictionary<string, JToken>>("config") ?? new Dictionary<string, JToken>();
            _etag = response.ETag;
            return _cached;
        }

        /// <summary>Compares this build with the server's "app_version" rule. <paramref name="store"/> is for example "bazaar" or "myket".</summary>
        public Task<VersionCheck> VersionCheckAsync(string version, string store = null, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync<VersionCheck>("GET", ApiClient.Query("/v1/version-check", "store", store, "version", version), null, false, ct);
    }
}
