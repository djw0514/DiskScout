using DiskScout.Core;
using System.Net;
using System.Net.Http;
using System.Text;

if (args.FirstOrDefault() == "--benchmark")
    return await ScanBenchmark.RunAsync(args.ElementAtOrDefault(1));

var failures = new List<string>();
var passed = 0;

await Run("递归容量、文件数与分类", async () =>
{
    await WithFixture(async root =>
    {
        var result = await new DiskScanner().ScanAsync(new ScanOptions(root, LargeFileThresholdBytes: 1_000));
        Equal(7_680L, result.TotalBytes, "总容量");
        Equal(4L, result.FileCount, "文件数");
        Equal(4L, result.DirectoryCount, "目录数（含根目录）");
        Equal(3, result.LargeFiles.Count, "大文件数量");

        var folderA = result.Folders.Single(x => x.Name == "A");
        Equal(7_168L, folderA.Size, "A 目录递归容量");
        Equal(3L, folderA.FileCount, "A 目录递归文件数");
        Equal(4_096L, result.Categories.Single(x => x.Name == "压缩包/镜像").Size, "压缩包分类容量");
    });
});

await Run("Top N 大文件保留", async () =>
{
    await WithFixture(async root =>
    {
        var result = await new DiskScanner().ScanAsync(new ScanOptions(root, 0, MaxLargeFiles: 2));
        Equal(2, result.LargeFiles.Count, "Top N 数量");
        Equal(4_096L, result.LargeFiles[0].Size, "第一大文件");
        Equal(2_048L, result.LargeFiles[1].Size, "第二大文件");
    });
});

await Run("跨目录 Top N、相同大小与完整统计", async () =>
{
    await WithFixture(async root =>
    {
        for (var i = 0; i < 100; i++)
        {
            var directory = Path.Combine(root, $"Group{i % 5}", $"Child{i % 7}");
            Directory.CreateDirectory(directory);
            using var file = File.Create(Path.Combine(directory, $"file{i:D3}.txt"));
            file.SetLength(i % 10 * 512);
        }

        var expected = new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).ToArray();
        var result = await new DiskScanner().ScanAsync(new ScanOptions(root, 0, MaxLargeFiles: 7));
        Equal(expected.LongLength, result.FileCount, "文件数不能受 Top N 限制影响");
        Equal(expected.Sum(x => x.Length), result.TotalBytes, "容量不能受 Top N 限制影响");
        True(expected.Select(x => x.Length).OrderDescending().Take(7)
            .SequenceEqual(result.LargeFiles.Select(x => x.Size)), "保留大小排序与完整枚举一致");
        Equal(7, result.LargeFiles.Select(x => x.FullPath).Distinct().Count(), "同大小文件不重复");
        Equal(result.TotalBytes, result.Categories.Sum(x => x.Size), "类别容量合计");
        Equal(result.FileCount, result.Categories.Sum(x => x.FileCount), "类别文件数合计");

        foreach (var folder in result.Folders)
        {
            var contents = new DirectoryInfo(folder.FullPath).EnumerateFiles("*", SearchOption.AllDirectories).ToArray();
            Equal(contents.Sum(x => x.Length), folder.Size, $"递归容量：{folder.Name}");
            Equal(contents.LongLength, folder.FileCount, $"递归文件数：{folder.Name}");
        }
    });
});

await Run("大文件阈值边界与 Top 1", async () =>
{
    await WithFixture(async root =>
    {
        var scanner = new DiskScanner();
        var atThreshold = await scanner.ScanAsync(new ScanOptions(root, 4_096, MaxLargeFiles: 1));
        Equal(1, atThreshold.LargeFiles.Count, "等于阈值的文件应保留");
        Equal(4_096L, atThreshold.LargeFiles[0].Size, "Top 1 大小");
        var aboveThreshold = await scanner.ScanAsync(new ScanOptions(root, 4_097));
        Equal(0, aboveThreshold.LargeFiles.Count, "阈值以上没有候选文件");
        Equal(7_680L, aboveThreshold.TotalBytes, "阈值不影响总容量");
    });
});

await Run("空目录、根文件与深层目录汇总", async () =>
{
    await WithFixture(async root =>
    {
        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(empty);
        var scanner = new DiskScanner();
        var emptyResult = await scanner.ScanAsync(new ScanOptions(empty));
        Equal(0L, emptyResult.TotalBytes, "空目录容量");
        Equal(1L, emptyResult.DirectoryCount, "空目录计数");
        Equal(0, emptyResult.Folders.Count, "根目录不出现在目录排行");

        await File.WriteAllBytesAsync(Path.Combine(root, "root.txt"), new byte[123]);
        var deepest = root;
        for (var i = 0; i < 24; i++)
            deepest = Path.Combine(deepest, "d");
        Directory.CreateDirectory(deepest);
        await File.WriteAllBytesAsync(Path.Combine(deepest, "deep.txt"), new byte[321]);
        var result = await scanner.ScanAsync(new ScanOptions(root + Path.DirectorySeparatorChar, 0));
        Equal(8_124L, result.TotalBytes, "根文件与深层文件均计入总容量");
        Equal(6L, result.FileCount, "文件数");
        Equal(29L, result.DirectoryCount, "目录数");
        Equal(28, result.Folders.Count, "目录排行不含根目录");
        foreach (var folder in result.Folders.Where(x => x.Name == "d"))
        {
            Equal(321L, folder.Size, "每层祖先容量");
            Equal(1L, folder.FileCount, "每层祖先文件数");
        }
    });
});

await Run("最终进度回调", async () =>
{
    await WithFixture(async root =>
    {
        ScanProgress? latest = null;
        var progress = new InlineProgress<ScanProgress>(value => latest = value);
        var result = await new DiskScanner().ScanAsync(new ScanOptions(root), progress);
        True(latest is not null, "应至少收到最终进度");
        Equal(4L, latest!.FilesScanned, "最终进度文件数");
        Equal(7_680L, latest.BytesScanned, "最终进度容量");
        Equal(result.Duration, latest.Elapsed, "最终进度包含结果整理耗时");
    });
});

await Run("扫描取消", async () =>
{
    await WithFixture(async root =>
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await new DiskScanner().ScanAsync(new ScanOptions(root), cancellationToken: cancellation.Token);
            throw new InvalidOperationException("取消后的扫描不应完成");
        }
        catch (OperationCanceledException)
        {
        }
    });
});

await Run("进度回调取消后不返回结果", async () =>
{
    await WithFixture(async root =>
    {
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<ScanProgress>(_ => cancellation.Cancel());
        try
        {
            await new DiskScanner().ScanAsync(new ScanOptions(root), progress, cancellation.Token);
            throw new InvalidOperationException("收到取消信号后不应返回扫描结果");
        }
        catch (OperationCanceledException)
        {
        }
    });
});

await Run("同一扫描器并发调用相互隔离", async () =>
{
    await WithFixture(async root =>
    {
        var scanner = new DiskScanner();
        var results = await Task.WhenAll(
            scanner.ScanAsync(new ScanOptions(Path.Combine(root, "A"), 0, MaxLargeFiles: 1)),
            scanner.ScanAsync(new ScanOptions(Path.Combine(root, "B"), 0, MaxLargeFiles: 1)));
        Equal(7_168L, results[0].TotalBytes, "A 独立容量");
        Equal(512L, results[1].TotalBytes, "B 独立容量");
        Equal(3L, results[0].FileCount, "A 独立文件数");
        Equal(1L, results[1].FileCount, "B 独立文件数");
    });
});

await Run("AI 结构化审查与隐私默认值", async () =>
{
    const string apiResponse = """
        {
          "output": [{
            "type": "message",
            "content": [{
              "type": "output_text",
              "text": "{\"reviews\":[{\"id\":\"F00001\",\"action\":\"move\",\"confidence\":\"high\",\"reason\":\"适合迁移归档，仍需用户确认。\",\"target\":\"普通数据盘\"}]}"
            }]
          }]
        }
        """;
    var handler = new StubHttpHandler(apiResponse);
    var service = new AiReviewService(new HttpClient(handler));
    var file = new FileEntry("private_archive.zip", Path.Combine(Path.GetTempPath(), "private_archive.zip"), 8_000_000, DateTime.Now.AddYears(-2), "压缩包/镜像");
    var settings = new AiReviewSettings("https://example.test/v1/responses", "test-key", "test-model", AiApiProtocol.OpenAiResponses);

    var results = await service.ReviewAsync(settings, new[] { file });
    Equal(FileAdviceAction.Move, results[0].Action, "AI 建议动作");
    Equal("普通数据盘", results[0].SuggestedTarget!, "迁移目标");
    True(handler.RequestBody.Contains("json_schema", StringComparison.Ordinal), "应请求结构化输出");
    True(handler.RequestBody.Contains("\"store\":false", StringComparison.Ordinal), "OpenAI 请求应关闭存储");
    True(!handler.RequestBody.Contains(file.Name, StringComparison.Ordinal), "默认不得发送文件名");
    True(!handler.RequestBody.Contains(file.FullPath, StringComparison.Ordinal), "默认不得发送完整路径");
});

await Run("系统文件由本地规则强制保护", async () =>
{
    var handler = new StubHttpHandler("{}");
    var service = new AiReviewService(new HttpClient(handler));
    var protectedFile = new FileEntry(
        "kernel-test.dll",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "kernel-test.dll"),
        200_000_000,
        DateTime.Now,
        "程序与组件");
    var settings = new AiReviewSettings("http://localhost/v1/responses", string.Empty, "local-model", AiApiProtocol.OpenAiResponses);

    var results = await service.ReviewAsync(settings, new[] { protectedFile });
    Equal(FileAdviceAction.NeverDelete, results[0].Action, "系统文件动作");
    True(results[0].EnforcedByLocalSafety, "必须标记为本地安全规则");
    Equal(0, handler.CallCount, "系统文件不应发送到 API");
});

await Run("容量格式化", () =>
{
    Equal("1 KB", ByteSizeFormatter.Format(1_024), "1 KB 格式");
    Equal("1.5 MB", ByteSizeFormatter.Format(1_572_864), "1.5 MB 格式");
    return Task.CompletedTask;
});

Console.WriteLine();
Console.WriteLine(failures.Count == 0
    ? $"全部通过：{passed} 项"
    : $"失败：{failures.Count} 项，通过：{passed} 项");
foreach (var failure in failures)
    Console.WriteLine($"  - {failure}");

return failures.Count == 0 ? 0 : 1;

async Task Run(string name, Func<Task> test)
{
    try
    {
        await test();
        passed++;
        Console.WriteLine($"[PASS] {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
        Console.WriteLine($"[FAIL] {name}");
    }
}

static async Task WithFixture(Func<string, Task> test)
{
    var root = Path.Combine(Path.GetTempPath(), $"DiskScout.Tests.{Guid.NewGuid():N}");
    Directory.CreateDirectory(Path.Combine(root, "A", "Nested"));
    Directory.CreateDirectory(Path.Combine(root, "B"));
    try
    {
        await File.WriteAllBytesAsync(Path.Combine(root, "A", "video.mp4"), new byte[2_048]);
        await File.WriteAllBytesAsync(Path.Combine(root, "A", "notes.txt"), new byte[1_024]);
        await File.WriteAllBytesAsync(Path.Combine(root, "A", "Nested", "backup.zip"), new byte[4_096]);
        await File.WriteAllBytesAsync(Path.Combine(root, "B", "source.cs"), new byte[512]);
        await test(root);
    }
    finally
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}

static void Equal<T>(T expected, T actual, string context) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{context}：期望 {expected}，实际 {actual}");
}

static void True(bool condition, string context)
{
    if (!condition) throw new InvalidOperationException(context);
}

sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}

sealed class StubHttpHandler(string responseBody) : HttpMessageHandler
{
    public int CallCount { get; private set; }
    public string RequestBody { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        RequestBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        };
    }
}
