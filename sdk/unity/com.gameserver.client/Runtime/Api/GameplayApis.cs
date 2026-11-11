using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    /// <summary>Wallet and inventory (read only: the server changes balances).</summary>
    public sealed class EconomyApi
    {
        private readonly ApiClient _api;

        internal EconomyApi(ApiClient api)
        {
            _api = api;
        }

        public async Task<Dictionary<string, long>> WalletAsync(CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync("/v1/wallet", ct);
            return root.Get<Dictionary<string, long>>("wallet") ?? new Dictionary<string, long>();
        }

        public async Task<Dictionary<string, long>> InventoryAsync(CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync("/v1/inventory", ct);
            return root.Get<Dictionary<string, long>>("items") ?? new Dictionary<string, long>();
        }
    }

    /// <summary>Shop products paid with in-game currency. Prices always come from the server.</summary>
    public sealed class ShopApi
    {
        private readonly ApiClient _api;

        internal ShopApi(ApiClient api)
        {
            _api = api;
        }

        /// <summary>Products on sale and announced ones (<see cref="ShopProduct.Active"/> false), with what the player may still buy.</summary>
        public async Task<List<ShopProduct>> ListAsync(string category = null, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync(ApiClient.Query("/v1/shop", "category", category), ct);
            return root.Get<List<ShopProduct>>("products");
        }

        /// <summary>
        /// Buys a product. <paramref name="requestId"/> makes a retry safe: the same id is charged once. When you do not
        /// pass one, a new id is made for this call; keep your own id if you want to retry after a timeout.
        /// Errors: insufficient_funds, purchase_limit_reached, product_unavailable, product_not_found.
        /// </summary>
        public async Task<ShopPurchase> BuyAsync(string productId, int quantity = 1, string requestId = null, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.SendAsync<JObject>("POST", "/v1/shop/" + ApiClient.Seg(productId) + "/buy",
                new { quantity, requestId = requestId ?? Guid.NewGuid().ToString("N") }, true, ct);
            return root.Get<ShopPurchase>("purchase");
        }
    }

    /// <summary>Stats, achievements, quests and the daily reward.</summary>
    public sealed class ProgressApi
    {
        private readonly ApiClient _api;

        internal ProgressApi(ApiClient api)
        {
            _api = api;
        }

        public async Task<Dictionary<string, long>> StatsAsync(CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync("/v1/stats", ct);
            return root.Get<Dictionary<string, long>>("stats") ?? new Dictionary<string, long>();
        }

        public async Task<List<Goal>> AchievementsAsync(CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync("/v1/achievements", ct);
            return root.Get<List<Goal>>("achievements");
        }

        /// <summary>Takes the reward once the goal is completed. Errors: goal_not_complete, already_claimed.</summary>
        public async Task<GoalClaim> ClaimAchievementAsync(string id, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.SendAsync<JObject>("POST", "/v1/achievements/" + ApiClient.Seg(id) + "/claim", null, true, ct);
            return root.Get<GoalClaim>("claim");
        }

        public async Task<List<Goal>> QuestsAsync(CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync("/v1/quests", ct);
            return root.Get<List<Goal>>("quests");
        }

        public async Task<GoalClaim> ClaimQuestAsync(string id, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.SendAsync<JObject>("POST", "/v1/quests/" + ApiClient.Seg(id) + "/claim", null, true, ct);
            return root.Get<GoalClaim>("claim");
        }

        public async Task<DailyStatus> DailyAsync(CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync("/v1/daily", ct);
            return root.Get<DailyStatus>("daily");
        }

        /// <summary>Takes today's login reward. Error: daily_already_claimed.</summary>
        public async Task<DailyClaim> ClaimDailyAsync(CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.SendAsync<JObject>("POST", "/v1/daily/claim", null, true, ct);
            return root.Get<DailyClaim>("claim");
        }
    }

    /// <summary>Leaderboards and tournaments (read only: the server submits scores).</summary>
    public sealed class CompetitionsApi
    {
        private readonly ApiClient _api;

        internal CompetitionsApi(ApiClient api)
        {
            _api = api;
        }

        /// <param name="around">When above 0, that many rows above and below the player instead of the top.</param>
        /// <param name="period">null/"current", "all", or an earlier "2026-11-08", "2026-W45", "2026-11".</param>
        public Task<Board> LeaderboardAsync(string id, int limit = 50, int around = 0, string period = null, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync<Board>("GET", ApiClient.Query("/v1/leaderboards/" + ApiClient.Seg(id),
                "period", period, "limit", limit, "around", around), null, true, ct);

        public async Task<List<Tournament>> TournamentsAsync(CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync("/v1/tournaments", ct);
            return root.Get<List<Tournament>>("tournaments");
        }

        public Task<TournamentDetail> TournamentAsync(string id, int limit = 50, int around = 0, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync<TournamentDetail>("GET", ApiClient.Query("/v1/tournaments/" + ApiClient.Seg(id),
                "limit", limit, "around", around), null, true, ct);

        /// <summary>Pays the entry fee and joins. Errors: insufficient_funds, tournament_not_active.</summary>
        public async Task<Tournament> JoinTournamentAsync(string id, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.SendAsync<JObject>("POST", "/v1/tournaments/" + ApiClient.Seg(id) + "/join", null, true, ct);
            return root.Get<Tournament>("tournament");
        }

        /// <summary>After the tournament ended: receive the reward for the final rank (once).</summary>
        public Task<TournamentClaim> ClaimTournamentAsync(string id, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync<TournamentClaim>("POST", "/v1/tournaments/" + ApiClient.Seg(id) + "/claim", null, true, ct);
    }

    /// <summary>The player's inbox. New messages also arrive live as the realtime event "notification.new".</summary>
    public sealed class NotificationsApi
    {
        private readonly ApiClient _api;

        internal NotificationsApi(ApiClient api)
        {
            _api = api;
        }

        public async Task<List<Notification>> ListAsync(int limit = 50, bool unreadOnly = false, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync(ApiClient.Query("/v1/notifications", "limit", limit, "unread", unreadOnly), ct);
            return root.Get<List<Notification>>("notifications");
        }

        public Task MarkReadAsync(long id, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync("POST", "/v1/notifications/" + id + "/read", null, true, ct);

        /// <summary>Takes the attached reward (once). Error: already_claimed / no_reward / expired.</summary>
        public Task<NotificationClaim> ClaimAsync(long id, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync<NotificationClaim>("POST", "/v1/notifications/" + id + "/claim", null, true, ct);

        public Task DeleteAsync(long id, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync("DELETE", "/v1/notifications/" + id, null, true, ct);
    }

    /// <summary>Calls server module functions ("RPC"). The payload is any string, usually JSON.</summary>
    public sealed class RpcApi
    {
        private readonly ApiClient _api;

        internal RpcApi(ApiClient api)
        {
            _api = api;
        }

        public Task<string> CallAsync(string id, string payload = "", CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync("POST", "/v1/rpc/" + ApiClient.Seg(id), payload ?? "", true, ct);

        /// <summary>Sends <paramref name="request"/> as JSON and reads the JSON answer as <typeparamref name="TResponse"/>.</summary>
        public async Task<TResponse> CallAsync<TResponse>(string id, object request = null, CancellationToken ct = default(CancellationToken))
        {
            var text = await CallAsync(id, request == null ? "" : Json.Serialize(request), ct);
            return string.IsNullOrWhiteSpace(text) ? default(TResponse) : Json.Deserialize<TResponse>(text);
        }
    }
}
