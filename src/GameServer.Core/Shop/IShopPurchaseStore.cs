namespace GameServer.Core.Shop;

/// <summary>How many of a product a player bought in one period (period key like 2026-11-15, 2026-W46, 2026-11 or all).</summary>
public sealed record ShopPurchaseCount(string ProductId, string Period, int Count);

/// <summary>
/// The purchase counters behind shop limits ("3 per day"). The shop logic is in <see cref="ShopService"/>;
/// this only stores numbers.
/// </summary>
public interface IShopPurchaseStore
{
    /// <summary>The player's counters in the given periods: (product, period key) -> count.</summary>
    Task<IReadOnlyDictionary<(string Product, string Key), int>> CountsAsync(
        string userId, IReadOnlyCollection<string> periodKeys, CancellationToken ct);

    /// <summary>Every counter of the player, newest period first (admin view).</summary>
    Task<IReadOnlyList<ShopPurchaseCount>> ListAsync(string userId, int limit, CancellationToken ct);

    /// <summary>
    /// Adds <paramref name="quantity"/> to the counter only if it stays within <paramref name="limit"/>; atomic, so two
    /// parallel purchases cannot both pass. False when the limit would be exceeded.
    /// Throws GameException "not_found" when the user does not exist.
    /// </summary>
    Task<bool> TryReserveAsync(string userId, string productId, string periodKey, int quantity, int limit, CancellationToken ct);

    /// <summary>Takes a reservation back (never below zero).</summary>
    Task ReleaseAsync(string userId, string productId, string periodKey, int quantity, CancellationToken ct);
}
