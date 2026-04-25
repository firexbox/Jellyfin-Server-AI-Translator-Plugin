using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AITranslator.SubtitleProcessing.Models;

namespace Jellyfin.Plugin.AITranslator.SubtitleProcessing;

/// <summary>
/// Parses subtitle text in SRT, ASS, or VTT format into a list of <see cref="SubtitleEntry"/>.
/// </summary>
public static class SubtitleParser
{
    /// <summary>
    /// Parses the raw subtitle content into a list of subtitle entries.
    /// Auto-detects format based on content structure.
    /// </summary>
    /// <param name="content">Raw subtitle file content.</param>
    /// <returns>A list of parsed <see cref="SubtitleEntry"/> objects.</returns>
    public static List<SubtitleEntry> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new List<SubtitleEntry>();
        }

        // Try SRT format first
        var entries = ParseSrt(content);
        if (entries.Count > 0)
        {
            return entries;
        }

        // Try ASS format
        entries = ParseAss(content);
        if (entries.Count > 0)
        {
            return entries;
        }

        // Try VTT format
        entries = ParseVtt(content);
        if (entries.Count > 0)
        {
            return entries;
        }

        return new List<SubtitleEntry>();
    }

    /// <summary>
    /// Parses SRT format subtitle content.
    /// SRT format:
    /// 1
    /// 00:01:30,500 --> 00:01:35,000
    /// Some subtitle text
    ///
    /// </summary>
    private static List<SubtitleEntry> ParseSrt(string content)
    {
        var entries = new List<SubtitleEntry>();
        // Split on double newlines to get individual subtitle blocks
        var blocks = content.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var lines = block.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length < 3)
                continue;

            if (!int.TryParse(lines[0].Trim(), out int index))
                continue;

            var timeMatch = Regex.Match(lines[1].Trim(),
                @"^(\d{2}:\d{2}:\d{2}[,\.]\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}[,\.]\d{3})$");
            if (!timeMatch.Success)
                continue;

            var text = string.Join("\n", lines.Skip(2)).Trim();
            entries.Add(new SubtitleEntry
            {
                Index = index,
                StartTime = timeMatch.Groups[1].Value.Replace('.', ','),
                EndTime = timeMatch.Groups[2].Value.Replace('.', ','),
                Text = text
            });
        }
        return entries;
    }

    /// <summary>
    /// Parses ASS (Advanced SubStation Alpha) format subtitle content.
    /// Extracts Dialogue lines containing timing and text.
    /// </summary>
    private static List<SubtitleEntry> ParseAss(string content)
    {
        var entries = new List<SubtitleEntry>();
        var lines = content.Split('\n', StringSplitOptions.None);
        var dialoguePattern = new Regex(
            @"^Dialogue:\s*\d+," +
            @"(\d):(\d{2}):(\d{2})\.(\d{2})," +
            @"(\d):(\d{2}):(\d{2})\.(\d{2})," +
            @"([^,]*,[^,]*,[^,]*,[^,]*,[^,]*,[^,]*,[^,]*),(.*)",
            RegexOptions.IgnoreCase);

        int index = 0;
        foreach (var line in lines)
        {
            var match = dialoguePattern.Match(line.Trim());
            if (!match.Success)
                continue;

            // Convert ASS time format (H:MM:SS.cc) to SRT-style timestamp (HH:MM:SS,mmm)
            int h1 = int.Parse(match.Groups[1].Value);
            int m1 = int.Parse(match.Groups[2].Value);
            int s1 = int.Parse(match.Groups[3].Value);
            int c1 = int.Parse(match.Groups[4].Value);
            string startTime = $"{h1:D2}:{m1:D2}:{s1:D2},{c1 * 10:D3}";

            int h2 = int.Parse(match.Groups[5].Value);
            int m2 = int.Parse(match.Groups[6].Value);
            int s2 = int.Parse(match.Groups[7].Value);
            int c2 = int.Parse(match.Groups[8].Value);
            string endTime = $"{h2:D2}:{m2:D2}:{s2:D2},{c2 * 10:D3}";

            string text = match.Groups[10].Value
                .Replace("\\N", "\n")
                .Replace("\\n", "\n")
                .Replace("{\\i1}", "<i>")
                .Replace("{\\i0}", "</i>")
                .Replace("{\\b1}", "<b>")
                .Replace("{\\b0}", "</b>")
                .Replace("{\\u1}", "<u>")
                .Replace("{\\u0}", "</u>");

            // Remove remaining ASS override tags
            text = Regex.Replace(text, @"\{[^}]*\}", string.Empty);

            index++;
            entries.Add(new SubtitleEntry
            {
                Index = index,
                StartTime = startTime,
                EndTime = endTime,
                Text = text.Trim()
            });
        }

        return entries;
    }

    /// <summary>
    /// Parses WebVTT (VTT) format subtitle content.
    /// VTT format:
    /// WEBVTT
    ///
    /// 00:01:30.500 --> 00:01:35.000
    /// Some subtitle text
    ///
    /// </summary>
    private static List<SubtitleEntry> ParseVtt(string content)
    {
        var entries = new List<SubtitleEntry>();

        // Remove WEBVTT header and any metadata
        var vttContent = content;
        var headerEnd = vttContent.IndexOf("\n\n", StringComparison.Ordinal);
        if (headerEnd > 0 && vttContent.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
        {
            vttContent = vttContent.Substring(headerEnd).TrimStart('\n', '\r');
        }

        var pattern = new Regex(
            @"(\d{2}:\d{2}:\d{2}\.\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}\.\d{3})\s*\n((?:(?!\n\n).)*)",
            RegexOptions.Singleline | RegexOptions.ExplicitCapture);

        int index = 0;
        var matches = pattern.Matches(vttContent);
        foreach (Match match in matches)
        {
            index++;
            var entry = new SubtitleEntry
            {
                Index = index,
                StartTime = match.Groups[1].Value.Replace('.', ','),
                EndTime = match.Groups[2].Value.Replace('.', ','),
                Text = match.Groups[3].Value.Trim()
            };
            entries.Add(entry);
        }

        return entries;
    }
}
