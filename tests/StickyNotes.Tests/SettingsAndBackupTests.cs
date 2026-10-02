using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Data;
using StickyNotes.Infrastructure;
using StickyNotes.Models;
using StickyNotes.Services;

namespace StickyNotes.Tests;

[TestClass]
public class SettingsAndBackupTests
{
    private string _testDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"StickyNotes_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch { }
    }

    [TestMethod]
    public void SettingsService_DefaultAndPersistence_ShouldWork()
    {
        var settingsFile = Path.Combine(_testDir, "settings.json");
        var service = new SettingsService(settingsFile);

        // 默认字号为 14.0
        Assert.AreEqual(14.0, service.EditorFontSize);

        // 修改并自动保存
        service.SetEditorFontSize(18.0);
        Assert.AreEqual(18.0, service.EditorFontSize);
        Assert.IsTrue(File.Exists(settingsFile));

        // 重新加载验证持久化
        var reloadedService = new SettingsService(settingsFile);
        Assert.AreEqual(18.0, reloadedService.EditorFontSize);

        // 超界保护（10 ~ 36）
        service.SetEditorFontSize(5.0);
        Assert.AreEqual(10.0, service.EditorFontSize);

        service.SetEditorFontSize(50.0);
        Assert.AreEqual(36.0, service.EditorFontSize);
    }

    [TestMethod]
    public async Task ExportImportService_RoundTrip_ShouldPreserveNotes()
    {
        var dbPath = Path.Combine(_testDir, "test.db");
        var dbContext = new SqliteDatabaseContext(dbPath);
        await dbContext.InitializeAndMigrateAsync();
        var repo = new NoteRepository(dbContext);
        var backupService = new ExportImportService(repo);

        var note1 = new Note
        {
            Id = Guid.NewGuid(),
            Content = "这是待备份便签 1",
            Color = NoteColor.Blue,
            IsPinned = true,
            WindowX = 200,
            WindowY = 220,
            WindowWidth = 380,
            WindowHeight = 420
        };

        var note2 = new Note
        {
            Id = Guid.NewGuid(),
            Content = "这是已归档便签 2",
            Color = NoteColor.Purple,
            IsDeleted = true
        };

        await repo.SaveAsync(note1);
        await repo.SaveAsync(note2);

        var exportFile = Path.Combine(_testDir, "backup.json");
        int exportedCount = await backupService.ExportNotesAsync(exportFile);

        Assert.AreEqual(2, exportedCount);
        Assert.IsTrue(File.Exists(exportFile));

        // 模拟还原到全新干净的数据库
        var newDbPath = Path.Combine(_testDir, "restored.db");
        var newDbContext = new SqliteDatabaseContext(newDbPath);
        await newDbContext.InitializeAndMigrateAsync();
        var newRepo = new NoteRepository(newDbContext);
        var newBackupService = new ExportImportService(newRepo);

        int importedCount = await newBackupService.ImportNotesAsync(exportFile);
        Assert.AreEqual(2, importedCount);

        var restoredActive = await newRepo.GetAllActiveAsync();
        var restoredArchived = await newRepo.GetAllArchivedAsync();

        Assert.AreEqual(1, restoredActive.Count);
        Assert.AreEqual(note1.Id, restoredActive[0].Id);
        Assert.AreEqual("这是待备份便签 1", restoredActive[0].Content);
        Assert.AreEqual(NoteColor.Blue, restoredActive[0].Color);
        Assert.IsTrue(restoredActive[0].IsPinned);

        Assert.AreEqual(1, restoredArchived.Count);
        Assert.AreEqual(note2.Id, restoredArchived[0].Id);
        Assert.AreEqual("这是已归档便签 2", restoredArchived[0].Content);
        Assert.AreEqual(NoteColor.Purple, restoredArchived[0].Color);
        Assert.IsTrue(restoredArchived[0].IsDeleted);
    }
}
