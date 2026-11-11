using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    /// <summary>Sends REST calls with the player's token, refreshes it when it runs out and turns errors into <see cref="ApiException"/>.</summary>
    internal sealed class ApiClient
    {
        private readonly GameClientOptions _options;
        private readonly IHttp _http;
        private readonly IClientStorage _storage;
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);
        private const string SessionKey = "gs.session";

        public Session Session { get; private set; } = new Session();
        public string BaseUrl { get; }

        /// <summary>Raised when the refresh token is no longer accepted: the player has to sign in again.</summary>
        public event Action SessionEnded;

        public Action<string> Log => _options.Log;

        public ApiClient(GameClientOptions options, IHttp http, IClientStorage storage)
        {
            _options = options;
            _http = http;
            _storage = storage;
            BaseUrl = options.BaseUrl.TrimEnd('/');
        }

        // ---- session ------------------------------------------------------------------------------------------

        public void LoadSession()
        {
            var json = _storage.Get(SessionKey);
            if (string.IsNullOrEmpty(json))
                return;
            try
            {
                Session = Json.Deserialize<Session>(json) ?? new Session();
            }
            catch (Exception)
            {
                Session = new Session();
            }
        }

        public void SetSession(LoginResult result)
        {
            Session = new Session
            {
                Token = result.Token,
                RefreshToken = result.RefreshToken ?? Session.RefreshToken,
                UserId = result.UserId,
                Username = result.Username,
                ExpiresUtc = result.ExpiresUtc
            };
            _storage.Set(SessionKey, Json.Serialize(Session));
        }

        public void ClearSession()
        {
            Session = new Session();
            _storage.Delete(SessionKey);
        }

        // ---- calls --------------------------------------------------------------------------------------------

        public async Task<JObject> GetObjectAsync(string path, CancellationToken ct = default(CancellationToken))
        {
            var text = await SendAsync("GET", path, null, true, ct);
            return text.Length == 0 ? new JObject() : JObject.Parse(text);
        }

        public async Task<T> SendAsync<T>(string method, string path, object body = null, bool auth = true, CancellationToken ct = default(CancellationToken))
        {
            var text = await SendAsync(method, path, body, auth, ct);
            if (text.Length == 0)
                return default(T);
            return Json.Deserialize<T>(text);
        }

        /// <summary>Calls the server and returns the body as text ("" for 204). Throws <see cref="ApiException"/> for 4xx/5xx.</summary>
        public async Task<string> SendAsync(string method, string path, object body, bool auth, CancellationToken ct)
        {
            if (auth && Session.RefreshToken != null && Session.AccessTokenExpiresSoon)
                await RefreshAsync(Session.Token, ct);

            var sentToken = Session.Token;
            var response = await RawAsync(method, path, body, auth, ct);

            if (response.Status == 401 && auth && Session.RefreshToken != null)
            {
                if (await RefreshAsync(sentToken, ct))
                    response = await RawAsync(method, path, body, auth, ct);
            }

            if (response.Status >= 400)
                throw ToException(response);

            return response.Body ?? "";
        }

        /// <summary>The access token, refreshed first if it is about to expire (used by the realtime connection).</summary>
        public async Task<string> FreshTokenAsync(CancellationToken ct)
        {
            if (Session.RefreshToken != null && Session.AccessTokenExpiresSoon)
                await RefreshAsync(Session.Token, ct);
            if (string.IsNullOrEmpty(Session.Token))
                throw new ApiException("not_logged_in", "Sign in first.", 401);
            return Session.Token;
        }

        /// <summary>
        /// Trades the refresh token for new tokens. Returns false if the session is over. If another call already
        /// refreshed while this one waited, it returns true without asking the server again.
        /// </summary>
        public async Task<bool> RefreshAsync(string staleToken, CancellationToken ct)
        {
            await _refreshLock.WaitAsync(ct);
            try
            {
                if (Session.Token != staleToken && !string.IsNullOrEmpty(Session.Token))
                    return true;

                var refreshToken = Session.RefreshToken;
                if (string.IsNullOrEmpty(refreshToken))
                    return false;

                var response = await RawAsync("POST", "/v1/auth/refresh", new { refreshToken }, false, ct);
                if (response.Status == 401 || response.Status == 403)
                {
                    ClearSession();
                    SessionEnded?.Invoke();
                    return false;
                }

                if (response.Status >= 400)
                    throw ToException(response);

                SetSession(Json.Deserialize<LoginResult>(response.Body));
                return true;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private Task<HttpResponse> RawAsync(string method, string path, object body, bool auth, CancellationToken ct)
        {
            var request = new HttpRequest
            {
                Method = method,
                Url = BaseUrl + path,
                Body = body == null ? null : (body is string ? (string)body : Json.Serialize(body))
            };

            if (auth && !string.IsNullOrEmpty(Session.Token))
                request.Headers["Authorization"] = "Bearer " + Session.Token;

            return _http.SendAsync(request, ct);
        }

        public Task<HttpResponse> RawAsync(HttpRequest request, CancellationToken ct) => _http.SendAsync(request, ct);

        internal static ApiException ToException(HttpResponse response)
        {
            string code = "http_" + response.Status;
            string message = "The server answered " + response.Status + ".";
            if (!string.IsNullOrEmpty(response.Body))
            {
                try
                {
                    var obj = JObject.Parse(response.Body);
                    if (obj["error"] != null) code = (string)obj["error"];
                    if (obj["message"] != null) message = (string)obj["message"];
                    else if (obj["error"] != null) message = code;
                }
                catch (Exception)
                {
                    // not JSON: keep the generic text
                }
            }

            return new ApiException(code, message, response.Status);
        }

        // ---- helpers for the API classes ----------------------------------------------------------------------

        public static string Query(string path, params object[] pairs)
        {
            var sb = new StringBuilder(path);
            var first = true;
            for (var i = 0; i + 1 < pairs.Length; i += 2)
            {
                var value = pairs[i + 1];
                if (value == null)
                    continue;

                string text;
                if (value is bool)
                    text = (bool)value ? "true" : "false";
                else
                    text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

                sb.Append(first ? '?' : '&');
                first = false;
                sb.Append(Uri.EscapeDataString((string)pairs[i])).Append('=').Append(Uri.EscapeDataString(text));
            }

            return sb.ToString();
        }

        public static string Seg(string value) => Uri.EscapeDataString(value);
    }
}
