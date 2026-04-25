namespace Jellyfin.Plugin.AITranslator.SubtitleProcessing.Models;

/// <summary>
/// Represents a single subtitle entry with timing and text content.
/// </summary>
public class SubtitleEntry
{
    /// <summary>
    /// Gets or sets the sequential index of this subtitle entry.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Gets or sets the start time as a formatted timestamp string (e.g., "00:01:30,500").
    /// </summary>
    public string StartTime { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the end time as a formatted timestamp string (e.g., "00:01:35,000").
    /// </summary>
    public string EndTime { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the subtitle text content.
    /// </summary>
    public string Text { get; set; } = string.Empty;
}
