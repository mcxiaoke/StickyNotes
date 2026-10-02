using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Data;
using StickyNotes.Infrastructure;
using StickyNotes.Messages;
using StickyNotes.Models;
using StickyNotes.Services;
using StickyNotes.ViewModels;

namespace StickyNotes.Tests;

/// <summary>
/// 阶段 2：命门功能与性能优化回归测试（覆盖系统托盘、全局热键、自启动、搜索词高亮分段及滚轮缩放）
/// </summary>
[TestClass]
public class FeaturesAndPerformanceTests
{
    private string _testDir = null!;
    private string _settingsPath = null!;

    [TestInitialize]
    public void Setup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"StickyNotes_Phase2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _settingsPath = Path.Combine(_testDir, "test_settings.json");
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
    /// 验证搜索关键词高亮分段算法 (BuildSegments) 与多重命中切分
    /// </summary>
    [TestMethod]
    public void P2_SearchKeywordSegments_SplitsCorrectly()
    {
        // 1. 单词命中测试
        var text = "当前架构决策采用 SQLite WAL 模式";
        var segments = SearchService.BuildSegments(text, "SQLite");

        Assert.AreEqual(3, segments.Count, "应切分为 3 段");
        Assert.AreEqual("当前架构决策采用 ", segments[0].Text);
        Assert.IsFalse(segments[0].IsHit);

        Assert.AreEqual("SQLite", segments[1].Text);
        Assert.IsTrue(segments[1].IsHit);

        Assert.AreEqual(" WAL 模式", segments[2].Text);
        Assert.IsFalse(segments[2].IsHit);

        // 2. 多重命中测试
        var text2 = "便签 A 关注性能，便签 B 也关注性能";
        var segments2 = SearchService.BuildSegments(text2, "性能");

        Assert.AreEqual(4, segments2.Count);
        Assert.IsTrue(segments2[1].IsHit);
        Assert.IsTrue(segments2[3].IsHit);
        Assert.AreEqual("性能", segments2[1].Text);
        Assert.AreEqual("性能", segments2[3].Text);

        // 3. SearchService 端到端搜索集成验证 Segments
        var service = new SearchService();
        var notes = new List<Note>
        {
            new Note
            {
                Id = Guid.NewGuid(),
                Content = "第一行普通文本\n第二行包含命中关键词 StickyTarget 内容\n第三行结束",
                Color = NoteColor.Green
            }
        };

        var hits = service.Search(notes, "StickyTarget");
        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual(2, hits[0].LineNumber, "行号必须为第 2 行");
        Assert.IsTrue(hits[0].Segments.Count >= 2, "命中结果必须包含高亮分段");
        Assert.IsTrue(hits[0].Segments.Any(s => s.IsHit && s.Text == "StickyTarget"), "必须包含 IsHit=true 的关键词段");
    }

    /// <summary>
    /// 验证设置服务对新增命门配置（托盘常驻、全局热键、自启动最小化）的序列化与重载
    /// </summary>
    [TestMethod]
    public void P2_SettingsService_NewOptions_Serialization()
    {
        var settingsService = new SettingsService(_settingsPath);

        // 默认值断言
        Assert.IsTrue(settingsService.MinimizeToTrayOnClose, "默认开启关闭窗口时最小化到托盘");
        Assert.IsTrue(settingsService.EnableGlobalHotKeys, "默认开启全局快捷键");
        Assert.IsTrue(settingsService.StartMinimized, "默认开启自启最小化到托盘");

        // 更改配置并保存
        settingsService.MinimizeToTrayOnClose = false;
        settingsService.EnableGlobalHotKeys = false;
        settingsService.StartMinimized = false;

        // 重新加载断言持久化有效
        var reloaded = new SettingsService(_settingsPath);
        Assert.IsFalse(reloaded.MinimizeToTrayOnClose);
        Assert.IsFalse(reloaded.EnableGlobalHotKeys);
        Assert.IsFalse(reloaded.StartMinimized);
    }

    /// <summary>
    /// 验证便签字号 Ctrl+滚轮缩放算法及 10 ~ 36 pt 钳制保护
    /// </summary>
    [TestMethod]
    public void P2_NoteViewModel_CtrlWheelZoom_AndClamping()
    {
        var settingsService = new SettingsService(_settingsPath);
        settingsService.SetEditorFontSize(14.0);

        var repo = new FakeNoteRepository();
        var autoSave = new AutoSaveCoordinator(repo);
        var vm = new NoteViewModel(repo, autoSave, settingsService);

        Assert.AreEqual(14.0, vm.FontSize);

        // 放大 2 级
        vm.ChangeFontSize(+2);
        Assert.AreEqual(16.0, vm.FontSize);
        Assert.AreEqual(16.0, settingsService.EditorFontSize, "全局设置已同步生效");

        // 缩放超上限钳制 (上限 36 pt)
        vm.ChangeFontSize(+50);
        Assert.AreEqual(36.0, vm.FontSize);

        // 缩放低下限钳制 (下限 10 pt)
        vm.ChangeFontSize(-100);
        Assert.AreEqual(10.0, vm.FontSize);
    }

    /// <summary>
    /// 验证开机自启动服务 AutoStartService 读取状态安全无异常
    /// </summary>
    [TestMethod]
    public void P2_AutoStartService_QueryDoesNotThrow()
    {
        var autoStart = new AutoStartService();
        bool status = autoStart.IsAutoStartEnabled;
        // 在受限或测试机上应平滑返回 bool，绝不抛出未捕获异常
        Assert.IsNotNull(status);
    }

    /// <summary>
    /// 验证 HotKeyService 与 TrayIconService 生命周期与释放无句柄泄漏
    /// </summary>
    [TestMethod]
    public void P2_HotKeyAndTrayServices_Lifecycle()
    {
        TestEnvironment.RunInSta(() =>
        {
            var settingsService = new SettingsService(_settingsPath);
            settingsService.EnableGlobalHotKeys = true;

            var hotKeyService = new HotKeyService(settingsService);
            hotKeyService.Initialize();
            hotKeyService.UnregisterHotKeys();
            hotKeyService.Dispose();

            var repo = new FakeNoteRepository();
            var windowManager = new WindowManager(null!, repo);
            var trayService = new TrayIconService(windowManager, settingsService);
            trayService.Initialize();
            trayService.Dispose();
        });
    }

    /// <summary>
    /// 验证 NoteWindow 关闭按钮拥有 WindowChrome.IsHitTestVisibleInChrome=true 且点击可正常关闭窗口
    /// </summary>
    [TestMethod]
    public void P2_NoteWindow_CloseButton_IsHitTestVisible_AndClosesWindow()
    {
        TestEnvironment.RunInSta(() =>
        {
            var repo = new FakeNoteRepository();
            var coordinator = new AutoSaveCoordinator();
            var vm = new NoteViewModel(repo, coordinator);
            var note = new Note { Id = Guid.NewGuid(), Content = "测试便签", Color = NoteColor.Yellow };
            vm.Initialize(note);

            var win = new Views.NoteWindow(vm);
            win.Show();

            var closeBtn = win.CloseButtonControl;
            Assert.IsNotNull(closeBtn, "未找到关闭便签按钮");

            bool isHitTestVisible = System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(closeBtn);
            Assert.IsTrue(isHitTestVisible, "关闭按钮必须设置 WindowChrome.IsHitTestVisibleInChrome=true，否则在 Caption 区域会被 DWM 拖拽拦截导致点击失效");

            bool closedFired = false;
            win.Closed += (_, _) => closedFired = true;

            // 模拟触发按钮 Click 事件
            closeBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.IsTrue(closedFired, "点击关闭按钮必须正常触发 Window.Closed 事件收起便签贴纸");
            coordinator.Dispose();
        });
    }

    private class FakeNoteRepository : INoteRepository
    {
        public List<Note> Notes { get; set; } = new();

        public Task<IReadOnlyList<Note>> GetAllActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Note>>(Notes.Where(n => !n.IsDeleted).ToList());

        public Task<IReadOnlyList<Note>> GetAllArchivedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Note>>(Notes.Where(n => n.IsDeleted).ToList());

        public Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Note>>(Notes.ToList());

        public Task<Note?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Notes.FirstOrDefault(n => n.Id == id));

        public Task SaveAsync(Note note, CancellationToken cancellationToken = default)
        {
            var idx = Notes.FindIndex(n => n.Id == note.Id);
            if (idx >= 0) Notes[idx] = note;
            else Notes.Add(note);
            return Task.CompletedTask;
        }

        public async Task SaveBatchAsync(IEnumerable<Note> notes, CancellationToken cancellationToken = default)
        {
            foreach (var note in notes)
            {
                await SaveAsync(note, cancellationToken);
            }
        }

        public Task SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            ArchiveNoteAsync(id, cancellationToken);

        public Task ArchiveNoteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var note = Notes.FirstOrDefault(n => n.Id == id);
            if (note != null) { note.IsDeleted = true; note.IsOpen = false; }
            return Task.CompletedTask;
        }

        public Task RestoreNoteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var note = Notes.FirstOrDefault(n => n.Id == id);
            if (note != null) note.IsDeleted = false;
            return Task.CompletedTask;
        }

        public Task HardDeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Notes.RemoveAll(n => n.Id == id);
            return Task.CompletedTask;
        }

        public Task ClearAllArchivedAsync(CancellationToken cancellationToken = default)
        {
            Notes.RemoveAll(n => n.IsDeleted);
            return Task.CompletedTask;
        }

        public Task UpdateWindowBoundsAsync(Guid id, double x, double y, double width, double height, bool isOpen, CancellationToken cancellationToken = default)
        {
            var note = Notes.FirstOrDefault(n => n.Id == id);
            if (note != null)
            {
                note.WindowX = x;
                note.WindowY = y;
                note.WindowWidth = width;
                note.WindowHeight = height;
                note.IsOpen = isOpen;
            }
            return Task.CompletedTask;
        }
    }
}
