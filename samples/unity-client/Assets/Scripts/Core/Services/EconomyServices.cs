using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GameServer.Client;

namespace Game.Core
{
    /// <summary>Currencies and items of the signed-in player. Every screen reads balances from here.</summary>
    public sealed class WalletService
    {
        private readonly GameClient _client;
        private Dictionary<string, long> _wallet = new Dictionary<string, long>();
        private Dictionary<string, long> _items = new Dictionary<string, long>();

        public event Action Changed;

        public IReadOnlyDictionary<string, long> Wallet => _wallet;
        public IReadOnlyDictionary<string, long> Items => _items;

        public long Coins => Get(GameIds.Coins);
        public long Gems => Get(GameIds.Gems);

        public WalletService(GameClient client)
        {
            _client = client;
        }

        public long Get(string currency) => _wallet.TryGetValue(currency, out var v) ? v : 0;

        public long Item(string id) => _items.TryGetValue(id, out var v) ? v : 0;

        public async Task RefreshAsync()
        {
            _wallet = await _client.Economy.WalletAsync() ?? new Dictionary<string, long>();
            _items = await _client.Economy.InventoryAsync() ?? new Dictionary<string, long>();
            Changed?.Invoke();
        }

        /// <summary>Takes the balances the server returned with a purchase or a claim.</summary>
        public void Apply(EconomyState state)
        {
            if (state == null)
                return;

            _wallet = state.Wallet ?? _wallet;
            _items = state.Items ?? _items;
            Changed?.Invoke();
        }

        public bool CanAfford(Bundle cost)
        {
            if (cost == null)
                return true;

            return (cost.Wallet == null || cost.Wallet.All(kv => Get(kv.Key) >= kv.Value)) &&
                   (cost.Items == null || cost.Items.All(kv => Item(kv.Key) >= kv.Value));
        }
    }

    public sealed class ShopService
    {
        private readonly GameClient _client;
        private readonly WalletService _wallet;

        public event Action Changed;

        public List<ShopProduct> Products { get; private set; } = new List<ShopProduct>();

        public ShopService(GameClient client, WalletService wallet)
        {
            _client = client;
            _wallet = wallet;
        }

        public async Task RefreshAsync()
        {
            Products = await _client.Shop.ListAsync() ?? new List<ShopProduct>();
            Changed?.Invoke();
        }

        public bool IsAvailable(ShopProduct p) => p.Active && (!p.Remaining.HasValue || p.Remaining.Value > 0);

        /// <summary>Buys one. A fresh request id per tap makes a retry after a lost answer safe.</summary>
        public async Task<ShopPurchase> BuyAsync(string productId)
        {
            var purchase = await _client.Shop.BuyAsync(productId, 1, Guid.NewGuid().ToString("N"));
            _wallet.Apply(purchase.State);

            // Read the balances again instead of trusting the purchase answer alone: the new item must show up at once.
            await Task.WhenAll(RefreshAsync(), _wallet.RefreshAsync());
            return purchase;
        }
    }

    /// <summary>Quests, achievements and the daily reward, plus how many of them are waiting to be claimed.</summary>
    public sealed class GoalsService
    {
        private readonly GameClient _client;
        private readonly WalletService _wallet;

        public event Action Changed;

        public List<Goal> Quests { get; private set; } = new List<Goal>();
        public List<Goal> Achievements { get; private set; } = new List<Goal>();
        public DailyStatus Daily { get; private set; }
        public long Wins { get; private set; }
        public long MatchesPlayed { get; private set; }

        public bool DailyReady => Daily != null && !Daily.ClaimedToday;

        public int ClaimableCount =>
            Quests.Count(g => g.Completed && !g.Claimed) + Achievements.Count(g => g.Completed && !g.Claimed) + (DailyReady ? 1 : 0);

        public GoalsService(GameClient client, WalletService wallet)
        {
            _client = client;
            _wallet = wallet;
        }

        public async Task RefreshAsync()
        {
            var quests = _client.Progress.QuestsAsync();
            var achievements = _client.Progress.AchievementsAsync();
            var daily = _client.Progress.DailyAsync();
            var stats = _client.Progress.StatsAsync();
            await Task.WhenAll(quests, achievements, daily, stats);

            Quests = quests.Result ?? new List<Goal>();
            Achievements = achievements.Result ?? new List<Goal>();
            Daily = daily.Result;
            var s = stats.Result ?? new Dictionary<string, long>();
            Wins = s.TryGetValue(GameIds.StatWins, out var w) ? w : 0;
            MatchesPlayed = s.TryGetValue(GameIds.StatPlayed, out var p) ? p : 0;
            Changed?.Invoke();
        }

        public async Task<GoalClaim> ClaimQuestAsync(string id)
        {
            var claim = await _client.Progress.ClaimQuestAsync(id);
            _wallet.Apply(claim.State);
            await RefreshAsync();
            return claim;
        }

        public async Task<GoalClaim> ClaimAchievementAsync(string id)
        {
            var claim = await _client.Progress.ClaimAchievementAsync(id);
            _wallet.Apply(claim.State);
            await RefreshAsync();
            return claim;
        }

        public async Task<DailyClaim> ClaimDailyAsync()
        {
            var claim = await _client.Progress.ClaimDailyAsync();
            _wallet.Apply(claim.State);
            await RefreshAsync();
            return claim;
        }
    }
}
