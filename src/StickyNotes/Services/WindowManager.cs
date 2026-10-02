using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using StickyNotes.Data;
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

    public WindowManager(IServiceProvider serviceProvider, INoteRepository repository)
    {
        _serviceProvider = serviceProvider;
        _repository = repository;
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

        var vm = _serviceProvider.GetRequiredService<NoteViewModel>();
        vm.Initialize(note);

        var window = new NoteWindow(vm);

        // 如果坐标属于未初始化的默认值，或者与现有窗口完全重叠，计算主窗口右侧防遮挡错开坐标
        bool isDefaultOrUnset = (note.WindowX <= 0 || (Math.Abs(note.WindowX - 150) < 1 && Math.Abs(note.WindowY - 150) < 1));
        bool isOverlapping = _activeNoteWindows.Values.Any(w => Math.Abs(w.Left - note.WindowX) < 6 && Math.Abs(w.Top - note.WindowY) < 6);

        if (isDefaultOrUnset || isOverlapping)
        {
            var (newLeft, newTop) = CalculateSmartRightPlacement(note.WindowWidth, note.WindowHeight);
            note.WindowX = newLeft;
            note.WindowY = newTop;
        }
        else
        {
            // 屏幕工作区保护（防止拔掉显示器后窗口移出视野）
            var workArea = SystemParameters.WorkArea;
            if (note.WindowX + 50 > workArea.Right || note.WindowY + 50 > workArea.Bottom ||
                note.WindowX + note.WindowWidth < workArea.Left || note.WindowY < workArea.Top)
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WindowManager] 保存窗口状态失败: {ex.Message}");
            }
        };

        _activeNoteWindows[note.Id] = window;
        window.Show();
        onReady?.Invoke(window);
        return window;
    }

    /// <summary>
    /// 关闭指定便签窗口（用于删除便签时）
    /// </summary>
    public void CloseNoteWindow(Guid noteId)
    {
        if (_activeNoteWindows.TryGetValue(noteId, out var win))
        {
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
    /// 打开或激活独立设置窗口
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
    /// 获取当前正在运行的便签窗口字典
    /// </summary>
    public IReadOnlyDictionary<Guid, NoteWindow> ActiveWindows => _activeNoteWindows;

    /// <summary>
    /// 计算相对于主管理窗口右侧的错开定位坐标（防止多窗口完全重叠遮挡）
    /// </summary>
    public (double Left, double Top) CalculateSmartRightPlacement(double windowWidth, double windowHeight)
    {
        var workArea = SystemParameters.WorkArea;
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
        if (startX + windowWidth > workArea.Right)
        {
            startX = mainLeft - windowWidth - 14;
            if (startX < workArea.Left)
            {
                startX = Math.Max(workArea.Left + 10, workArea.Right - windowWidth - 20);
            }
        }

        // 错开层叠偏移算法（Staggered cascade offset，避免完全重叠遮挡）
        int staggerIndex = _activeNoteWindows.Count % 7;
        double offsetX = staggerIndex * 30;
        double offsetY = staggerIndex * 32;

        double targetX = startX + offsetX;
        double targetTop = mainTop + offsetY;

        // 屏幕边界保护
        if (targetX + windowWidth > workArea.Right)
            targetX = Math.Max(workArea.Left + 10, workArea.Right - windowWidth - 10);
        if (targetTop + windowHeight > workArea.Bottom)
            targetTop = Math.Max(workArea.Top + 10, workArea.Bottom - windowHeight - 20);

        return (targetX, targetTop);
    }
}
