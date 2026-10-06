using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using StickyNotes.Data;
using StickyNotes.Infrastructure;
using StickyNotes.Models;
using StickyNotes.ViewModels;
using StickyNotes.Views;

namespace StickyNotes.Services;

/// <summary>
/// 独立便签窗口单例与调度中心
/// </summary>
public sealed class WindowManager
{
    private readonly IServiceProvider _serviceProvider;
    private readonly INoteRepository _repository;
    private readonly Dictionary<Guid, NoteWindow> _activeNoteWindows = new();
    private ArchivedNotesWindow? _archivedNotesWindow;
    private SettingsWindow? _settingsWindow;
    private SyncSettingsWindow? _syncSettingsWindow;
    private bool _isShuttingDown;

    /// <summary>
    /// IsOpen/坐标写入串行闸：保证同一便签的「打开写 IsOpen=true」与「关闭写 IsOpen=false」
    /// 严格按时间先后落地。两条写入各自走独立数据库连接，若无此闸，快速打开后立即关闭时
    /// 打开写入可能后落地，把用户已关闭的便签在下次启动时错误复活（N-3）。
    /// </summary>
    private readonly SemaphoreSlim _placementWriteGate = new(1, 1);

    public WindowManager(IServiceProvider serviceProvider, INoteRepository repository)
    {
        _serviceProvider = serviceProvider;
        _repository = repository;
    }

    /// <summary>
    /// 打开或将已打开的便签贴纸窗口激活至最前
    /// </summary>
    /// <summary>
    /// 同步下行应用后，刷新已打开便签窗口的内存内容（协议设计 §4）。
    /// 仅刷新「无未落盘改动」的窗口（<see cref="NoteViewModel.HasPendingChanges"/>），
    /// 正在输入的窗口以本机在途编辑为准，不在此处强行覆盖。
    /// </summary>
    public async Task RefreshOpenNoteContentsAsync(IReadOnlyCollection<Guid> noteIds)
    {
        foreach (var noteId in noteIds)
        {
            if (!_activeNoteWindows.TryGetValue(noteId, out var window)) continue;
            if (window.DataContext is not NoteViewModel vm) continue;
            if (vm.HasPendingChanges) continue;

            var fresh = await _repository.GetByIdAsync(noteId).ConfigureAwait(true);
            if (fresh == null) continue;
            if (fresh.UpdatedAt <= vm.Note.UpdatedAt) continue;

            // Initialize 会抑制防抖保存等副作用，是安全的重灌路径
            vm.Initialize(fresh);
            AppLog.Info($"[WindowManager] 已按远端最新内容刷新打开中的便签 {noteId}");
        }
    }

    /// <summary>
    /// 打开或将已打开的便签贴纸窗口激活至最前
    /// </summary>
    public NoteWindow OpenOrActivateNote(Note note, Action<NoteWindow>? onReady = null)
    {
        if (_activeNoteWindows.TryGetValue(note.Id, out var existingWindow))
        {
            if (existingWindow.WindowState == WindowState.Minimized)
                existingWindow.WindowState = WindowState.Normal;

            existingWindow.Activate();
            onReady?.Invoke(existingWindow);
            return existingWindow;
        }

        // NoteViewModel 已注册为 Transient，必须经容器解析；
        // 原先的 `?? new NoteViewModel(...)` 兜底永不执行，却会在 DI 失败时静默造出
        // 不受容器管理、无人 Dispose 的实例（原 F-P2-12）。
        var vm = _serviceProvider.GetRequiredService<NoteViewModel>();
        vm.Initialize(note);

        var window = new NoteWindow(vm);

        // 如果坐标属于未初始化的默认值，或者与现有窗口完全重叠，计算主窗口右侧防遮挡错开坐标。
        // 虚拟桌面坐标允许为负（主屏左侧的副屏是合法位置），不能用 WindowX <= 0 判定未初始化，
        // 否则左屏便签每次打开都被强制搬回主屏并落库，布局永久丢失（P1-5）
        bool isDefaultOrUnset = double.IsNaN(note.WindowX) || double.IsNaN(note.WindowY)
            || (Math.Abs(note.WindowX - Note.DefaultWindowX) < 1 && Math.Abs(note.WindowY - Note.DefaultWindowY) < 1);
        bool isOverlapping = _activeNoteWindows.Values.Any(w => Math.Abs(w.Left - note.WindowX) < 6 && Math.Abs(w.Top - note.WindowY) < 6);

        if (isDefaultOrUnset || isOverlapping)
        {
            var (newLeft, newTop) = CalculateSmartRightPlacement(note.WindowWidth, note.WindowHeight);
            note.WindowX = newLeft;
            note.WindowY = newTop;
        }
        else
        {
            // 屏幕边界保护（防止拔掉显示器后窗口移出视野）。
            // 必须用**虚拟桌面**（全部显示器的并集）判断，不能用 SystemParameters.WorkArea ——
            // 那只是主显示器的工作区，副屏上的便签会被误判越界，在每次启动恢复/重开时
            // 被强制搬回主屏，丢失用户手工摆放的桌面布局（N-1）。
            double vLeft = SystemParameters.VirtualScreenLeft;
            double vTop = SystemParameters.VirtualScreenTop;
            double vRight = vLeft + SystemParameters.VirtualScreenWidth;
            double vBottom = vTop + SystemParameters.VirtualScreenHeight;

            if (note.WindowX + 50 > vRight || note.WindowY + 50 > vBottom ||
                note.WindowX + note.WindowWidth < vLeft || note.WindowY < vTop)
            {
                var (safeLeft, safeTop) = CalculateSmartRightPlacement(note.WindowWidth, note.WindowHeight);
                note.WindowX = safeLeft;
                note.WindowY = safeTop;
            }
        }

        // 应用窗口坐标与尺寸
        window.Left = note.WindowX;
        window.Top = note.WindowY;
        window.Width = note.WindowWidth;
        window.Height = note.WindowHeight;

        // 窗口关闭时更新尺寸并移出激活池
        window.Closed += async (_, _) =>
        {
            _activeNoteWindows.Remove(note.Id);

            // 当便签窗口关闭时，若主列表窗口可见，则主动通知其恢复最佳键盘焦点
            if (_notesListWindow != null && _notesListWindow.IsVisible)
            {
                _ = _notesListWindow.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
                {
                    if (_notesListWindow.IsVisible)
                    {
                        _notesListWindow.RestoreOptimalFocus();
                    }
                });
            }

            // 只有用户主动关闭才把 IsOpen 置为 false（IsOpen 的唯一写入点之一）。
            // 应用退出流程由 App.IsShuttingDown / _isShuttingDown 提前拦截，绝不在此回写，
            // 因为 WPF 的事件顺序是「全部窗口 Closed → App.OnExit」，退出时若在此写入 false，
            // 便签将在下次启动时全部消失（原 F-P0-1）。
            if (App.IsShuttingDown || _isShuttingDown)
            {
                AppLog.Info($"[WindowManager] 便签 {note.Id} 窗口随应用退出关闭，保持 IsOpen 不变");
                return;
            }

            // 内存实例与库同步回写：列表与便签窗口共享同一 Note 实例，若只写库不清内存标记，
            // 列表侧随后的全行 Upsert（置顶/换色等）会把陈旧的 IsOpen=true 带回数据库，
            // 下次启动时用户已关闭的便签被错误复活（P1-1）
            note.IsOpen = false;

            try
            {
                // 与「打开写 IsOpen=true」共用写闸（N-3），保证关闭写入不会越过仍的在途打开写入
                await _placementWriteGate.WaitAsync();
                try
                {
                    await _repository.UpdateWindowBoundsAsync(
                        note.Id,
                        window.Left,
                        window.Top,
                        window.ActualWidth > 0 ? window.ActualWidth : window.Width,
                        window.ActualHeight > 0 ? window.ActualHeight : window.Height,
                        isOpen: false
                    );
                }
                finally
                {
                    _placementWriteGate.Release();
                }

                AppLog.Info($"[WindowManager] 用户关闭便签 {note.Id}，已写入 IsOpen=false");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[WindowManager] 保存窗口状态失败: {ex.Message}", ex);
            }
        };

        _activeNoteWindows[note.Id] = window;

        // IsOpen 的唯一写入入口之一：窗口创建（打开）时写 true。
        // 与「用户主动关闭写 false」配对，彻底摆脱对退出事件顺序的依赖（原 F-P0-1 的根因）。
        if (!note.IsOpen)
        {
            note.IsOpen = true;
        }

        window.Show();

        // 打开即落库，确保异常退出（崩溃/断电）后重启仍能恢复这张贴纸
        _ = PersistIsOpenOnOpenAsync(note);

        onReady?.Invoke(window);
        return window;
    }

    /// <summary>
    /// 便签窗口打开时写入 IsOpen = true（不阻塞 UI，失败仅记日志）。
    /// 与「用户主动关闭写 false」共用 <see cref="_placementWriteGate"/> 串行闸（N-3）；
    /// 若窗口在写入生效前已被用户关闭（不在激活池），则放弃本次写入，把最终状态让给关闭写入。
    /// </summary>
    private async Task PersistIsOpenOnOpenAsync(Note note)
    {
        await _placementWriteGate.WaitAsync();
        try
        {
            if (!_activeNoteWindows.ContainsKey(note.Id))
            {
                return;
            }

            await _repository.UpdateWindowBoundsAsync(
                note.Id,
                note.WindowX,
                note.WindowY,
                note.WindowWidth,
                note.WindowHeight,
                isOpen: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[WindowManager] 打开便签 {note.Id} 时写入 IsOpen=true 失败: {ex.Message}", ex);
        }
        finally
        {
            _placementWriteGate.Release();
        }
    }

    /// <summary>
    /// 声明应用进入退出流程：此后窗口 Closed 回调不再回写 IsOpen=false。
    /// 与 App.BeginShutdown 同步语义，便于在退出发起时立即生效（而非等到 OnExit —— 那时窗口已全部关闭）。
    /// 注意：调用本方法后窗口字典可能在随后的窗口关闭中被清空，
    /// 因此若还需持久化坐标，请优先使用 <see cref="BeginShutdownAndPersistPinnedPlacement"/>。
    /// </summary>
    public void BeginShutdown()
    {
        _isShuttingDown = true;
        AppLog.Info("[WindowManager] 已标记退出流程，后续窗口关闭不再回写 IsOpen=false");
    }

    /// <summary>
    /// 原子化的退出前处理：先持久化「桌面置顶」便签坐标（此刻窗口字典仍完整），
    /// 再标记退出使后续窗口关闭跳过 IsOpen=false 回写。
    /// 顺序不可颠倒 —— WPF 的 Shutdown() 会先关闭全部窗口并触发 Closed 回调，
    /// 之后才轮到 App.OnExit；若在 OnExit 才保存坐标，字典已空、坐标永久丢失（F-P0-1 的完整根因）。
    /// 本方法幂等，可重复调用。
    /// </summary>
    public void BeginShutdownAndPersistPinnedPlacement()
    {
        if (!_isShuttingDown)
        {
            PersistPinnedWindowsPlacementOnExit();
            BeginShutdown();
        }
    }

    /// <summary>
    /// 应用退出时持久化「仅桌面置顶便签」的精确坐标与尺寸。
    /// 设计说明（对齐 F-P0-1 的用户决策）：
    ///  - 只有 AlwaysOnTop（便签窗口右上角图钉 / Ctrl+P）的便签才需要记忆窗口位置；
    ///  - 其余便签下次启动仍会重新打开（IsOpen 由创建/关闭两个入口维护），但走层叠自动排布，不再记忆精确坐标；
    ///  - 本方法**不再写 IsOpen**，从根上消除「退出时补写」与 WPF 事件顺序（全部窗口 Closed → App.OnExit）的耦合。
    /// </summary>
    public void PersistPinnedWindowsPlacementOnExit()
    {
        _isShuttingDown = true;

        // 先做快照：窗口 Closed 回调可能在遍历期间修改字典
        var snapshot = _activeNoteWindows.ToList();
        int persisted = 0;

        foreach (var (id, win) in snapshot)
        {
            try
            {
                var note = win.ViewModel.Note;
                if (!note.AlwaysOnTop) continue;

                _repository.UpdateWindowPlacementAsync(
                    id,
                    win.Left,
                    win.Top,
                    win.ActualWidth > 0 ? win.ActualWidth : win.Width,
                    win.ActualHeight > 0 ? win.ActualHeight : win.Height
                ).ConfigureAwait(false).GetAwaiter().GetResult();

                persisted++;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[WindowManager] 退出持久化置顶便签窗口 {id} 坐标失败: {ex.Message}", ex);
            }
        }

        AppLog.Info($"[WindowManager] 退出流程完成：活动窗口 {snapshot.Count} 张，其中桌面置顶并已记忆坐标 {persisted} 张");
    }

    private NotesListWindow? _notesListWindow;

    /// <summary>
    /// 打开或激活便签主列表窗口（无论最小化、隐藏在托盘还是尚未显示，均可平滑唤醒并置前）
    /// </summary>
    public NotesListWindow? OpenOrActivateNotesListWindow()
    {
        if (_notesListWindow != null && _notesListWindow.IsLoaded)
        {
            if (_notesListWindow.WindowState == WindowState.Minimized)
                _notesListWindow.WindowState = WindowState.Normal;
            _notesListWindow.Show();
            _notesListWindow.Activate();
            _notesListWindow.Focus();

            var hwnd = new System.Windows.Interop.WindowInteropHelper(_notesListWindow).Handle;
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(hwnd);
            }
            return _notesListWindow;
        }

        if (_serviceProvider != null)
        {
            _notesListWindow = _serviceProvider.GetService<NotesListWindow>();
            if (_notesListWindow != null)
            {
                _notesListWindow.Closed += (_, _) => _notesListWindow = null;
                if (_notesListWindow.WindowState == WindowState.Minimized)
                    _notesListWindow.WindowState = WindowState.Normal;
                _notesListWindow.Show();
                _notesListWindow.Activate();
                _notesListWindow.Focus();

                var hwnd = new System.Windows.Interop.WindowInteropHelper(_notesListWindow).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(hwnd);
                }
                return _notesListWindow;
            }
        }

        var app = Application.Current;
        if (app != null)
        {
            foreach (Window win in app.Windows)
            {
                if (win is NotesListWindow nlw)
                {
                    _notesListWindow = nlw;
                    if (nlw.WindowState == WindowState.Minimized)
                        nlw.WindowState = WindowState.Normal;
                    nlw.Show();
                    nlw.Activate();
                    nlw.Focus();

                    var hwnd = new System.Windows.Interop.WindowInteropHelper(nlw).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                        NativeMethods.SetForegroundWindow(hwnd);
                    }
                    return nlw;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 关闭指定便签窗口（用于删除便签时）
    /// </summary>
    public void CloseNoteWindow(Guid noteId)
    {
        if (_activeNoteWindows.TryGetValue(noteId, out var win))
        {
            win.ViewModel.Note.IsDeleted = true;
            win.ViewModel.Note.IsOpen = false;
            win.Close();
        }
    }

    /// <summary>
    /// 根据搜索命中项，打开对应便签并触发精准选中与视口居中
    /// </summary>
    public void NavigateToHit(SearchHit hit)
    {
        var note = _repository.GetByIdAsync(hit.NoteId).GetAwaiter().GetResult();
        if (note == null) return;

        OpenOrActivateNote(note, win =>
        {
            win.JumpToSearchHit(hit.CharIndex, hit.Length);
        });
    }

    /// <summary>
    /// 打开或激活已归档管理窗口
    /// </summary>
    public ArchivedNotesWindow OpenOrActivateArchivedNotesWindow()
    {
        if (_archivedNotesWindow != null && _archivedNotesWindow.IsLoaded)
        {
            if (_archivedNotesWindow.WindowState == WindowState.Minimized)
                _archivedNotesWindow.WindowState = WindowState.Normal;
            _archivedNotesWindow.Activate();
            return _archivedNotesWindow;
        }

        _archivedNotesWindow = _serviceProvider.GetRequiredService<ArchivedNotesWindow>();
        _archivedNotesWindow.Closed += (_, _) => _archivedNotesWindow = null;
        _archivedNotesWindow.Show();
        _archivedNotesWindow.Activate();
        return _archivedNotesWindow;
    }

    /// <summary>
    /// 打开或激活设置窗口
    /// </summary>
    public SettingsWindow OpenOrActivateSettingsWindow()
    {
        if (_settingsWindow != null && _settingsWindow.IsLoaded)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return _settingsWindow;
        }

        _settingsWindow = _serviceProvider.GetRequiredService<SettingsWindow>();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
        return _settingsWindow;
    }

    /// <summary>
    /// 打开或激活网络同步设置窗口（独立于主设置窗口，避免设置页过长）
    /// </summary>
    public SyncSettingsWindow OpenOrActivateSyncSettingsWindow()
    {
        if (_syncSettingsWindow != null && _syncSettingsWindow.IsLoaded)
        {
            if (_syncSettingsWindow.WindowState == WindowState.Minimized)
                _syncSettingsWindow.WindowState = WindowState.Normal;
            _syncSettingsWindow.Activate();
            return _syncSettingsWindow;
        }

        _syncSettingsWindow = _serviceProvider.GetRequiredService<SyncSettingsWindow>();
        _syncSettingsWindow.Closed += (_, _) => _syncSettingsWindow = null;
        _syncSettingsWindow.Show();
        _syncSettingsWindow.Activate();
        return _syncSettingsWindow;
    }

    /// <summary>
    /// 获取当前正在运行的便签窗口字典
    /// </summary>
    public IReadOnlyDictionary<Guid, NoteWindow> ActiveWindows => _activeNoteWindows;

    /// <summary>
    /// 计算相对于主管理窗口右侧的错开定位坐标（防止多窗口完全重叠遮挡）
    /// </summary>
    public (double Left, double Top) CalculateSmartRightPlacement(double windowWidth, double windowHeight)
    {
        // 边界约束使用虚拟桌面（全部显示器并集）：主窗口本身可能就在副屏，
        // 若按主屏 WorkArea 收敛，跟随主窗口的新便签会被拽回主屏（N-1）。
        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;
        double mainLeft = 100;
        double mainTop = 100;
        double mainWidth = 480;

        var mainWindow = Application.Current?.MainWindow;
        if (mainWindow != null && mainWindow.IsVisible && mainWindow.WindowState != WindowState.Minimized)
        {
            mainLeft = mainWindow.Left;
            mainTop = mainWindow.Top;
            mainWidth = mainWindow.ActualWidth > 0 ? mainWindow.ActualWidth : mainWindow.Width;
        }

        // 默认放置在主窗口右侧，留 14px 间隙
        double startX = mainLeft + mainWidth + 14;
        // 如果右侧屏幕放不下，则自适应尝试主窗口左侧或靠屏幕右边缘
        if (startX + windowWidth > vRight)
        {
            startX = mainLeft - windowWidth - 14;
            if (startX < vLeft)
            {
                startX = Math.Max(vLeft + 10, vRight - windowWidth - 20);
            }
        }

        // 错开层叠偏移算法（Staggered cascade offset，避免完全重叠遮挡）
        int staggerIndex = _activeNoteWindows.Count % 7;
        double offsetX = staggerIndex * 30;
        double offsetY = staggerIndex * 32;

        double targetX = startX + offsetX;
        double targetTop = mainTop + offsetY;

        // 屏幕边界保护
        if (targetX + windowWidth > vRight)
            targetX = Math.Max(vLeft + 10, vRight - windowWidth - 10);
        if (targetTop + windowHeight > vBottom)
            targetTop = Math.Max(vTop + 10, vBottom - windowHeight - 20);

        return (targetX, targetTop);
    }
}
