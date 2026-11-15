using System.Text.Json;
using GameServer.Abstractions;
using GameServer.Core.Progression;
using GameServer.Core.Shop;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace GameServer.Core.Tests;

/// <summary>
/// The real services from AddGameServerCore with the database parts swapped for the in-memory fakes below and a
/// FakeTimeProvider as the clock. Nothing opens a database connection.
/// </summary>
internal sealed class TestServer : IDisposable
{
    private readonly ServiceProvider _sp;

    public TestServer(DateTime startUtc)
    {
        Clock = new FakeTimeProvider(new DateTimeOffset(startUtc));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Host=localhost;Database=none;Username=x;Password=x",
            ["GameServer:Match:Transports"] = "",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddGameServerCore(config, realtime: rt => rt.Add("websocket", _ => throw new NotSupportedException()));

        // Registered after AddGameServerCore, so these win over the Postgres ones.
        services.AddSingleton<IConfigStore>(Config);
        services.AddSingleton<IEconomy>(Economy);
        services.AddSingleton<IShopPurchaseStore>(Purchases);
        services.AddSingleton<IDailyRewardStore>(Daily);
        services.AddSingleton<IProgressStore>(Progress);
        _sp = services.BuildServiceProvider();
    }

    public FakeTimeProvider Clock { get; }
    public MemoryConfig Config { get; } = new();
    public MemoryEconomy Economy { get; } = new();
    public MemoryShopPurchases Purchases { get; } = new();
    public MemoryDailyRewards Daily { get; } = new();
    public MemoryProgress Progress { get; } = new();

    public T Get<T>() where T : notnull => _sp.GetRequiredService<T>();

    public void Dispose() => _sp.Dispose();
}

internal sealed class MemoryConfig : IConfigStore
{
    private readonly Dictionary<string, ConfigEntry> _entries = new(StringComparer.Ordinal);

    public void Set(string key, string json) =>
        _entries[key] = new ConfigEntry(key, JsonDocument.Parse(json).RootElement.Clone(), false, DateTime.UtcNow);

    public Task<IReadOnlyList<ConfigEntry>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConfigEntry>>(_entries.Values.ToList());

    public Task<ConfigEntry?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_entries.GetValueOrDefault(key));

    public Task<ConfigEntry> SetAsync(string key, JsonElement value, bool isPublic, CancellationToken ct = default) =>
        Task.FromResult(_entries[key] = new ConfigEntry(key, value.Clone(), isPublic, DateTime.UtcNow));

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => Task.FromResult(_entries.Remove(key));
}

/// <summary>Wallets with the same rules as the real economy: no negative balance, a key is applied once.</summary>
internal sealed class MemoryEconomy : IEconomy
{
    private readonly Dictionary<string, Dictionary<string, long>> _wallets = new();
    private readonly Dictionary<string, Dictionary<string, long>> _items = new();
    private readonly HashSet<(string User, string Key)> _applied = new();

    /// <summary>The next ApplyAsync throws this (to test what the services undo).</summary>
    public Exception? FailNext { get; set; }

    public long Balance(string userId, string currency) => _wallets.GetValueOrDefault(userId)?.GetValueOrDefault(currency) ?? 0;

    public void Give(string userId, string currency, long amount) => Wallet(userId)[currency] = Balance(userId, currency) + amount;

    private Dictionary<string, long> Wallet(string userId) =>
        _wallets.TryGetValue(userId, out var w) ? w : _wallets[userId] = new Dictionary<string, long>();

    private Dictionary<string, long> Items(string userId) =>
        _items.TryGetValue(userId, out var i) ? i : _items[userId] = new Dictionary<string, long>();

    public Task<EconomyResult> ApplyAsync(string userId, EconomyTransaction transaction, CancellationToken ct = default)
    {
        if (FailNext is { } fail)
        {
            FailNext = null;
            throw fail;
        }

        if (transaction.IdempotencyKey is { } key && _applied.Contains((userId, key)))
            return Task.FromResult(new EconomyResult(true, State(userId)));

        var wallet = Wallet(userId);
        var items = Items(userId);
        if (transaction.Currencies.Any(c => wallet.GetValueOrDefault(c.Key) + c.Value < 0))
            throw new GameException("insufficient_funds", "Not enough.");
        if (transaction.Items.Any(i => items.GetValueOrDefault(i.Key) + i.Value < 0))
            throw new GameException("insufficient_items", "Not enough.");

        foreach (var (name, delta) in transaction.Currencies) wallet[name] = wallet.GetValueOrDefault(name) + delta;
        foreach (var (id, delta) in transaction.Items) items[id] = items.GetValueOrDefault(id) + delta;
        if (transaction.IdempotencyKey is { } k)
            _applied.Add((userId, k));
        return Task.FromResult(new EconomyResult(false, State(userId)));
    }

    public Task<EconomyState> GetAsync(string userId, CancellationToken ct = default) => Task.FromResult(State(userId));

    private EconomyState State(string userId) =>
        new(new Dictionary<string, long>(Wallet(userId)), Items(userId).Where(i => i.Value != 0).ToDictionary());
}

internal sealed class MemoryShopPurchases : IShopPurchaseStore
{
    public Dictionary<(string User, string Product, string Key), int> Counts { get; } = new();

    public Task<IReadOnlyDictionary<(string Product, string Key), int>> CountsAsync(
        string userId, IReadOnlyCollection<string> periodKeys, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<(string, string), int>>(Counts
            .Where(c => c.Key.User == userId && periodKeys.Contains(c.Key.Key))
            .ToDictionary(c => (c.Key.Product, c.Key.Key), c => c.Value));

    public Task<IReadOnlyList<ShopPurchaseCount>> ListAsync(string userId, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ShopPurchaseCount>>(Counts.Where(c => c.Key.User == userId)
            .Select(c => new ShopPurchaseCount(c.Key.Product, c.Key.Key, c.Value)).Take(limit).ToList());

    public Task<bool> TryReserveAsync(string userId, string productId, string periodKey, int quantity, int limit, CancellationToken ct)
    {
        var used = Counts.GetValueOrDefault((userId, productId, periodKey));
        if (used + quantity > limit)
            return Task.FromResult(false);
        Counts[(userId, productId, periodKey)] = used + quantity;
        return Task.FromResult(true);
    }

    public Task ReleaseAsync(string userId, string productId, string periodKey, int quantity, CancellationToken ct)
    {
        if (Counts.TryGetValue((userId, productId, periodKey), out var used))
            Counts[(userId, productId, periodKey)] = Math.Max(used - quantity, 0);
        return Task.CompletedTask;
    }
}

/// <summary>A claim changes the stored streak only when it is committed, like the database transaction.</summary>
internal sealed class MemoryDailyRewards : IDailyRewardStore
{
    public Dictionary<string, DailyState> States { get; } = new();

    public Task<DailyState> GetAsync(string userId, CancellationToken ct) =>
        Task.FromResult(States.GetValueOrDefault(userId) ?? new DailyState(0, null));

    public async Task<IDailyClaim> BeginClaimAsync(string userId, CancellationToken ct) =>
        new Claim(this, userId, await GetAsync(userId, ct));

    private sealed class Claim : IDailyClaim
    {
        private readonly MemoryDailyRewards _owner;
        private readonly string _userId;
        private DailyState? _pending;

        public Claim(MemoryDailyRewards owner, string userId, DailyState state)
        {
            _owner = owner;
            _userId = userId;
            State = state;
        }

        public DailyState State { get; }

        public Task SaveAsync(int streak, DateOnly date, CancellationToken ct)
        {
            _pending = new DailyState(streak, date);
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken ct)
        {
            if (_pending is not null)
                _owner.States[_userId] = _pending;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class MemoryProgress : IProgressStore
{
    private readonly Dictionary<(string User, string Stat, string Key), long> _stats = new();
    private readonly HashSet<(string User, string Kind, string Goal, string Key)> _claims = new();

    public Task<IReadOnlyDictionary<string, StatChange>> ChangeStatAsync(
        string userId, string stat, IReadOnlyCollection<string> periodKeys, long amount, bool max, CancellationToken ct)
    {
        var changes = new Dictionary<string, StatChange>();
        foreach (var key in periodKeys)
        {
            var old = _stats.GetValueOrDefault((userId, stat, key));
            var value = max ? Math.Max(old, amount) : old + amount;
            _stats[(userId, stat, key)] = value;
            changes[key] = new StatChange(old, value);
        }
        return Task.FromResult<IReadOnlyDictionary<string, StatChange>>(changes);
    }

    public Task<IReadOnlyDictionary<string, long>> LifetimeStatsAsync(string userId, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, long>>(_stats
            .Where(s => s.Key.User == userId && s.Key.Key == "all")
            .OrderBy(s => s.Key.Stat).Take(limit)
            .ToDictionary(s => s.Key.Stat, s => s.Value));

    public Task<IReadOnlyDictionary<(string Stat, string Key), long>> StatValuesAsync(
        string userId, IReadOnlyCollection<string> stats, IReadOnlyCollection<string> periodKeys, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<(string, string), long>>(_stats
            .Where(s => s.Key.User == userId && stats.Contains(s.Key.Stat) && periodKeys.Contains(s.Key.Key))
            .ToDictionary(s => (s.Key.Stat, s.Key.Key), s => s.Value));

    public Task<IReadOnlySet<(string Goal, string Key)>> ClaimsAsync(
        string userId, string kind, IReadOnlyCollection<string> periodKeys, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<(string, string)>>(_claims
            .Where(c => c.User == userId && c.Kind == kind && periodKeys.Contains(c.Key))
            .Select(c => (c.Goal, c.Key)).ToHashSet());

    public Task<bool> TryClaimAsync(string userId, string kind, string goalId, string periodKey, CancellationToken ct) =>
        Task.FromResult(_claims.Add((userId, kind, goalId, periodKey)));

    public Task UnclaimAsync(string userId, string kind, string goalId, string periodKey, CancellationToken ct)
    {
        _claims.Remove((userId, kind, goalId, periodKey));
        return Task.CompletedTask;
    }
}
