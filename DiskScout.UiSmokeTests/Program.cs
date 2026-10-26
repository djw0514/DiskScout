using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskScout.App;
using DiskScout.Core;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = RunAsync();
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => app.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync()
    {
        var failures = 0;
        await Run("扫描控件状态与跨扫描搜索筛选", async (window, first, second) =>
        {
            Control<TextBox>(window, "FileSearchBox").Text = ".txt";
            BeginScan(window, first);
            Check(!Control<Button>(window, "BrowseButton").IsEnabled, "扫描时应禁用路径选择");
            Check(!Control<Button>(window, "AiReviewButton").IsEnabled, "扫描时应禁用 AI 审查");
            await FinishScan(window);
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

        Console.WriteLine(failures == 0 ? "全部通过：3 项 WPF 回归测试" : $"失败：{failures} 项 WPF 回归测试");
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
