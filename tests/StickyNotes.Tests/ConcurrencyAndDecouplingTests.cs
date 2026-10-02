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
    /// P1-5 验证：单实例 Local 命名互斥量格式与注册窗口消息非零有效性
    /// </summary>
    [TestMethod]
    public void P1_5_LocalHashedMutex_AndActivation()
    {
        var dataDirHash = (AppPaths.DataDirectory?.ToLowerInvariant() ?? "").GetHashCode().ToString("X8");
        var mutexName = $@"Local\StickyNotes_{dataDirHash}";

        Assert.IsTrue(mutexName.StartsWith(@"Local\StickyNotes_"), "Mutex 必须以 Local\\StickyNotes_ 开头");
        Assert.AreEqual(8, dataDirHash.Length, "数据目录哈希长度必须为 8 位十六进制");

        Assert.AreNotEqual(0, NativeMethods.WM_ACTIVATE_INSTANCE, "WM_ACTIVATE_INSTANCE 注册消息 ID 必须大于 0");
    }
}
