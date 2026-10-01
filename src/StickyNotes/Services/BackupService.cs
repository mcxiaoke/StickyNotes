using System.IO;
using StickyNotes.Infrastructure;

namespace StickyNotes.Services;

/// <summary>
/// 数据库每日自动冷备份服务
/// </summary>
public static class BackupService
{
    public static void RunDailyBackupIfNeeded()
    {
        try
        {
            var dbPath = AppPaths.DatabasePath;
            if (!File.Exists(dbPath)) return;

            var todayStr = DateTime.Now.ToString("yyyyMMdd");
            var backupDir = AppPaths.BackupsDirectory;
            var targetBackupFile = Path.Combine(backupDir, $"notes_{todayStr}.db");

            // 若当天尚未创建备份，则执行复制
            if (!File.Exists(targetBackupFile))
            {
                File.Copy(dbPath, targetBackupFile, overwrite: true);
            }

            // 轮转清理：仅保留最近 14 份备份
            var di = new DirectoryInfo(backupDir);
            var backupFiles = di.GetFiles("notes_*.db")
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            if (backupFiles.Count > 14)
            {
                foreach (var oldFile in backupFiles.Skip(14))
                {
                    try { oldFile.Delete(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BackupService] 备份失败: {ex.Message}");
        }
    }
}
