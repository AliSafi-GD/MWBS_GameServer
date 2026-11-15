using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    /// <summary>Wallet and inventory (read only: the server changes balances).</summary>
    public interface IEconomyApi
    {
        Task<Dictionary<string, long>> WalletAsync(CancellationToken ct = default(CancellationToken));

        Task<Dictionary<string, long>> InventoryAsync(CancellationToken ct = default(CancellationToken));
    }

    /// <summary>Shop products paid with in-game currency. Prices always come from the server.</summary>
    public interface IShopApi
    {
        /// <summary>Products on sale and announced ones (<see cref="ShopProduct.Active"/> false), with what the player may still buy.</summary>
        Task<List<ShopProduct>> ListAsync(string category = null, CancellationToken ct = default(CancellationToken));

        /// <summary>
        /// Buys a product. <paramref name="requestId"/> makes a retry safe: the same id is charged once. When you do not
        /// pass one, a new id is made for this call; keep your own id if you want to retry after a timeout.
        /// Errors: insufficient_funds, purchase_limit_reached, product_unavailable, product_not_found.
        /// </summary>
        Task<ShopPurchase> BuyAsync(string productId, int quantity = 1, string requestId = null, CancellationToken ct = default(CancellationToken));
    }

    /// <summary>Stats, achievements, quests and the daily reward.</summary>
    public interface IProgressApi
    {
        Task<Dictionary<string, long>> StatsAsync(CancellationToken ct = default(CancellationToken));

        Task<List<Goal>> AchievementsAsync(CancellationToken ct = default(CancellationToken));

        /// <summary>Takes the reward once the goal is completed. Errors: goal_not_complete, already_claimed.</summary>
        Task<GoalClaim> ClaimAchievementAsync(string id, CancellationToken ct = default(CancellationToken));

        Task<List<Goal>> QuestsAsync(CancellationToken ct = default(CancellationToken));

        Task<GoalClaim> ClaimQuestAsync(string id, CancellationToken ct = default(CancellationToken));

        Task<DailyStatus> DailyAsync(CancellationToken ct = default(CancellationToken));

        /// <summary>Takes today's login reward. Error: daily_already_claimed.</summary>
        Task<DailyClaim> ClaimDailyAsync(CancellationToken ct = default(CancellationToken));
    }

    /// <summary>Leaderboards and tournaments (read only: the server submits scores).</summary>
    public interface ICompetitionsApi
    {
        /// <param name="around">When above 0, that many rows above and below the player instead of the top.</param>
        /// <param name="period">null/"current", "all", or an earlier "2026-11-08", "2026-W45", "2026-11".</param>
        Task<Board> LeaderboardAsync(string id, int limit = 50, int around = 0, string period = null, CancellationToken ct = default(CancellationToken));

        Task<List<Tournament>> TournamentsAsync(CancellationToken ct = default(CancellationToken));

        Task<TournamentDetail> TournamentAsync(string id, int limit = 50, int around = 0, CancellationToken ct = default(CancellationToken));

        /// <summary>Pays the entry fee and joins. Errors: insufficient_funds, tournament_not_active.</summary>
        Task<Tournament> JoinTournamentAsync(string id, CancellationToken ct = default(CancellationToken));

        /// <summary>After the tournament ended: receive the reward for the final rank (once).</summary>
        Task<TournamentClaim> ClaimTournamentAsync(string id, CancellationToken ct = default(CancellationToken));
    }

    /// <summary>The player's inbox. New messages also arrive live as the realtime event "notification.new".</summary>
    public interface INotificationsApi
    {
        Task<List<Notification>> ListAsync(int limit = 50, bool unreadOnly = false, CancellationToken ct = default(CancellationToken));

        Task MarkReadAsync(long id, CancellationToken ct = default(CancellationToken));

        /// <summary>Takes the attached reward (once). Error: already_claimed / no_reward / expired.</summary>
        Task<NotificationClaim> ClaimAsync(long id, CancellationToken ct = default(CancellationToken));

        Task DeleteAsync(long id, CancellationToken ct = default(CancellationToken));
    }

    /// <summary>Calls server module functions ("RPC"). The payload is any string, usually JSON.</summary>
    public interface IRpcApi
    {
        Task<string> CallAsync(string id, string payload = "", CancellationToken ct = default(CancellationToken));

        /// <summary>Sends <paramref name="request"/> as JSON and reads the JSON answer as <typeparamref name="TResponse"/>.</summary>
        Task<TResponse> CallAsync<TResponse>(string id, object request = null, CancellationToken ct = default(CancellationToken));
    }
}
