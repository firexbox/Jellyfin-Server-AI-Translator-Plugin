using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AITranslator.Configuration;
using Jellyfin.Plugin.AITranslator.SubtitleProcessing.Models;

namespace Jellyfin.Plugin.AITranslator.Services;

/// <summary>
/// Translation service using Ollama local models.
/// Connects to a local Ollama instance for offline subtitle translation.
/// </summary>
public class OllamaTranslationService : ITranslationService
{
    private const string DefaultApiUrl = "http://localhost:11434/api/chat";
    private const string DefaultModel = "llama3";

    private readonly HttpClient _httpClient;
    private readonly PluginConfiguration _config;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="OllamaTranslationService"/> class.
    /// </summary>
    /// <param name="httpClient">The HttpClient instance.</param>
    /// <param name="config">The plugin configuration.</param>
    public OllamaTranslationService(HttpClient httpClient, PluginConfiguration config)
    {
        _httpClient = httpClient;
        _config = config;

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public async Task<List<SubtitleEntry>> TranslateAsync(
        List<SubtitleEntry> entries,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
            return entries;

        var prompt = BuildFullPrompt(entries, targetLanguage);
        var requestBody = CreateRequestBody(prompt);
        var apiUrl = string.IsNullOrWhiteSpace(_config.ApiUrl) ? DefaultApiUrl : _config.ApiUrl;

        var requestContent = new StringContent(
            JsonSerializer.Serialize(requestBody, _jsonOptions),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(apiUrl, requestContent, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseString = await response.Content.ReadAsStringAsync(cancellationToken);
        var ollamaResponse = JsonSerializer.Deserialize<OllamaResponse>(responseString, _jsonOptions);
        var translatedText = ollamaResponse?.Message?.Content ?? string.Empty;

        return ParseTranslatedResponse(translatedText, entries);
    }

    private static string BuildFullPrompt(
        List<SubtitleEntry> entries,
        string targetLanguage)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are a professional subtitle translator. Translate the following subtitle entries to {targetLanguage}.");
        sb.AppendLine($"Total entries: {entries.Count}. Maintain the exact same format, timestamps, and structure.");
        sb.AppendLine("Keep HTML/formatting tags intact (e.g., <i>, <b>, <u>).");
        sb.AppendLine("Preserve line breaks within subtitle entries.");
        sb.AppendLine("Do NOT change the index, start time, or end time of any entry.");
        sb.AppendLine("Return ONLY the translated entries.");
        sb.AppendLine();

        foreach (var entry in entries)
        {
            sb.AppendLine(entry.Index.ToString());
            sb.AppendLine($"{entry.StartTime} --> {entry.EndTime}");
            sb.AppendLine(entry.Text);
            sb.AppendLine();
        }

        sb.AppendLine($"Translate the above {entries.Count} subtitle entries to {targetLanguage}. Return them in the exact same format with timestamps preserved.");
        return sb.ToString();
    }

    private object CreateRequestBody(string prompt)
    {
        return new
        {
            model = string.IsNullOrWhiteSpace(_config.ModelName) ? DefaultModel : _config.ModelName,
            messages = new[]
            {
                new
                {
                    role = "user" as string,
                    content = prompt
                }
            },
            options = new
            {
                temperature = 0.3
            },
            stream = false
        };
    }

    /// <summary>
    /// Parses the translated response back into SubtitleEntry objects.
    /// </summary>
    private static List<SubtitleEntry> ParseTranslatedResponse(string response, List<SubtitleEntry> originalBatch)
    {
        var result = new List<SubtitleEntry>();
        var lines = response.Split('\n', StringSplitOptions.None);

        SubtitleEntry? currentEntry = null;
        var textBuilder = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            var indexMatch = Regex.Match(trimmed, @"^\[(\d+)\]$");
            if (indexMatch.Success)
            {
                if (currentEntry != null)
                {
                    currentEntry.Text = textBuilder.ToString().Trim();
                    result.Add(currentEntry);
                }

                var idx = int.Parse(indexMatch.Groups[1].Value);
                currentEntry = new SubtitleEntry
                {
                    Index = idx,
                    StartTime = string.Empty,
                    EndTime = string.Empty,
                    Text = string.Empty
                };
                textBuilder.Clear();
                continue;
            }

            var timeMatch = Regex.Match(trimmed, @"^(\d{2}:\d{2}:\d{2}[,\.]\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}[,\.]\d{3})$");
            if (timeMatch.Success && currentEntry != null)
            {
                currentEntry.StartTime = timeMatch.Groups[1].Value.Replace('.', ',');
                currentEntry.EndTime = timeMatch.Groups[2].Value.Replace('.', ',');
                continue;
            }

            if (trimmed.StartsWith("---") || trimmed.Length == 0)
            {
                continue;
            }

            if (currentEntry != null)
            {
                if (textBuilder.Length > 0)
                    textBuilder.Append('\n');
                textBuilder.Append(trimmed);
            }
        }

        if (currentEntry != null)
        {
            currentEntry.Text = textBuilder.ToString().Trim();
            result.Add(currentEntry);
        }

        // Map back to original entries preserving timestamps
        var mappedResult = new List<SubtitleEntry>();
        foreach (var original in originalBatch)
        {
            var translated = result.FirstOrDefault(r => r.Index == original.Index);
            if (translated != null && !string.IsNullOrWhiteSpace(translated.Text))
            {
                translated.StartTime = original.StartTime;
                translated.EndTime = original.EndTime;
                mappedResult.Add(translated);
            }
            else
            {
                mappedResult.Add(original);
            }
        }

        return mappedResult;
    }

    private static List<List<SubtitleEntry>> SplitIntoBatches(List<SubtitleEntry> entries, int batchSize)
    {
        var batches = new List<List<SubtitleEntry>>();
        for (int i = 0; i < entries.Count; i += batchSize)
        {
            batches.Add(entries.Skip(i).Take(batchSize).ToList());
        }
        return batches;
    }

    // Ollama API response models
    private class OllamaResponse
    {
        public string? Model { get; set; }
        public DateTime CreatedAt { get; set; }
        public OllamaMessage? Message { get; set; }
        public bool Done { get; set; }
        public int? TotalDuration { get; set; }
        public int? PromptEvalCount { get; set; }
        public int? EvalCount { get; set; }
    }

    private class OllamaMessage
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
    }
}
