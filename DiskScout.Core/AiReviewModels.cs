namespace DiskScout.Core;

public enum AiApiProtocol
{
    OpenAiResponses,
    CompatibleChatCompletions
}

public sealed record AiReviewSettings(
    string Endpoint,
    string ApiKey,
    string Model,
    AiApiProtocol Protocol,
    bool IncludeFileNamesAndPaths = false,
    int BatchSize = 25);

public enum FileAdviceAction
{
    Keep,
    Move,
    ReviewDelete,
    NeverDelete,
    NeedsReview
}

public sealed record FileAdviceResult(
    FileEntry File,
    FileAdviceAction Action,
    string Confidence,
    string Reason,
    string? SuggestedTarget,
    bool EnforcedByLocalSafety)
{
    public string ActionText => Action switch
    {
        FileAdviceAction.Keep => "建议保留",
        FileAdviceAction.Move => "建议转移",
        FileAdviceAction.ReviewDelete => "可考虑删除",
        FileAdviceAction.NeverDelete => "禁止删除",
        _ => "需要人工确认"
    };

    public string ConfidenceText => Confidence.ToLowerInvariant() switch
    {
        "high" => "高",
        "medium" => "中",
        _ => "低"
    };

    public string SourceText => EnforcedByLocalSafety ? "本地安全规则" : "AI 元数据审查";
    public string TargetText => string.IsNullOrWhiteSpace(SuggestedTarget) ? "—" : SuggestedTarget;
}

public sealed record AiReviewProgress(int Completed, int Total, int CurrentBatch, int BatchCount)
{
    public int Percentage => Total == 0 ? 100 : (int)Math.Round(Completed * 100d / Total);
}
