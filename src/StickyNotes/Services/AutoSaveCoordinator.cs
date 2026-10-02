using System.Collections.Concurrent;
using StickyNotes.Data;
using StickyNotes.Infrastructure;
using StickyNotes.Models;

namespace StickyNotes.Services;

/// <summary>
/// 防抖自动保存协调器：保证频繁输入不阻塞 IO，并在突发事件时立即同步 Flush 刷盘
/// </summary>
public sealed class AutoSaveCoordinator : IDisposable
{
    private readonly int _debounceMilliseconds;
    private readonly INoteRepository? _repository;
    private readonly ConcurrentDictionary<Guid, SaveTaskInfo> _pendingTasks = new();

    private sealed class SaveTaskInfo
    {
        public Note Note { get; set; } = null!;
        public Func<Note, Task> SaveAction { get; set; } = null!;
        public CancellationTokenSource Cts { get; set; } = null!;
    }

    public AutoSaveCoordinator(INoteRepository? repository = null, int debounceMilliseconds = 500)
    {
        _repository = repository;
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
                AppLog.Error($"[AutoSaveCoordinator] 自动保存失败: {ex.Message}", ex);
            }
        });
    }

    /// <summary>
    /// 取消并废弃指定便签的待执行保存（用于便签归档、彻底删除时）
    /// </summary>
    public void CancelPendingSave(Guid noteId)
    {
        if (_pendingTasks.TryRemove(noteId, out var info))
        {
            info.Cts.Cancel();
            info.Cts.Dispose();
        }
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

    /// <summary>
    /// 退出/关机时专用：直接持久化所有脏便签至底层存储，绕过 UI 消息总线，防止死锁
    /// </summary>
    public void FlushAllDirectToStorage()
    {
        var tasks = _pendingTasks.Values.ToList();
        _pendingTasks.Clear();

        foreach (var info in tasks)
        {
            try
            {
                info.Cts.Cancel();
                info.Cts.Dispose();

                if (_repository != null)
                {
                    _repository.SaveAsync(info.Note).ConfigureAwait(false).GetAwaiter().GetResult();
                }
                else
                {
                    info.SaveAction(info.Note).ConfigureAwait(false).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"[AutoSaveCoordinator] 退出时持久化便签 {info.Note.Id} 失败: {ex.Message}", ex);
            }
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
