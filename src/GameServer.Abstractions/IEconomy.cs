namespace GameServer.Abstractions;

/// <summary>
/// A set of balance changes applied together or not at all: currencies (coins, gems...) and stackable items
/// (card ids, boosters...). Negative numbers spend, positive numbers grant. Names are chosen by the game:
/// lowercase letters, digits, '_', '-' or '.', at most 64 characters.
/// </summary>
public sealed class EconomyTransaction
{
    private readonly Dictionary<string, long> _currencies = new();
    private readonly Dictionary<string, long> _items = new();

    /// <param name="reason">Why this happens (e.g. "daily_reward", "shop_purchase"). Saved in the history.</param>
    /// <param name="idempotencyKey">
    /// Optional unique id of this operation per user. If a transaction with the same key was already applied,
    /// nothing changes and the result says AlreadyApplied. Use it to make retries and "once per day" rewards safe.
    /// </param>
    public EconomyTransaction(string reason, string? idempotencyKey = null)
    {
        Reason = reason;
        IdempotencyKey = idempotencyKey;
    }

    public string Reason { get; }
    public string? IdempotencyKey { get; }
    public IReadOnlyDictionary<string, long> Currencies => _currencies;
    public IReadOnlyDictionary<string, long> Items => _items;

    public EconomyTransaction Currency(string name, long delta)
    {
        _currencies[name] = _currencies.GetValueOrDefault(name) + delta;
        return this;
    }

    public EconomyTransaction Item(string itemId, long delta)
    {
        _items[itemId] = _items.GetValueOrDefault(itemId) + delta;
        return this;
    }
}

/// <summary>All balances of one player. Zero-quantity items are left out.</summary>
public sealed record EconomyState(IReadOnlyDictionary<string, long> Wallet, IReadOnlyDictionary<string, long> Items);

public sealed record EconomyResult(bool AlreadyApplied, EconomyState State);

/// <summary>
/// Wallet (currencies) and inventory (stackable items) with safe, atomic changes and a permanent history.
/// Games define their own currency and item names; the server only guarantees the rules:
/// no negative balances, all-or-nothing transactions, no double-applying a transaction with the same key.
/// </summary>
public interface IEconomy
{
    /// <summary>
    /// Applies the transaction. Throws GameException "insufficient_funds" or "insufficient_items" (nothing is changed),
    /// "invalid_transaction" for bad input, "not_found" if the user does not exist.
    /// </summary>
    Task<EconomyResult> ApplyAsync(string userId, EconomyTransaction transaction, CancellationToken ct = default);

    Task<EconomyState> GetAsync(string userId, CancellationToken ct = default);
}
