using System.Windows.Threading;

namespace DiskScout.App;

public partial class App : System.Windows.Application
{
    private int _fatalErrorHandled;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        StartupDiagnostics.Initialize();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);
        try
        {
            StartupDiagnostics.Write("正在创建主窗口。", null);
            MainWindow = new MainWindow();
            MainWindow.ContentRendered += (_, _) =>
                StartupDiagnostics.Write("主窗口已完成首帧渲染。", null);
            MainWindow.Show();
            StartupDiagnostics.Write("主窗口 Show 调用已完成。", null);
        }
        catch (Exception exception)
        {
            ShowFatalError("程序初始化失败", exception);
            Shutdown(-1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (Interlocked.Exchange(ref _fatalErrorHandled, 1) != 0)
            return;
        ShowFatalError("程序遇到未处理的界面错误", e.Exception);
        Shutdown(-1);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        StartupDiagnostics.Write("发生未处理的进程错误。", e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        StartupDiagnostics.Write("发生未观察到的后台任务错误。", e.Exception);
        e.SetObserved();
    }

    private static void ShowFatalError(string title, Exception exception)
    {
        StartupDiagnostics.Write(title, exception);
        var message = $"{exception.Message}\n\n诊断日志：\n{StartupDiagnostics.LogPath}";
        try
        {
            System.Windows.MessageBox.Show(
                message,
                $"DiskScout - {title}",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        catch
        {
            // If WPF itself cannot initialize, the log remains available for diagnosis.
        }
    }
}
