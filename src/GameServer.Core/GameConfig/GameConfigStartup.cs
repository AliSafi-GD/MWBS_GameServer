using GameServer.Core.Admin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Core.GameConfig;

/// <summary>
/// Startup step for the game's config files. Two calls, in this order (see Program.cs):
/// <see cref="LoadAndValidate"/> needs no database and stops the server on a bad file; <see cref="ApplyAsync"/> runs after the
/// migrations and writes to the database according to <c>GameServer:GameConfig:Mode</c>.
/// </summary>
public sealed class GameConfigStartup : IGameConfigRecorder
{
    /// <summary>The name used in the config history and the audit log for writes made at startup.</summary>
    public const string Actor = "config-files";

    private readonly ServerOptions _options;
    private readonly string _contentRoot;
    private readonly ModuleLoader _modules;
    private readonly IConfigStore _store;
    private readonly IConfigHistoryStore _history;
    private readonly IAdminAuditLog _audit;
    private readonly ILogger<GameConfigStartup> _log;
    private IReadOnlyList<GameConfigFile> _files = Array.Empty<GameConfigFile>();
    private GameConfigMode _mode;

    public GameConfigStartup(IOptions<ServerOptions> options, IHostEnvironment environment, ModuleLoader modules,
        IConfigStore store, IConfigHistoryStore history, IAdminAuditLog audit, ILogger<GameConfigStartup> log)
    {
        _options = options.Value;
        _contentRoot = environment.ContentRootPath;
        _modules = modules;
        _store = store;
        _history = history;
        _audit = audit;
        _log = log;
    }

    /// <summary>The files read by <see cref="LoadAndValidate"/> (empty when the mode is off).</summary>
    public IReadOnlyList<GameConfigFile> Files => _files;

    /// <summary>Reads the module config folders (and <c>GameConfig:Path</c>) and validates every file. Throws <see cref="GameConfigException"/>.</summary>
    public void LoadAndValidate()
    {
        _mode = GameConfigModes.Parse(_options.GameConfig.Mode);
        if (_mode == GameConfigMode.Off)
        {
            _log.LogInformation("Game config files are off (GameServer:GameConfig:Mode=off).");
            return;
        }

        var sources = _modules.ConfigSources().ToList();
        if (!string.IsNullOrWhiteSpace(_options.GameConfig.Path))
        {
            var extra = ContentPaths.Resolve(_options.GameConfig.Path, _contentRoot);
            if (!Directory.Exists(extra))
                throw new GameConfigException($"GameServer:GameConfig:Path '{extra}' does not exist.");
            sources.Add(new GameConfigSource("GameServer:GameConfig:Path", extra));
        }

        _files = GameConfigLoader.Load(sources);
        if (sources.Count == 0)
            _log.LogWarning("No game config folder found: no module under '{Modules}' has a config/ folder and GameServer:GameConfig:Path is not set. "
                + "If the game's config is meant to come from files, publish the module again so config/*.json is copied next to its DLL.", _modules.ModulesRoot);
        _log.LogInformation("Game config: {Count} key(s) from {Sources} source(s), mode {Mode}.", _files.Count, sources.Count, _mode.ToString().ToLowerInvariant());
    }

    /// <summary>Writes the files into the database as the mode says. Needs the database tables to exist.</summary>
    public Task ApplyAsync(CancellationToken ct = default) =>
        _mode == GameConfigMode.Off || _files.Count == 0
            ? Task.CompletedTask
            : GameConfigApplier.ApplyAsync(_files, _mode, _store, this, _log, ct);

    async Task IGameConfigRecorder.BeforeWriteAsync(GameConfigStep step, CancellationToken ct)
    {
        if (step.Previous is not null)
            await _history.AddConfigHistoryAsync(step.Previous, Actor, ct);
        await _audit.AuditAsync(new AdminActor(Actor, Actor, AdminRole.Admin), "PUT", "/config/" + step.File.Key,
            "from " + step.File.Path + " (" + step.Reason + ")", 200, null, ct);
    }
}
