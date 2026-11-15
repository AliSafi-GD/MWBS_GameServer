using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    // ---- economy ----------------------------------------------------------------------------------------------

    /// <summary>Currencies and items, as a reward or a price.</summary>
    public class Bundle
    {
        public Dictionary<string, long> Wallet { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> Items { get; set; } = new Dictionary<string, long>();
    }

    public class EconomyState
    {
        public Dictionary<string, long> Wallet { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> Items { get; set; } = new Dictionary<string, long>();
    }

    // ---- shop -------------------------------------------------------------------------------------------------

    public class PurchaseLimit
    {
        /// <summary>"day", "week", "month" or "lifetime".</summary>
        public string Per { get; set; }
        public int Count { get; set; }
    }

    public class ShopProduct
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public Bundle Cost { get; set; }
        public Bundle Gives { get; set; }
        public PurchaseLimit Limit { get; set; }
        /// <summary>How many more times this player can buy it, or null when there is no limit.</summary>
        public int? Remaining { get; set; }
        /// <summary>False for products announced but not yet on sale (or already past their end date).</summary>
        public bool Active { get; set; }
        public DateTime? StartUtc { get; set; }
        public DateTime? EndUtc { get; set; }
    }

    public class ShopPurchase
    {
        public string ProductId { get; set; }
        public int Quantity { get; set; }
        /// <summary>True when the same requestId was sent before: nothing was charged again.</summary>
        public bool AlreadyApplied { get; set; }
        public EconomyState State { get; set; }
    }

    // ---- progress ---------------------------------------------------------------------------------------------

    public class Goal
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public string Stat { get; set; }
        public long Target { get; set; }
        public long Progress { get; set; }
        public bool Completed { get; set; }
        public bool Claimed { get; set; }
        public Bundle Reward { get; set; }
        /// <summary>"all", "day", "week" or "month".</summary>
        public string Period { get; set; }
        public DateTime? ResetsUtc { get; set; }
    }

    public class GoalClaim
    {
        public string Id { get; set; }
        public Bundle Reward { get; set; }
        public EconomyState State { get; set; }
    }

    public class DailyStatus
    {
        public int Streak { get; set; }
        public bool ClaimedToday { get; set; }
        public int NextDay { get; set; }
        public List<Bundle> Rewards { get; set; }
        public DateTime NextClaimUtc { get; set; }
        public int UtcOffsetMinutes { get; set; }
    }

    public class DailyClaim
    {
        public int Day { get; set; }
        public int Streak { get; set; }
        public Bundle Reward { get; set; }
        public EconomyState State { get; set; }
    }
}
