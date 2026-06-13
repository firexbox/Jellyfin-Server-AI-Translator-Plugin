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
            string? subtitleCodec = null;
            string? streamLanguage = null;
            foreach (var stream in streams)
            {
                if (stream.TryGetProperty("Type", out var typeProp) && typeProp.GetString() == "Subtitle")
                {
                    if (stream.TryGetProperty("Index", out var idxProp) && idxProp.GetInt32() == request.SubtitleIndex)
                    {
                        indexFound = true;
                        subtitleCodec = stream.TryGetProperty("Codec", out var cp) ? cp.GetString() : null;
                        streamLanguage = stream.TryGetProperty("Language", out var lp) ? lp.GetString() : null;
                        break;
                    }
                }
            }

            if (!indexFound)
                return BadRequest(new { Error = $"未找到字幕轨道索引 {request.SubtitleIndex}" });

            // Reject bitmap subtitle codecs
            var bitmapCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "pgssub", "hdmv_pgs", "dvdsub", "vobsub", "dvbsub", "xsub" };
            if (subtitleCodec != null && bitmapCodecs.Contains(subtitleCodec))
                return BadRequest(new { Error = $"字幕 #{request.SubtitleIndex} 为位图格式 ({subtitleCodec})，无法提取文字进行翻译" });

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

            // Detect source language: quick Unicode → Jellyfin metadata → AI fallback
            // Strip ASS/SSA formatting tags that could confuse detection
            var sampleText = string.Join("\n", entries.Take(5).Select(e =>
                System.Text.RegularExpressions.Regex.Replace(e.Text, @"\{[^}]*\}|<[^>]*>", "")));
            var quickResult = QuickDetectLanguage(sampleText);
            string sourceLangName, sourceLangCode;

            if (quickResult.Code != "??")
            {
                sourceLangName = LangNameToChinese(quickResult.Code) ?? quickResult.Name;
                sourceLangCode = quickResult.Code;
            }
            else
            {
                // Try Jellyfin metadata first
                if (!string.IsNullOrWhiteSpace(streamLanguage))
                {
                    sourceLangName = LangNameToChinese(streamLanguage) ?? streamLanguage;
                    sourceLangCode = streamLanguage;
                }
                else
                {
                    sourceLangName = "Unknown";
                    sourceLangCode = "??";
                }
                // If still unknown, fall back to AI
                if (sourceLangCode == "??" || sourceLangCode == "und")
                {
                    try
                    {
                        var detectedLang = await DetectLanguageAsync(sampleText, cancellationToken);
                        sourceLangName = LangNameToChinese(detectedLang.Code) ?? detectedLang.Name;
                        sourceLangCode = detectedLang.Code;
                    }
                    catch { }
                }
            }

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
                        var savedPath = TrySaveToMediaDir(capturedMediaDir, capturedVideoName,
                            $".{LangCode(capturedTargetLang)}.{capturedFormat}", translatedContent);
                        await UploadSubtitleAsync(capturedFactory, capturedToken, capturedItemId, translatedContent, capturedFormat, LangCode(capturedTargetLang), CancellationToken.None);
                        var msg = savedPath != null
                            ? $"已生成 {capturedTargetLang} 字幕"
                            : $"已通过 API 上传 {capturedTargetLang} 字幕 (媒体目录不可写)";
                        WriteProgress(capturedProgressPath, new TaskProgress
                        {
                            TaskId = taskId, ItemId = capturedItemId, ItemName = capturedVideoName,
                            SubtitleIndex = request.SubtitleIndex, Mode = capturedMode,
                            Status = "completed", SourceLanguage = capturedSourceLang,
                            TargetLanguage = capturedTargetLang, EntryCount = capturedEntries.Count,
                            StartTime = DateTime.UtcNow.ToString("O"), EndTime = DateTime.UtcNow.ToString("O"),
                            Message = msg
                        });
                    }
                    else if (capturedMode == "bilingual")
                    {
                        var bilingualEntries = new List<SubtitleEntry>();
                        foreach (var entry in capturedEntries)
                        {
                            var t = translatedEntries.FirstOrDefault(x => x.Index == entry.Index);
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
                        var savedPath = TrySaveToMediaDir(capturedMediaDir, capturedVideoName,
                            $".{LangCode(capturedTargetLang)}-{NormalizeLangCode(capturedSourceCode)}.{capturedFormat}", bilingualContent);
                        await UploadSubtitleAsync(capturedFactory, capturedToken, capturedItemId, bilingualContent, capturedFormat, LangCode(capturedTargetLang), CancellationToken.None);
                        var msg2 = savedPath != null
                            ? $"已生成 {capturedTargetLang}-{capturedSourceLang} 双语字幕"
                            : $"已通过 API 上传 {capturedTargetLang}-{capturedSourceLang} 双语字幕 (媒体目录不可写)";
                        WriteProgress(capturedProgressPath, new TaskProgress
                        {
                            TaskId = taskId, ItemId = capturedItemId, ItemName = capturedVideoName,
                            SubtitleIndex = request.SubtitleIndex, Mode = capturedMode,
                            Status = "completed", SourceLanguage = capturedSourceLang,
                            TargetLanguage = capturedTargetLang, EntryCount = capturedEntries.Count,
                            StartTime = DateTime.UtcNow.ToString("O"), EndTime = DateTime.UtcNow.ToString("O"),
                            Message = msg2
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

        var hasCJK = System.Text.RegularExpressions.Regex.IsMatch(s, @"[\u4e00-\u9fff\u3400-\u4dbf]");
        var hasLatin = System.Text.RegularExpressions.Regex.IsMatch(s, @"[a-zA-Z]{2,}");
        var hasKana = System.Text.RegularExpressions.Regex.IsMatch(s, @"[\u3040-\u309f\u30a0-\u30ff]");
        var hasHangul = System.Text.RegularExpressions.Regex.IsMatch(s, @"[\uac00-\ud7af]");

        // Detect mixed bilingual subtitles
        if (hasCJK && hasKana)
            return new LanguageResult { Code = "zh-ja", Name = "中日" };
        if (hasCJK && hasHangul)
            return new LanguageResult { Code = "zh-ko", Name = "中韩" };
        if (hasCJK && hasLatin)
            return new LanguageResult { Code = "zh-en", Name = "中英" };

        // Single-language detection
        if (hasCJK)
            return new LanguageResult { Code = "zh", Name = "中文" };
        if (hasKana)
            return new LanguageResult { Code = "ja", Name = "日文" };
        if (hasHangul)
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
        if (hasLatin)
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

            // Get reference subtitle content — skip bitmap codecs, try native codec first
            var refIndex = request.ReferenceSubtitleIndex;
            var refCodec = "srt";
            // Find the reference subtitle in MediaStreams to get its codec
            foreach (var stream in mediaSources.GetProperty("MediaStreams").EnumerateArray())
            {
                if (stream.TryGetProperty("Type", out var t) && t.GetString() == "Subtitle"
                    && stream.TryGetProperty("Index", out var idxProp) && idxProp.GetInt32() == refIndex)
                {
                    refCodec = stream.TryGetProperty("Codec", out var cp) ? (cp.GetString() ?? "srt") : "srt";
                    break;
                }
            }

            // Reject bitmap subtitle codecs early
            var bitmapCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "pgssub", "hdmv_pgs", "dvdsub", "vobsub", "dvbsub", "xsub" };
            if (bitmapCodecs.Contains(refCodec))
                return BadRequest(new { Error = $"参考字幕 #{refIndex} 为位图格式 ({refCodec})，无法提取时间轴。请选择文字格式的内嵌字幕作为参考（如 SRT/ASS）" });

            string? refContent = null;
            // Try formats in order: native codec → srt → ass → vtt
            var tryFormats = new[] { refCodec, "srt", "ass", "vtt" }.Distinct().ToArray();
            string? lastError = null;
            foreach (var tryFormat in tryFormats)
            {
                try
                {
                    var refSubUrl = $"http://localhost:8096/Videos/{request.ItemId}/{sourceId}/Subtitles/{refIndex}/Stream.{tryFormat}";
                    refContent = await GetStringAsync(refSubUrl, token, ct);
                    if (!string.IsNullOrWhiteSpace(refContent)) break;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            if (string.IsNullOrWhiteSpace(refContent))
                return BadRequest(new { Error = $"无法获取参考字幕 (索引 {refIndex}, 尝试格式 {string.Join("/", tryFormats)}): {lastError}" });

            // Parse both with detailed error reporting
            var extEntries = SubtitleParser.Parse(extContent);
            var refEntries = SubtitleParser.Parse(refContent);

            if (extEntries.Count == 0)
            {
                var preview = extContent.Length > 200 ? extContent[..200] : extContent;
                return BadRequest(new { Error = $"无法解析待调整字幕 (索引 {request.SubtitleIndex})，内容预览: {preview}" });
            }
            if (refEntries.Count == 0)
            {
                var preview = refContent.Length > 200 ? refContent[..200] : refContent;
                return BadRequest(new { Error = $"无法解析参考字幕 (索引 {refIndex}, codec={refCodec})，内容预览: {preview}" });
            }

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

                // Try save to media dir, fall back to API upload only
                var langCode = request.Language ?? "chi";
                var savedPath = TrySaveToMediaDir(mediaDir, videoName,
                    $".adjusted.{format}", adjustedContent);
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
                    SavedPath = savedPath,
                    Message = savedPath != null
                        ? $"已调整并保存 {extEntries.Count} 条字幕，偏移 {offsetDisplay}"
                        : $"已通过 API 上传调整后的字幕 ({extEntries.Count} 条，偏移 {offsetDisplay})"
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

    /// <summary>
    /// Tries to save content to media directory. Returns the saved path, or null if media dir is not writable.
    /// </summary>
    private static string? TrySaveToMediaDir(string mediaDir, string videoName, string suffix, string content)
    {
        if (string.IsNullOrWhiteSpace(mediaDir) || string.IsNullOrWhiteSpace(videoName))
            return null;
        try
        {
            var path = System.IO.Path.Combine(mediaDir, $"{videoName}{suffix}");
            System.IO.File.WriteAllText(path, content);
            try
            {
                System.IO.File.SetUnixFileMode(path,
                    System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite |
                    System.IO.UnixFileMode.GroupRead | System.IO.UnixFileMode.OtherRead);
            }
            catch { }
            return path;
        }
        catch
        {
            return null;
        }
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
            ["zh-en"] = "\u4e2d\u82f1", ["en-zh"] = "\u4e2d\u82f1",
            ["zh-ja"] = "\u4e2d\u65e5", ["ja-zh"] = "\u4e2d\u65e5",
            ["zh-ko"] = "\u4e2d\u97e9", ["ko-zh"] = "\u4e2d\u97e9",
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
            ["中文"] = "chi", ["Chinese"] = "chi", ["日文"] = "jpn", ["Japanese"] = "jpn",
            ["德语"] = "deu", ["German"] = "deu", ["韩文"] = "kor", ["Korean"] = "kor",
            ["西班牙语"] = "spa", ["Spanish"] = "spa", ["法语"] = "fra", ["French"] = "fra",
            ["英文"] = "eng", ["English"] = "eng", ["俄语"] = "rus", ["Russian"] = "rus",
            ["葡萄牙语"] = "por", ["Portuguese"] = "por", ["阿拉伯语"] = "ara", ["Arabic"] = "ara",
            ["意大利语"] = "ita", ["Italian"] = "ita"
        };
        return map.TryGetValue(language, out var code) ? code : "chi";
    }

    /// <summary>
    /// Normalizes a language code to the 3-letter format Jellyfin expects.
    /// Handles 2-letter ISO codes (en→eng, zh→chi, ja→jpn, ko→kor, etc.)
    /// and passes through already-valid 3-letter codes.
    /// </summary>
    private static string NormalizeLangCode(string code)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "eng", ["eng"] = "eng",
            ["zh"] = "chi", ["chi"] = "chi", ["zho"] = "chi",
            ["ja"] = "jpn", ["jpn"] = "jpn",
            ["ko"] = "kor", ["kor"] = "kor",
            ["de"] = "deu", ["deu"] = "deu",
            ["fr"] = "fra", ["fra"] = "fra",
            ["es"] = "spa", ["spa"] = "spa",
            ["ru"] = "rus", ["rus"] = "rus",
            ["pt"] = "por", ["por"] = "por",
            ["ar"] = "ara", ["ara"] = "ara",
            ["it"] = "ita", ["ita"] = "ita",
            ["th"] = "tha", ["tha"] = "tha",
        };
        return map.TryGetValue(code, out var result) ? result : code.ToLowerInvariant();
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
