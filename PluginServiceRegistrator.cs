using Jellyfin.Plugin.AITranslator.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AITranslator;

/// <summary>
/// Registers plugin services into Jellyfin's DI container.
/// Discovered automatically by PluginManager via IPluginServiceRegistrator.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IServerApplicationHost appHost)
    {
        // Manually add the global filter to MvcOptions
        // This runs before MVC is fully configured, so we add a Configure callback
        services.AddSingleton<IStartupFilter>(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("AI Translator");
            return new StartupFilterWithFilter(logger);
        });

        var logger2 = services.BuildServiceProvider()
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("AI Translator");
        logger2.LogInformation("AI Translator: Service registration complete");
    }
}

/// <summary>
/// IStartupFilter that adds our SubtitleTranslationMiddleware to the pipeline.
/// </summary>
internal class StartupFilterWithFilter : IStartupFilter
{
    private readonly ILogger _logger;

    public StartupFilterWithFilter(ILogger logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder =>
        {
            // Middleware disabled - intercepts subtitle API calls which causes issues
            // with internal Jellyfin subtitle fetching
            next(builder);
        };
    }
}
