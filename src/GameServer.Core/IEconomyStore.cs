using GameServer.Abstractions;

namespace GameServer.Core;

/// <summary>One line of the permanent history. Kind is "wallet" or "inventory"; Name is the currency or item id.</summary>
public sealed record LedgerEntry(
    long Id, string Kind, string Name, long Delta, long BalanceAfter, string Reason, string? Ref, DateTime CreatedUtc);

/// <summary>The economy as the host and admin panel see it (modules only get <see cref="IEconomy"/>).</summary>
public interface IEconomyStore : IEconomy
{
    /// <summary>Newest first. Pass the last Id you received as <paramref name="beforeId"/> to get the next page.</summary>
    Task<IReadOnlyList<LedgerEntry>> LedgerAsync(string userId, int limit, long? beforeId, CancellationToken ct = default);
}
