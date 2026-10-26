namespace DiskScout.Core;

public static class FileCategoryClassifier
{
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
        { ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".txt", ".md", ".csv", ".rtf" };
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".tif", ".tiff", ".heic", ".raw" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts" };
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma" };
    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".iso", ".cab" };
    private static readonly HashSet<string> Programs = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".msi", ".sys", ".appx", ".msix", ".apk" };
    private static readonly HashSet<string> Development = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".cpp", ".c", ".h", ".hpp", ".js", ".ts", ".py", ".java", ".go", ".rs", ".json", ".xml", ".yaml", ".yml", ".pdb", ".obj", ".lib" };
    private static readonly HashSet<string> Data = new(StringComparer.OrdinalIgnoreCase)
        { ".db", ".sqlite", ".sqlite3", ".bak", ".sql", ".vhd", ".vhdx", ".vmdk", ".bin" };

    public static string Classify(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return "无扩展名";
        if (Documents.Contains(extension)) return "文档";
        if (Images.Contains(extension)) return "图片";
        if (Videos.Contains(extension)) return "视频";
        if (Audio.Contains(extension)) return "音频";
        if (Archives.Contains(extension)) return "压缩包/镜像";
        if (Programs.Contains(extension)) return "程序与组件";
        if (Development.Contains(extension)) return "开发文件";
        if (Data.Contains(extension)) return "数据与虚拟磁盘";
        return "其他";
    }
}
