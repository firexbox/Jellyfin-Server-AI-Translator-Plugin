using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AITranslator.Configuration;
using Jellyfin.Plugin.AITranslator.Services;
using Jellyfin.Plugin.AITranslator.SubtitleProcessing;
using Microsoft.AspNetCore.Mvc;
using SubtitleEntry = Jellyfin.Plugin.AITranslator.SubtitleProcessing.Models.SubtitleEntry;

namespace Jellyfin.Plugin.AITranslator.Api;

[ApiController]
[Route("AITranslator")]
public class AISubtitleController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PluginConfiguration _config;

    public AISubtitleController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
    }

    [HttpPost("Subtitle/TranslateAndSave")]
    public async Task<IActionResult> TranslateAndSave(
        [FromBody] TranslateAndSaveRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
            return BadRequest(new { Error = "\u8bf7\u5148\u5728\u63d2\u4ef6\u8bbe\u7f6e\u4e2d\u914d\u7f6e API Key" });

        if (string.IsNullOrWhiteSpace(request.ItemId))
            return BadRequest(new { Error = "\u7f3a\u5c11\u89c6\u9891 ID" });

        var token = GetTokenFromHeader();
        if (string.IsNullOrWhiteSpace(token))
            return BadRequest(new { Error = "\u65e0\u6cd5\u83b7\u53d6\u8ba4\u8bc1 Token" });

        try
        {
            var mediaInfo = await GetStringAsync($"http://localhost:8096/Users/{request.UserId}/Items/{request.ItemId}?Fields=MediaSources", token, cancellationToken);
            var sources = JsonSerializer.Deserialize<JsonElement>(mediaInfo);
            var mediaSources = sources.GetProperty("MediaSources").EnumerateArray().FirstOrDefault();
            if (mediaSources.ValueKind == JsonValueKind.Undefined)
                return BadRequest(new { Error = "\u65e0\u6cd5\u83b7\u53d6\u89c6\u9891\u5a92\u4f53\u4fe1\u606f" });

            var sourceId = mediaSources.GetProperty("Id").GetString() ?? request.ItemId;
            var videoPath = mediaSources.TryGetProperty("Path", out var pathProp) ? pathProp.GetString() : null;

            var streams = mediaSources.GetProperty("MediaStreams").EnumerateArray();
            bool indexFound = false;
            foreach (var stream in streams)
            {
                if (stream.TryGetProperty("Type", out var typeProp) && typeProp.GetString() == "Subtitle")
                {
                    if (stream.TryGetProperty("Index", out var idxProp) && idxProp.GetInt32() == request.SubtitleIndex)
                    {
                        indexFound = true;
                        break;
                    }
                }
            }

            if (!indexFound)
                return BadRequest(new { Error = $"\u672a\u627e\u5230\u5b57\u5e55\u8f68\u9053\u7d22\u5f15 {request.SubtitleIndex}" });

            var format = request.Format ?? "srt";
            var subUrl = $"http://localhost:8096/Videos/{request.ItemId}/{sourceId}/Subtitles/{request.SubtitleIndex}/Stream.{format}";
            var subtitleContent = await GetStringAsync(subUrl, token, cancellationToken);

            if (string.IsNullOrWhiteSpace(subtitleContent))
                return NotFound(new { Error = "\u65e0\u6cd5\u83b7\u53d6\u539f\u59cb\u5b57\u5e55" });

            var mediaDir = !string.IsNullOrWhiteSpace(videoPath) ? System.IO.Path.GetDirectoryName(videoPath) ?? "" : "";
            var videoName = !string.IsNullOrWhiteSpace(videoPath) ? System.IO.Path.GetFileNameWithoutExtension(videoPath) : "";
            var mode = (request.Mode ?? "translated").ToLowerInvariant();
            var entries = SubtitleParser.Parse(subtitleContent);
            if (entries.Count == 0)
                return BadRequest(new { Error = "\u65e0\u6cd5\u89e3\u6790\u5b57\u5e55\u5185\u5bb9" });

            // Detect source language by AI sampling
            var sampleText = string.Join("\n", entries.Take(5).Select(e => e.Text));
            var detectedLang = await DetectLanguageAsync(sampleText, cancellationToken);
            var sourceLangName = detectedLang.Name;
            var sourceLangCode = detectedLang.Code;

            var targetLang = !string.IsNullOrWhiteSpace(request.TargetLanguage)
                ? request.TargetLanguage
                : _config.TargetLanguage;

            if (mode == "original")
            {
                if (!string.IsNullOrWhiteSpace(videoPath))
                {
                    var origPath = System.IO.Path.Combine(mediaDir, $"{videoName}.original.{sourceLangCode}.srt");
                    await System.IO.File.WriteAllTextAsync(origPath, subtitleContent, cancellationToken);
                    try { System.IO.File.SetUnixFileMode(origPath,
                        System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite |
                        System.IO.UnixFileMode.GroupRead | System.IO.UnixFileMode.OtherRead); } catch { }
                }
                return Ok(new { Success = true, Message = "\u2705 \u5df2\u4fdd\u5b58\u539f\u5b57\u5e55", Mode = "original", EntryCount = entries.Count, SourceLanguage = sourceLangName });
            }

            // Write progress file
            var taskId = $"{request.ItemId}_{request.SubtitleIndex}_{mode}";
            var progressPath = GetProgressPath(taskId);
            WriteProgress(progressPath, new TaskProgress
            {
                TaskId = taskId,
                ItemId = request.ItemId,
                ItemName = videoName,
                SubtitleIndex = request.SubtitleIndex,
                Mode = mode,
                Status = "running",
                SourceLanguage = sourceLangName,
                TargetLanguage = targetLang,
                EntryCount = entries.Count,
                StartTime = DateTime.UtcNow.ToString("O"),
                Message = "\u7ffb\u8bd1\u4efb\u52a1\u5df2\u542f\u52a8..."
            });

            var capturedFactory = _httpClientFactory;
            var capturedConfig = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var capturedToken = token;
            var capturedItemId = request.ItemId;
            var capturedFormat = format;
            var capturedVideoPath = videoPath;
            var capturedMediaDir = mediaDir;
            var capturedVideoName = videoName;
            var capturedMode = mode;
            var capturedEntries = entries;
            var capturedSourceLang = sourceLangName;
            var capturedSourceCode = sourceLangCode;
            var capturedTargetLang = targetLang;
            var capturedProgressPath = progressPath;

            _ = Task.Run(async () =>
            {
                try
                {
                    WriteProgress(capturedProgressPath, new TaskProgress
                    {
                        TaskId = taskId, ItemId = capturedItemId, ItemName = capturedVideoName,
                        SubtitleIndex = request.SubtitleIndex, Mode = capturedMode,
                        Status = "translating", SourceLanguage = capturedSourceLang,
                        TargetLanguage = capturedTargetLang, EntryCount = capturedEntries.Count,
                        StartTime = DateTime.UtcNow.ToString("O"),
                        Message = $"\u6b63\u5728\u7ffb\u8bd1 {capturedEntries.Count} \u6761\u5b57\u5e55..."
                    });

                    var service = TranslationServiceFactory.Create(capturedConfig);
                    var translatedEntries = await service.TranslateAsync(capturedEntries, capturedTargetLang, CancellationToken.None);

                    if (capturedMode == "translated")
                    {
                        var translatedContent = SubtitleWriter.Write(translatedEntries, capturedFormat);
                        if (!string.IsNullOrWhiteSpace(capturedVideoPath))
                        {
                            var outPath = System.IO.Path.Combine(capturedMediaDir, $"{capturedVideoName}.{LangCode(capturedTargetLang)}.srt");
                            await System.IO.File.WriteAllTextAsync(outPath, translatedContent);
                            try { System.IO.File.SetUnixFileMode(outPath,
                                System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite |
                                System.IO.UnixFileMode.GroupRead | System.IO.UnixFileMode.OtherRead); } catch { }
                        }
                        await UploadSubtitleAsync(capturedFactory, capturedToken, capturedItemId, translatedContent, capturedFormat, LangCode(capturedTargetLang), CancellationToken.None);
                        WriteProgress(capturedProgressPath, new TaskProgress
                        {
                            TaskId = taskId, ItemId = capturedItemId, ItemName = capturedVideoName,
                            SubtitleIndex = request.SubtitleIndex, Mode = capturedMode,
                            Status = "completed", SourceLanguage = capturedSourceLang,
                            TargetLanguage = capturedTargetLang, EntryCount = capturedEntries.Count,
                            StartTime = DateTime.UtcNow.ToString("O"), EndTime = DateTime.UtcNow.ToString("O"),
                            Message = $"\u2705 \u5df2\u751f\u6210 {capturedTargetLang} \u5b57\u5e55"
                        });
                    }
                    else if (capturedMode == "bilingual")
                    {
                        var bilingualEntries = new List<SubtitleEntry>();
                        foreach (var entry in capturedEntries)
                        {
                            var t = translatedEntries.FirstOrDefault(x => x.Index == entry.Index);
                            // Target language on top, source below
                            bilingualEntries.Add(new SubtitleEntry
                            {
                                Index = entry.Index,
                                StartTime = entry.StartTime,
                                EndTime = entry.EndTime,
                                Text = t != null && !string.IsNullOrWhiteSpace(t.Text)
                                    ? t.Text + "\n" + entry.Text
                                    : entry.Text
                            });
                        }
                        var bilingualContent = WriteSrtDirect(bilingualEntries);
                        if (!string.IsNullOrWhiteSpace(capturedVideoPath))
                        {
                            var bilPath = System.IO.Path.Combine(capturedMediaDir, $"{capturedVideoName}.{LangCode(capturedTargetLang)}-{capturedSourceCode}.srt");
                            await System.IO.File.WriteAllTextAsync(bilPath, bilingualContent);
                            try { System.IO.File.SetUnixFileMode(bilPath,
                                System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite |
                                System.IO.UnixFileMode.GroupRead | System.IO.UnixFileMode.OtherRead); } catch { }
                        }
                        await UploadSubtitleAsync(capturedFactory, capturedToken, capturedItemId, bilingualContent, capturedFormat, LangCode(capturedTargetLang), CancellationToken.None);
                        WriteProgress(capturedProgressPath, new TaskProgress
                        {
                            TaskId = taskId, ItemId = capturedItemId, ItemName = capturedVideoName,
                            SubtitleIndex = request.SubtitleIndex, Mode = capturedMode,
                            Status = "completed", SourceLanguage = capturedSourceLang,
                            TargetLanguage = capturedTargetLang, EntryCount = capturedEntries.Count,
                            StartTime = DateTime.UtcNow.ToString("O"), EndTime = DateTime.UtcNow.ToString("O"),
                            Message = $"\u2705 \u5df2\u751f\u6210 {capturedTargetLang}-{capturedSourceLang} \u53cc\u8bed\u5b57\u5e55"
                        });
                    }
                }
                catch (Exception ex)
                {
                    WriteProgress(capturedProgressPath, new TaskProgress
                    {
                        TaskId = taskId, ItemId = capturedItemId, ItemName = capturedVideoName,
                        SubtitleIndex = request.SubtitleIndex, Mode = capturedMode,
                        Status = "failed", SourceLanguage = capturedSourceLang,
                        TargetLanguage = capturedTargetLang, EntryCount = capturedEntries.Count,
                        StartTime = DateTime.UtcNow.ToString("O"), EndTime = DateTime.UtcNow.ToString("O"),
                        Message = $"\u274c \u5931\u8d25: {ex.Message}"
                    });
                    Console.WriteLine($"[AI Translator] Background translation failed: {ex}");
                }
            });

            return Ok(new
            {
                Success = true,
                Message = $"\ud83d\ude80 \u4efb\u52a1\u5df2\u542f\u52a8\uff08{entries.Count} \u6761\u5b57\u5e55\uff0c{sourceLangName} \u2192 {targetLang}\uff09",
                Mode = capturedMode,
                EntryCount = entries.Count,
                TaskId = taskId,
                SourceLanguage = sourceLangName,
                TargetLanguage = targetLang
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = $"\u7ffb\u8bd1\u5931\u8d25: {ex.Message}" });
        }
    }

    [HttpPost("DetectLanguage")]
    public async Task<IActionResult> DetectLanguage([FromBody] DetectLanguageRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Sample))
            return BadRequest(new { Error = "No sample text" });
        try
        {
            var result = await DetectLanguageAsync(request.Sample, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpGet("Subtitle/LanguageInfo")]
    public async Task<IActionResult> GetSubtitleLanguageInfo([FromQuery] string itemId, [FromQuery] string userId, CancellationToken ct)
    {
        var token = GetTokenFromHeader();
        if (string.IsNullOrWhiteSpace(token))
            return BadRequest(new { Error = "无法获取认证 Token" });

        try
        {
            var playbackInfo = await GetStringAsync($"http://localhost:8096/Items/{itemId}/PlaybackInfo?UserId={userId}", token, ct);
            var doc = JsonDocument.Parse(playbackInfo);
            var mediaSources = doc.RootElement.GetProperty("MediaSources").EnumerateArray().FirstOrDefault();
            if (mediaSources.ValueKind == JsonValueKind.Undefined)
                return BadRequest(new { Error = "无法获取媒体信息" });

            var sourceId = mediaSources.TryGetProperty("Id", out var idProp) ? idProp.GetString() ?? itemId : itemId;
            var streams = mediaSources.GetProperty("MediaStreams").EnumerateArray()
                .Where(s => s.TryGetProperty("Type", out var t) && t.GetString() == "Subtitle")
                .ToList();

            // Phase 1: collect metadata + quick local detection (parallel fetch)
            var infos = new List<(SubtitleLanguageInfo Info, string Sample, bool NeedAI)>();
            foreach (var stream in streams)
            {
                var idx = stream.GetProperty("Index").GetInt32();
                var jfLang = stream.TryGetProperty("Language", out var lp) ? lp.GetString() : null;
                var codec = stream.TryGetProperty("Codec", out var cp) ? cp.GetString() : "srt";
                var title = stream.TryGetProperty("Title", out var tp) ? tp.GetString() : null;
                var isExternal = stream.TryGetProperty("IsExternal", out var ep) && ep.GetBoolean();

                var quickResult = QuickDetectFromJellyfin(jfLang);
                var detectedName = quickResult.Name;
                var detectedCode = quickResult.Code;
                var sample = "";
                var needAI = false;

                try
                {
                    var subUrl = $"http://localhost:8096/Videos/{itemId}/{sourceId}/Subtitles/{idx}/Stream.srt";
                    var content = await GetStringAsync(subUrl, token, ct);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        sample = ExtractSample(content);
                        if (sample.Length > 5)
                        {
                            var localResult = QuickDetectLanguage(sample);
                            if (localResult.Code != "??")
                            {
                                // Unicode check succeeded — use it
                                detectedName = LangNameToChinese(localResult.Code) ?? localResult.Name;
                                detectedCode = localResult.Code;
                            }
                            else if (string.IsNullOrWhiteSpace(jfLang) || jfLang == "und")
                            {
                                // No Jellyfin tag and Unicode failed — need AI
                                needAI = true;
                            }
                            // else: Unicode failed but Jellyfin tag exists — trust Jellyfin
                        }
                    }
                }
                catch { }

                infos.Add((new SubtitleLanguageInfo
                {
                    Index = idx, JellyfinLanguage = jfLang,
                    DetectedName = detectedName, DetectedCode = detectedCode,
                    Codec = codec, IsExternal = isExternal, Title = title
                }, sample, needAI));
            }

            // Phase 2: run AI detection in parallel for those that need it
            var aiTasks = infos
                .Where(x => x.NeedAI && x.Sample.Length > 5)
                .Select(async x =>
                {
                    try
                    {
                        var detected = await DetectLanguageAsync(x.Sample, ct);
                        x.Info.DetectedName = LangNameToChinese(detected.Code) ?? detected.Name;
                        x.Info.DetectedCode = detected.Code;
                    }
                    catch { }
                });

            await Task.WhenAll(aiTasks);

            return Ok(infos.Select(x => x.Info).ToList());
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = $"检测失败: {ex.Message}" });
        }
    }

    /// <summary>
    /// Fast Unicode-range language detection. Returns Code="??" if ambiguous.
    /// </summary>
    private static LanguageResult QuickDetectLanguage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new LanguageResult { Code = "??", Name = "Unknown" };

        var s = text.Substring(0, Math.Min(text.Length, 200));

        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"[\u4e00-\u9fff\u3400-\u4dbf]"))
            return new LanguageResult { Code = "zh", Name = "中文" };
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"[\u3040-\u309f\u30a0-\u30ff]"))
            return new LanguageResult { Code = "ja", Name = "日文" };
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"[\uac00-\ud7af]"))
            return new LanguageResult { Code = "ko", Name = "韩文" };
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"[\u0400-\u04ff]"))
            return new LanguageResult { Code = "ru", Name = "俄文" };
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"[\u0600-\u06ff]"))
            return new LanguageResult { Code = "ar", Name = "阿拉伯文" };
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"[\u0e00-\u0e7f]"))
            return new LanguageResult { Code = "th", Name = "泰文" };

        // Latin-script languages: check for specific accented chars
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"[áéíóúñüçàèìòù]"))
            return new LanguageResult { Code = "??", Name = "拉丁语系" }; // ambiguous, need AI
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"[a-zA-Z]{3,}"))
            return new LanguageResult { Code = "en", Name = "英文" };

        return new LanguageResult { Code = "??", Name = "Unknown" };
    }

    private static LanguageResult QuickDetectFromJellyfin(string? jfLang)
    {
        var name = LangNameToChinese(jfLang) ?? jfLang ?? "Unknown";
        var code = jfLang ?? "??";
        return new LanguageResult { Code = code, Name = name };
    }

    [HttpPost("Subtitle/AdjustTiming")]
    public async Task<IActionResult> AdjustTiming(
        [FromBody] AdjustTimingRequest request,
        CancellationToken ct)
    {
        var token = GetTokenFromHeader();
        if (string.IsNullOrWhiteSpace(token))
            return BadRequest(new { Error = "无法获取认证 Token" });

        try
        {
            // Get media info for video path and source ID
            var mediaInfo = await GetStringAsync(
                $"http://localhost:8096/Users/{request.UserId}/Items/{request.ItemId}?Fields=MediaSources",
                token, ct);
            var sources = JsonSerializer.Deserialize<JsonElement>(mediaInfo);
            var mediaSources = sources.GetProperty("MediaSources").EnumerateArray().FirstOrDefault();
            if (mediaSources.ValueKind == JsonValueKind.Undefined)
                return BadRequest(new { Error = "无法获取视频媒体信息" });

            var sourceId = mediaSources.GetProperty("Id").GetString() ?? request.ItemId;
            var videoPath = mediaSources.TryGetProperty("Path", out var pathProp) ? pathProp.GetString() : null;

            // Get external subtitle content (the one to adjust)
            var format = request.Format ?? "srt";
            var extSubUrl = $"http://localhost:8096/Videos/{request.ItemId}/{sourceId}/Subtitles/{request.SubtitleIndex}/Stream.{format}";
            var extContent = await GetStringAsync(extSubUrl, token, ct);
            if (string.IsNullOrWhiteSpace(extContent))
                return BadRequest(new { Error = "无法获取待调整字幕内容" });

            // Get reference subtitle content (embedded, default to first embedded sub)
            var refIndex = request.ReferenceSubtitleIndex;
            var refSubUrl = $"http://localhost:8096/Videos/{request.ItemId}/{sourceId}/Subtitles/{refIndex}/Stream.{format}";
            var refContent = await GetStringAsync(refSubUrl, token, ct);
            if (string.IsNullOrWhiteSpace(refContent))
                return BadRequest(new { Error = $"无法获取参考字幕内容 (索引 {refIndex})" });

            // Parse both
            var extEntries = SubtitleParser.Parse(extContent);
            var refEntries = SubtitleParser.Parse(refContent);
            if (extEntries.Count == 0 || refEntries.Count == 0)
                return BadRequest(new { Error = "无法解析字幕内容" });

            // Calculate offset using first N entries
            var sampleCount = Math.Min(Math.Min(extEntries.Count, refEntries.Count), 10);
            var offsets = new List<double>();
            for (int i = 0; i < sampleCount; i++)
            {
                var extStart = ParseTimestamp(extEntries[i].StartTime);
                var refStart = ParseTimestamp(refEntries[i].StartTime);
                if (extStart.HasValue && refStart.HasValue)
                    offsets.Add((extStart.Value - refStart.Value).TotalSeconds);
            }

            if (offsets.Count == 0)
                return BadRequest(new { Error = "无法计算时间偏移" });

            // Use median to avoid outlier influence
            offsets.Sort();
            double medianOffset = offsets.Count % 2 == 1
                ? offsets[offsets.Count / 2]
                : (offsets[offsets.Count / 2 - 1] + offsets[offsets.Count / 2]) / 2.0;

            var absOffset = Math.Abs(medianOffset);
            var direction = medianOffset >= 0 ? "延后" : "提前";
            var offsetDisplay = $"{direction} {absOffset:F3} 秒";

            // Generate preview of first 3 entries
            var previewBefore = string.Join("\n", extEntries.Take(3).Select(e =>
                $"{e.Index}\n{e.StartTime} --> {e.EndTime}\n{e.Text}"));
            var previewAfter = string.Join("\n", extEntries.Take(3).Select(e =>
            {
                var newStart = AdjustTimestamp(e.StartTime, -medianOffset);
                var newEnd = AdjustTimestamp(e.EndTime, -medianOffset);
                return $"{e.Index}\n{newStart} --> {newEnd}\n{e.Text}";
            }));

            // If apply mode, actually adjust and save
            if (request.Apply && !string.IsNullOrWhiteSpace(videoPath))
            {
                var mediaDir = System.IO.Path.GetDirectoryName(videoPath) ?? "";
                var videoName = System.IO.Path.GetFileNameWithoutExtension(videoPath);

                // Adjust all entries
                var adjusted = extEntries.Select(e => new SubtitleEntry
                {
                    Index = e.Index,
                    StartTime = AdjustTimestamp(e.StartTime, -medianOffset),
                    EndTime = AdjustTimestamp(e.EndTime, -medianOffset),
                    Text = e.Text
                }).ToList();

                var adjustedContent = WriteSrtDirect(adjusted);

                // Save as new file with _adjusted suffix
                var outPath = System.IO.Path.Combine(mediaDir,
                    $"{videoName}.adjusted.{format}");
                await System.IO.File.WriteAllTextAsync(outPath, adjustedContent, ct);
                try
                {
                    System.IO.File.SetUnixFileMode(outPath,
                        System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite |
                        System.IO.UnixFileMode.GroupRead | System.IO.UnixFileMode.OtherRead);
                }
                catch { }

                // Upload as new subtitle track
                var langCode = request.Language ?? "chi";
                await UploadSubtitleAsync(_httpClientFactory, token, request.ItemId,
                    adjustedContent, format, langCode, ct);

                return Ok(new
                {
                    Applied = true,
                    OffsetSeconds = -medianOffset,
                    OffsetDisplay = offsetDisplay,
                    ExternalFirstTimestamp = extEntries[0].StartTime,
                    ReferenceFirstTimestamp = refEntries[0].StartTime,
                    TotalEntries = extEntries.Count,
                    SavedPath = outPath,
                    Message = $"已调整并保存 {extEntries.Count} 条字幕，偏移 {offsetDisplay}"
                });
            }

            return Ok(new
            {
                Applied = false,
                OffsetSeconds = -medianOffset,
                OffsetDisplay = offsetDisplay,
                ExternalFirstTimestamp = extEntries[0].StartTime,
                ReferenceFirstTimestamp = refEntries[0].StartTime,
                PreviewBefore = previewBefore,
                PreviewAfter = previewAfter,
                TotalEntries = extEntries.Count,
                ReferenceTotalEntries = refEntries.Count,
                Message = $"检测到偏移 {offsetDisplay}（基于前 {sampleCount} 条字幕）"
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = $"调整失败: {ex.Message}" });
        }
    }

    private static TimeSpan? ParseTimestamp(string ts)
    {
        // Supports "HH:MM:SS,mmm" and "HH:MM:SS.mmm"
        if (string.IsNullOrWhiteSpace(ts)) return null;
        ts = ts.Replace(',', '.');
        if (TimeSpan.TryParse(ts, CultureInfo.InvariantCulture, out var result))
            return result;
        return null;
    }

    private static string AdjustTimestamp(string ts, double offsetSeconds)
    {
        var parsed = ParseTimestamp(ts);
        if (!parsed.HasValue) return ts;
        var adjusted = parsed.Value + TimeSpan.FromSeconds(offsetSeconds);
        if (adjusted < TimeSpan.Zero) adjusted = TimeSpan.Zero;
        return adjusted.ToString(@"hh\:mm\:ss\,fff");
    }

    [HttpGet("Progress")]
    public IActionResult GetProgress([FromQuery] string? taskId)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(GetProgressPath("x")) ?? ".";
            if (!System.IO.Directory.Exists(dir))
                return Ok(new List<TaskProgress>());
            var files = System.IO.Directory.GetFiles(dir, "*.progress.json");
            var results = new List<TaskProgress>();
            foreach (var f in files)
            {
                try
                {
                    var json = System.IO.File.ReadAllText(f);
                    var p = System.Text.Json.JsonSerializer.Deserialize<TaskProgress>(json);
                    if (p != null)
                    {
                        if (string.IsNullOrWhiteSpace(taskId) || p.TaskId == taskId)
                            results.Add(p);
                    }
                }
                catch { }
            }
            // Clean old completed/failed entries (> 1 hour)
            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var f in files)
            {
                try
                {
                    var fi = new System.IO.FileInfo(f);
                    if (fi.LastWriteTimeUtc < cutoff)
                        System.IO.File.Delete(f);
                }
                catch { }
            }
            return Ok(results.OrderByDescending(r => r.StartTime).Take(20));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    private async Task<LanguageResult> DetectLanguageAsync(string sample, CancellationToken ct)
    {
        var apiUrl = string.IsNullOrWhiteSpace(_config.ApiUrl) ? "https://api.deepseek.com/chat/completions" : _config.ApiUrl;
        var apiKey = _config.ApiKey;
        var model = !string.IsNullOrWhiteSpace(_config.ModelName) ? _config.ModelName : "deepseek-chat";

        var prompt = $"What language is this subtitle text? Respond with ONLY a JSON object in this exact format: {{\"code\":\"ISO-639-1-code\",\"name\":\"Language name in English\"}}. Do not include any other text.\n\nText sample:\n{sample.Substring(0, Math.Min(sample.Length, 500))}";

        var body = new
        {
            model = model,
            messages = new[]
            {
                new { role = "system", content = "You are a language detection AI. Always respond with valid JSON only." },
                new { role = "user", content = prompt }
            },
            temperature = 0.1,
            max_tokens = 128
        };

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(body),
            Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var respJson = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(respJson);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";

        // Try to extract JSON from content
        var jsonMatch = System.Text.RegularExpressions.Regex.Match(content, "\\{[^}]+\\}");
        if (jsonMatch.Success)
        {
            try
            {
                var langDoc = JsonDocument.Parse(jsonMatch.Value);
                var code = langDoc.RootElement.GetProperty("code").GetString() ?? "??";
                var name = langDoc.RootElement.GetProperty("name").GetString() ?? "Unknown";
                return new LanguageResult { Code = code, Name = name };
            }
            catch { }
        }

        // Fallback
        return new LanguageResult { Code = "??", Name = "Unknown" };
    }

    private static string ExtractSample(string content)
    {
        var lines = content.Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d+$"))
            .Where(l => !l.Contains("-->"))
            .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d{2}:"))
            .Take(3);
        return string.Join("\n", lines);
    }

    private static string? LangNameToChinese(string? code)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = "\u82f1\u6587", ["en"] = "\u82f1\u6587",
            ["chi"] = "\u4e2d\u6587", ["zho"] = "\u4e2d\u6587", ["zh"] = "\u4e2d\u6587",
            ["jpn"] = "\u65e5\u6587", ["ja"] = "\u65e5\u6587",
            ["kor"] = "\u97e9\u6587", ["ko"] = "\u97e9\u6587",
            ["deu"] = "\u5fb7\u8bed", ["de"] = "\u5fb7\u8bed",
            ["fra"] = "\u6cd5\u8bed", ["fr"] = "\u6cd5\u8bed",
            ["spa"] = "\u897f\u73ed\u7259\u8bed", ["es"] = "\u897f\u73ed\u7259\u8bed",
            ["rus"] = "\u4fc4\u6587", ["ru"] = "\u4fc4\u6587",
            ["por"] = "\u8461\u8404\u7259\u8bed", ["pt"] = "\u8461\u8404\u7259\u8bed",
            ["ara"] = "\u963f\u62c9\u4f2f\u8bed", ["ar"] = "\u963f\u62c9\u4f2f\u8bed",
            ["ita"] = "\u610f\u5927\u5229\u8bed", ["it"] = "\u610f\u5927\u5229\u8bed"
        };
        return code != null && map.TryGetValue(code, out var name) ? name : null;
    }

    private string GetProgressPath(string taskId)
    {
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Plugin.Instance?.AssemblyFilePath ?? ".") ?? ".",
            "AITranslator_progress");
        if (!System.IO.Directory.Exists(dir))
            System.IO.Directory.CreateDirectory(dir);
        return System.IO.Path.Combine(dir, $"{taskId}.progress.json");
    }

    private static void WriteProgress(string path, TaskProgress progress)
    {
        try
        {
            System.IO.File.WriteAllText(path,
                System.Text.Json.JsonSerializer.Serialize(progress,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static string LangCode(string language)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["\u4e2d\u6587"] = "chi", ["Chinese"] = "chi", ["\u65e5\u6587"] = "jpn", ["Japanese"] = "jpn",
            ["\u5fb7\u8bed"] = "deu", ["German"] = "deu", ["\u97e9\u6587"] = "kor", ["Korean"] = "kor",
            ["\u897f\u73ed\u7259\u8bed"] = "spa", ["Spanish"] = "spa", ["\u6cd5\u8bed"] = "fra", ["French"] = "fra",
            ["\u82f1\u6587"] = "eng", ["English"] = "eng", ["\u4fc4\u8bed"] = "rus", ["Russian"] = "rus",
            ["\u8461\u8404\u7259\u8bed"] = "por", ["Portuguese"] = "por", ["\u963f\u62c9\u4f2f\u8bed"] = "ara", ["Arabic"] = "ara",
            ["\u610f\u5927\u5229\u8bed"] = "ita", ["Italian"] = "ita"
        };
        return map.TryGetValue(language, out var code) ? code : "chi";
    }

    private static string WriteSrtDirect(List<SubtitleEntry> entries)
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

    private static async Task<(bool Success, string? Error)> UploadSubtitleAsync(
        IHttpClientFactory httpClientFactory, string token, string itemId,
        string content, string format, string language, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(5);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"MediaBrowser Token={token}");

            var dataB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
            var body = new { Data = dataB64, Language = language, Format = format, IsForced = false, IsHearingImpaired = false };
            var jsonBody = JsonSerializer.Serialize(body);
            var response = await client.PostAsync(
                $"http://localhost:8096/Videos/{itemId}/Subtitles",
                new StringContent(jsonBody, Encoding.UTF8, "application/json"), ct);

            if (response.IsSuccessStatusCode)
            {
                _ = client.PostAsync($"http://localhost:8096/Items/{itemId}/Refresh",
                    new StringContent("{}", Encoding.UTF8, "application/json"), ct);
                return (true, null);
            }

            var error = await response.Content.ReadAsStringAsync(ct);
            return (false, $"HTTP {(int)response.StatusCode}: {error}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private string? GetTokenFromHeader()
    {
        if (Request.Headers.TryGetValue("Authorization", out var auth))
        {
            var a = auth.ToString();
            if (a.StartsWith("MediaBrowser Token=", StringComparison.OrdinalIgnoreCase))
                return a["MediaBrowser Token=".Length..].Trim();
            if (a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return a["Bearer ".Length..].Trim();
        }
        return null;
    }

    private async Task<string> GetStringAsync(string url, string token, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"MediaBrowser Token={token}");
        var resp = await client.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    [HttpGet("Config")]
    public IActionResult GetConfig()
    {
        return Ok(new
        {
            ApiProvider = _config.ApiProvider,
            ApiUrl = _config.ApiUrl,
            ModelName = _config.ModelName,
            TargetLanguage = _config.TargetLanguage,
            BatchSize = _config.BatchSize,
            ApiKeyConfigured = !string.IsNullOrWhiteSpace(_config.ApiKey)
        });
    }

    [HttpPost("Config")]
    public IActionResult SaveConfig([FromBody] JsonElement jsonConfig)
    {
        try
        {
            var existing = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            if (jsonConfig.TryGetProperty("apiProvider", out var p) || jsonConfig.TryGetProperty("ApiProvider", out p))
                existing.ApiProvider = p.GetString() ?? existing.ApiProvider;
            if (jsonConfig.TryGetProperty("apiKey", out var k) || jsonConfig.TryGetProperty("ApiKey", out k))
                existing.ApiKey = k.GetString() ?? existing.ApiKey;
            if (jsonConfig.TryGetProperty("apiUrl", out var u) || jsonConfig.TryGetProperty("ApiUrl", out u))
                existing.ApiUrl = u.GetString() ?? existing.ApiUrl;
            if (jsonConfig.TryGetProperty("modelName", out var m) || jsonConfig.TryGetProperty("ModelName", out m))
                existing.ModelName = m.GetString() ?? existing.ModelName;
            if (jsonConfig.TryGetProperty("targetLanguage", out var t) || jsonConfig.TryGetProperty("TargetLanguage", out t))
                existing.TargetLanguage = t.GetString() ?? existing.TargetLanguage;
            if (jsonConfig.TryGetProperty("batchSize", out var b) || jsonConfig.TryGetProperty("BatchSize", out b))
                existing.BatchSize = b.GetInt32();

            Plugin.Instance?.SaveConfig(existing);
            return Ok(new { Status = "ok", Message = "\u914d\u7f6e\u5df2\u4fdd\u5b58" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { Error = $"\u914d\u7f6e\u4fdd\u5b58\u5931\u8d25: {ex.Message}" });
        }
    }
}

public class TranslateAndSaveRequest
{
    public string ItemId { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? MediaSourceId { get; set; }
    public int SubtitleIndex { get; set; } = 6;
    public string? Format { get; set; }
    public string? OriginalLanguage { get; set; }
    public string Mode { get; set; } = "translated";
    public string? TargetLanguage { get; set; }
}

public class DetectLanguageRequest
{
    public string Sample { get; set; } = string.Empty;
}

public class LanguageResult
{
    public string Code { get; set; } = "??";
    public string Name { get; set; } = "Unknown";
}

public class TaskProgress
{
    public string TaskId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string ItemName { get; set; } = "";
    public int SubtitleIndex { get; set; }
    public string Mode { get; set; } = "";
    public string Status { get; set; } = "";
    public string SourceLanguage { get; set; } = "";
    public string TargetLanguage { get; set; } = "";
    public int EntryCount { get; set; }
    public string StartTime { get; set; } = "";
    public string? EndTime { get; set; }
    public string Message { get; set; } = "";
}

public class SubtitleLanguageInfo
{
    public int Index { get; set; }
    public string? JellyfinLanguage { get; set; }
    public string DetectedName { get; set; } = "Unknown";
    public string DetectedCode { get; set; } = "??";
    public string? Codec { get; set; }
    public bool IsExternal { get; set; }
    public string? Title { get; set; }
}

public class AdjustTimingRequest
{
    public string ItemId { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public int SubtitleIndex { get; set; }
    public int ReferenceSubtitleIndex { get; set; }
    public string? Format { get; set; } = "srt";
    public string? Language { get; set; }
    public bool Apply { get; set; }
}
