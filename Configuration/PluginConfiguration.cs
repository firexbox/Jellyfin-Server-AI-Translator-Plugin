using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AITranslator.Configuration;

/// <summary>
/// Configuration model for the AI Translator plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        ApiProvider = "DeepSeek";
        ApiKey = string.Empty;
        ApiUrl = string.Empty;
        ModelName = "deepseek-chat";
        TargetLanguage = "Chinese";
        BatchSize = 10;
    }

    /// <summary>
    /// Gets or sets the AI provider (DeepSeek, OpenAI, Ollama).
    /// </summary>
    public string ApiProvider { get; set; }

    /// <summary>
    /// Gets or sets the API key for external providers.
    /// </summary>
    public string ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the custom API URL (for OpenAI-compatible or Ollama endpoints).
    /// </summary>
    public string ApiUrl { get; set; }

    /// <summary>
    /// Gets or sets the model name to use for translation.
    /// </summary>
    public string ModelName { get; set; }

    /// <summary>
    /// Gets or sets the target language for translation.
    /// </summary>
    public string TargetLanguage { get; set; }

    /// <summary>
    /// Gets or sets the batch size for sending subtitles in a single API request.
    /// </summary>
    public int BatchSize { get; set; }
}
