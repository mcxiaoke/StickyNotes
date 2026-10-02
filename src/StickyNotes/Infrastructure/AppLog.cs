using System.IO;

namespace StickyNotes.Infrastructure;

/// <summary>
/// 生产级轻量文件日志记录器（按日自动轮转，Release 模式生效，不依赖第三方库）
/// </summary>
public static class AppLog
{
    private static readonly object _lock = new();

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var now = DateTime.Now;
            var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
            if (ex != null)
            {
                line += Environment.NewLine + ex;
            }
            line += Environment.NewLine;

            // 输出到 Debug 控制台
            System.Diagnostics.Debug.Write(line);

            // 安全写入本地日志文件
            lock (_lock)
            {
                var logDir = AppPaths.LogsDirectory;
                var logFile = Path.Combine(logDir, $"app-{now:yyyyMMdd}.log");
                File.AppendAllText(logFile, line);
            }
        }
        catch
        {
            // 日志本身写入异常绝不引起宿主进程崩溃
        }
    }
}
