using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Data;
using StickyNotes.Infrastructure;
using StickyNotes.Messages;
using StickyNotes.Models;
using StickyNotes.Services;
using StickyNotes.ViewModels;

namespace StickyNotes.Tests;

/// <summary>
/// 阶段 1：并发与架构解耦回归测试（覆盖 P1-1 至 P1-5）
/// </summary>
[TestClass]
public class ConcurrencyAndDecouplingTests
{
    private string _testDir = null!;
    private string _dbPath = null!;
    private SqliteDatabaseContext _context = null!;
    private NoteRepository _repository = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"StickyNotes_Phase1_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "notes.db");
        _context = new SqliteDatabaseContext(_dbPath);
        await _context.InitializeAndMigrateAsync();
        _repository = new NoteRepository(_context);
    }

    [TestCleanup]
    public void Cleanup()
    {
        WeakReferenceMessenger.Default.Reset();
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
    /// P1-1 验证：拆分置顶语义（IsPinnedInList vs AlwaysOnTop）独立性、兼容性及 SQLite V2 升级
    /// </summary>
    [TestMethod]
    public async Task P1_1_SeparatePinSemantics_AndDbV2Migration()
    {
        // 1. 独立性与持久化验证
        var note = new Note
        {
            Id = Guid.NewGuid(),
            Content = "分离置顶测试",
            IsPinnedInList = true,
            AlwaysOnTop = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _repository.SaveAsync(note);

        var retrieved = await _repository.GetByIdAsync(note.Id);
        Assert.IsNotNull(retrieved);
        Assert.IsTrue(retrieved.IsPinnedInList, "IsPinnedInList 应为 true");
        Assert.IsFalse(retrieved.AlwaysOnTop, "AlwaysOnTop 应为 false");

        // 2. 兼容属性验证
        Assert.IsTrue(note.IsPinned, "兼容读取 IsPinned 应反映 IsPinnedInList");
        note.IsPinned = false;
        Assert.IsFalse(note.IsPinnedInList);
        Assert.IsFalse(note.AlwaysOnTop);

        // 3. SQLite V1 -> V2 升级迁移验证
        var v1DbPath = Path.Combine(_testDir, "v1_legacy.db");
        using (var conn = new SqliteConnection($"Data Source={v1DbPath}"))
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA user_version = 1;
                CREATE TABLE Notes (
                    Id             TEXT PRIMARY KEY,
                    Content        TEXT,
                    Color          INTEGER NOT NULL DEFAULT 0,
                    IsPinned       INTEGER NOT NULL DEFAULT 0,
                    IsDeleted      INTEGER NOT NULL DEFAULT 0,
                    DeletedAt      TEXT,
                    CreatedAt      TEXT NOT NULL,
                    UpdatedAt      TEXT NOT NULL,
                    IsOpen         INTEGER NOT NULL DEFAULT 0,
                    WindowX        REAL NOT NULL DEFAULT 0,
                    WindowY        REAL NOT NULL DEFAULT 0,
                    WindowWidth    REAL NOT NULL DEFAULT 320,
                    WindowHeight   REAL NOT NULL DEFAULT 360
                );
                INSERT INTO Notes (Id, Content, Color, IsPinned, IsDeleted, CreatedAt, UpdatedAt, IsOpen)
                VALUES ('11111111-1111-1111-1111-111111111111', '旧数据', 0, 1, 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 1);
            ";
            await cmd.ExecuteNonQueryAsync();
        }

        var v2Context = new SqliteDatabaseContext(v1DbPath);
        await v2Context.InitializeAndMigrateAsync();

        // 验证迁移后 Schema 和数据是否正确升级
        var v2Repo = new NoteRepository(v2Context);
        var migrated = await v2Repo.GetByIdAsync(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        Assert.IsNotNull(migrated);
        Assert.IsTrue(migrated.IsPinnedInList, "旧 IsPinned=1 迁移后 IsPinnedInList 应为 true");
        Assert.IsTrue(migrated.AlwaysOnTop, "旧 IsPinned=1 迁移后 AlwaysOnTop 应为 true");
    }

    /// <summary>
    /// P1-2 验证：便签正文保存时发送 NoteContentChangedMessage，列表不触发 RemoveAt/Insert 重排抖动
    /// </summary>
    [TestMethod]
    public void P1_2_ContentSaveDoesNotJitterOrReorderList()
    {
        TestEnvironment.RunInSta(() =>
        {
            var services = new ServiceCollection();
            services.AddSingleton<INoteRepository>(_repository);
            services.AddSingleton<ISearchService, SearchService>();
            services.AddSingleton<AutoSaveCoordinator>();
            services.AddSingleton<WindowManager>();
            services.AddSingleton(TestEnvironment.CreateSettingsService());
            services.AddTransient<NoteViewModel>();
            services.AddSingleton<NotesListViewModel>();
            var sp = services.BuildServiceProvider();

            using var vm = sp.GetRequiredService<NotesListViewModel>();

            var noteA = new Note
            {
                Id = Guid.NewGuid(),
                Content = "便签 A (新)",
                IsPinnedInList = false,
                UpdatedAt = DateTime.UtcNow.AddMinutes(1)
            };
            var noteB = new Note
            {
                Id = Guid.NewGuid(),
                Content = "便签 B (旧)",
                IsPinnedInList = false,
                UpdatedAt = DateTime.UtcNow
            };

            vm.Notes.Add(noteA);
            vm.Notes.Add(noteB);

            // 初始索引：A 在 0，B 在 1
            Assert.AreEqual(noteA.Id, vm.Notes[0].Id);
            Assert.AreEqual(noteB.Id, vm.Notes[1].Id);

            // 模拟用户在便签 B 窗口中打字并自动保存：发出 NoteContentChangedMessage
            var updatedB = new Note
            {
                Id = noteB.Id,
                Content = "便签 B 修改了正文内容...",
                IsPinnedInList = false,
                UpdatedAt = DateTime.UtcNow.AddMinutes(2) // 即使 UpdatedAt 更新了
            };

            WeakReferenceMessenger.Default.Send(new NoteContentChangedMessage(updatedB.Id, updatedB.Content, updatedB.UpdatedAt));

            // 核心断言：正文修改不导致列表重排，B 依旧在 index 1，彻底消灭输入卡片跳动
            Assert.AreEqual(noteA.Id, vm.Notes[0].Id, "Note A 必须仍然保持在第 0 项");
            Assert.AreEqual(noteB.Id, vm.Notes[1].Id, "Note B 必须仍然保持在第 1 项");
            Assert.AreEqual("便签 B 修改了正文内容...", vm.Notes[1].Content, "Note B 的正文已平滑更新");

            // 模拟元数据变更（如置顶了 B）：发出 NoteMetaChangedMessage
            updatedB.IsPinnedInList = true;
            WeakReferenceMessenger.Default.Send(new NoteMetaChangedMessage(updatedB));

            // 此时置顶状态改变，B 应当安全置顶移动到 index 0
            Assert.AreEqual(noteB.Id, vm.Notes[0].Id, "置顶操作后 Note B 应移至第 0 项");
            Assert.AreEqual(noteA.Id, vm.Notes[1].Id, "Note A 应顺移至第 1 项");
        });
    }

    /// <summary>
    /// P1-3 验证：搜索只读快照机制在高并发读写下绝不抛出 Collection was modified 异常
    /// </summary>
    [TestMethod]
    public void P1_3_SearchSnapshot_ConcurrencySafe()
    {
        TestEnvironment.RunInSta(() =>
        {
            var services = new ServiceCollection();
            services.AddSingleton<INoteRepository>(_repository);
            services.AddSingleton<ISearchService, SearchService>();
            services.AddSingleton<AutoSaveCoordinator>();
            services.AddSingleton<WindowManager>();
            services.AddSingleton(TestEnvironment.CreateSettingsService());
            services.AddTransient<NoteViewModel>();
            services.AddSingleton<NotesListViewModel>();
            var sp = services.BuildServiceProvider();

            using var vm = sp.GetRequiredService<NotesListViewModel>();

            // 添加 100 张便签
            for (int i = 0; i < 100; i++)
            {
                vm.Notes.Add(new Note
                {
                    Id = Guid.NewGuid(),
                    Content = $"便签内容项目 {i} 测试文本 keyword_{(i % 5)}",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            // 更新搜索快照
            var updateSnapshotMethod = typeof(NotesListViewModel).GetMethod("UpdateSearchSnapshot",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            updateSnapshotMethod?.Invoke(vm, null);

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            // 后台线程持续高频触发搜索
            var searchTask = Task.Run(() =>
            {
                var rng = new Random();
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var kw = $"keyword_{rng.Next(0, 5)}";
                        vm.SearchText = kw;
                        Thread.Sleep(5);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            }, cts.Token);

            // STA 主线程同时模拟用户高频增删改便签
            var rng2 = new Random();
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (vm.Notes.Count > 10)
                    {
                        vm.Notes.RemoveAt(rng2.Next(0, vm.Notes.Count));
                    }
                    vm.Notes.Add(new Note
                    {
                        Id = Guid.NewGuid(),
                        Content = $"新并发便签 {rng2.Next()}",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                    updateSnapshotMethod?.Invoke(vm, null);
                    Thread.Sleep(10);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                    break;
                }
            }

            searchTask.Wait(TimeSpan.FromSeconds(1));

            // 断言未发生任何并发异常
            Assert.AreEqual(0, exceptions.Count, $"并发搜索与修改过程中发生异常: {string.Join("; ", exceptions.Select(e => e.Message))}");
        });
    }

    /// <summary>
    /// P1-4 验证：NoteWindow 与主窗口通过 Messenger 解耦消息传递
    /// </summary>
    [TestMethod]
    public void P1_4_MessengerDecoupledActions()
    {
        TestEnvironment.RunInSta(() =>
        {
            var services = new ServiceCollection();
            services.AddSingleton<INoteRepository>(_repository);
            services.AddSingleton<ISearchService, SearchService>();
            services.AddSingleton<AutoSaveCoordinator>();
            services.AddSingleton<WindowManager>();
            services.AddSingleton(TestEnvironment.CreateSettingsService());
            services.AddTransient<NoteViewModel>();
            services.AddSingleton<NotesListViewModel>();
            var sp = services.BuildServiceProvider();

            using var vm = sp.GetRequiredService<NotesListViewModel>();

            int initialCount = vm.Notes.Count;

            // 模拟 NoteWindow 发送新建请求
            WeakReferenceMessenger.Default.Send(new NewNoteRequestedMessage());

            // 给异步操作与 UI 调度一定执行时间
            for (int i = 0; i < 30 && vm.Notes.Count == initialCount; i++)
            {
                Thread.Sleep(50);
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            }

            // 验证新建便签已被 NotesListViewModel 响应并加入列表
            Assert.AreEqual(initialCount + 1, vm.Notes.Count, "NewNoteRequestedMessage 应触发新建便签并加入列表");
        });
    }

    /// <summary>
    /// P1-5 验证：单实例 Local 确定性 SHA256 互斥量格式、跨进程一致性与注册窗口消息非零有效性
    /// </summary>
    [TestMethod]
    public void P1_5_LocalHashedMutex_AndActivation()
    {
        var dataDirHash = AppPaths.GetDataDirectoryHash();
        var mutexName = AppPaths.InstanceMutexName;

        Assert.IsTrue(mutexName.StartsWith(@"Local\StickyNotes_"), "Mutex 必须以 Local\\StickyNotes_ 开头");
        Assert.AreEqual(8, dataDirHash.Length, "数据目录哈希长度必须为 8 位十六进制");
        Assert.AreEqual(mutexName, $@"Local\StickyNotes_{dataDirHash}");

        // 验证确定性哈希：多次计算结果必须完全恒定一致，避免 .NET 8 字符串随机加盐失效
        Assert.AreEqual(dataDirHash, AppPaths.GetDataDirectoryHash(), "确定性哈希在同目录多次计算必须完全一致");

        Assert.AreNotEqual(0, NativeMethods.WM_ACTIVATE_INSTANCE, "WM_ACTIVATE_INSTANCE 注册消息 ID 必须大于 0");
    }

    /// <summary>
    /// 验证：工作集修剪安全执行无异常，且单实例互斥量排他机制有效拦截多开
    /// </summary>
    [TestMethod]
    public void WorkingSetTrimming_AndMutexExclusion()
    {
        // 1. 验证工作集修剪 Win32 API 与 GC 流程正常运转
        NativeMethods.TrimWorkingSet();

        // 2. 验证命名互斥量多实例排他逻辑
        var testMutexName = $@"Local\StickyNotes_Test_{Guid.NewGuid():N}";
        using var m1 = new Mutex(true, testMutexName, out bool isNew1);
        Assert.IsTrue(isNew1, "首次获取独立互斥量应当成功");

        using var m2 = new Mutex(true, testMutexName, out bool isNew2);
        Assert.IsFalse(isNew2, "二次尝试获取相同命名互斥量必须被拦截判定为已存在");
    }

    /// <summary>
    /// F-P1-7 验证：连续高频重调度（模拟用户快速输入）时，被取消的 CancellationTokenSource
    /// 不得在使用中被释放 —— 复现原缺陷的关键是「上一个防抖任务仍停在 Task.Delay 上时，
    /// 新一次调度就把它的 CTS Cancel + Dispose」，那会在 Task.Delay 内部抛 ObjectDisposedException，
    /// 被通用 catch 误记为「自动保存失败」。
    /// 本用例同时确保最终只有最后一次调度落盘（防抖语义不被破坏）。
    /// </summary>
    [TestMethod]
    public async Task F_P1_7_RapidRescheduling_DoesNotDisposeCtsInUse()
    {
        // 防抖窗口内反复重调度，确保旧任务必然还停在 Task.Delay 上就被取消
        var repo = new CountingRepository();
        var coordinator = new AutoSaveCoordinator(repo, debounceMilliseconds: 120);

        var note = new Note { Id = Guid.NewGuid(), Content = "版本 0" };
        var captured = new List<string>();
        AppLog.DiagnosticSinkForTest = captured.Add;

        try
        {
            for (int i = 1; i <= 40; i++)
            {
                note.Content = $"版本 {i}";
                coordinator.ScheduleSave(note, n => repo.SaveAsync(n));
                Thread.Sleep(2);
            }

            // 等待最后一次防抖到期并落盘
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (repo.SaveCallCount == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }
        finally
        {
            AppLog.DiagnosticSinkForTest = null;
        }

        var reports = captured
            .Where(line => line.Contains("自动保存失败", StringComparison.Ordinal))
            .ToList();

        Assert.AreEqual(
            0, reports.Count,
            "被取消的 CTS 若在使用中被 Dispose，Task.Delay 会抛 ObjectDisposedException 并被通用 catch " +
            "误记为「自动保存失败」（原 F-P1-7）。实际捕获到: " + string.Join(" | ", reports));

        Assert.AreEqual(1, repo.SaveCallCount, "防抖应只落盘最后一次调度");
        Assert.AreEqual("版本 40", repo.LastSavedContent, "落盘内容应为最后一次输入");

        coordinator.Dispose();
    }

    /// <summary>
    /// F-P1-7 验证：CancelPendingSave / FlushAsync / Dispose 之后，同一便签可继续正常重新调度，
    /// 说明「释放权归持有任务」的重构没有破坏既有取消语义，也没有误删后续新调度。
    /// </summary>
    [TestMethod]
    public async Task F_P1_7_CancelledScheduling_CanStillBeRescheduled()
    {
        var repo = new CountingRepository();
        var coordinator = new AutoSaveCoordinator(repo, debounceMilliseconds: 200);

        var note = new Note { Id = Guid.NewGuid(), Content = "第一次" };

        // 1. 调度后立即取消：不应落盘
        coordinator.ScheduleSave(note, n => repo.SaveAsync(n));
        coordinator.CancelPendingSave(note.Id);
        await Task.Delay(350);
        Assert.AreEqual(0, repo.SaveCallCount, "被取消的调度不得落盘");

        // 2. 取消后仍能重新调度并落盘（验证旧 CTS 释放策略未破坏重调度路径）
        note.Content = "第二次";
        coordinator.ScheduleSave(note, n => repo.SaveAsync(n));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (repo.SaveCallCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.AreEqual(1, repo.SaveCallCount, "取消后重新调度应该正常落盘");
        Assert.AreEqual("第二次", repo.LastSavedContent);

        // 3. FlushAsync 走同一释放路径，之后仍可继续调度
        note.Content = "第三次";
        coordinator.ScheduleSave(note, n => repo.SaveAsync(n));
        await coordinator.FlushAsync(note.Id);
        Assert.AreEqual(2, repo.SaveCallCount, "FlushAsync 必须立即落盘");

        // 4. Dispose 后再调度也不得抛异常（令牌只取消不释放，由持有任务收尾）
        coordinator.Dispose();
        note.Content = "第四次";
        coordinator.ScheduleSave(note, n => repo.SaveAsync(n));
        await Task.Delay(350);
        Assert.AreEqual("第四次", repo.LastSavedContent, "Dispose 后重新调度仍应正常落盘");
    }

    /// <summary>
    /// 统计落盘次数与最近一次落盘内容的测试替身。
    /// </summary>
    private sealed class CountingRepository : INoteRepository
    {
        private int _saveCallCount;
        private string _lastSavedContent = string.Empty;

        public int SaveCallCount => Volatile.Read(ref _saveCallCount);
        public string LastSavedContent => Volatile.Read(ref _lastSavedContent);

        public Task SaveAsync(Note note, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _saveCallCount);
            Volatile.Write(ref _lastSavedContent, note.Content);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Note>> GetAllActiveAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Note>>(Array.Empty<Note>());

        public Task<IReadOnlyList<Note>> GetAllArchivedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Note>>(Array.Empty<Note>());

        public Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Note>>(Array.Empty<Note>());

        public Task<Note?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<Note?>(null);

        public Task<ApplyRemoteResult> ApplyRemoteBatchAsync(IEnumerable<RemoteApplyItem> items, CancellationToken ct = default) =>
            Task.FromResult(new ApplyRemoteResult(0, 0));

        public Task SaveBatchAsync(IEnumerable<Note> notes, CancellationToken ct = default) =>
            Task.WhenAll(notes.Select(n => SaveAsync(n, ct)));

        public Task SoftDeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ArchiveNoteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreNoteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task HardDeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAllArchivedAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateWindowBoundsAsync(Guid id, double x, double y, double width, double height, bool isOpen, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UpdateWindowPlacementAsync(Guid id, double x, double y, double width, double height, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
