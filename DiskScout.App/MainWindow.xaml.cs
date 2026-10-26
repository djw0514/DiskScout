using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DiskScout.Core;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace DiskScout.App;

public partial class MainWindow : Window
{
    private readonly DiskScanner _scanner = new();
    private readonly AiReviewService _aiReviewService = new();
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _aiReviewCancellation;
    private ScanResult? _lastResult;
    private bool _isClosed;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadDrives();
        UpdateAdministratorBadge();

        var startupPath = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(Directory.Exists);
        if (startupPath is not null)
            ScopeTextBox.Text = startupPath;
        if (Environment.GetCommandLineArgs().Contains("--show-ai-review", StringComparer.OrdinalIgnoreCase))
            ResultTabs.SelectedIndex = 4;
    }

    private void LoadDrives()
    {
        var drives = new List<DriveViewModel>();
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.IsReady))
        {
            try
            {
                drives.Add(new DriveViewModel(drive));
            }
            catch (IOException)
            {
                // A removable drive can disappear while the list is being built.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        DriveList.ItemsSource = drives;
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        DriveList.SelectedItem = drives.FirstOrDefault(x =>
            x.RootPath.Equals(systemRoot, StringComparison.OrdinalIgnoreCase)) ?? drives.FirstOrDefault();
    }

    private void UpdateAdministratorBadge()
    {
        var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        var isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
        AdminBadgeText.Text = isAdmin ? "管理员权限" : "普通权限";
        AdminBadgeText.Foreground = isAdmin
            ? new SolidColorBrush(Color.FromRgb(65, 214, 163))
            : new SolidColorBrush(Color.FromRgb(244, 197, 106));
        AdminBadge.Background = new SolidColorBrush(isAdmin
            ? Color.FromRgb(23, 63, 58)
            : Color.FromRgb(51, 42, 27));
    }

    private void DriveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveList.SelectedItem is DriveViewModel drive && _scanCancellation is null)
            ScopeTextBox.Text = drive.RootPath;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择要排查的磁盘或文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(ScopeTextBox.Text) ? ScopeTextBox.Text : string.Empty
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            ScopeTextBox.Text = dialog.SelectedPath;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null)
        {
            ScanButton.IsEnabled = false;
            ScanButton.Content = "正在停止…";
            _scanCancellation.Cancel();
            return;
        }

        var path = Environment.ExpandEnvironmentVariables(ScopeTextBox.Text.Trim().Trim('"'));
        if (!Directory.Exists(path))
        {
            MessageBox.Show(this, "请输入存在的磁盘或文件夹路径。", "路径不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _aiReviewCancellation?.Cancel();
        var threshold = GetSelectedThreshold();
        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        SetScanningState(true);
        ClearResults();

        var progress = new Progress<ScanProgress>(value =>
        {
            if (!_isClosed && ReferenceEquals(_scanCancellation, cancellation) && !cancellation.IsCancellationRequested)
                UpdateProgress(value);
        });
        try
        {
            var result = await _scanner.ScanAsync(
                new ScanOptions(path, threshold),
                progress,
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_isClosed) return;
            _lastResult = result;
            PresentResult(result);
        }
        catch (OperationCanceledException)
        {
            if (!_isClosed)
                StatusText.Text = "扫描已停止；未完成的数据没有展示。";
        }
        catch (Exception ex)
        {
            if (!_isClosed)
            {
                StatusText.Text = "扫描失败";
                MessageBox.Show(this, ex.Message, "扫描失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _scanCancellation = null;
            if (!_isClosed)
                SetScanningState(false);
        }
    }

    private long GetSelectedThreshold()
    {
        if (ThresholdComboBox.SelectedItem is ComboBoxItem { Tag: not null } item &&
            long.TryParse(item.Tag.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;
        return 100L * 1024 * 1024;
    }

    private void SetScanningState(bool scanning)
    {
        ScanButton.IsEnabled = true;
        ScanButton.Content = scanning ? "停止扫描" : "开始扫描";
        ScopeTextBox.IsEnabled = !scanning;
        BrowseButton.IsEnabled = !scanning;
        ThresholdComboBox.IsEnabled = !scanning;
        DriveList.IsEnabled = !scanning;
        ScanProgressBar.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        ScanProgressBar.IsIndeterminate = scanning;
        SetAiReviewState(_aiReviewCancellation is not null);
        if (scanning)
            StatusText.Text = "正在准备扫描…";
    }

    private void UpdateProgress(ScanProgress progress)
    {
        TotalSizeText.Text = ByteSizeFormatter.Format(progress.BytesScanned);
        FileCountText.Text = progress.FilesScanned.ToString("N0");
        DirectoryCountText.Text = progress.DirectoriesScanned.ToString("N0");
        DurationText.Text = $"{Math.Max(1, (int)progress.Elapsed.TotalSeconds)} 秒";
        StatusText.Text = $"正在扫描：{progress.CurrentPath}";
    }

    private void ClearResults()
    {
        _lastResult = null;
        TotalSizeText.Text = "0 B";
        FileCountText.Text = "0";
        DirectoryCountText.Text = "0";
        DurationText.Text = "计时中…";
        InsightsList.ItemsSource = null;
        TopFoldersList.ItemsSource = null;
        LargeFilesGrid.ItemsSource = null;
        FoldersGrid.ItemsSource = null;
        CategoriesGrid.ItemsSource = null;
        IssuesGrid.ItemsSource = null;
        AiReviewsGrid.ItemsSource = null;
        AiReviewProgressBar.Value = 0;
        AiStatusText.Text = "请先完成磁盘扫描";
    }

    private void PresentResult(ScanResult result)
    {
        TotalSizeText.Text = result.TotalBytesText;
        FileCountText.Text = result.FileCountText;
        DirectoryCountText.Text = result.DirectoryCountText;
        DurationText.Text = $"{result.DurationText} / {result.Issues.Count:N0}";
        InsightsList.ItemsSource = result.Insights;
        TopFoldersList.ItemsSource = result.Folders.Take(9);
        LargeFilesGrid.ItemsSource = result.LargeFiles;
        ApplyFileFilter();
        FoldersGrid.ItemsSource = result.Folders;
        CategoriesGrid.ItemsSource = result.Categories;
        IssuesGrid.ItemsSource = result.Issues;
        AiStatusText.Text = result.LargeFiles.Count == 0
            ? "当前阈值下没有候选大文件"
            : $"可审查 {result.LargeFiles.Count:N0} 个大文件";
        ResultTabs.SelectedIndex = 0;
        StatusText.Text = $"完成：{result.RootPath} · 统计为文件逻辑大小 · 双击表格行可打开位置";
    }

    private void FileSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFileFilter();

    private void ApplyFileFilter()
    {
        if (LargeFilesGrid.ItemsSource is null) return;
        var term = FileSearchBox.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(LargeFilesGrid.ItemsSource);
        view.Filter = term.Length == 0 ? null : item => item is FileEntry file &&
            file.FullPath.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private void CopySelectedPath_Click(object sender, RoutedEventArgs e)
    {
        var path = GetSelectedPath();
        if (path is null)
        {
            MessageBox.Show(this, "请先在当前表格中选择一项。", "尚未选择", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Clipboard.SetText(path);
        StatusText.Text = $"已复制：{path}";
    }

    private void OpenSelected_Click(object sender, RoutedEventArgs e) => OpenSelectedPath();

    private void LargeFilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedPath();

    private void FoldersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedPath();

    private void AiReviewsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedPath();

    private void OpenSelectedPath()
    {
        var path = GetSelectedPath();
        if (path is null) return;

        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show(this, "该项目可能已被移动或删除。", "路径不存在", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "无法打开", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string? GetSelectedPath()
    {
        return ResultTabs.SelectedIndex switch
        {
            1 when LargeFilesGrid.SelectedItem is FileEntry file => file.FullPath,
            2 when FoldersGrid.SelectedItem is FolderEntry folder => folder.FullPath,
            4 when AiReviewsGrid.SelectedItem is FileAdviceResult advice => advice.File.FullPath,
            _ => null
        };
    }

    private async void AiReview_Click(object sender, RoutedEventArgs e)
    {
        if (_aiReviewCancellation is not null)
        {
            AiReviewButton.IsEnabled = false;
            AiReviewButton.Content = "正在停止…";
            _aiReviewCancellation.Cancel();
            return;
        }

        if (_lastResult is null)
        {
            MessageBox.Show(this, "请先完成一次磁盘或目录扫描。", "没有扫描结果", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var scanResult = _lastResult;
        var maxFiles = GetSelectedAiMaxFiles();
        var candidates = scanResult.LargeFiles.Take(maxFiles).ToArray();
        if (candidates.Length == 0)
        {
            MessageBox.Show(this, "当前大文件阈值下没有可审查的候选文件。", "没有候选文件", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var protocol = AiProtocolComboBox.SelectedItem is ComboBoxItem { Tag: not null } protocolItem &&
                       protocolItem.Tag.ToString() == "chat"
            ? AiApiProtocol.CompatibleChatCompletions
            : AiApiProtocol.OpenAiResponses;
        var apiKey = string.IsNullOrWhiteSpace(AiApiKeyBox.Password)
            ? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty
            : AiApiKeyBox.Password;
        var settings = new AiReviewSettings(
            AiEndpointTextBox.Text.Trim(),
            apiKey,
            AiModelTextBox.Text.Trim(),
            protocol,
            AiIncludeNamesCheckBox.IsChecked == true);

        using var cancellation = new CancellationTokenSource();
        _aiReviewCancellation = cancellation;
        SetAiReviewState(running: true);
        AiReviewsGrid.ItemsSource = null;
        AiReviewProgressBar.Value = 0;

        var progress = new Progress<AiReviewProgress>(value =>
        {
            if (!IsCurrentReview() || cancellation.IsCancellationRequested) return;
            AiReviewProgressBar.Value = value.Percentage;
            AiStatusText.Text = value.CurrentBatch == 0
                ? $"本地安全检查完成，准备审查 {value.Total:N0} 个文件…"
                : $"AI 审查批次 {value.CurrentBatch}/{value.BatchCount} · {value.Completed:N0}/{value.Total:N0}";
        });

        try
        {
            var results = await _aiReviewService.ReviewAsync(settings, candidates, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentReview()) return;
            AiReviewsGrid.ItemsSource = results;
            var moveCount = results.Count(x => x.Action == FileAdviceAction.Move);
            var deleteCount = results.Count(x => x.Action == FileAdviceAction.ReviewDelete);
            var protectedCount = results.Count(x => x.EnforcedByLocalSafety);
            AiStatusText.Text = $"完成 {results.Count:N0} 项 · 建议转移 {moveCount:N0} · 可考虑删除 {deleteCount:N0} · 本地保护 {protectedCount:N0}";
            AiReviewProgressBar.Value = 100;
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentReview())
                AiStatusText.Text = "AI 审查已停止。";
        }
        catch (Exception ex) when (ex is ArgumentException or HttpRequestException or InvalidDataException or JsonException)
        {
            if (IsCurrentReview())
            {
                AiStatusText.Text = "AI 审查失败";
                MessageBox.Show(this, ex.Message, "AI 审查失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _aiReviewCancellation = null;
            if (!_isClosed)
                SetAiReviewState(running: false);
        }

        bool IsCurrentReview() => !_isClosed && ReferenceEquals(_lastResult, scanResult) &&
                                  ReferenceEquals(_aiReviewCancellation, cancellation);
    }

    private int GetSelectedAiMaxFiles()
    {
        return AiMaxFilesComboBox.SelectedItem is ComboBoxItem { Tag: not null } item &&
               int.TryParse(item.Tag.ToString(), out var value)
            ? value
            : 100;
    }

    private void SetAiReviewState(bool running)
    {
        AiReviewButton.IsEnabled = _scanCancellation is null && (running || _lastResult is not null);
        AiReviewButton.Content = running ? "停止审查" : "开始审查";
        AiEndpointTextBox.IsEnabled = !running;
        AiModelTextBox.IsEnabled = !running;
        AiProtocolComboBox.IsEnabled = !running;
        AiApiKeyBox.IsEnabled = !running;
        AiIncludeNamesCheckBox.IsEnabled = !running;
        AiMaxFilesComboBox.IsEnabled = !running;
    }

    private void AiProtocolComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AiEndpointTextBox is null || AiProtocolComboBox.SelectedItem is not ComboBoxItem { Tag: not null } item)
            return;
        if (item.Tag.ToString() == "chat" && AiEndpointTextBox.Text.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            AiEndpointTextBox.Text = AiEndpointTextBox.Text[..^"responses".Length] + "chat/completions";
        else if (item.Tag.ToString() == "responses" && AiEndpointTextBox.Text.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            AiEndpointTextBox.Text = AiEndpointTextBox.Text[..^"chat/completions".Length] + "responses";
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null)
        {
            MessageBox.Show(this, "完成一次扫描后才能导出。", "没有结果", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出扫描结果",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"DiskScout_{DateTime.Now:yyyyMMdd_HHmm}.csv",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var csv = BuildCsv(_lastResult);
            File.WriteAllText(dialog.FileName, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            StatusText.Text = $"已导出：{dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string BuildCsv(ScanResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("类型,名称,字节数,显示大小,文件数,修改时间,路径");
        foreach (var folder in result.Folders)
            builder.AppendLine($"目录,{Csv(folder.Name)},{folder.Size},{Csv(folder.SizeText)},{folder.FileCount},,{Csv(folder.FullPath)}");
        foreach (var file in result.LargeFiles)
            builder.AppendLine($"大文件,{Csv(file.Name)},{file.Size},{Csv(file.SizeText)},1,{file.LastWriteTime:yyyy-MM-dd HH:mm:ss},{Csv(file.FullPath)}");
        return builder.ToString();
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return;
            var arguments = Directory.Exists(ScopeTextBox.Text) ? $"\"{ScopeTextBox.Text}\"" : string.Empty;
            Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            Close();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            StatusText.Text = "已取消管理员授权。";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "无法以管理员身份启动", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenStorageSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "无法打开系统设置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _scanCancellation?.Cancel();
        _aiReviewCancellation?.Cancel();
        base.OnClosed(e);
    }
}

public sealed class DriveViewModel
{
    public DriveViewModel(DriveInfo drive)
    {
        RootPath = drive.RootDirectory.FullName;
        var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel;
        DisplayName = $"{label} ({RootPath.TrimEnd('\\')})";
        var used = Math.Max(0, drive.TotalSize - drive.AvailableFreeSpace);
        UsagePercent = drive.TotalSize == 0 ? 0 : used * 100d / drive.TotalSize;
        UsageText = $"{UsagePercent:0}%";
        SpaceText = $"可用 {ByteSizeFormatter.Format(drive.AvailableFreeSpace)} / {ByteSizeFormatter.Format(drive.TotalSize)}";
    }

    public string RootPath { get; }
    public string DisplayName { get; }
    public double UsagePercent { get; }
    public string UsageText { get; }
    public string SpaceText { get; }
}
