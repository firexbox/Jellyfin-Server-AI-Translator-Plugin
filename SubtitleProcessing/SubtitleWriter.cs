using System.Text;
using Jellyfin.Plugin.AITranslator.SubtitleProcessing.Models;

namespace Jellyfin.Plugin.AITranslator.SubtitleProcessing;

/// <summary>
/// Writes subtitle entries back to SRT or VTT format.
/// </summary>
public static class SubtitleWriter
{
    /// <summary>
    /// Writes the subtitle entries to SRT format.
    /// </summary>
    /// <param name="entries">The list of subtitle entries.</param>
    /// <returns>SRT-formatted string.</returns>
    public static string WriteSrt(List<SubtitleEntry> entries)
    {
        var sb = new StringBuilder();

        foreach (var entry in entries)
        {
            sb.AppendLine(entry.Index.ToString());
            sb.AppendLine($"{entry.StartTime} --> {entry.EndTime}");
            sb.AppendLine(entry.Text);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Writes the subtitle entries to WebVTT format.
    /// </summary>
    /// <param name="entries">The list of subtitle entries.</param>
    /// <returns>VTT-formatted string.</returns>
    public static string WriteVtt(List<SubtitleEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();

        foreach (var entry in entries)
        {
            // VTT uses dot as decimal separator
            var start = entry.StartTime.Replace(',', '.');
            var end = entry.EndTime.Replace(',', '.');

            sb.AppendLine($"{start} --> {end}");
            sb.AppendLine(entry.Text);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Writes the subtitle entries to the specified format.
    /// </summary>
    /// <param name="entries">The list of subtitle entries.</param>
    /// <param name="format">The target format (srt or vtt).</param>
    /// <returns>Formatted subtitle string.</returns>
    public static string Write(List<SubtitleEntry> entries, string format)
    {
        return format.ToLowerInvariant() switch
        {
            "vtt" or "webvtt" => WriteVtt(entries),
            _ => WriteSrt(entries)
        };
    }
}
