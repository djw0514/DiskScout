using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DiskScout.Core;

public sealed class AiReviewService
{
    private const string Instructions = """
        你是谨慎的 Windows 磁盘空间整理顾问。你只能根据给出的文件元数据提供建议，不能假装看过文件内容。
        action 只能是 keep、move、review_delete、never_delete：
        - keep：仍可能使用的文档、项目、程序数据或信息不足的文件；
        - move：适合迁移到普通盘、归档盘或外置存储的媒体、归档、镜像、备份、旧项目；
        - review_delete：仅用于很可能可再生成的缓存、临时文件、崩溃转储、过期日志或用户确认不用的旧安装包；
        - never_delete：操作系统、驱动、程序组件、恢复数据或删除风险很高的文件。
        始终保守判断。不得直接命令用户删除；理由用简短中文，并明确仍需用户确认。target 仅在 move 时给出通用目标，否则为 null。
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public AiReviewService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public async Task<IReadOnlyList<FileAdviceResult>> ReviewAsync(
        AiReviewSettings settings,
        IReadOnlyList<FileEntry> files,
        IProgress<AiReviewProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(settings);
        if (files.Count == 0) return Array.Empty<FileAdviceResult>();

        var final = new Dictionary<string, FileAdviceResult>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string Id, FileEntry File)>();
        for (var i = 0; i < files.Count; i++)
        {
            var id = $"F{i + 1:D5}";
            var file = files[i];
            if (IsProtectedSystemFile(file.FullPath))
            {
                final[id] = new FileAdviceResult(
                    file,
                    FileAdviceAction.NeverDelete,
                    "high",
                    "该文件位于 Windows 或程序受保护区域，本地安全规则禁止给出删除建议。",
                    null,
                    EnforcedByLocalSafety: true);
            }
            else
            {
                candidates.Add((id, file));
            }
        }

        var batchSize = Math.Clamp(settings.BatchSize, 1, 50);
        var batchCount = Math.Max(1, (int)Math.Ceiling(candidates.Count / (double)batchSize));
        var completed = final.Count;
        progress?.Report(new AiReviewProgress(completed, files.Count, 0, batchCount));

        for (var offset = 0; offset < candidates.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchNumber = offset / batchSize + 1;
            var batch = candidates.Skip(offset).Take(batchSize).ToArray();
            var responseItems = await ReviewBatchAsync(settings, batch, cancellationToken).ConfigureAwait(false);

            var byId = responseItems
                .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in batch)
            {
                if (byId.TryGetValue(candidate.Id, out var modelResult))
                    final[candidate.Id] = ToSafeResult(candidate.File, modelResult);
                else
                    final[candidate.Id] = MissingResult(candidate.File);
            }

            completed += batch.Length;
            progress?.Report(new AiReviewProgress(completed, files.Count, batchNumber, batchCount));
        }

        return Enumerable.Range(1, files.Count)
            .Select(i => final[$"F{i:D5}"])
            .ToArray();
    }

    private async Task<IReadOnlyList<ModelReview>> ReviewBatchAsync(
        AiReviewSettings settings,
        IReadOnlyList<(string Id, FileEntry File)> batch,
        CancellationToken cancellationToken)
    {
        var payload = batch.Select(x => BuildMetadata(x.Id, x.File, settings.IncludeFileNamesAndPaths)).ToArray();
        var inputJson = JsonSerializer.Serialize(payload, JsonOptions);
        var body = settings.Protocol == AiApiProtocol.OpenAiResponses
            ? BuildResponsesBody(settings.Model, inputJson)
            : BuildChatCompletionsBody(settings.Model, inputJson);

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = responseText.Length > 600 ? responseText[..600] + "…" : responseText;
            throw new HttpRequestException($"AI API 返回 {(int)response.StatusCode} {response.ReasonPhrase}：{detail}");
        }

        var outputJson = settings.Protocol == AiApiProtocol.OpenAiResponses
            ? ExtractResponsesText(responseText)
            : ExtractChatText(responseText);
        outputJson = StripMarkdownFence(outputJson);

        var parsed = JsonSerializer.Deserialize<ModelResponse>(outputJson, JsonOptions)
            ?? throw new InvalidDataException("AI 返回了空的审查结果。");
        return parsed.Reviews ?? Array.Empty<ModelReview>();
    }

    private static object BuildResponsesBody(string model, string inputJson)
    {
        var schema = BuildSchema();
        return new
        {
            model,
            instructions = Instructions,
            input = inputJson,
            store = false,
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "disk_file_reviews",
                    strict = true,
                    schema
                }
            }
        };
    }

    private static object BuildChatCompletionsBody(string model, string inputJson) => new
    {
        model,
        temperature = 0.1,
        messages = new object[]
        {
            new { role = "system", content = Instructions },
            new { role = "user", content = $"请审查以下 JSON 数组，并返回 {{\"reviews\":[...]}}：\n{inputJson}" }
        },
        response_format = new { type = "json_object" }
    };

    private static object BuildSchema() => new
    {
        type = "object",
        properties = new
        {
            reviews = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        id = new { type = "string" },
                        action = new { type = "string", @enum = new[] { "keep", "move", "review_delete", "never_delete" } },
                        confidence = new { type = "string", @enum = new[] { "low", "medium", "high" } },
                        reason = new { type = "string" },
                        target = new { type = new[] { "string", "null" } }
                    },
                    required = new[] { "id", "action", "confidence", "reason", "target" },
                    additionalProperties = false
                }
            }
        },
        required = new[] { "reviews" },
        additionalProperties = false
    };

    private static object BuildMetadata(string id, FileEntry file, bool includeSensitiveNames)
    {
        var ageDays = Math.Max(0, (int)(DateTime.Now - file.LastWriteTime).TotalDays);
        return new
        {
            id,
            name = includeSensitiveNames ? file.Name : null,
            path_hint = includeSensitiveNames ? RedactUserProfile(file.FullPath) : null,
            extension = Path.GetExtension(file.Name).ToLowerInvariant(),
            size_bytes = file.Size,
            age_days = ageDays,
            modified = file.LastWriteTime.ToString("yyyy-MM-dd"),
            category = file.Category,
            location = ClassifyLocation(file.FullPath)
        };
    }

    private static FileAdviceResult ToSafeResult(FileEntry file, ModelReview review)
    {
        var action = review.Action?.ToLowerInvariant() switch
        {
            "keep" => FileAdviceAction.Keep,
            "move" => FileAdviceAction.Move,
            "review_delete" => FileAdviceAction.ReviewDelete,
            "never_delete" => FileAdviceAction.NeverDelete,
            _ => FileAdviceAction.NeedsReview
        };
        var confidence = review.Confidence?.ToLowerInvariant() is "high" or "medium" or "low"
            ? review.Confidence.ToLowerInvariant()
            : "low";
        var reason = string.IsNullOrWhiteSpace(review.Reason)
            ? "模型未提供理由，需要人工确认。"
            : review.Reason.Trim();
        return new FileAdviceResult(
            file,
            action,
            confidence,
            reason,
            action == FileAdviceAction.Move ? review.Target?.Trim() : null,
            EnforcedByLocalSafety: false);
    }

    private static FileAdviceResult MissingResult(FileEntry file) => new(
        file,
        FileAdviceAction.NeedsReview,
        "low",
        "模型未返回该文件的结果，需要人工确认。",
        null,
        EnforcedByLocalSafety: false);

    private static void Validate(AiReviewSettings settings)
    {
        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("AI API 地址必须是有效的 HTTP/HTTPS 地址。", nameof(settings));
        if (string.IsNullOrWhiteSpace(settings.Model))
            throw new ArgumentException("模型名称不能为空。", nameof(settings));
        if (endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new ArgumentException("OpenAI API 需要 API Key；可在界面输入或设置 OPENAI_API_KEY。", nameof(settings));
    }

    private static string ExtractResponsesText(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        if (root.TryGetProperty("output_text", out var direct) && direct.ValueKind == JsonValueKind.String)
            return direct.GetString()!;

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var type) && type.GetString() == "output_text" &&
                        part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        return text.GetString()!;
                }
            }
        }
        throw new InvalidDataException("AI Responses API 响应中没有 output_text。");
    }

    private static string ExtractChatText(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        if (document.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            return content.GetString()!;
        throw new InvalidDataException("兼容 API 响应中没有 choices[0].message.content。");
    }

    private static string StripMarkdownFence(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var firstNewLine = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewLine >= 0 && lastFence > firstNewLine
            ? text[(firstNewLine + 1)..lastFence].Trim()
            : text;
    }

    private static bool IsProtectedSystemFile(string fullPath)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };
        if (roots.Where(x => !string.IsNullOrWhiteSpace(x)).Any(root => IsUnder(fullPath, root)))
            return true;

        var name = Path.GetFileName(fullPath);
        return name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("bootmgr", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifyLocation(string fullPath)
    {
        if (IsUnder(fullPath, Environment.GetFolderPath(Environment.SpecialFolder.Windows))) return "windows_system";
        if (IsUnder(fullPath, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) ||
            IsUnder(fullPath, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))) return "installed_program";
        if (IsUnder(fullPath, Path.GetTempPath()) || fullPath.Contains("\\Temp\\", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("\\Cache\\", StringComparison.OrdinalIgnoreCase)) return "temp_or_cache";
        if (IsUnder(fullPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))) return "user_profile";
        return "other_drive_or_folder";
    }

    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string RedactUserProfile(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return IsUnder(path, profile)
            ? "%USERPROFILE%" + path[Path.TrimEndingDirectorySeparator(profile).Length..]
            : path;
    }

    private sealed class ModelResponse
    {
        public ModelReview[]? Reviews { get; init; }
    }

    private sealed class ModelReview
    {
        public string Id { get; init; } = string.Empty;
        public string? Action { get; init; }
        public string? Confidence { get; init; }
        public string? Reason { get; init; }
        public string? Target { get; init; }
    }
}
