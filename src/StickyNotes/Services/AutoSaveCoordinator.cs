using System.Collections.Concurrent;
using StickyNotes.Models;

namespace StickyNotes.Services;

/// <summary>
/// 防抖自动保存协调器：保证频繁输入不阻塞 IO，并在突发事件时立即同步 Flush 刷盘
/// </summary>
public sealed class AutoSaveCoordinator : IDisposable
{
    private readonly int _debounceMilliseconds;
    private readonly ConcurrentDictionary<Guid, SaveTaskInfo> _pendingTasks = new();

    private sealed class SaveTaskInfo
    {
        public Note Note { get; set; } = null!;
        public Func<Note, Task> SaveAction { get; set; } = null!;
        public CancellationTokenSource Cts { get; set; } = null!;
    }

    public AutoSaveCoordinator(int debounceMilliseconds = 500)
    {
        _debounceMilliseconds = debounceMilliseconds;
    }

    /// <summary>
    /// 触发防抖保存调度（在 500ms 内无新输入则执行落盘）
    /// </summary>
    public void ScheduleSave(Note note, Func<Note, Task> saveAction)
    {
        var noteId = note.Id;

        // 若已有未执行的延迟保存，取消旧计时器
        if (_pendingTasks.TryGetValue(noteId, out var existing))
        {
            existing.Cts.Cancel();
            existing.Cts.Dispose();
        }

        var cts = new CancellationTokenSource();
        var info = new SaveTaskInfo
        {
            Note = note,
            SaveAction = saveAction,
            Cts = cts
        };

        _pendingTasks[noteId] = info;

        // 启动后台延迟任务
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounceMilliseconds, cts.Token);
                if (!cts.IsCancellationRequested)
                {
                    if (_pendingTasks.TryRemove(noteId, out var taskInfo))
                    {
                        await taskInfo.SaveAction(taskInfo.Note);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常防抖取消
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AutoSaveCoordinator] 自动保存失败: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 强制立即执行指定便签的落盘保存（用于失焦、窗口关闭时）
    /// </summary>
    public async Task FlushAsync(Guid noteId)
    {
        if (_pendingTasks.TryRemove(noteId, out var info))
        {
            info.Cts.Cancel();
            try
            {
                await info.SaveAction(info.Note);
            }
            finally
            {
                info.Cts.Dispose();
            }
        }
    }

    /// <summary>
    /// 强制立即执行所有未保存的便签落盘（用于软件退出、系统关机时）
    /// </summary>
    public async Task FlushAllAsync()
    {
        var keys = _pendingTasks.Keys.ToList();
        foreach (var key in keys)
        {
            await FlushAsync(key);
        }
    }

    public void Dispose()
    {
        foreach (var info in _pendingTasks.Values)
        {
            info.Cts.Cancel();
            info.Cts.Dispose();
        }
        _pendingTasks.Clear();
    }
}
