using System.Text.Json;
using GameServer.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameServer.Core;

/// <summary>Request body of operations that have none (for hooks that need a request type).</summary>
public sealed record EmptyRequest;

/// <summary>Request JSON of the <c>auth.login</c> after-hook.</summary>
public sealed record LoginEvent(string Provider, bool IsNew);

/// <summary>
/// Runs the hooks modules registered around an operation. Endpoints call <see cref="RunAsync{TReq,TRes}"/>;
/// when no module registered a hook for the operation it just calls the action.
/// </summary>
public sealed class HookRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ModuleRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly ILogger<HookRunner> _log;

    // The context services are resolved on demand: some of them (chat, social) depend on parts that also use hooks.
    public HookRunner(ModuleRegistry registry, IServiceProvider services, ILogger<HookRunner> log)
    {
        _registry = registry;
        _services = services;
        _log = log;
    }

    /// <summary>Before hooks may change <paramref name="request"/>; after hooks see request and response.</summary>
    public async Task<TRes> RunAsync<TReq, TRes>(
        string operation, string userId, TReq request, Func<TReq, Task<TRes>> action, CancellationToken ct = default)
    {
        if (!_registry.HasHooks(operation))
            return await action(request);

        var original = JsonSerializer.Serialize(request, Json);
        TRes? result = default;

        await RunRawAsync(operation, userId, original, async payload =>
        {
            var typed = payload == original
                ? request
                : JsonSerializer.Deserialize<TReq>(payload, Json)
                  ?? throw new GameException("invalid_request", "A hook returned an invalid request.");
            result = await action(typed);
            return JsonSerializer.Serialize(result, Json);
        }, ct);

        return result!;
    }

    public async Task RunVoidAsync<TReq>(
        string operation, string userId, TReq request, Func<TReq, Task> action, CancellationToken ct = default)
    {
        await RunAsync<TReq, EmptyRequest>(operation, userId, request, async r =>
        {
            await action(r);
            return new EmptyRequest();
        }, ct);
    }

    /// <summary>Same as <see cref="RunAsync{TReq,TRes}"/> but request and response are raw strings (module RPCs).</summary>
    public async Task<string> RunRawAsync(
        string operation, string userId, string payload, Func<string, Task<string>> action, CancellationToken ct = default)
    {
        var before = _registry.BeforeHooks(operation);
        var after = _registry.AfterHooks(operation);
        if (before.Length == 0 && after.Length == 0)
            return await action(payload);

        var ctx = CreateContext(operation, userId, ct);

        foreach (var hook in before)
            payload = await hook(ctx, payload) ?? payload;

        var response = await action(payload);

        foreach (var hook in after)
        {
            try
            {
                await hook(ctx, payload, response);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "After hook for '{Operation}' failed.", operation);
            }
        }

        return response;
    }

    /// <summary>Runs only the after hooks, for events that already happened (a login, for example).</summary>
    public async Task NotifyAsync<TReq, TRes>(
        string operation, string userId, TReq request, TRes result, CancellationToken ct = default)
    {
        var after = _registry.AfterHooks(operation);
        if (after.Length == 0)
            return;

        var ctx = CreateContext(operation, userId, ct);
        var requestJson = JsonSerializer.Serialize(request, Json);
        var responseJson = JsonSerializer.Serialize(result, Json);

        foreach (var hook in after)
        {
            try
            {
                await hook(ctx, requestJson, responseJson);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "After hook for '{Operation}' failed.", operation);
            }
        }
    }

    private HookContext CreateContext(string operation, string userId, CancellationToken ct) => new(
        operation, userId,
        _services.GetRequiredService<IStorage>(),
        _services.GetRequiredService<IConfig>(),
        _services.GetRequiredService<IEconomy>(),
        _services.GetRequiredService<ILeaderboards>(),
        _services.GetRequiredService<INotifications>(),
        _services.GetRequiredService<ISocial>(),
        _services.GetRequiredService<IChat>(),
        ct);
}
