using Jellyfin.Plugin.AITranslator.SubtitleProcessing.Models;

namespace Jellyfin.Plugin.AITranslator.Services;

/// <summary>
/// Interface for AI-powered subtitle translation services.
/// </summary>
public interface ITranslationService
{
    /// <summary>
    /// Translates a list of subtitle entries to the target language.
    /// </summary>
    /// <param name="entries">The subtitle entries to translate.</param>
    /// <param name="targetLanguage">The target language (e.g., "Chinese", "Spanish").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The translated subtitle entries with preserved formatting and timestamps.</returns>
    Task<List<SubtitleEntry>> TranslateAsync(
        List<SubtitleEntry> entries,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}
