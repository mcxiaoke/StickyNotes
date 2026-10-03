using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Data;
using StickyNotes.Infrastructure;
using StickyNotes.Models;

namespace StickyNotes.Tests;

[TestClass]
public class NoteRepositoryTests
{
    private SqliteDatabaseContext _context = null!;
    private NoteRepository _repository = null!;
    private string _testDbPath = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _testDbPath = Path.Combine(AppPaths.DataDirectory, $"test_{Guid.NewGuid():N}.db");
        _context = new SqliteDatabaseContext(_testDbPath);
        await _context.InitializeAndMigrateAsync();
        _repository = new NoteRepository(_context);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (File.Exists(_testDbPath))
            {
                File.Delete(_testDbPath);
            }
        }
        catch { }
    }

    [TestMethod]
    public async Task SaveAsync_And_GetByIdAsync_ShouldPersistCorrectly()
    {
        var note = new Note
        {
            Id = Guid.NewGuid(),
            Content = "第一行标题\n这是正文内容，支持中文字符与换行\n第三行结束",
            Color = NoteColor.Green,
            IsPinned = true,
            WindowX = 250,
            WindowY = 180,
            WindowWidth = 350,
            WindowHeight = 400
        };

        await _repository.SaveAsync(note);
        var loaded = await _repository.GetByIdAsync(note.Id);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(note.Id, loaded.Id);
        Assert.AreEqual(note.Content, loaded.Content);
        Assert.AreEqual(NoteColor.Green, loaded.Color);
        Assert.IsTrue(loaded.IsPinned);
        Assert.AreEqual(250, loaded.WindowX);
        Assert.AreEqual(180, loaded.WindowY);
        Assert.AreEqual(350, loaded.WindowWidth);
        Assert.AreEqual(400, loaded.WindowHeight);
        Assert.AreEqual("第一行标题", loaded.DisplayTitle);
    }

    [TestMethod]
    public async Task GetAllActiveAsync_ShouldOrderBy_IsPinned_Then_UpdatedAtDesc()
    {
        var noteOld = new Note
        {
            Id = Guid.NewGuid(),
            Content = "普通便签1",
            IsPinned = false,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-20)
        };

        var noteNew = new Note
        {
            Id = Guid.NewGuid(),
            Content = "普通便签2",
            IsPinned = false,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        var notePinned = new Note
        {
            Id = Guid.NewGuid(),
            Content = "置顶便签",
            IsPinned = true,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10)
        };

        await _repository.SaveAsync(noteOld);
        await _repository.SaveAsync(noteNew);
        await _repository.SaveAsync(notePinned);

        var list = await _repository.GetAllActiveAsync();

        Assert.AreEqual(3, list.Count);
        // 置顶排在第 1 个
        Assert.AreEqual(notePinned.Id, list[0].Id);
        // 最新修改的普通便签排在第 2 个
        Assert.AreEqual(noteNew.Id, list[1].Id);
        // 较早修改的普通便签排在第 3 个
        Assert.AreEqual(noteOld.Id, list[2].Id);
    }

    [TestMethod]
    public async Task SoftDeleteAsync_ShouldExcludeNote_From_GetAllActiveAsync()
    {
        var note = new Note
        {
            Id = Guid.NewGuid(),
            Content = "待删除便签"
        };

        await _repository.SaveAsync(note);
        var beforeDelete = await _repository.GetAllActiveAsync();
        Assert.AreEqual(1, beforeDelete.Count);

        await _repository.SoftDeleteAsync(note.Id);

        var afterDelete = await _repository.GetAllActiveAsync();
        Assert.AreEqual(0, afterDelete.Count);

        // 数据库物理记录仍在，但标记为 IsDeleted = true
        var raw = await _repository.GetByIdAsync(note.Id);
        Assert.IsNotNull(raw);
        Assert.IsTrue(raw.IsDeleted);
    }

    [TestMethod]
    public async Task UpdateWindowBoundsAsync_ShouldUpdateCoordinates()
    {
        var note = new Note
        {
            Id = Guid.NewGuid(),
            Content = "窗口坐标测试"
        };
        await _repository.SaveAsync(note);

        await _repository.UpdateWindowBoundsAsync(note.Id, 320, 240, 400, 500, isOpen: false);

        var updated = await _repository.GetByIdAsync(note.Id);
        Assert.IsNotNull(updated);
        Assert.AreEqual(320, updated.WindowX);
        Assert.AreEqual(240, updated.WindowY);
        Assert.AreEqual(400, updated.WindowWidth);
        Assert.AreEqual(500, updated.WindowHeight);
        Assert.IsFalse(updated.IsOpen);
    }

    [TestMethod]
    public async Task ArchiveNoteAsync_And_GetAllArchivedAsync_ShouldWork()
    {
        var activeNote = new Note { Id = Guid.NewGuid(), Content = "活动便签" };
        var archiveNote = new Note { Id = Guid.NewGuid(), Content = "待归档便签" };

        await _repository.SaveAsync(activeNote);
        await _repository.SaveAsync(archiveNote);

        await _repository.ArchiveNoteAsync(archiveNote.Id);

        var activeList = await _repository.GetAllActiveAsync();
        var archivedList = await _repository.GetAllArchivedAsync();

        Assert.AreEqual(1, activeList.Count);
        Assert.AreEqual(activeNote.Id, activeList[0].Id);

        Assert.AreEqual(1, archivedList.Count);
        Assert.AreEqual(archiveNote.Id, archivedList[0].Id);
        Assert.IsTrue(archivedList[0].IsDeleted);
        Assert.IsFalse(archivedList[0].IsOpen);
    }

    [TestMethod]
    public async Task RestoreNoteAsync_ShouldMoveBackToActiveNotes()
    {
        var note = new Note { Id = Guid.NewGuid(), Content = "归档再恢复" };
        await _repository.SaveAsync(note);
        await _repository.ArchiveNoteAsync(note.Id);

        var archived = await _repository.GetAllArchivedAsync();
        Assert.AreEqual(1, archived.Count);

        await _repository.RestoreNoteAsync(note.Id);

        var archivedAfter = await _repository.GetAllArchivedAsync();
        var activeAfter = await _repository.GetAllActiveAsync();

        Assert.AreEqual(0, archivedAfter.Count);
        Assert.AreEqual(1, activeAfter.Count);
        Assert.IsFalse(activeAfter[0].IsDeleted);
    }

    [TestMethod]
    public async Task ClearAllArchivedAsync_ShouldDeleteArchivedOnly()
    {
        var activeNote = new Note { Id = Guid.NewGuid(), Content = "保留的活动便签" };
        var archive1 = new Note { Id = Guid.NewGuid(), Content = "归档 1" };
        var archive2 = new Note { Id = Guid.NewGuid(), Content = "归档 2" };

        await _repository.SaveAsync(activeNote);
        await _repository.SaveAsync(archive1);
        await _repository.SaveAsync(archive2);

        await _repository.ArchiveNoteAsync(archive1.Id);
        await _repository.ArchiveNoteAsync(archive2.Id);

        await _repository.ClearAllArchivedAsync();

        var archivedAfter = await _repository.GetAllArchivedAsync();
        var activeAfter = await _repository.GetAllActiveAsync();

        Assert.AreEqual(0, archivedAfter.Count);
        Assert.AreEqual(1, activeAfter.Count);
        Assert.AreEqual(activeNote.Id, activeAfter[0].Id);
    }

    /// <summary>
    /// F-P1-5 验证（路径 a）：全新数据库建库后，V1 建表**不含** V2 两列，
    /// 由 V2 迁移通过 ALTER 补齐；最终 schema 版本为 2 且列齐全、可正常读写。
    /// </summary>
    [TestMethod]
    public async Task F_P1_5_FreshDatabase_MigratesToV2_WithBothColumnsAddedByAlter()
    {
        var dbPath = Path.Combine(AppPaths.DataDirectory, $"fresh_{Guid.NewGuid():N}.db");
        try
        {
            var ctx = new SqliteDatabaseContext(dbPath);
            await ctx.InitializeAndMigrateAsync();

            await using var conn = ctx.CreateConnection();
            await conn.OpenAsync();

            // 版本必须是 2
            await using (var verCmd = conn.CreateCommand())
            {
                verCmd.CommandText = "PRAGMA user_version;";
                var ver = Convert.ToInt32(await verCmd.ExecuteScalarAsync());
                Assert.AreEqual(2, ver, "全新库迁移后版本应为 2");
            }

            // 两列必须存在
            var columns = new List<string>();
            await using (var infoCmd = conn.CreateCommand())
            {
                infoCmd.CommandText = "PRAGMA table_info(Notes);";
                await using var reader = await infoCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(1));
                }
            }

            CollectionAssert.Contains(columns, "IsPinnedInList", "V2 迁移必须补齐 IsPinnedInList 列");
            CollectionAssert.Contains(columns, "AlwaysOnTop", "V2 迁移必须补齐 AlwaysOnTop 列");
            CollectionAssert.Contains(columns, "IsPinned", "V1 的 IsPinned 列必须保留");

            // 可正常读写
            var repo = new NoteRepository(ctx);
            var note = new Note { Id = Guid.NewGuid(), Content = "全新库写入", IsPinnedInList = true };
            await repo.SaveAsync(note);
            var loaded = await repo.GetByIdAsync(note.Id);
            Assert.IsNotNull(loaded);
            Assert.IsTrue(loaded.IsPinnedInList);
        }
        finally
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    /// <summary>
    /// F-P1-5 验证（路径 b）：历史遗留库（早期版本在 V1 建表时就带上了 V2 两列）升级时，
    /// 迁移必须幂等 —— 跳过 ALTER、不抛异常、不靠 catch 掩盖，并正确继承 IsPinned。
    /// 这是原报告指出「两条路径只覆盖一条」中缺失的那条。
    /// </summary>
    [TestMethod]
    public async Task F_P1_5_LegacyDatabaseWithV2ColumnsAlreadyPresent_MigratesIdempotently()
    {
        var dbPath = Path.Combine(AppPaths.DataDirectory, $"legacy_{Guid.NewGuid():N}.db");
        try
        {
            // 手工构造一个「v1 版本号，但表里已含 V2 两列」的遗留库
            var ctx = new SqliteDatabaseContext(dbPath);
            await using (var conn = ctx.CreateConnection())
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE Notes (
                        Id             TEXT PRIMARY KEY,
                        Content        TEXT NOT NULL DEFAULT '',
                        Color          INTEGER NOT NULL DEFAULT 0,
                        IsPinned       INTEGER NOT NULL DEFAULT 0,
                        IsPinnedInList INTEGER NOT NULL DEFAULT 0,
                        AlwaysOnTop    INTEGER NOT NULL DEFAULT 0,
                        IsDeleted      INTEGER NOT NULL DEFAULT 0,
                        WindowX        REAL NOT NULL DEFAULT 150,
                        WindowY        REAL NOT NULL DEFAULT 150,
                        WindowWidth    REAL NOT NULL DEFAULT 320,
                        WindowHeight   REAL NOT NULL DEFAULT 360,
                        IsOpen         INTEGER NOT NULL DEFAULT 1,
                        CreatedAt      TEXT NOT NULL,
                        UpdatedAt      TEXT NOT NULL
                    );
                    INSERT INTO Notes (Id, Content, IsPinned, CreatedAt, UpdatedAt)
                    VALUES ('11111111-1111-1111-1111-111111111111', '遗留便签', 1, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                    PRAGMA user_version = 1;
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            // 迁移必须成功且不抛异常（旧实现会走 catch 掩盖后再强推版本号）
            await ctx.InitializeAndMigrateAsync();

            await using var conn2 = ctx.CreateConnection();
            await conn2.OpenAsync();

            await using (var verCmd = conn2.CreateCommand())
            {
                verCmd.CommandText = "PRAGMA user_version;";
                var ver = Convert.ToInt32(await verCmd.ExecuteScalarAsync());
                Assert.AreEqual(2, ver, "遗留库迁移后版本应提升为 2");
            }

            // IsPinned = 1 必须被继承到两个新字段
            var repo = new NoteRepository(ctx);
            var legacy = await repo.GetByIdAsync(Guid.Parse("11111111-1111-1111-1111-111111111111"));
            Assert.IsNotNull(legacy, "遗留数据必须完好保留");
            Assert.IsTrue(legacy.IsPinnedInList, "IsPinned 应被继承到 IsPinnedInList");
            Assert.IsTrue(legacy.AlwaysOnTop, "IsPinned 应被继承到 AlwaysOnTop");
        }
        finally
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    /// <summary>
    /// F-P3-9：默认尺寸只能有一个事实来源。实体的默认值必须与建表列默认值一致，
    /// 否则「新建便签的默认大小」会随「走实体还是走 SQL 默认」而漂移。
    /// </summary>
    [TestMethod]
    public async Task F_P3_9_DefaultWindowGeometry_HasSingleSourceOfTruth()
    {
        // 1. 实体层默认值
        var fresh = new Note();
        Assert.AreEqual(380, fresh.WindowWidth, "实体默认宽度应为 380");
        Assert.AreEqual(420, fresh.WindowHeight, "实体默认高度应为 420");

        // 2. 数据库列默认值（不显式写 WindowWidth/WindowHeight，让 SQL 默认生效）
        await using var conn = _context.CreateConnection();
        await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO Notes (Id, Content, Color, IsPinned, IsPinnedInList, AlwaysOnTop, IsDeleted,
                                   WindowX, WindowY, IsOpen, CreatedAt, UpdatedAt)
                VALUES ('22222222-2222-2222-2222-222222222222', '默认尺寸校验', 0, 0, 0, 0, 0,
                        150, 150, 1, '2026-10-03T00:00:00Z', '2026-10-03T00:00:00Z');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var loaded = await _repository.GetByIdAsync(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        Assert.IsNotNull(loaded);
        Assert.AreEqual(Note.DefaultWindowWidth, loaded.WindowWidth,
            "SQL 列默认宽度必须与实体默认值一致（原为 320 vs 380 的双源漂移）");
        Assert.AreEqual(Note.DefaultWindowHeight, loaded.WindowHeight,
            "SQL 列默认高度必须与实体默认值一致（原为 360 vs 420 的双源漂移）");
        Assert.AreEqual(Note.DefaultWindowX, loaded.WindowX);
        Assert.AreEqual(Note.DefaultWindowY, loaded.WindowY);
    }
}
