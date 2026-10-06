using System.IO;
using Microsoft.Data.Sqlite;
using StickyNotes.Infrastructure;

namespace StickyNotes.Services;

/// <summary>
/// 数据库每日自动冷备份服务（基于 SQLite VACUUM INTO 事务一致性快照）
/// </summary>
public static class BackupService
{
    public static void RunDailyBackupIfNeeded(string? customDbPath = null, string? customBackupDir = null)
    {
        try
        {
            var dbPath = customDbPath ?? AppPaths.DatabasePath;
            if (!File.Exists(dbPath)) return;

            var todayStr = DateTime.Now.ToString("yyyyMMdd");
            var backupDir = customBackupDir ?? AppPaths.BackupsDirectory;
            Directory.CreateDirectory(backupDir);

            var targetBackupFile = Path.Combine(backupDir, $"notes_{todayStr}.db");

            // 若当天尚未创建备份，则执行 VACUUM INTO 事务快照
            if (!File.Exists(targetBackupFile))
            {
                CreateBackup(dbPath, targetBackupFile);
            }

            // 轮转清理：仅保留最近 14 份备份。
            // GetFiles 的模式扩展名 ".db" 只有 2 字符，不存在 DOS 风格的 3 字符扩展名前缀匹配，
            // 因此不会命中 ".db.tmp" 临时文件，无需再做后缀过滤。
            var di = new DirectoryInfo(backupDir);
            var backupFiles = di.GetFiles("notes_*.db")
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            if (backupFiles.Count > 14)
            {
                foreach (var oldBackup in backupFiles.Skip(14))
                {
                    try { oldBackup.Delete(); }
                    catch (Exception ex) { AppLog.Warn($"[BackupService] 清理过期备份 {oldBackup.Name} 失败: {ex.Message}", ex); }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"[BackupService] 每日备份失败: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 使用 SQLite 原生 VACUUM INTO 语法创建物理事务一致性冷备份
    /// </summary>
    public static void CreateBackup(string sourceDbPath, string targetBackupFilePath)
    {
        var tempFile = targetBackupFilePath + ".tmp";
        if (File.Exists(tempFile))
        {
            try { File.Delete(tempFile); } catch (Exception ex) { AppLog.Warn($"[BackupService] 清理遗留临时备份文件失败: {ex.Message}", ex); }
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = sourceDbPath,
            Mode = SqliteOpenMode.ReadOnly
            // 不使用 Shared Cache：VACUUM INTO 持有读事务期间，Shared Cache 的表级锁
            // 会让主流程写入收到不可重试的 SQLITE_LOCKED（P1-3）
        };

        using (var connection = new SqliteConnection(builder.ToString()))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "VACUUM INTO $target;";
            cmd.Parameters.AddWithValue("$target", tempFile);
            cmd.ExecuteNonQuery();
        }

        // 成功生成完整快照后原子替换目标文件
        File.Move(tempFile, targetBackupFilePath, overwrite: true);
        AppLog.Info($"[BackupService] 成功创建数据库冷备份: {targetBackupFilePath}");
    }
}
