using Microsoft.Extensions.Logging;

namespace GameServer.Core.GameConfig;

/// <summary>Keeps the trail of a startup write: the old value goes to the config history, the write to the audit log.</summary>
public interface IGameConfigRecorder
{
    /// <summary>Called right before <paramref name="step"/> is written. <c>step.Previous</c> is the value that is about to be replaced.</summary>
    Task BeforeWriteAsync(GameConfigStep step, CancellationToken ct);
}

/// <summary>Runs a <see cref="GameConfigPlanner"/> plan against the config store and logs one line per key.</summary>
public static class GameConfigApplier
{
    public static async Task<IReadOnlyList<GameConfigStep>> ApplyAsync(
        IReadOnlyList<GameConfigFile> files, GameConfigMode mode, IConfigStore store,
        IGameConfigRecorder? recorder, ILogger log, CancellationToken ct = default)
    {
        var plan = GameConfigPlanner.Plan(files, await store.ListAsync(ct), mode);
        foreach (var step in plan)
        {
            var file = step.File;
            if (step.Action == GameConfigAction.Skip)
            {
                log.LogInformation("Game config '{Key}': skipped, {Reason} ({Path})", file.Key, step.Reason, file.Path);
                continue;
            }

            if (recorder is not null)
                await recorder.BeforeWriteAsync(step, ct);
            await store.SetAsync(file.Key, file.Value, file.EffectivePublic, ct);
            log.LogInformation("Game config '{Key}': written, {Reason} ({Path})", file.Key, step.Reason, file.Path);
        }
        return plan;
    }
}
