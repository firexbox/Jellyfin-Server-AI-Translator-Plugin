using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AITranslator.Configuration;
using Jellyfin.Plugin.AITranslator.SubtitleProcessing.Models;

namespace Jellyfin.Plugin.AITranslator.Services;

/// <summary>
/// Translation service using OpenAI-compatible API endpoints.
/// Supports any OpenAI-compatible API (OpenAI, Azure OpenAI, Together, etc.).
/// </summary>
public class OpenAITranslationService : ITranslationService
{
    private const string DefaultApiUrl = "https://api.openai.com/v1/chat/completions";
    private const string DefaultModel = "gpt-4o-mini";

    private readonly HttpClient _httpClient;
    private readonly PluginConfiguration _config;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAITranslationService"/> class.
    /// </summary>
    /// <param name="httpClient">The HttpClient instance.</param>
    /// <param name="config">The plugin configuration.</param>
    public OpenAITranslationService(HttpClient httpClient, PluginConfiguration config)
    {
        _httpClient = httpClient;
        _config = config;

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
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
        var apiKey = _config.ApiKey;

        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody, _jsonOptions),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadFromJsonAsync<OpenAiResponse>(_jsonOptions, cancellationToken);
        if (responseBody?.Choices == null || responseBody.Choices.Count == 0)
            throw new InvalidOperationException("OpenAI API returned no choices.");

        var translatedText = responseBody.Choices[0].Message?.Content ?? string.Empty;
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
        sb.AppendLine("Do NOT add any explanations, notes, or extra text outside the entries.");
        sb.AppendLine("Return ONLY the translated entries in the exact same format:");
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
                    role = "system" as string,
                    content = "You are a professional subtitle translator. You translate subtitle content accurately while preserving all formatting, timestamps, and structure."
                },
                new
                {
                    role = "user" as string,
                    content = prompt
                }
            },
            temperature = 0.3,
            max_tokens = 4096,
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

    // OpenAI API response models
    private class OpenAiResponse
    {
        public string? Id { get; set; }
        public string? Object { get; set; }
        public long Created { get; set; }
        public string? Model { get; set; }
        public List<OpenAiChoice>? Choices { get; set; }
        public OpenAiUsage? Usage { get; set; }
    }

    private class OpenAiChoice
    {
        public int Index { get; set; }
        public OpenAiMessage? Message { get; set; }
        public string? FinishReason { get; set; }
    }

    private class OpenAiMessage
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
    }

    private class OpenAiUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
    }
}
