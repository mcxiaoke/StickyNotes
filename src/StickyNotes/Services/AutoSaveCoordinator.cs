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
        var info = new SaveTaskInfo
        {
            Note = note,
            SaveAction = saveAction,
            Cts = new CancellationTokenSource()
        };

        // 先登记新调度、再取消旧调度：新调度一旦入字典即为权威，旧任务随后被取消，
        // 不会再进入落盘分支（它退出前会比对字典，发现已不是自己）。
        var previous = _pendingTasks.TryGetValue(noteId, out var existingBefore) ? existingBefore : null;
        _pendingTasks[noteId] = info;
        if (previous != null && !ReferenceEquals(previous, info))
        {
            // 只 Cancel、绝不 Dispose：该 CTS 的令牌此刻仍被旧延迟任务的 Task.Delay 注册，
            // Dispose 会与之竞态（旧代码原 F-P1-7）。释放权归旧的持有任务，在其 finally 中执行。
            CancelOnly(previous.Cts);
        }

        // 启动后台延迟任务（本任务是 cts 的持有者，唯一负责释放它）
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounceMilliseconds, info.Cts.Token);
                if (info.Cts.IsCancellationRequested)
                {
                    return;
                }

                // 仅当字典里仍是自己的调度时才落盘，防止把后来的新调度误当成自己的
                if (_pendingTasks.TryRemove(new KeyValuePair<Guid, SaveTaskInfo>(noteId, info)))
                {
                    await info.SaveAction(info.Note);
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
            finally
            {
                // 此刻 Task.Delay 已返回/已取消，令牌不再被使用，释放是安全的
                DisposeQuietly(info.Cts);
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
            CancelOnly(info.Cts);
        }
    }

    /// <summary>
    /// 强制立即执行指定便签的落盘保存（用于失焦、窗口关闭时）。
    /// 返回是否确实执行了一次保存（存在待保存调度并已完成）；false 表示无待保存调度。
    /// </summary>
    public async Task<bool> FlushAsync(Guid noteId)
    {
        if (_pendingTasks.TryRemove(noteId, out var info))
        {
            // 只取消、不释放：持有该 CTS 的延迟任务会在自身 finally 中释放
            CancelOnly(info.Cts);
            await info.SaveAction(info.Note);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 取消令牌，绝不在此处释放 —— 令牌可能仍被延迟任务使用（原 F-P1-7 的核心约束）。
    /// </summary>
    private static void CancelOnly(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已被持有任务释放（极端并发），无需处理
        }
    }

    private static void DisposeQuietly(CancellationTokenSource cts)
    {
        try
        {
            cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
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
                // 只取消；释放权归持有该 CTS 的延迟任务（原 F-P1-7）
                CancelOnly(info.Cts);

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
        // 只取消，不释放：每个 CTS 由持有它的延迟任务在 finally 中释放。
        // 在此 Dispose 会让仍处于 Task.Delay 的令牌遭遇 ObjectDisposedException（原 F-P1-7）。
        foreach (var info in _pendingTasks.Values)
        {
            CancelOnly(info.Cts);
        }
        _pendingTasks.Clear();
    }
}
