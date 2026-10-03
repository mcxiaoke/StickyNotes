using System.Globalization;
using System.IO;

namespace StickyNotes.Infrastructure;

/// <summary>
/// 生产级轻量文件日志记录器（按日自动轮转并清理过期文件，不依赖第三方库）
/// </summary>
public static class AppLog
{
    private static readonly object _lock = new();

    /// <summary>日志保留天数：更早的 app-YYYYMMDD.log 会被自动清理，避免日志目录无限增长</summary>
    private const int RetentionDays = 30;

    private static bool _cleanupDone;

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
                CleanupExpiredLogs(logDir, now);
                var logFile = Path.Combine(logDir, $"app-{now:yyyyMMdd}.log");
                File.AppendAllText(logFile, line);
            }
        }
        catch
        {
            // 日志本身写入异常绝不引起宿主进程崩溃
        }
    }

    /// <summary>
    /// 判定给定日志文件是否已超过保留期。文件名形如 <c>app-YYYYMMDD.log</c>；
    /// 命名不符合约定者一律视为「不清理」，避免激进删除误伤无关文件。
    /// 独立为纯函数以便单测直接验证边界。
    /// </summary>
    internal static bool IsExpiredLogFile(string filePath, DateTime now)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (name.Length != 12 || !name.StartsWith("app-", StringComparison.Ordinal))
        {
            return false;
        }

        return DateTime.TryParseExact(name.AsSpan(4), "yyyyMMdd", CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out var date)
               && date < now.Date.AddDays(-RetentionDays);
    }

    /// <summary>
    /// 清理超过保留期的历史日志。每个进程仅执行一次（首次写日志时），
    /// 失败绝不向上抛出 —— 日志清理问题不能影响宿主运行。
    /// </summary>
    private static void CleanupExpiredLogs(string logDir, DateTime now)
    {
        if (_cleanupDone) return;
        _cleanupDone = true;

        try
        {
            foreach (var file in Directory.GetFiles(logDir, "app-*.log"))
            {
                if (IsExpiredLogFile(file, now))
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 清理失败不影响日志写入与宿主运行
        }
    }
}
