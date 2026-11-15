using System.Reflection;
using System.Runtime.Loader;
using GameServer.Abstractions;
using GameServer.Core.GameConfig;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Core;

/// <summary>
/// Loads game modules at startup. Layout: modules/&lt;Name&gt;/&lt;Name&gt;.dll (publish output of the module project),
/// plus an optional modules/&lt;Name&gt;/config/&lt;key&gt;.json folder with the game's config (see GameConfigStartup).
/// A relative ModulesPath is resolved against the host's content root.
/// </summary>
public sealed class ModuleLoader
{
    private readonly ModuleRegistry _registry;
    private readonly ServerOptions _options;
    private readonly ILogger<ModuleLoader> _log;
    private readonly string _root;

    public ModuleLoader(ModuleRegistry registry, IOptions<ServerOptions> options, IHostEnvironment environment, ILogger<ModuleLoader> log)
    {
        _registry = registry;
        _options = options.Value;
        _log = log;
        _root = ContentPaths.Resolve(_options.ModulesPath, environment.ContentRootPath);
    }

    /// <summary>The modules folder as an absolute path.</summary>
    public string ModulesRoot => _root;

    /// <summary>The <c>config</c> folder of every module that has one (same rule as LoadAll: the folder holds Name/Name.dll).</summary>
    public IEnumerable<GameConfigSource> ConfigSources()
    {
        if (!Directory.Exists(_root))
            yield break;
        foreach (var dir in Directory.GetDirectories(_root).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            var folder = Path.Combine(dir, "config");
            if (File.Exists(Path.Combine(dir, name + ".dll")) && Directory.Exists(folder))
                yield return new GameConfigSource("module " + name, folder);
        }
    }

    public void LoadAll()
    {
        var root = _root;
        if (!Directory.Exists(root))
        {
            _log.LogWarning("Modules folder '{Root}' not found. Starting with no modules.", root);
            return;
        }

        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(dir);
            var dll = Path.Combine(dir, name + ".dll");
            if (!File.Exists(dll))
            {
                _log.LogWarning("Skipping '{Dir}': expected {Dll}", dir, dll);
                continue;
            }

            var context = new ModuleLoadContext(dll);
            var assembly = context.LoadFromAssemblyPath(dll);

            var moduleTypes = assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(IServerModule).IsAssignableFrom(t));

            foreach (var type in moduleTypes)
            {
                var module = (IServerModule)Activator.CreateInstance(type)!;
                module.Initialize(_registry);
                _log.LogInformation("Loaded module {Module} from {Assembly}", type.FullName, name);
            }
        }
    }
}

/// <summary>
/// Gives each module its own dependency resolution, but always shares GameServer.Abstractions with the host
/// so that interfaces like IServerModule have the same identity on both sides.
/// </summary>
internal sealed class ModuleLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public ModuleLoadContext(string modulePath)
        : base(Path.GetFileNameWithoutExtension(modulePath), isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(modulePath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name == "GameServer.Abstractions")
            return null; // fall back to the host's copy

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
