using System.Collections.ObjectModel;

namespace DiskScout.Core;

public sealed record ScanOptions(
    string RootPath,
    long LargeFileThresholdBytes = 100L * 1024 * 1024,
    int MaxLargeFiles = 2_000,
    int MaxIssues = 500);

public enum ScanPhase
{
    Enumerating,
    Aggregating
}

public sealed record ScanProgress(
    long FilesScanned,
    long DirectoriesScanned,
    long BytesScanned,
    string CurrentPath,
    TimeSpan Elapsed)
{
    public long IssuesFound { get; init; }
    public ScanPhase Phase { get; init; } = ScanPhase.Enumerating;
}

public sealed record FileEntry(
    string Name,
    string FullPath,
    long Size,
    DateTime LastWriteTime,
    string Category)
{
    public string SizeText => ByteSizeFormatter.Format(Size);
    public string ModifiedText => LastWriteTime.ToString("yyyy-MM-dd HH:mm");
}

public sealed record FolderEntry(
    string Name,
    string FullPath,
    long Size,
    long FileCount)
{
    public string SizeText => ByteSizeFormatter.Format(Size);
    public string FileCountText => FileCount.ToString("N0");
}

public sealed record CategoryEntry(
    string Name,
    long Size,
    long FileCount,
    double Percentage)
{
    public string SizeText => ByteSizeFormatter.Format(Size);
    public string FileCountText => FileCount.ToString("N0");
    public string PercentageText => $"{Percentage:0.0}%";
}

public enum ScanIssueKind
{
    AccessDenied,
    PathTooLong,
    IoError,
    Other
}

public sealed record ScanIssue(string Path, ScanIssueKind Kind, string Message)
{
    public string KindText => Kind switch
    {
        ScanIssueKind.AccessDenied => "无权限",
        ScanIssueKind.PathTooLong => "路径过长",
        ScanIssueKind.IoError => "读写错误",
        _ => "其他"
    };
}

public enum InsightLevel
{
    Info,
    Attention,
    Warning
}

public sealed record ScanInsight(string Title, string Description, InsightLevel Level)
{
    public string LevelText => Level switch
    {
        InsightLevel.Warning => "优先处理",
        InsightLevel.Attention => "建议检查",
        _ => "提示"
    };
}

public sealed class ScanResult
{
    public required string RootPath { get; init; }
    public required DateTime StartedAt { get; init; }
    public required TimeSpan Duration { get; init; }
    public required long TotalBytes { get; init; }
    public required long FileCount { get; init; }
    public required long DirectoryCount { get; init; }
    public required IReadOnlyList<FileEntry> LargeFiles { get; init; }
    public required IReadOnlyList<FolderEntry> Folders { get; init; }
    public required IReadOnlyList<CategoryEntry> Categories { get; init; }
    public required IReadOnlyList<ScanIssue> Issues { get; init; }
    public required IReadOnlyList<ScanInsight> Insights { get; init; }

    public string TotalBytesText => ByteSizeFormatter.Format(TotalBytes);
    public string FileCountText => FileCount.ToString("N0");
    public string DirectoryCountText => DirectoryCount.ToString("N0");
    public string DurationText => Duration.TotalMinutes >= 1
        ? $"{(int)Duration.TotalMinutes}分 {Duration.Seconds}秒"
        : $"{Math.Max(1, (int)Math.Ceiling(Duration.TotalSeconds))}秒";
}

public static class ByteSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        if (bytes < 0) return "—";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} B" : $"{value:0.##} {Units[unit]}";
    }
}
