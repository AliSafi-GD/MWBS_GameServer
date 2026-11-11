using System;
using System.Threading.Tasks;
using GameServer.Client;

namespace Game.Core
{
    /// <summary>The signed-in player's own profile: name, username, avatar.</summary>
    public sealed class ProfileService
    {
        private readonly GameClient _client;
        private readonly Action<Account> _onUpdated;

        public event Action Changed;

        public Account Account { get; private set; }

        public ProfileService(GameClient client, Account account, Action<Account> onUpdated)
        {
            _client = client;
            Account = account;
            _onUpdated = onUpdated;
        }

        /// <summary>Saves only what changed (null = leave as it is). The server checks the rules (for example a free username).</summary>
        public async Task<Account> SaveAsync(string displayName, string username, string avatar)
        {
            var update = new ProfileUpdate { DisplayName = displayName, Username = username, Avatar = avatar };
            Account = await _client.Account.UpdateAsync(update);
            _onUpdated(Account);
            Changed?.Invoke();
            return Account;
        }

        /// <summary>Deletes the account and everything stored for it. This cannot be undone.</summary>
        public Task DeleteAsync() => _client.Account.DeleteAsync();
    }
}
