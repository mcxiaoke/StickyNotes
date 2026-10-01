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

        // 恢复窗口上次保存的坐标与尺寸
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
    /// 获取当前正在运行的便签窗口字典
    /// </summary>
    public IReadOnlyDictionary<Guid, NoteWindow> ActiveWindows => _activeNoteWindows;
}
