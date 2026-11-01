using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiskScout.App;
using DiskScout.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = args.FirstOrDefault() == "--screenshots"
            ? CaptureScreenshotsAsync(args.ElementAtOrDefault(1) ?? "artifacts/ui-review")
            : RunAsync();
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => app.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync()
    {
        var failures = 0;
        var passed = 0;
        await Run("准备、扫描与汇总阶段反馈", async (window, first, second) =>
        {
            Check(Control<Grid>(window, "ActivityPanel").Visibility == Visibility.Visible, "初始界面应提供开始引导");
            Check(Control<TabControl>(window, "ResultTabs").Visibility == Visibility.Collapsed, "尚未扫描时不应显示空结果表格");
            Check(!string.IsNullOrWhiteSpace(Control<TextBlock>(window, "ActivityTitleText").Text), "准备状态应有标题");

            BeginScan(window, first);
            Check(Control<Grid>(window, "ActivityPanel").Visibility == Visibility.Visible, "扫描时应显示实时面板");
            Check(Control<TabControl>(window, "ResultTabs").Visibility == Visibility.Collapsed, "扫描时不应展示旧结果");
            Check(Control<ProgressBar>(window, "ScanProgressBar").Visibility == Visibility.Visible, "扫描时应显示进度指示");
            Check(Control<ProgressBar>(window, "ScanProgressBar").IsIndeterminate, "未知总量时应使用不定进度");

            InjectProgress(window, new ScanProgress(12_000, 740, 3L * 1024 * 1024 * 1024, first, TimeSpan.FromSeconds(12))
            {
                IssuesFound = 3
            });
            Check(Control<TextBlock>(window, "FileCountText").Text == 12_000.ToString("N0"), "扫描中应即时显示文件数");
            Check(Control<TextBlock>(window, "ActivityPathText").Text.Contains(first, StringComparison.OrdinalIgnoreCase), "扫描中应显示当前路径");
            Check(Control<TextBlock>(window, "ScanIssueCountText").Text.Contains('3'), "扫描中应显示累计访问问题");
            Check(!string.IsNullOrWhiteSpace(Control<TextBlock>(window, "ScanRateText").Text), "扫描中应提供处理速率反馈");
            Check(!Control<TextBlock>(window, "DurationText").Text.Contains('/'), "耗时和访问问题应分开显示");

            InjectProgress(window, new ScanProgress(12_000, 740, 3L * 1024 * 1024 * 1024, first, TimeSpan.FromSeconds(13))
            {
                Phase = ScanPhase.Aggregating,
                IssuesFound = 3
            });
            var phaseTitle = Control<TextBlock>(window, "ActivityTitleText").Text;
            Check(phaseTitle.Contains("整理", StringComparison.Ordinal) || phaseTitle.Contains("汇总", StringComparison.Ordinal),
                "汇总期间应明确说明正在整理结果");

            Click(window, "ScanButton");
            await FinishScan(window);
            Check(Control<Grid>(window, "ActivityPanel").Visibility == Visibility.Visible, "停止后应保留明确状态面板");
            Check(Control<TextBlock>(window, "ActivityTitleText").Text.Contains("停止", StringComparison.Ordinal), "停止后应明确反馈操作完成");
            Check(!Control<TextBlock>(window, "ScanRateText").Text.Contains("汇总中", StringComparison.Ordinal), "停止后速率不能残留汇总状态");
        });

        await Run("磁盘卡片模板只读属性绑定与布局", async (window, first, second) =>
        {
            var drive = new DriveViewModel(new DriveInfo(Path.GetPathRoot(first)!));
            var template = Control<ListBox>(window, "DriveList").ItemTemplate;
            var card = (FrameworkElement)template.LoadContent();
            card.DataContext = drive;
            card.Measure(new Size(220, 200));
            card.Arrange(new Rect(0, 0, 220, 200));
            card.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            var usage = FindVisualChild<ProgressBar>(card)
                ?? throw new InvalidOperationException("磁盘卡片应包含容量进度条。");
            Check(usage.GetBindingExpression(ProgressBar.ValueProperty)?.Status == BindingStatus.Active,
                "磁盘容量绑定应正常激活，不能尝试写入只读属性");
            Check(Math.Abs(usage.Value - drive.UsagePercent) < 0.000001,
                "进度条应显示磁盘模型提供的容量百分比");
        });

        await Run("扫描控件状态与跨扫描搜索筛选", async (window, first, second) =>
        {
            Control<TextBox>(window, "FileSearchBox").Text = ".txt";
            BeginScan(window, first);
            Check(!Control<Button>(window, "BrowseButton").IsEnabled, "扫描时应禁用路径选择");
            Check(!Control<Button>(window, "AiReviewButton").IsEnabled, "扫描时应禁用 AI 审查");
            await FinishScan(window);
            Check(Control<Grid>(window, "ActivityPanel").Visibility == Visibility.Collapsed, "完成后应收起扫描面板");
            Check(Control<TabControl>(window, "ResultTabs").Visibility == Visibility.Visible, "完成后应展示结果页");
            Check(Control<DataGrid>(window, "LargeFilesGrid").Items.Count == 1, "首次扫描应应用已有搜索词");

            BeginScan(window, second);
            await FinishScan(window);
            Check(Control<DataGrid>(window, "LargeFilesGrid").Items.Count == 0, "新结果应继续应用搜索词");
            Control<TextBox>(window, "FileSearchBox").Clear();
            Check(Control<DataGrid>(window, "LargeFilesGrid").Items.Count == 1, "清空搜索词后应恢复结果");
            Check(Control<Button>(window, "BrowseButton").IsEnabled, "扫描完成应恢复路径选择");
            Check(Control<TextBlock>(window, "StatusText").Text.StartsWith("完成："), "排队的进度不能覆盖完成状态");
        });

        await Run("取消后再次扫描", async (window, first, second) =>
        {
            BeginScan(window, first);
            Click(window, "ScanButton");
            await FinishScan(window);
            Check(Control<TextBlock>(window, "StatusText").Text.StartsWith("扫描已停止"), "应显示已停止");
            Check(Control<DataGrid>(window, "LargeFilesGrid").ItemsSource is null, "取消后不应发布结果");

            BeginScan(window, second);
            await FinishScan(window);
            Check(Control<DataGrid>(window, "LargeFilesGrid").Items.Count == 1, "再次扫描应正确完成");
            Check(Control<TextBlock>(window, "StatusText").Text.StartsWith("完成："), "旧进度不能覆盖新扫描状态");
        });

        await Run("旧 AI 请求结束后不覆盖新扫描", async (window, first, second) =>
        {
            using var handler = new DelayedHttpHandler();
            using var client = new HttpClient(handler);
            typeof(MainWindow).GetField("_aiReviewService", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, new AiReviewService(client));
            Control<TextBox>(window, "AiEndpointTextBox").Text = "https://example.test/v1/responses";
            Control<TextBox>(window, "AiModelTextBox").Text = "test-model";

            BeginScan(window, first);
            await FinishScan(window);
            Click(window, "AiReviewButton");
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            BeginScan(window, second);
            await FinishScan(window);
            var currentStatus = Control<TextBlock>(window, "AiStatusText").Text;
            handler.Complete();
            await WaitUntil(() => Equals(Control<Button>(window, "AiReviewButton").Content, "开始审查"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Check(Control<TextBlock>(window, "AiStatusText").Text == currentStatus, "旧请求的结束状态不能覆盖新结果");
            Check(Control<DataGrid>(window, "AiReviewsGrid").ItemsSource is null, "旧审查结果不能重新出现");
            Check(Control<Button>(window, "AiReviewButton").IsEnabled, "新扫描完成后应能开始审查");
        });

        Console.WriteLine(failures == 0 ? $"全部通过：{passed} 项 WPF 回归测试" : $"失败：{failures} 项 WPF 回归测试，通过：{passed} 项");
        return failures == 0 ? 0 : 1;

        async Task Run(string name, Func<MainWindow, string, string, Task> test)
        {
            var root = Path.Combine(Path.GetTempPath(), $"DiskScout.UiTests.{Guid.NewGuid():N}");
            MainWindow? window = null;
            try
            {
                var first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
                var second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
                await File.WriteAllTextAsync(Path.Combine(first, "notes.txt"), "notes");
                await File.WriteAllTextAsync(Path.Combine(first, "data.bin"), "data");
                await File.WriteAllTextAsync(Path.Combine(second, "other.bin"), "other");
                // Initialize WPF controls and pump the dispatcher without showing a desktop window.
                window = new MainWindow();
                Control<ComboBox>(window, "ThresholdComboBox").SelectedItem = null;
                var threshold = new ComboBoxItem { Content = "全部文件", Tag = "0" };
                Control<ComboBox>(window, "ThresholdComboBox").Items.Add(threshold);
                Control<ComboBox>(window, "ThresholdComboBox").SelectedItem = threshold;
                await test(window, first, second);
                passed++;
                Console.WriteLine($"[PASS] {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.WriteLine($"[FAIL] {name}: {exception}");
            }
            finally
            {
                window?.Close();
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }
    }

    private static T Control<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);

    private static void InjectProgress(MainWindow window, ScanProgress progress) => Invoke(window, "UpdateProgress", progress);

    private static void Invoke(MainWindow window, string methodName, params object[] arguments) =>
        typeof(MainWindow).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);

    private static async Task<int> CaptureScreenshotsAsync(string destination)
    {
        var output = Path.GetFullPath(destination);
        Directory.CreateDirectory(output);
        var fixture = Path.Combine(Path.GetTempPath(), $"DiskScout.UiPreview.{Guid.NewGuid():N}");
        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(fixture);
            await File.WriteAllTextAsync(Path.Combine(fixture, "preview.txt"), "UI preview fixture");
            window = new MainWindow();
            Control<ListBox>(window, "DriveList").ItemsSource = new[]
            {
                new { DisplayName = "系统 (C:)", UsageText = "72%", UsagePercent = 72d, SpaceText = "可用 142 GB / 512 GB" },
                new { DisplayName = "资料 (D:)", UsageText = "38%", UsagePercent = 38d, SpaceText = "可用 620 GB / 1 TB" }
            };
            Control<ListBox>(window, "DriveList").SelectedIndex = 0;
            Control<TextBox>(window, "ScopeTextBox").Text = @"C:\Users\Demo\Downloads";
            SaveScreenshot(window, output, "ready.png", 1380, 820);

            // Start a real, tiny scan but keep its UI continuation queued until the
            // synthetic progress frames are captured. Only this owned fixture is read.
            BeginScan(window, fixture);
            Control<TextBox>(window, "ScopeTextBox").Text = @"C:\Users\Demo\Downloads";
            Invoke(window, "BeginScanPresentation", @"C:\Users\Demo\Downloads");
            InjectProgress(window, new ScanProgress(38_240, 2_176, 27L * 1024 * 1024 * 1024,
                @"C:\Users\Demo\Downloads\Projects\Design assets", TimeSpan.FromSeconds(24)) { IssuesFound = 2 });
            SaveScreenshot(window, output, "scanning.png", 1380, 820);
            SaveScreenshot(window, output, "scanning-minimum.png", 1080, 700);

            InjectProgress(window, new ScanProgress(64_820, 3_482, 48L * 1024 * 1024 * 1024,
                @"C:\Users\Demo\Downloads", TimeSpan.FromSeconds(42)) { Phase = ScanPhase.Aggregating, IssuesFound = 2 });
            SaveScreenshot(window, output, "aggregating.png", 1380, 820);
            Click(window, "ScanButton");
            await FinishScan(window);
            SaveScreenshot(window, output, "cancelled.png", 1380, 820);

            var result = CreatePreviewResult();
            typeof(MainWindow).GetField("_lastResult", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, result);
            Invoke(window, "PresentResult", result);
            Invoke(window, "SetScanningState", false);
            SaveScreenshot(window, output, "completed.png", 1380, 820);
            SaveScreenshot(window, output, "completed-minimum.png", 1080, 700);
            Control<TabControl>(window, "ResultTabs").SelectedIndex = 1;
            SaveScreenshot(window, output, "large-files.png", 1380, 820);
            Control<TabControl>(window, "ResultTabs").SelectedIndex = 4;
            SaveScreenshot(window, output, "ai-review.png", 1380, 820);
            SaveScreenshot(window, output, "ai-review-minimum.png", 1080, 700);
            Console.WriteLine($"Saved UI previews to {output}");
            return 0;
        }
        finally
        {
            window?.Close();
            // This path is generated above and never accepts user-supplied input.
            if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true);
        }
    }

    private static void SaveScreenshot(MainWindow window, string destination, string fileName, int width, int height)
    {
        var content = (FrameworkElement)window.Content;
        content.Width = width - content.Margin.Left - content.Margin.Right;
        content.Height = height - content.Margin.Top - content.Margin.Bottom;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            var brush = new VisualBrush(content)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight)
            };
            context.DrawRectangle(brush, null,
                new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(destination, fileName));
        encoder.Save(stream);
    }

    private static ScanResult CreatePreviewResult()
    {
        const long gib = 1024L * 1024 * 1024;
        const string root = @"C:\Users\Demo\Downloads";
        var folders = new[]
        {
            new FolderEntry("Projects", root + @"\Projects", 19 * gib, 28_460),
            new FolderEntry("视频素材", root + @"\视频素材", 12 * gib, 142),
            new FolderEntry("Archive", root + @"\Archive", 8 * gib, 5_207),
            new FolderEntry("安装包", root + @"\安装包", 5 * gib, 86),
            new FolderEntry("文档", root + @"\文档", 3 * gib, 12_800),
            new FolderEntry("其他", root + @"\其他", gib, 18_125)
        };
        var files = new[]
        {
            new FileEntry("project-backup.zip", root + @"\Archive\project-backup.zip", 6 * gib, new DateTime(2024, 4, 18), "压缩包/镜像"),
            new FileEntry("interview-raw.mp4", root + @"\视频素材\interview-raw.mp4", 4 * gib, new DateTime(2025, 9, 12), "视频"),
            new FileEntry("design-assets.zip", root + @"\Projects\design-assets.zip", 3 * gib, new DateTime(2026, 6, 8), "压缩包/镜像"),
            new FileEntry("windows-image.iso", root + @"\安装包\windows-image.iso", 2 * gib, new DateTime(2025, 2, 22), "压缩包/镜像")
        };
        return new ScanResult
        {
            RootPath = root,
            StartedAt = new DateTime(2026, 10, 27, 10, 32, 0),
            Duration = TimeSpan.FromSeconds(42),
            TotalBytes = 48 * gib,
            FileCount = 64_820,
            DirectoryCount = 3_482,
            LargeFiles = files,
            Folders = folders,
            Categories = [new("开发文件", 19 * gib, 28_460, 39.6), new("视频", 12 * gib, 142, 25), new("压缩包/镜像", 13 * gib, 5_293, 27.1), new("文档", 4 * gib, 30_925, 8.3)],
            Issues = [new(root + @"\Cache\Locked", ScanIssueKind.AccessDenied, "访问被拒绝。"), new(root + @"\Temp\Pending", ScanIssueKind.IoError, "文件正在使用。")],
            Insights = [new("先查看 Projects 目录", "该目录占已统计容量的 39.6%，可从旧项目和构建产物开始排查。", InsightLevel.Attention), new("发现长期未修改的大文件", "旧备份和镜像占用较高，建议先确认用途，再决定是否转移。", InsightLevel.Attention), new("2 个位置未能读取", "可在“访问问题”中查看路径和原因。", InsightLevel.Info)]
        };
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } descendant) return descendant;
        }

        return null;
    }

    private static void Click(MainWindow window, string name) =>
        Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void BeginScan(MainWindow window, string path)
    {
        Control<TextBox>(window, "ScopeTextBox").Text = path;
        Click(window, "ScanButton");
    }

    private static async Task FinishScan(MainWindow window)
    {
        await WaitUntil(() => Control<ProgressBar>(window, "ScanProgressBar").Visibility == Visibility.Collapsed);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("等待界面操作完成超时。");
            await Task.Delay(10);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class DelayedHttpHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            // Deliberately finish after cancellation to exercise stale continuations.
            return _response.Task;
        }

        public void Complete() => _response.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"output_text\":\"{\\\"reviews\\\":[]}\"}")
        });

        protected override void Dispose(bool disposing)
        {
            if (disposing) _response.TrySetCanceled();
            base.Dispose(disposing);
        }
    }
}
