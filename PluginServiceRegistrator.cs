using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AITranslator;

/// <summary>
/// Registers plugin services into Jellyfin's DI container.
/// Uses dual registration strategy to ensure controllers are discoverable
/// on all platforms (Linux, macOS, Windows) and all Jellyfin build variants.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IServerApplicationHost appHost)
    {
        var pluginAssembly = typeof(Plugin).Assembly;
        var pluginAssemblyName = pluginAssembly.GetName().Name;

        // Strategy 1: Register IStartupFilter that adds ApplicationPart at app startup time.
        // This works when MVC is already fully initialized.
        services.AddSingleton<IStartupFilter>(new AITranslatorStartupFilter(pluginAssembly));

        // Strategy 2: If ApplicationPartManager already exists in services, add directly.
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ApplicationPartManager));
        if (descriptor?.ImplementationInstance is ApplicationPartManager existingPartManager)
        {
            AddPartIfMissing(existingPartManager, pluginAssembly, pluginAssemblyName);
        }
    }

    private static void AddPartIfMissing(ApplicationPartManager partManager, System.Reflection.Assembly assembly, string assemblyName)
    {
        if (!partManager.ApplicationParts.Any(p => p.Name == assemblyName))
        {
            partManager.ApplicationParts.Add(new AssemblyPart(assembly));
        }
    }
}

/// <summary>
/// IStartupFilter that adds the plugin assembly to MVC's ApplicationPartManager
/// after the DI container is fully built. This ensures compatibility with macOS
/// and other platforms where early registration may not work.
/// </summary>
public class AITranslatorStartupFilter : IStartupFilter
{
    private readonly System.Reflection.Assembly _pluginAssembly;

    public AITranslatorStartupFilter(System.Reflection.Assembly pluginAssembly)
    {
        _pluginAssembly = pluginAssembly;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder =>
        {
            try
            {
                var partManager = builder.ApplicationServices.GetService<ApplicationPartManager>();
                if (partManager != null)
                {
                    var name = _pluginAssembly.GetName().Name;
                    if (!partManager.ApplicationParts.Any(p => p.Name == name))
                    {
                        partManager.ApplicationParts.Add(new AssemblyPart(_pluginAssembly));
                    }
                }
            }
            catch
            {
                // Ignore - ApplicationPartManager may not be available on all builds
            }
            next(builder);
        };
    }
}
