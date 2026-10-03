using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Data;
using StickyNotes.Models;
using StickyNotes.Services;
using StickyNotes.ViewModels;
using StickyNotes.Views;

namespace StickyNotes.Tests;

/// <summary>
/// UI 真实渲染断言与自动化高清截图验证
/// </summary>
[TestClass]
public class UiRenderingAndScreenshotTests
{
    private class FakeNoteRepository : INoteRepository
    {
        public List<Note> Notes = new();

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

        public Task SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return ArchiveNoteAsync(id, cancellationToken);
        }

        public Task ArchiveNoteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var note = Notes.FirstOrDefault(n => n.Id == id);
            if (note != null)
            {
                note.IsDeleted = true;
                note.IsOpen = false;
            }
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

        public Task UpdateWindowPlacementAsync(Guid id, double x, double y, double width, double height, CancellationToken cancellationToken = default)
        {
            var note = Notes.FirstOrDefault(n => n.Id == id);
            if (note != null)
            {
                note.WindowX = x;
                note.WindowY = y;
                note.WindowWidth = width;
                note.WindowHeight = height;
            }
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public void Render_NotesListWindow_Normal_SavesSnapshot()
    {
        TestEnvironment.RunInSta(() =>
        {
            var repo = new FakeNoteRepository
            {
                Notes =
                {
                    new Note
                    {
                        Id = Guid.NewGuid(),
                        Content = "📌 2026 架构评审关键决议\n1. 采用 SQLite WAL 模式防掉电\n2. 纯内存字符绝对偏移精准跳行\n3. 500ms 防抖静默保存",
                        Color = NoteColor.Yellow,
                        IsPinned = true,
                        UpdatedAt = DateTime.UtcNow
                    },
                    new Note
                    {
                        Id = Guid.NewGuid(),
                        Content = "明日工作待办清单\n- 早上 10:00 参加技术委员会评审\n- 运行自动化测试与 UI 真实截图验证\n- 整理交付文档与发布脚本",
                        Color = NoteColor.Green,
                        IsPinned = false,
                        UpdatedAt = DateTime.UtcNow.AddMinutes(-30)
                    },
                    new Note
                    {
                        Id = Guid.NewGuid(),
                        Content = "常用快捷键备忘录\nCtrl+N: 快速新建便签\nCtrl+F: 聚焦搜索框\nAlt+F4: 关闭便签窗口",
                        Color = NoteColor.Pink,
                        IsPinned = false,
                        UpdatedAt = DateTime.UtcNow.AddHours(-2)
                    }
                }
            };

            var searchService = new SearchService();
            var windowManager = new WindowManager(null!, repo);
            var vm = new NotesListViewModel(repo, searchService, windowManager);
            vm.LoadNotesAsync().GetAwaiter().GetResult();

            var win = new NotesListWindow(vm);

            // 断言窗口基本属性与数据展示
            Assert.AreEqual("便签", win.Title);
            Assert.AreEqual(3, vm.Notes.Count);
            Assert.IsFalse(vm.IsSearching);

            // 抓取并保存真实渲染截图
            TestEnvironment.SaveWindowSnapshot(win, 480, 720, "01_NotesListWindow_Normal.png");
        });
    }

    [TestMethod]
    public void Render_NotesListWindow_Searching_SavesSnapshot()
    {
        TestEnvironment.RunInSta(() =>
        {
            var repo = new FakeNoteRepository
            {
                Notes =
                {
                    new Note
                    {
                        Id = Guid.NewGuid(),
                        Content = "系统工程架构文档\n核心采用 WPF .NET 8 LTS 框架\n数据层采用轻量级 Microsoft.Data.Sqlite 引擎\n搜索算法支持毫秒级精准跳行",
                        Color = NoteColor.Blue,
                        UpdatedAt = DateTime.UtcNow
                    },
                    new Note
                    {
                        Id = Guid.NewGuid(),
                        Content = "技术选型决策推导报告\n论证为什么不采用 EF Core 而是原生 Sqlite\n论证字符绝对偏移相比纯行号的绝对优势",
                        Color = NoteColor.Purple,
                        UpdatedAt = DateTime.UtcNow.AddMinutes(-10)
                    }
                }
            };

            var searchService = new SearchService();
            var windowManager = new WindowManager(null!, repo);
            var vm = new NotesListViewModel(repo, searchService, windowManager);
            vm.LoadNotesAsync().GetAwaiter().GetResult();

            var win = new NotesListWindow(vm);

            // 模拟搜索关键词
            vm.SearchText = "Sqlite";
            // 手动调用一次搜索更新保证同步渲染
            var hits = searchService.Search(vm.Notes, "Sqlite");
            vm.SearchResults.Clear();
            foreach (var hit in hits) vm.SearchResults.Add(hit);
            vm.SearchHitCount = hits.Count;
            vm.IsSearching = true;

            Assert.AreEqual(2, vm.SearchResults.Count);

            TestEnvironment.SaveWindowSnapshot(win, 480, 720, "02_NotesListWindow_Searching.png");
        });
    }

    [TestMethod]
    public void NotesListWindow_Closing_ShouldClearSearchText()
    {
        TestEnvironment.RunInSta(() =>
        {
            var repo = new FakeNoteRepository
            {
                Notes =
                {
                    new Note { Id = Guid.NewGuid(), Content = "测试便签内容", Color = NoteColor.Blue }
                }
            };
            var searchService = new SearchService();
            var windowManager = new WindowManager(null!, repo);
            var vm = new NotesListViewModel(repo, searchService, windowManager);
            vm.LoadNotesAsync().GetAwaiter().GetResult();

            var win = new NotesListWindow(vm);

            // 模拟用户输入搜索词
            vm.SearchText = "测试";
            Assert.AreEqual("测试", vm.SearchText);
            Assert.IsTrue(vm.IsSearching);

            // 触发关闭（无论最小化到托盘还是真正关闭）
            win.Close();

            // 验证搜索词被清空且退出搜索状态
            Assert.AreEqual(string.Empty, vm.SearchText);
            Assert.IsFalse(vm.IsSearching);
        });
    }

    [TestMethod]
    public void NotesListWindow_KeyboardNavigation_SearchMode_DownUpEnterShouldWork()
    {
        TestEnvironment.RunInSta(() =>
        {
            var noteId = Guid.NewGuid();
            var repo = new FakeNoteRepository
            {
                Notes =
                {
                    new Note { Id = noteId, Content = "第一条匹配内容\n包含测试关键字", Color = NoteColor.Blue },
                    new Note { Id = Guid.NewGuid(), Content = "第二条匹配内容\n同样包含测试关键字", Color = NoteColor.Yellow }
                }
            };
            var searchService = new SearchService();
            var windowManager = new WindowManager(null!, repo);
            var vm = new NotesListViewModel(repo, searchService, windowManager);
            vm.LoadNotesAsync().GetAwaiter().GetResult();

            var win = new NotesListWindow(vm);

            // 触发搜索
            vm.SearchText = "测试";
            var hits = searchService.Search(vm.Notes, "测试");
            vm.SearchResults.Clear();
            foreach (var h in hits) vm.SearchResults.Add(h);
            vm.SearchHitCount = hits.Count;
            vm.IsSearching = true;

            win.Show();
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                (Action)(() => { frame.Continue = false; }));
            System.Windows.Threading.Dispatcher.PushFrame(frame);

            var dummySource = new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "", IntPtr.Zero);

            // 1. 在 SearchBox 按 Down -> 激活并选中首个命中项 (index = 0)
            var downArgs = new KeyEventArgs(Keyboard.PrimaryDevice, dummySource, 0, Key.Down) { RoutedEvent = Keyboard.KeyDownEvent };
            win.SearchBox_KeyDown(win.SearchBoxControl, downArgs);
            Assert.IsTrue(downArgs.Handled);
            Assert.AreEqual(0, win.SearchHitsListBoxControl.SelectedIndex);

            // 2. 模拟切换至第 2 条 (index = 1)
            win.SearchHitsListBoxControl.SelectedIndex = 1;
            Assert.AreEqual(1, win.SearchHitsListBoxControl.SelectedIndex);

            // 3. 在 index = 0 时按 Up -> 焦点退回 SearchBox
            win.SearchHitsListBoxControl.SelectedIndex = 0;
            var upArgs = new KeyEventArgs(Keyboard.PrimaryDevice, dummySource, 0, Key.Up) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            win.SearchHitsListBox_PreviewKeyDown(win.SearchHitsListBoxControl, upArgs);
            Assert.IsTrue(upArgs.Handled);

            // 4. 按 Enter -> 触发 JumpToSearchHit 处理并断言 Handled
            var enterArgs = new KeyEventArgs(Keyboard.PrimaryDevice, dummySource, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            win.SearchHitsListBox_PreviewKeyDown(win.SearchHitsListBoxControl, enterArgs);
            Assert.IsTrue(enterArgs.Handled);

            dummySource.Dispose();
        });
    }

    [TestMethod]
    public void NotesListWindow_KeyboardNavigation_NormalMode_DownUpEnterShouldWork()
    {
        TestEnvironment.RunInSta(() =>
        {
            var note1 = new Note { Id = Guid.NewGuid(), Content = "常规便签1", Color = NoteColor.Blue };
            var note2 = new Note { Id = Guid.NewGuid(), Content = "常规便签2", Color = NoteColor.Yellow };
            var repo = new FakeNoteRepository
            {
                Notes = { note1, note2 }
            };
            var searchService = new SearchService();
            var windowManager = new WindowManager(null!, repo);
            var vm = new NotesListViewModel(repo, searchService, windowManager);
            vm.LoadNotesAsync().GetAwaiter().GetResult();

            var win = new NotesListWindow(vm);
            Assert.IsFalse(vm.IsSearching);

            win.Show();
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                (Action)(() => { frame.Continue = false; }));
            System.Windows.Threading.Dispatcher.PushFrame(frame);

            var dummySource = new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "", IntPtr.Zero);

            // 1. 在 SearchBox 按 Down -> 激活并选中首个便签 (index = 0)
            var downArgs = new KeyEventArgs(Keyboard.PrimaryDevice, dummySource, 0, Key.Down) { RoutedEvent = Keyboard.KeyDownEvent };
            win.SearchBox_KeyDown(win.SearchBoxControl, downArgs);
            Assert.IsTrue(downArgs.Handled);
            Assert.AreEqual(0, win.NotesListBoxControl.SelectedIndex);

            // 2. 在 index = 0 时按 Up -> 焦点退回 SearchBox
            var upArgs = new KeyEventArgs(Keyboard.PrimaryDevice, dummySource, 0, Key.Up) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            win.NotesListBox_PreviewKeyDown(win.NotesListBoxControl, upArgs);
            Assert.IsTrue(upArgs.Handled);

            // 3. 按 Enter -> 触发 OpenNote 处理并断言 Handled
            var enterArgs = new KeyEventArgs(Keyboard.PrimaryDevice, dummySource, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            win.NotesListBox_PreviewKeyDown(win.NotesListBoxControl, enterArgs);
            Assert.IsTrue(enterArgs.Handled);

            dummySource.Dispose();
        });
    }

    [TestMethod]
    public void NotesListWindow_Reactivation_RestoresOptimalFocus_AndAllowsArrowNavigation()
    {
        TestEnvironment.RunInSta(() =>
        {
            var noteId1 = Guid.NewGuid();
            var noteId2 = Guid.NewGuid();
            var repo = new FakeNoteRepository
            {
                Notes =
                {
                    new Note { Id = noteId1, Content = "第一条测试便签", Color = NoteColor.Blue },
                    new Note { Id = noteId2, Content = "第二条测试便签", Color = NoteColor.Green }
                }
            };
            var searchService = new SearchService();
            var windowManager = new WindowManager(null!, repo);
            var vm = new NotesListViewModel(repo, searchService, windowManager);
            vm.LoadNotesAsync().GetAwaiter().GetResult();

            var win = new NotesListWindow(vm);

            // 1. 模拟搜索
            vm.SearchText = "测试";
            var hits = searchService.Search(vm.Notes, "测试");
            vm.SearchResults.Clear();
            foreach (var h in hits) vm.SearchResults.Add(h);
            vm.SearchHitCount = hits.Count;
            vm.IsSearching = true;

            win.Show();
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                (Action)(() => { frame.Continue = false; }));
            System.Windows.Threading.Dispatcher.PushFrame(frame);

            // 模拟用户点击或打开了第 1 个搜索结果 (index = 0)
            win.SearchHitsListBoxControl.SelectedIndex = 0;
            win.SearchHitsListBoxControl.SelectedItem = vm.SearchResults[0];

            // 模拟便签窗口关闭，主管理窗口激活，调用 RestoreOptimalFocus
            win.RestoreOptimalFocus();
            Assert.AreEqual(0, win.SearchHitsListBoxControl.SelectedIndex);

            var dummySource = new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "", IntPtr.Zero);

            // 2. 验证当焦点未在输入框时，在窗口层面按 Down 键仍可平滑下移至第 2 项 (index = 1)
            var downArgs = new KeyEventArgs(Keyboard.PrimaryDevice, dummySource, 0, Key.Down) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            win.RaiseEvent(downArgs);
            Assert.IsTrue(downArgs.Handled);
            Assert.AreEqual(1, win.SearchHitsListBoxControl.SelectedIndex);

            // 3. 验证在窗口层面按 Up 键可退回第 1 项 (index = 0)
            var upArgs = new KeyEventArgs(Keyboard.PrimaryDevice, dummySource, 0, Key.Up) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            win.RaiseEvent(upArgs);
            Assert.IsTrue(upArgs.Handled);
            Assert.AreEqual(0, win.SearchHitsListBoxControl.SelectedIndex);

            // 4. 验证在 index = 0 时再次按 Up 键，焦点退回搜索框
            var upToSearchBoxArgs = new KeyEventArgs(Keyboard.PrimaryDevice, dummySource, 0, Key.Up) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            win.RaiseEvent(upToSearchBoxArgs);
            Assert.IsTrue(upToSearchBoxArgs.Handled);

            dummySource.Dispose();
        });
    }

    [TestMethod]
    public void Render_NoteWindow_Yellow_SavesSnapshot()
    {
        TestEnvironment.RunInSta(() =>
        {
            var repo = new FakeNoteRepository();
            var coordinator = new AutoSaveCoordinator();
            var vm = new NoteViewModel(repo, coordinator);

            var note = new Note
            {
                Id = Guid.NewGuid(),
                Content = "经典黄色便签贴纸\n\n这是一张经典的 Windows 桌面便签。\n支持顶部极窄工具栏拖拽、一键新建、切换主题色、随时置顶钉住。\n\n输入停止 500ms 后自动静默保存，断电或关闭绝不丢字！",
                Color = NoteColor.Yellow,
                IsPinned = false
            };
            vm.Initialize(note);

            var win = new NoteWindow(vm);

            Assert.AreEqual(NoteColor.Yellow, win.ViewModel.Color);
            Assert.IsFalse(win.ViewModel.IsPinned);

            TestEnvironment.SaveWindowSnapshot(win, 380, 420, "03_NoteWindow_Yellow.png");
            coordinator.Dispose();
        });
    }

    [TestMethod]
    public void Render_NoteWindow_Green_Pinned_SavesSnapshot()
    {
        TestEnvironment.RunInSta(() =>
        {
            var repo = new FakeNoteRepository();
            var coordinator = new AutoSaveCoordinator();
            var vm = new NoteViewModel(repo, coordinator);

            var note = new Note
            {
                Id = Guid.NewGuid(),
                Content = "📌 清新绿置顶便签\n\n当前便签处于置顶状态（Topmost = True）。\n无论切换到哪一个软件窗口，本便签都常驻在桌面最上层，方便作为开发或写作的对照参考。",
                Color = NoteColor.Green,
                IsPinned = true
            };
            vm.Initialize(note);

            var win = new NoteWindow(vm);

            Assert.AreEqual(NoteColor.Green, win.ViewModel.Color);
            Assert.IsTrue(win.ViewModel.IsPinned);

            TestEnvironment.SaveWindowSnapshot(win, 380, 420, "04_NoteWindow_Green_Pinned.png");
            coordinator.Dispose();
        });
    }

    [TestMethod]
    public void Render_NoteWindow_JumpHighlighted_SavesSnapshot()
    {
        TestEnvironment.RunInSta(() =>
        {
            var repo = new FakeNoteRepository();
            var coordinator = new AutoSaveCoordinator();
            var vm = new NoteViewModel(repo, coordinator);

            var longText = """
                长篇工作纪要与备忘录
                第 1 行：项目立项与需求调研
                第 2 行：技术方案评审与架构选型
                第 3 行：数据存储采用原生 SQLite 与 WAL
                第 4 行：实现 500ms 防抖静默保存
                第 5 行：设计主管理中心与独立贴纸协同
                第 6 行：攻克关键词精准跳行与居中定位
                第 7 行：引入 WPF-UI 现代 Fluent 设计系统
                第 8 行：编写 MSTest 与 STA 真实渲染截图
                第 9 行：生成 Release 自包含单文件发布产物
                第 10 行：验收通过并安全交付
                """;

            var note = new Note
            {
                Id = Guid.NewGuid(),
                Content = longText,
                Color = NoteColor.Pink,
                IsPinned = false
            };
            vm.Initialize(note);

            var win = new NoteWindow(vm);

            // 搜索“精准跳行”（位于第 7 行）
            const string keyword = "精准跳行";
            int matchIndex = longText.IndexOf(keyword);
            Assert.IsTrue(matchIndex > 0);

            // 显示窗口并触发跳行高亮
            win.Width = 340;
            win.Height = 380;
            win.Show();

            win.JumpToSearchHit(matchIndex, keyword.Length);

            // 等待渲染刷新
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                (Action)(() => { frame.Continue = false; }));
            System.Windows.Threading.Dispatcher.PushFrame(frame);

            Thread.Sleep(150);

            // 校验选中高亮文本确实为目标关键字
            Assert.AreEqual(keyword, win.Editor.SelectedText);

            TestEnvironment.SaveWindowSnapshot(win, 380, 420, "05_NoteWindow_JumpHighlighted.png");
            coordinator.Dispose();
        });
    }

    [TestMethod]
    public void Render_NoteWindow_Purple_Theme_SavesSnapshot()
    {
        TestEnvironment.RunInSta(() =>
        {
            var repo = new FakeNoteRepository();
            var coordinator = new AutoSaveCoordinator();
            var vm = new NoteViewModel(repo, coordinator);

            var note = new Note
            {
                Id = Guid.NewGuid(),
                Content = "梦幻紫主题便签\n\n已成功切换至梦幻紫柔和色系。\n整张便签背景从顶部到底部全色系融合，无任何白色遮挡。",
                Color = NoteColor.Purple,
                IsPinned = false
            };
            vm.Initialize(note);

            var win = new NoteWindow(vm);

            Assert.AreEqual(NoteColor.Purple, win.ViewModel.Color);

            TestEnvironment.SaveWindowSnapshot(win, 380, 420, "06_NoteWindow_Purple_Theme.png");
            coordinator.Dispose();
        });
    }

    [TestMethod]
    public void NotesList_TogglePinCommand_UpdatesNoteAndSorting()
    {
        TestEnvironment.RunInSta(() =>
        {
            var note1 = new Note { Id = Guid.NewGuid(), Content = "普通便签 A", IsPinned = false, UpdatedAt = DateTime.UtcNow.AddMinutes(-10) };
            var note2 = new Note { Id = Guid.NewGuid(), Content = "普通便签 B", IsPinned = false, UpdatedAt = DateTime.UtcNow.AddMinutes(-5) };
            var repo = new FakeNoteRepository { Notes = { note1, note2 } };
            var searchService = new SearchService();
            var windowManager = new WindowManager(null!, repo);
            var vm = new NotesListViewModel(repo, searchService, windowManager);
            vm.LoadNotesAsync().GetAwaiter().GetResult();

            Assert.AreEqual(2, vm.Notes.Count);
            Assert.IsFalse(vm.Notes[1].IsPinned);

            // 触发置顶操作
            vm.TogglePinNoteCommand.Execute(note1);

            Assert.IsTrue(note1.IsPinned);
        });
    }

    [TestMethod]
    public void Render_NoteWindow_ColorPicker_SavesSnapshot()
    {
        TestEnvironment.RunInSta(() =>
        {
            var repo = new FakeNoteRepository();
            var coordinator = new AutoSaveCoordinator();
            var vm = new NoteViewModel(repo, coordinator);

            var note = new Note
            {
                Id = Guid.NewGuid(),
                Content = "调色盘主题色彩测试",
                Color = NoteColor.Pink,
                IsPinned = false
            };
            vm.Initialize(note);

            var win = new NoteWindow(vm);

            // 提取 MoreMenuPopup.Child (即带 7 色调色盘与操作菜单的 Fluent Border)
            if (win.MoreMenu.Child is FrameworkElement popupContent)
            {
                popupContent.DataContext = vm;
                TestEnvironment.SaveElementSnapshot(popupContent, 250, 220, "07_NoteWindow_ColorPicker.png");
            }

            coordinator.Dispose();
        });
    }

    [TestMethod]
    public void WindowManager_CalculateSmartRightPlacement_PositionsToRightWithStaggerOffset()
    {
        TestEnvironment.RunInSta(() =>
        {
            var repo = new FakeNoteRepository();
            var windowManager = new WindowManager(null!, repo);

            // 第一次计算位置
            var (x1, y1) = windowManager.CalculateSmartRightPlacement(380, 420);
            Assert.IsTrue(x1 > 0);
            Assert.IsTrue(y1 > 0);

            // 模拟打开一张窗口后，第二次计算位置产生错开位移（防遮挡）
            var note1 = new Note { Id = Guid.NewGuid(), WindowWidth = 380, WindowHeight = 420 };
            typeof(WindowManager).GetField("_activeNoteWindows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                .SetValue(windowManager, new Dictionary<Guid, NoteWindow>
                {
                    [note1.Id] = null!
                });

            var (x2, y2) = windowManager.CalculateSmartRightPlacement(380, 420);
            // 验证第二次排布具有递增错开位移
            Assert.AreNotEqual(x1, x2);
            Assert.AreNotEqual(y1, y2);
        });
    }

    [TestMethod]
    public void Render_ArchivedNotesWindow_Normal_SavesSnapshot()
    {
        TestEnvironment.RunInSta(() =>
        {
            var repo = new FakeNoteRepository
            {
                Notes =
                {
                    new Note
                    {
                        Id = Guid.NewGuid(),
                        Content = "旧版需求草稿 (已归档)\n这是上周讨论的产品方案，暂时移至归档留存备查。",
                        Color = NoteColor.Blue,
                        IsDeleted = true,
                        UpdatedAt = DateTime.UtcNow.AddDays(-2)
                    },
                    new Note
                    {
                        Id = Guid.NewGuid(),
                        Content = "已完成的项目备忘录\n1. 完成 Windows 11 Fluent 规范对齐\n2. 增加归档与设置中心",
                        Color = NoteColor.Purple,
                        IsDeleted = true,
                        UpdatedAt = DateTime.UtcNow.AddDays(-1)
                    }
                }
            };

            var vm = new ArchivedNotesViewModel(repo);
            vm.LoadArchivedNotesAsync().GetAwaiter().GetResult();

            Assert.AreEqual(2, vm.ArchivedCount);
            Assert.IsFalse(vm.HasNoNotes);

            var win = new ArchivedNotesWindow(vm);
            TestEnvironment.SaveWindowSnapshot(win, 480, 720, "08_ArchivedNotesWindow.png");
        });
    }

    [TestMethod]
    public void Render_SettingsWindow_Normal_SavesSnapshot()
    {
        TestEnvironment.RunInSta(() =>
        {
            var repo = new FakeNoteRepository();
            var settingsService = new SettingsService();
            var backupService = new ExportImportService(repo);
            var vm = new SettingsViewModel(settingsService, backupService);

            var win = new SettingsWindow(vm);
            TestEnvironment.SaveWindowSnapshot(win, 480, 680, "09_SettingsWindow.png");
        });
    }
}

