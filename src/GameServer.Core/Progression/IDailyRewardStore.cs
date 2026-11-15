namespace GameServer.Core.Progression;

/// <summary>A player's login streak: how many days in a row, and the game day of the last claim (null = never).</summary>
public sealed record DailyState(int Streak, DateOnly? LastDate);

/// <summary>The streak rows behind the daily reward. The rules are in <see cref="DailyStreak"/> and <see cref="DailyRewardService"/>.</summary>
public interface IDailyRewardStore
{
    /// <summary>The player's streak; (0, null) when they never claimed.</summary>
    Task<DailyState> GetAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Starts a claim: reads the streak and keeps it locked until the claim is committed or disposed, so two phones
    /// cannot claim the same day. Throws GameException "not_found" when the user does not exist.
    /// </summary>
    Task<IDailyClaim> BeginClaimAsync(string userId, CancellationToken ct);
}

/// <summary>An open claim. Disposing it without <see cref="CommitAsync"/> undoes <see cref="SaveAsync"/>.</summary>
public interface IDailyClaim : IAsyncDisposable
{
    DailyState State { get; }

    Task SaveAsync(int streak, DateOnly date, CancellationToken ct);

    Task CommitAsync(CancellationToken ct);
}
