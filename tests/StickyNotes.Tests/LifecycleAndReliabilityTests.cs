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
    /// P0-1 验证（重写版）：严格复刻真实 WPF 退出时序 ——
    /// 「Shutdown 调用 → 全部窗口 Closed 触发 → Exit/OnExit → 退出持久化」。
    /// 关键点：窗口 Closed 时 IsShuttingDown 仍为 false（原缺陷的触发条件），
    /// 因此必须靠「创建时写 true / 用户主动关闭写 false」的单一写入口来保证退出后 IsOpen 仍为 true。
    /// 另：仅「桌面置顶」便签记忆精确坐标，其余便签不写坐标。
    /// </summary>
    [TestMethod]
    public void P0_1_Shutdown_PreservesIsOpenTrue_AndOnlyPinnedRemembersPlacement()
    {
        TestEnvironment.RunInSta(() =>
        {
            var services = new ServiceCollection();
            services.AddSingleton<INoteRepository>(_repository);
            services.AddSingleton(new AutoSaveCoordinator(_repository));
            services.AddTransient<NoteViewModel>();
            var sp = services.BuildServiceProvider();

            var wm = new WindowManager(sp, _repository);

            // 便签 1：桌面置顶（应记忆坐标）
            var pinned = new Note
            {
                Id = Guid.NewGuid(),
                Content = "置顶贴纸",
                AlwaysOnTop = true,
                WindowX = 200,
                WindowY = 220,
                WindowWidth = 320,
                WindowHeight = 360,
                IsOpen = true
            };
            // 便签 2：普通（不记忆坐标）
            var plain = new Note
            {
                Id = Guid.NewGuid(),
                Content = "普通贴纸",
                AlwaysOnTop = false,
                WindowX = 550,
                WindowY = 220,
                WindowWidth = 320,
                WindowHeight = 360,
                IsOpen = true
            };

            _repository.SaveAsync(pinned).GetAwaiter().GetResult();
            _repository.SaveAsync(plain).GetAwaiter().GetResult();

            var win1 = wm.OpenOrActivateNote(pinned);
            var win2 = wm.OpenOrActivateNote(plain);

            // 用户调整了置顶便签坐标
            win1.Left = 260;
            win1.Top = 280;

            // === 严格复刻真实时序 ===
            // 1) 退出发起：先持久化置顶便签坐标（窗口字典仍完整），再声明退出
            //    （真实路径为 TrayIconService → WindowManager.BeginShutdownAndPersistPinnedPlacement）
            wm.BeginShutdownAndPersistPinnedPlacement();

            // 2) Shutdown() 关闭全部窗口：此刻已标记退出，关闭回调不得回写 IsOpen=false
            win1.Close();
            win2.Close();

            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

            // 3) 退出阶段：仅置顶便签记忆坐标，且不修改 IsOpen
            wm.PersistPinnedWindowsPlacementOnExit();

            var afterPinned = _repository.GetByIdAsync(pinned.Id).GetAwaiter().GetResult();
            var afterPlain = _repository.GetByIdAsync(plain.Id).GetAwaiter().GetResult();

            Assert.IsNotNull(afterPinned);
            Assert.IsNotNull(afterPlain);

            // 核心断言：退出后两张便签都必须保持 IsOpen = true，重启才能恢复
            Assert.IsTrue(afterPinned.IsOpen, "退出后置顶便签必须保持 IsOpen = true 以便重启恢复");
            Assert.IsTrue(afterPlain.IsOpen, "退出后普通便签必须保持 IsOpen = true 以便重启恢复");

            // 置顶便签记忆精确坐标
            Assert.AreEqual(260, afterPinned.WindowX, 1.0, "置顶便签调整后的 X 坐标应被正确持久化");
            Assert.AreEqual(280, afterPinned.WindowY, 1.0, "置顶便签调整后的 Y 坐标应被正确持久化");

            // 普通便签不记忆坐标（仍是原始值，未被退出流程改写）
            Assert.AreEqual(550, afterPlain.WindowX, 1.0, "非置顶便签不应被记忆坐标");
            Assert.AreEqual(220, afterPlain.WindowY, 1.0, "非置顶便签不应被记忆坐标");
        });
    }

    /// <summary>
    /// P0-1 伴随验证（重写版）：用户主动关闭便签时 IsOpen 必须置为 false —— 这是 IsOpen 的另一个单一写入口
    /// </summary>
    [TestMethod]
    public void P0_1_NormalClose_SetsIsOpenFalse_EvenWithoutShutdownFlag()
    {
        TestEnvironment.RunInSta(() =>
        {
            var services = new ServiceCollection();
            services.AddSingleton<INoteRepository>(_repository);
            services.AddSingleton(new AutoSaveCoordinator(_repository));
            services.AddTransient<NoteViewModel>();
            var sp = services.BuildServiceProvider();

            var wm = new WindowManager(sp, _repository);

            var note = new Note { Id = Guid.NewGuid(), Content = "用户主动关闭的贴纸", IsOpen = true };
            _repository.SaveAsync(note).GetAwaiter().GetResult();

            var win = wm.OpenOrActivateNote(note);
            win.Close();

            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

            var after = _repository.GetByIdAsync(note.Id).GetAwaiter().GetResult();
            Assert.IsNotNull(after);
            Assert.IsFalse(after.IsOpen, "用户主动关闭的贴纸在数据库中 IsOpen 必须为 false");
        });
    }

    /// <summary>
    /// F-P1-4 验证：窗口关闭时必须**同步阻塞**完成刷盘。
    /// 旧实现用 `async void` 处理器 + await，实测续体在「全部窗口 Closed → App.OnExit → 进程结束」
    /// 的时序下不会执行；本测试在关闭后不泵任何消息循环，立即断言数据库已落盘，
    /// 以此证明刷盘发生在 Closing 的同步段内。
    /// </summary>
    [TestMethod]
    public void F_P1_4_Closing_FlushesSynchronously_BeforeReturning()
    {
        TestEnvironment.RunInSta(() =>
        {
            var services = new ServiceCollection();
            services.AddSingleton<INoteRepository>(_repository);
            services.AddSingleton(new AutoSaveCoordinator(_repository));
            services.AddTransient<NoteViewModel>();
            var sp = services.BuildServiceProvider();
            var wm = new WindowManager(sp, _repository);

            var note = new Note { Id = Guid.NewGuid(), Content = "初始内容", IsOpen = true };
            _repository.SaveAsync(note).GetAwaiter().GetResult();

            var win = wm.OpenOrActivateNote(note);
            var vm = win.ViewModel;

            // 输入新内容（进入防抖待写状态，尚未落盘）
            vm.Content = "关闭前最后一段输入";
            Assert.AreEqual(NoteSaveState.Pending, vm.SaveState, "前置条件：应存在未落盘改动");

            // 关闭窗口 —— 关键：关闭后不泵 Dispatcher，直接读库
            win.Close();

            var after = _repository.GetByIdAsync(note.Id).GetAwaiter().GetResult();
            Assert.IsNotNull(after);
            Assert.AreEqual("关闭前最后一段输入", after.Content,
                "Closing 必须同步完成刷盘：关闭返回后数据库就应是最新内容，不能依赖 await 续体");
            Assert.AreEqual(NoteSaveState.Saved, vm.SaveState, "同步刷盘成功后状态应为 Saved");
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

    /// <summary>
    /// F-P1-12 验证：底部状态栏的保存状态必须与真实落盘结果一致，不得无条件显示「已同步」。
    /// 覆盖三种状态流转：初始 Saved → 编辑后 Pending → 刷盘成功后 Saved（且 LastSavedAt 被填充）。
    /// </summary>
    [TestMethod]
    public void F_P1_12_SaveState_ReflectsRealPersistenceOutcome()
    {
        TestEnvironment.RunInSta(() =>
        {
            var services = new ServiceCollection();
            services.AddSingleton<INoteRepository>(_repository);
            services.AddSingleton(new AutoSaveCoordinator(_repository));
            services.AddTransient<NoteViewModel>();
            var sp = services.BuildServiceProvider();
            var wm = new WindowManager(sp, _repository);

            var note = new Note { Id = Guid.NewGuid(), Content = "初始正文", IsOpen = true };
            _repository.SaveAsync(note).GetAwaiter().GetResult();

            var win = wm.OpenOrActivateNote(note);
            var vm = win.ViewModel;

            // 初始态：已同步
            Assert.AreEqual(NoteSaveState.Saved, vm.SaveState, "新建便签初始应处于 Saved 状态");

            // 编辑正文 → 立即转为 Pending（存在未落盘改动）
            vm.Content = "编辑后的新正文";
            Assert.AreEqual(NoteSaveState.Pending, vm.SaveState, "正文变更后应立即转为 Pending");
            Assert.AreEqual("保存中…", vm.SaveStatusText, "Pending 状态下文案应为「保存中…」");

            // 强制刷盘 → 回到 Saved 且记录成功时间
            vm.FlushSaveAsync().GetAwaiter().GetResult();
            Assert.AreEqual(NoteSaveState.Saved, vm.SaveState, "刷盘成功后应回到 Saved");
            Assert.IsNotNull(vm.LastSavedAt, "刷盘成功后应记录 LastSavedAt");
            Assert.IsTrue(vm.SaveStatusText.StartsWith("已同步"), "Saved 状态文案应以「已同步」开头");

            // 数据库中确实是新正文（状态可信的底线）
            var reloaded = _repository.GetByIdAsync(note.Id).GetAwaiter().GetResult();
            Assert.IsNotNull(reloaded);
            Assert.AreEqual("编辑后的新正文", reloaded.Content);

            win.Close();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        });
    }
}
