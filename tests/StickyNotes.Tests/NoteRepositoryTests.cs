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
}
