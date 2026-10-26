using System.Runtime.InteropServices;
using System.Text;
using System.IO;

namespace DiskScout.App;

internal static class StartupDiagnostics
{
    private static readonly object SyncRoot = new();

    public static string LogPath { get; private set; } = Path.Combine(Path.GetTempPath(), "DiskScout-startup.log");

    public static void Initialize()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DiskScout");
            Directory.CreateDirectory(directory);
            LogPath = Path.Combine(directory, "startup.log");

            var header = new StringBuilder()
                .AppendLine($"DiskScout 启动时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}")
                .AppendLine($"应用版本：{typeof(StartupDiagnostics).Assembly.GetName().Version}")
                .AppendLine($"操作系统：{RuntimeInformation.OSDescription}")
                .AppendLine($"系统架构：{RuntimeInformation.OSArchitecture}")
                .AppendLine($"进程架构：{RuntimeInformation.ProcessArchitecture}")
                .AppendLine($".NET：{RuntimeInformation.FrameworkDescription}")
                .AppendLine($"可执行文件：{Environment.ProcessPath}")
                .AppendLine($"当前目录：{Environment.CurrentDirectory}")
                .AppendLine(new string('-', 72))
                .ToString();
            File.WriteAllText(LogPath, header, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch
        {
            // Diagnostics must never prevent the application from starting.
        }
    }

    public static void Write(string message, Exception? exception)
    {
        try
        {
            var text = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";
            if (exception is not null)
                text += exception + Environment.NewLine;
            lock (SyncRoot)
                File.AppendAllText(LogPath, text, Encoding.UTF8);
        }
        catch
        {
        }
    }
}
