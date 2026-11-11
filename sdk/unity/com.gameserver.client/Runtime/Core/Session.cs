using System;
using Newtonsoft.Json;

namespace GameServer.Client
{
    /// <summary>The signed-in player's tokens. Saved to <see cref="IClientStorage"/> so the next launch can continue.</summary>
    public sealed class Session
    {
        public string Token { get; set; }
        public string RefreshToken { get; set; }
        public string UserId { get; set; }
        public string Username { get; set; }
        public DateTime ExpiresUtc { get; set; }

        [JsonIgnore]
        public bool IsLoggedIn => !string.IsNullOrEmpty(Token) || !string.IsNullOrEmpty(RefreshToken);

        [JsonIgnore]
        public bool AccessTokenExpiresSoon => string.IsNullOrEmpty(Token) || ExpiresUtc <= DateTime.UtcNow.AddSeconds(30);
    }

    /// <summary>What login and refresh calls answer with.</summary>
    public sealed class LoginResult
    {
        public string Token { get; set; }
        public string RefreshToken { get; set; }
        public string UserId { get; set; }
        public string Username { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }
}
