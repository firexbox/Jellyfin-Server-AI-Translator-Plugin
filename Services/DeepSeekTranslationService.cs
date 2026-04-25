using System.Linq;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AITranslator.Configuration;
using Jellyfin.Plugin.AITranslator.SubtitleProcessing.Models;

namespace Jellyfin.Plugin.AITranslator.Services;

/// <summary>
/// Translation service using DeepSeek API.
/// Uses the DeepSeek chat completions endpoint with deepseek-chat model.
/// Sends ALL subtitle entries in a single request (1M context window supports full episodes).
/// </summary>
public class DeepSeekTranslationService : ITranslationService
{
    private const string DefaultApiUrl = "https://api.deepseek.com/chat/completions";
    private const string DefaultModel = "deepseek-chat";

    private readonly HttpClient _httpClient;
    private readonly PluginConfiguration _config;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeepSeekTranslationService"/> class.
    /// </summary>
    public DeepSeekTranslationService(HttpClient httpClient, PluginConfiguration config)
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

        // DeepSeek v4 Flash supports 1M context — send all in one request
        // But split into chunks of 200 if the response times out
        const int chunkSize = 200;
        if (entries.Count <= chunkSize)
        {
            return await TranslateAllAsync(entries, targetLanguage, cancellationToken);
        }

        var result = new List<SubtitleEntry>();
        for (int i = 0; i < entries.Count; i += chunkSize)
        {
            var chunk = entries.Skip(i).Take(chunkSize).ToList();
            var translated = await TranslateAllAsync(chunk, targetLanguage, cancellationToken);
            result.AddRange(translated);
        }
        return result;
    }

    /// <summary>
    /// Translates ALL subtitle entries in a single API call.
    /// DeepSeek v4 Flash supports 1M context — enough for a full episode.
    /// </summary>
    public async Task<List<SubtitleEntry>> TranslateAllAsync(
        List<SubtitleEntry> entries,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
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

        var responseBody = await response.Content.ReadFromJsonAsync<DeepSeekResponse>(_jsonOptions, cancellationToken);
        if (responseBody?.Choices == null || responseBody.Choices.Count == 0)
        {
            throw new InvalidOperationException("DeepSeek API returned no choices.");
        }

        var translatedText = responseBody.Choices[0].Message?.Content ?? string.Empty;
        return ParseTranslatedResponse(translatedText, entries);
    }

    /// <summary>
    /// Single-request prompt — all entries at once.
    /// </summary>
    private string BuildFullPrompt(
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
            model = !string.IsNullOrWhiteSpace(_config.ModelName) ? _config.ModelName : DefaultModel,
            messages = new[]
            {
                new { role = "system", content = "You are a professional subtitle translation AI. Translate all subtitle entries to the target language in one pass. Output ONLY the translated entries, preserving all timestamps and formatting exactly." },
                new { role = "user", content = prompt }
            },
            temperature = 0.3,
            max_tokens = 16384
        };
    }

    /// <summary>
    /// Parses the translated response back into SubtitleEntry objects.
    /// Extracts content by matching the bracket-index format.
    /// </summary>
    private List<SubtitleEntry> ParseTranslatedResponse(string response, List<SubtitleEntry> originalBatch)
    {
        var result = new List<SubtitleEntry>();
        var lines = response.Split('\n', StringSplitOptions.None);

        SubtitleEntry? currentEntry = null;
        var textBuilder = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Skip empty lines between entries
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            // Skip markers like --- START BATCH ---, --- END BATCH ---
            if (trimmed.StartsWith("---"))
            {
                continue;
            }

            // Check for index marker: either [1] or just 1 at start of line
            var indexMatch = Regex.Match(trimmed, @"^\[?(\d+)\]?$");
            if (indexMatch.Success && !trimmed.Contains("-->"))
            {
                // Save previous entry if exists
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

            // Check for timestamp line: START --> END
            var timeMatch = Regex.Match(trimmed,
                @"^(\d{2}:\d{2}:\d{2}[,\.]\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}[,\.]\d{3})$");
            if (timeMatch.Success && currentEntry != null)
            {
                currentEntry.StartTime = timeMatch.Groups[1].Value.Replace('.', ',');
                currentEntry.EndTime = timeMatch.Groups[2].Value.Replace('.', ',');
                continue;
            }

            // This is subtitle text content - append to current entry
            if (currentEntry != null)
            {
                if (textBuilder.Length > 0)
                    textBuilder.Append('\n');
                textBuilder.Append(trimmed);
            }
        }

        // Don't forget the last entry
        if (currentEntry != null)
        {
            currentEntry.Text = textBuilder.ToString().Trim();
            result.Add(currentEntry);
        }

        // If parsing failed entirely but entries were sent, return originals with placeholder
        if (result.Count == 0 && originalBatch.Count > 0)
        {
            return originalBatch;
        }

        return result;
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

    // DeepSeek API response models
    private class DeepSeekResponse
    {
        public string? Id { get; set; }
        public string? Object { get; set; }
        public long Created { get; set; }
        public string? Model { get; set; }
        public List<DeepSeekChoice>? Choices { get; set; }
        public DeepSeekUsage? Usage { get; set; }
    }

    private class DeepSeekChoice
    {
        public int Index { get; set; }
        public DeepSeekMessage? Message { get; set; }
        public string? FinishReason { get; set; }
    }

    private class DeepSeekMessage
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
    }

    private class DeepSeekUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
    }
}
