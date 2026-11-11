using System.Collections.Concurrent;
using GameServer.Abstractions;

namespace GameServer.Core;

/// <summary>Holds everything modules registered. Implements the initializer that modules receive.</summary>
public sealed class ModuleRegistry : IModuleInitializer
{
    private static readonly BeforeHook[] NoBefore = Array.Empty<BeforeHook>();
    private static readonly AfterHook[] NoAfter = Array.Empty<AfterHook>();

    private readonly ConcurrentDictionary<string, RpcHandler> _rpcs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BeforeHook[]> _before = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AfterHook[]> _after = new(StringComparer.Ordinal);

    public void RegisterRpc(string id, RpcHandler handler)
    {
        if (!_rpcs.TryAdd(id, handler))
            throw new InvalidOperationException($"RPC '{id}' is already registered.");
    }

    public void RegisterBefore(string operation, BeforeHook hook)
    {
        CheckOperation(operation);
        ArgumentNullException.ThrowIfNull(hook);
        _before.AddOrUpdate(operation, _ => new[] { hook }, (_, old) => old.Append(hook).ToArray());
    }

    public void RegisterAfter(string operation, AfterHook hook)
    {
        CheckOperation(operation);
        ArgumentNullException.ThrowIfNull(hook);
        _after.AddOrUpdate(operation, _ => new[] { hook }, (_, old) => old.Append(hook).ToArray());
    }

    private readonly ConcurrentDictionary<string, Func<MatchHandler>> _matchHandlers = new(StringComparer.Ordinal);

    public void RegisterMatchHandler(string name, Func<MatchHandler> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A match handler needs a name.", nameof(name));
        if (!_matchHandlers.TryAdd(name, factory))
            throw new InvalidOperationException($"Match handler '{name}' is already registered.");
    }

    public bool TryGetMatchHandler(string name, out Func<MatchHandler> factory) => _matchHandlers.TryGetValue(name, out factory!);

    public IReadOnlyCollection<string> MatchHandlerNames => _matchHandlers.Keys.ToArray();

    public bool TryGetRpc(string id, out RpcHandler handler) => _rpcs.TryGetValue(id, out handler!);

    public IReadOnlyCollection<string> RpcIds => _rpcs.Keys.ToArray();

    public BeforeHook[] BeforeHooks(string operation) => _before.TryGetValue(operation, out var hooks) ? hooks : NoBefore;

    public AfterHook[] AfterHooks(string operation) => _after.TryGetValue(operation, out var hooks) ? hooks : NoAfter;

    public bool HasHooks(string operation) => _before.ContainsKey(operation) || _after.ContainsKey(operation);

    private static void CheckOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A hook needs an operation name (see HookOps).", nameof(operation));
    }
}
