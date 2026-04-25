using Jellyfin.Plugin.AITranslator.Configuration;

namespace Jellyfin.Plugin.AITranslator.Services;

/// <summary>
/// Factory for creating the appropriate translation service based on configuration.
/// </summary>
public static class TranslationServiceFactory
{
    /// <summary>
    /// Creates a translation service based on the configured API provider.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>An instance of <see cref="ITranslationService"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when the API provider is not supported.</exception>
    public static ITranslationService Create(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromMinutes(15);

        return config.ApiProvider?.ToLowerInvariant() switch
        {
            "deepseek" => new DeepSeekTranslationService(httpClient, config),
            "openai" => new OpenAITranslationService(httpClient, config),
            "ollama" => new OllamaTranslationService(httpClient, config),
            _ => throw new ArgumentException(
                $"Unsupported API provider: '{config.ApiProvider}'. Supported providers: DeepSeek, OpenAI, Ollama.")
        };
    }

    /// <summary>
    /// Creates a translation service using a pre-configured HttpClient.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="httpClient">The HttpClient to use.</param>
    /// <returns>An instance of <see cref="ITranslationService"/>.</returns>
    public static ITranslationService Create(PluginConfiguration config, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(httpClient);

        return config.ApiProvider?.ToLowerInvariant() switch
        {
            "deepseek" => new DeepSeekTranslationService(httpClient, config),
            "openai" => new OpenAITranslationService(httpClient, config),
            "ollama" => new OllamaTranslationService(httpClient, config),
            _ => throw new ArgumentException(
                $"Unsupported API provider: '{config.ApiProvider}'. Supported providers: DeepSeek, OpenAI, Ollama.")
        };
    }
}
