using System.Diagnostics;

namespace DiskScout.Core;

public sealed class DiskScanner
{
    private sealed class MutableFolder(string path, MutableFolder? parent = null)
    {
        public string Path { get; } = path;
        public MutableFolder? Parent { get; } = parent;
        public long TotalSize { get; set; }
        public long TotalFileCount { get; set; }
    }

    private sealed class MutableCategory
    {
        public long Size { get; set; }
        public long FileCount { get; set; }
    }

    public Task<ScanResult> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => Scan(options, progress, cancellationToken), cancellationToken);
    }

    private static ScanResult Scan(
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.RootPath))
            throw new ArgumentException("扫描路径不能为空。", nameof(options));
        if (options.LargeFileThresholdBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "大文件阈值不能为负数。");
        if (options.MaxLargeFiles <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "最大保留文件数必须大于 0。");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));
        if (Path.GetPathRoot(root)?.Equals(root, StringComparison.OrdinalIgnoreCase) == true)
            root = Path.GetPathRoot(root)!;
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"找不到扫描目录：{root}");

        var startedAt = DateTime.Now;
        var watch = Stopwatch.StartNew();
        var progressWatch = Stopwatch.StartNew();
        var rootFolder = new MutableFolder(root);
        var folders = new List<MutableFolder> { rootFolder };
        var categories = new Dictionary<string, MutableCategory>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<ScanIssue>();
        var largeFiles = new PriorityQueue<FileEntry, long>();
        var pending = new Stack<MutableFolder>();
        pending.Push(rootFolder);

        long fileCount = 0;
        long directoryCount = 0;
        long totalBytes = 0;
        long issueCount = 0;
        var currentPath = root;
        var phase = ScanPhase.Enumerating;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentFolder = pending.Pop();
            currentPath = currentFolder.Path;
            directoryCount++;

            try
            {
                var directory = new DirectoryInfo(currentPath);
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        if ((entry.Attributes & FileAttributes.Directory) != 0)
                        {
                            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                                continue;

                            var child = new MutableFolder(entry.FullName, currentFolder);
                            folders.Add(child);
                            pending.Push(child);
                        }
                        else
                        {
                            ProcessFile((FileInfo)entry, currentFolder);
                        }
                    }
                    catch (Exception ex) when (IsRecoverable(ex))
                    {
                        AddIssue(entry.FullName, ex);
                    }

                    ReportProgressIfDue(force: false);
                }
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                AddIssue(currentPath, ex);
            }

            ReportProgressIfDue(force: false);
        }

        phase = ScanPhase.Aggregating;
        ReportProgressIfDue(force: true);
        cancellationToken.ThrowIfCancellationRequested();

        // Parents are discovered before their children. Reverse discovery order
        // therefore aggregates every descendant before its parent, without sorting paths.
        for (var i = folders.Count - 1; i > 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = folders[i];
            folder.Parent!.TotalSize += folder.TotalSize;
            folder.Parent.TotalFileCount += folder.TotalFileCount;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var folderResults = folders
            .Skip(1)
            .OrderByDescending(x => x.TotalSize)
            .Select(x => new FolderEntry(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(x.Path)) is { Length: > 0 } name ? name : x.Path,
                x.Path,
                x.TotalSize,
                x.TotalFileCount))
            .ToArray();

        var categoryResults = categories
            .OrderByDescending(x => x.Value.Size)
            .Select(x => new CategoryEntry(
                x.Key,
                x.Value.Size,
                x.Value.FileCount,
                totalBytes == 0 ? 0 : x.Value.Size * 100d / totalBytes))
            .ToArray();

        var fileResults = largeFiles.UnorderedItems
            .Select(x => x.Element)
            .OrderByDescending(x => x.Size)
            .ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        var preliminary = new ScanResult
        {
            RootPath = root,
            StartedAt = startedAt,
            Duration = watch.Elapsed,
            TotalBytes = totalBytes,
            FileCount = fileCount,
            DirectoryCount = directoryCount,
            LargeFiles = fileResults,
            Folders = folderResults,
            Categories = categoryResults,
            Issues = issues.AsReadOnly(),
            Insights = Array.Empty<ScanInsight>()
        };

        var insights = InsightBuilder.Build(preliminary);
        watch.Stop();
        ReportProgressIfDue(force: true);
        cancellationToken.ThrowIfCancellationRequested();

        return new ScanResult
        {
            RootPath = preliminary.RootPath,
            StartedAt = preliminary.StartedAt,
            Duration = watch.Elapsed,
            TotalBytes = preliminary.TotalBytes,
            FileCount = preliminary.FileCount,
            DirectoryCount = preliminary.DirectoryCount,
            LargeFiles = preliminary.LargeFiles,
            Folders = preliminary.Folders,
            Categories = preliminary.Categories,
            Issues = preliminary.Issues,
            Insights = insights
        };

        void ProcessFile(FileInfo file, MutableFolder folder)
        {
            var length = file.Length;
            var categoryName = FileCategoryClassifier.Classify(file.Extension);
            fileCount++;
            totalBytes += length;

            folder.TotalSize += length;
            folder.TotalFileCount++;

            if (!categories.TryGetValue(categoryName, out var category))
                categories[categoryName] = category = new MutableCategory();
            category.Size += length;
            category.FileCount++;

            if (length >= options.LargeFileThresholdBytes &&
                (largeFiles.Count < options.MaxLargeFiles || length > largeFiles.Peek().Size))
            {
                var item = new FileEntry(file.Name, file.FullName, length, file.LastWriteTime, categoryName);
                if (largeFiles.Count < options.MaxLargeFiles)
                    largeFiles.Enqueue(item, length);
                else
                    largeFiles.DequeueEnqueue(item, length);
            }
        }

        void AddIssue(string path, Exception exception)
        {
            issueCount++;
            if (issues.Count >= Math.Max(0, options.MaxIssues)) return;
            var kind = exception switch
            {
                UnauthorizedAccessException => ScanIssueKind.AccessDenied,
                PathTooLongException => ScanIssueKind.PathTooLong,
                IOException => ScanIssueKind.IoError,
                _ => ScanIssueKind.Other
            };
            issues.Add(new ScanIssue(path, kind, exception.Message));
        }

        void ReportProgressIfDue(bool force)
        {
            if (progress is null || (!force && progressWatch.ElapsedMilliseconds < 120)) return;
            progressWatch.Restart();
            progress.Report(new ScanProgress(fileCount, directoryCount, totalBytes, currentPath, watch.Elapsed)
            {
                IssuesFound = issueCount,
                Phase = phase
            });
        }
    }

    private static bool IsRecoverable(Exception exception) => exception is
        UnauthorizedAccessException or
        IOException or
        System.Security.SecurityException or
        NotSupportedException;
}
