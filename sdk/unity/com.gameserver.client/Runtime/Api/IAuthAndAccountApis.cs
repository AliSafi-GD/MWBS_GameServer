using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    /// <summary>Signing in and out. A successful login stores the session, so every other call is authenticated.</summary>
    public interface IAuthApi
    {
        Session Session { get; }

        bool IsLoggedIn { get; }

        string UserId { get; }

        /// <summary>Raised when the server ended the session (refresh token rejected): show the login screen.</summary>
        event Action SessionEnded;

        /// <summary>The id this install uses to sign in as a guest. Created once and kept.</summary>
        string DeviceId { get; }

        /// <summary>
        /// Guest login with this install's device id. The account is created the first time (unless
        /// <paramref name="create"/> is false, which only signs in to an existing one).
        /// </summary>
        Task<LoginResult> LoginDeviceAsync(string username = null, bool create = true, string deviceId = null, CancellationToken ct = default(CancellationToken));

        /// <summary>
        /// Which login methods the server has switched on, e.g. "device", "googleplay", "phone". "device" is the guest login
        /// (<see cref="LoginDeviceAsync"/>); it is missing when the server owner turned guest login off, so show only the buttons listed here.
        /// </summary>
        Task<List<string>> ProvidersAsync(CancellationToken ct = default(CancellationToken));

        /// <summary>First step of providers that need one, e.g. sending the SMS code. <paramref name="credential"/> is provider specific.</summary>
        Task BeginAsync(string provider, object credential, CancellationToken ct = default(CancellationToken));

        Task<LoginResult> LoginAsync(string provider, object credential, string username = null, bool create = true, CancellationToken ct = default(CancellationToken));

        /// <summary>
        /// Continues with the session saved on this device: refreshes it and returns true, or returns false when there is
        /// none or it has ended (then call <see cref="LoginDeviceAsync"/>). Network errors are thrown.
        /// </summary>
        Task<bool> TryRestoreSessionAsync(CancellationToken ct = default(CancellationToken));

        /// <summary>Ends this device's session.</summary>
        Task LogoutAsync(CancellationToken ct = default(CancellationToken));

        /// <summary>Ends the sessions on every device.</summary>
        Task LogoutEverywhereAsync(CancellationToken ct = default(CancellationToken));
    }

    /// <summary>The signed-in player's account and other players' public profiles.</summary>
    public interface IAccountApi
    {
        Task<Account> GetAsync(CancellationToken ct = default(CancellationToken));

        Task<Account> UpdateAsync(ProfileUpdate update, CancellationToken ct = default(CancellationToken));

        /// <summary>Deletes the account and everything stored for it. This cannot be undone.</summary>
        Task DeleteAsync(CancellationToken ct = default(CancellationToken));

        /// <summary>Adds a login method (for example Google) so progress survives a lost device.</summary>
        Task<List<IdentityInfo>> LinkAsync(string provider, object credential, CancellationToken ct = default(CancellationToken));

        Task UnlinkAsync(string provider, CancellationToken ct = default(CancellationToken));

        /// <summary>Public profiles of other players (up to 100 ids).</summary>
        Task<List<PublicProfile>> GetUsersAsync(IEnumerable<string> ids, CancellationToken ct = default(CancellationToken));
    }

    /// <summary>Public settings the game can read before login.</summary>
    public interface IConfigApi
    {
        /// <summary>
        /// The server's public config keys. Remembers the last answer and sends its ETag, so an unchanged config
        /// costs almost nothing.
        /// </summary>
        Task<Dictionary<string, JToken>> GetAsync(CancellationToken ct = default(CancellationToken));

        /// <summary>Compares this build with the server's "app_version" rule. <paramref name="store"/> is for example "bazaar" or "myket".</summary>
        Task<VersionCheck> VersionCheckAsync(string version, string store = null, CancellationToken ct = default(CancellationToken));
    }
}
