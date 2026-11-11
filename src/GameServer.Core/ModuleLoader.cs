using System.Reflection;
using System.Runtime.Loader;
using GameServer.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Core;

/// <summary>
/// Loads game modules at startup. Layout: modules/&lt;Name&gt;/&lt;Name&gt;.dll (publish output of the module project).
/// </summary>
public sealed class ModuleLoader
{
    private readonly ModuleRegistry _registry;
    private readonly ServerOptions _options;
    private readonly ILogger<ModuleLoader> _log;

    public ModuleLoader(ModuleRegistry registry, IOptions<ServerOptions> options, ILogger<ModuleLoader> log)
    {
        _registry = registry;
        _options = options.Value;
        _log = log;
    }

    public void LoadAll()
    {
        var root = Path.GetFullPath(_options.ModulesPath);
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
