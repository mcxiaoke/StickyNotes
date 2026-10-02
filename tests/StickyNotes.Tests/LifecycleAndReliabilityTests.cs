using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Data;
using StickyNotes.Infrastructure;
using StickyNotes.Models;
using StickyNotes.Services;
using StickyNotes.ViewModels;

namespace StickyNotes.Tests;

/// <summary>
/// 阶段 0：数据可靠性与生命周期回归测试（覆盖 P0-1 至 P0-5）
/// </summary>
[TestClass]
public class LifecycleAndReliabilityTests
{
    private string _testDir = null!;
    private string _dbPath = null!;
    private SqliteDatabaseContext _context = null!;
    private NoteRepository _repository = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"StickyNotes_Reliability_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "notes.db");
        _context = new SqliteDatabaseContext(_dbPath);
        await _context.InitializeAndMigrateAsync();
        _repository = new NoteRepository(_context);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    /// <summary>
    /// P0-1 验证：应用退出时统一同步窗口状态，桌面上打开的便签必须保持 IsOpen = true 且记住坐标尺寸
    /// </summary>
    [TestMethod]
    public void P0_1_Shutdown_PreservesActiveWindowsIsOpenTrueAndCoordinates()
    {
        TestEnvironment.RunInSta(() =>
        {
            var services = new ServiceCollection();
            services.AddSingleton<INoteRepository>(_repository);
            services.AddSingleton(new AutoSaveCoordinator(_repository));
            services.AddTransient<NoteViewModel>();
            var sp = services.BuildServiceProvider();

            var wm = new WindowManager(sp, _repository);

            var note1 = new Note
            {
                Id = Guid.NewGuid(),
                Content = "贴纸 1",
                WindowX = 200,
                WindowY = 220,
                WindowWidth = 320,
                WindowHeight = 360,
                IsOpen = true
            };
            var note2 = new Note
            {
                Id = Guid.NewGuid(),
                Content = "贴纸 2",
                WindowX = 550,
                WindowY = 220,
                WindowWidth = 320,
                WindowHeight = 360,
                IsOpen = true
            };

            _repository.SaveAsync(note1).GetAwaiter().GetResult();
            _repository.SaveAsync(note2).GetAwaiter().GetResult();

            var win1 = wm.OpenOrActivateNote(note1);
            var win2 = wm.OpenOrActivateNote(note2);

            // 用户调整了 win1 坐标
            win1.Left = 260;
            win1.Top = 280;

            // 模拟应用退出（调用 WindowManager.PersistActiveWindowsBoundsOnExit）
            wm.PersistActiveWindowsBoundsOnExit();

            // 模拟 WPF 连带关闭窗口
            win1.Close();
            win2.Close();

            // 给予短暂 Dispatcher 消息循环以便 Closed 异步处理完成
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

            // 重新从数据库读取验证
            var after1 = _repository.GetByIdAsync(note1.Id).GetAwaiter().GetResult();
            var after2 = _repository.GetByIdAsync(note2.Id).GetAwaiter().GetResult();

            Assert.IsNotNull(after1);
            Assert.IsNotNull(after2);
            Assert.IsTrue(after1.IsOpen, "退出后便签 1 必须保持 IsOpen = true 以便重启恢复");
            Assert.IsTrue(after2.IsOpen, "退出后便签 2 必须保持 IsOpen = true 以便重启恢复");
            Assert.AreEqual(260, after1.WindowX, 1.0, "便签 1 调整后的 X 坐标应被正确持久化");
            Assert.AreEqual(280, after1.WindowY, 1.0, "便签 1 调整后的 Y 坐标应被正确持久化");
        });
    }

    /// <summary>
    /// P0-1 伴随验证：用户在运行期主动关闭某张便签时，该便签的 IsOpen 必须置为 false
    /// </summary>
    [TestMethod]
    public void P0_1_NormalClose_SetsIsOpenFalse()
    {
        TestEnvironment.RunInSta(() =>
        {
            var services = new ServiceCollection();
            services.AddSingleton<INoteRepository>(_repository);
            services.AddSingleton(new AutoSaveCoordinator(_repository));
            services.AddTransient<NoteViewModel>();
            var sp = services.BuildServiceProvider();

            var wm = new WindowManager(sp, _repository);

            var note = new Note
            {
                Id = Guid.NewGuid(),
                Content = "即将被用户主动关闭的贴纸",
                IsOpen = true
            };
            _repository.SaveAsync(note).GetAwaiter().GetResult();

            var win = wm.OpenOrActivateNote(note);

            // 用户主动关闭贴纸
            win.Close();

            // 给予短暂 Dispatcher 消息循环以便 Closed 异步处理完成
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

            var after = _repository.GetByIdAsync(note.Id).GetAwaiter().GetResult();
            Assert.IsNotNull(after);
            Assert.IsFalse(after.IsOpen, "用户主动关闭的贴纸在数据库中 IsOpen 必须为 false");
        });
    }

    /// <summary>
    /// P0-2 验证：导入较旧备份时，由于本地记录较新，不应覆盖本地内容（防旧数据倒灌）
    /// </summary>
    [TestMethod]
    public async Task P0_2_ImportBackup_DoesNotOverwriteNewerNotes()
    {
        var noteId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // 本地现有较新的便签
        var localNote = new Note
        {
            Id = noteId,
            Content = "本地最新编辑的内容（2026-10-02）",
            UpdatedAt = now
        };
        await _repository.SaveAsync(localNote);

        // 备份文件中包含相同 ID，但时间戳较旧的内容
        var backupService = new ExportImportService(_repository);
        var backupPackage = new NoteBackupPackage
        {
            App = "StickyNotes",
            Version = "1.0",
            ExportedAt = now.AddDays(-7),
            TotalCount = 1,
            Notes = new List<NoteBackupItem>
            {
                new NoteBackupItem
                {
                    Id = noteId,
                    Content = "一周前的旧备份内容",
                    UpdatedAt = now.AddDays(-7)
                }
            }
        };

        var backupJsonPath = Path.Combine(_testDir, "test_backup.json");
        var json = System.Text.Json.JsonSerializer.Serialize(backupPackage);
        await File.WriteAllTextAsync(backupJsonPath, json);

        // 执行导入
        var result = await backupService.ImportNotesWithResultAsync(backupJsonPath);

        // 断言：该条目因冲突被跳过，导入为 0
        Assert.AreEqual(1, result.Total);
        Assert.AreEqual(0, result.Imported);
        Assert.AreEqual(1, result.Skipped);

        // 验证数据库中内容保持为本地最新版
        var current = await _repository.GetByIdAsync(noteId);
        Assert.IsNotNull(current);
        Assert.AreEqual("本地最新编辑的内容（2026-10-02）", current.Content);
    }

    /// <summary>
    /// P0-2 伴随验证：当备份中包含全新便签，或备份中的便签比本地更新时，应正常导入/覆盖
    /// </summary>
    [TestMethod]
    public async Task P0_2_ImportBackup_ImportsNewAndNewerNotes()
    {
        var existingId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // 本地旧便签
        var localNote = new Note
        {
            Id = existingId,
            Content = "本地旧内容",
            UpdatedAt = now.AddDays(-3)
        };
        await _repository.SaveAsync(localNote);

        // 备份中包含更新的 existingId，以及一个全新的 newId
        var newId = Guid.NewGuid();
        var backupService = new ExportImportService(_repository);
        var backupPackage = new NoteBackupPackage
        {
            App = "StickyNotes",
            Version = "1.0",
            ExportedAt = now,
            TotalCount = 2,
            Notes = new List<NoteBackupItem>
            {
                new NoteBackupItem
                {
                    Id = existingId,
                    Content = "备份中更新的内容",
                    UpdatedAt = now // 比本地更新
                },
                new NoteBackupItem
                {
                    Id = newId,
                    Content = "全新导入的便签",
                    UpdatedAt = now
                }
            }
        };

        var backupJsonPath = Path.Combine(_testDir, "test_newer_backup.json");
        var json = System.Text.Json.JsonSerializer.Serialize(backupPackage);
        await File.WriteAllTextAsync(backupJsonPath, json);

        var result = await backupService.ImportNotesWithResultAsync(backupJsonPath);

        Assert.AreEqual(2, result.Total);
        Assert.AreEqual(2, result.Imported);
        Assert.AreEqual(0, result.Skipped);

        var updated = await _repository.GetByIdAsync(existingId);
        var brandNew = await _repository.GetByIdAsync(newId);

        Assert.IsNotNull(updated);
        Assert.AreEqual("备份中更新的内容", updated.Content);
        Assert.IsNotNull(brandNew);
        Assert.AreEqual("全新导入的便签", brandNew.Content);
    }

    /// <summary>
    /// P0-3 验证：WAL 模式下采用 VACUUM INTO 创建冷备份，备份库可独立完整读取最新提交
    /// </summary>
    [TestMethod]
    public async Task P0_3_VacuumIntoBackup_ProducesCompleteIndependentDatabase()
    {
        var note = new Note
        {
            Id = Guid.NewGuid(),
            Content = "WAL 模式下刚写入的核心便签",
            Color = NoteColor.Purple,
            UpdatedAt = DateTime.UtcNow
        };
        await _repository.SaveAsync(note);

        var backupFile = Path.Combine(_testDir, "backup_vacuum.db");

        // 使用 SQLite VACUUM INTO 进行冷备份
        BackupService.CreateBackup(_dbPath, backupFile);

        Assert.IsTrue(File.Exists(backupFile), "备份文件必须成功生成");

        // 打开全新的备份文件连接进行独立校验
        var backupConnStr = new SqliteConnectionStringBuilder
        {
            DataSource = backupFile,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        await using var connection = new SqliteConnection(backupConnStr);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Content FROM Notes WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", note.Id.ToString());
        var content = (string?)await cmd.ExecuteScalarAsync();

        Assert.AreEqual("WAL 模式下刚写入的核心便签", content, "备份库必须能完整恢复最后一次写入的便签内容");
    }

    /// <summary>
    /// P0-4 验证：FlushAllDirectToStorage 在无 UI 消息泵环境下同步落盘不发生死锁
    /// </summary>
    [TestMethod]
    public async Task P0_4_FlushAllDirectToStorage_PersistsDirtyNotesWithoutDeadlock()
    {
        var coordinator = new AutoSaveCoordinator(_repository, debounceMilliseconds: 1000);

        var note = new Note
        {
            Id = Guid.NewGuid(),
            Content = "未到 1000ms 防抖时间的脏数据",
            UpdatedAt = DateTime.UtcNow
        };

        // 调度防抖保存
        coordinator.ScheduleSave(note, async n =>
        {
            await _repository.SaveAsync(n);
        });

        // 模拟退出时的立即同步落盘
        coordinator.FlushAllDirectToStorage();

        // 验证数据库中已持久化
        var loaded = await _repository.GetByIdAsync(note.Id);
        Assert.IsNotNull(loaded);
        Assert.AreEqual("未到 1000ms 防抖时间的脏数据", loaded.Content);
    }

    /// <summary>
    /// P0-5 验证：AppLog 正常写入日志文件且 Release 下可用
    /// </summary>
    [TestMethod]
    public void P0_5_AppLog_WritesLogFile_Successfully()
    {
        var uniqueMsg = $"ReliabilityTest_{Guid.NewGuid():N}";
        AppLog.Info(uniqueMsg);
        AppLog.Warn($"Warn_{uniqueMsg}");
        AppLog.Error($"Error_{uniqueMsg}", new InvalidOperationException("Test exception"));

        var logDir = AppPaths.LogsDirectory;
        Assert.IsTrue(Directory.Exists(logDir));

        var logFile = Path.Combine(logDir, $"app-{DateTime.Now:yyyyMMdd}.log");
        Assert.IsTrue(File.Exists(logFile), "日志文件必须存在");

        var text = File.ReadAllText(logFile);
        Assert.IsTrue(text.Contains(uniqueMsg), "日志文件应包含写入的消息");
        Assert.IsTrue(text.Contains("Test exception"), "日志文件应包含异常堆栈");
    }
}
