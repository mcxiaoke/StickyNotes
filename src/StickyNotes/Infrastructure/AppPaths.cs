using System.IO;

namespace StickyNotes.Infrastructure;

/// <summary>
/// 应用程序路径管理中心，集中管理数据库、备份与日志目录，支持测试环境隔离重定向
/// </summary>
public static class AppPaths
{
    private static string? _dataDirOverride;

    /// <summary>
    /// 测试环境可通过此属性重定向数据目录，避免污染生产数据
    /// </summary>
    public static string? DataDirOverride
    {
        get => _dataDirOverride;
        set => _dataDirOverride = value;
    }

    /// <summary>
    /// 数据根目录：%LOCALAPPDATA%\StickyNotes
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            var dir = _dataDirOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StickyNotes"
            );
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// SQLite 数据库文件绝对路径
    /// </summary>
    public static string DatabasePath => Path.Combine(DataDirectory, "notes.db");

    /// <summary>
    /// 本地轮转备份目录
    /// </summary>
    public static string BackupsDirectory
    {
        get
        {
            var dir = Path.Combine(DataDirectory, "backups");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// 日志目录
    /// </summary>
    public static string LogsDirectory
    {
        get
        {
            var dir = Path.Combine(DataDirectory, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
