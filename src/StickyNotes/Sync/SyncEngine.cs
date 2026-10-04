using System.Collections.Concurrent;
using StickyNotes.Data;
using StickyNotes.Infrastructure;
using StickyNotes.Models;

namespace StickyNotes.Sync;

/// <summary>
/// 同步引擎：无状态全量对账 + 时间戳 LWW + 墓碑 + 下行条件更新守卫
/// （协议与算法见 docs/SYNC-PROTOCOL-DESIGN-20261004.md §4）。
/// <para>
/// 每轮从「本地库 + 远端列表」两个真值现场重新推导差量，不维护任何同步游标，
/// 任意一轮中断，下一轮自动收敛。引擎自身通过单飞信号量保证同一时刻只有一轮在跑。
/// </para>
/// </summary>
public sealed class SyncEngine
{
    /// <summary>下行 GET 并发数（协议设计 §4.1）</summary>
    private const int DownloadConcurrency = 6;

    private readonly INoteRepository _repository;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);

    public SyncEngine(INoteRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// 执行一轮同步。返回 null 表示上一轮尚未结束（单飞直接放弃本轮，不排队）。
    /// 上传失败会抛出异常（本轮已应用的下行依然有效，下一轮自动补传差量）。
    /// </summary>
    public async Task<SyncRoundSummary?> RunAsync(
        IStorageBackend backend, string deviceId, CancellationToken ct = default)
    {
        if (!await _singleFlight.WaitAsync(0, ct).ConfigureAwait(false))
        {
            AppLog.Info("[SyncEngine] 上一轮同步尚未结束，放弃本轮触发");
            return null;
        }

        try
        {
            return await RunCoreAsync(backend, deviceId, ct).ConfigureAwait(false);
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    private async Task<SyncRoundSummary> RunCoreAsync(
        IStorageBackend backend, string deviceId, CancellationToken ct)
    {
        // 1. 列出远端并全量下载解析（坏文件隔离，绝不中断整轮）
        var remoteItems = await backend.ListAsync(ct).ConfigureAwait(false);
        var remote = new ConcurrentDictionary<Guid, SyncNoteDto>();
        int skippedInvalid = 0;
        int failedDownloads = 0;
        Exception? firstFailure = null;

        using (var gate = new SemaphoreSlim(DownloadConcurrency, DownloadConcurrency))
        {
            var tasks = remoteItems.Select(async item =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var text = await backend.GetTextAsync(item.Key, ct).ConfigureAwait(false);
                    if (text == null || text.Length > SyncProtocol.MaxNoteFileBytes)
                    {
                        Interlocked.Increment(ref skippedInvalid);
                        AppLog.Warn($"[SyncEngine] 跳过异常远端对象 {item.Key}（空/超过防呆上限）");
                        return;
                    }

                    var dto = SyncProtocol.TryDeserialize(text, item.Key);
                    if (dto == null)
                    {
                        Interlocked.Increment(ref skippedInvalid);
                        AppLog.Warn($"[SyncEngine] 跳过无法解析的远端对象 {item.Key}（坏 JSON/版本未知/id 不符）");
                        return;
                    }

                    remote.TryAdd(dto.Id, dto);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failedDownloads);
                    Interlocked.CompareExchange(ref firstFailure, ex, null);
                    AppLog.Error($"[SyncEngine] 下载 {item.Key} 失败: {ex.Message}", ex);
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        // 全部下载失败视为本轮失败（典型为认证/断网），不能当作「远端为空」处理，
        // 否则本地会把远端静默清空重建——那正是要避免的误覆盖
        if (remoteItems.Count > 0 && failedDownloads == remoteItems.Count && firstFailure != null)
        {
            throw new InvalidOperationException($"远端下载全部失败（共 {remoteItems.Count} 个对象），本轮中止", firstFailure);
        }

        // 2. 全量对账
        var localNotes = await _repository.GetAllAsync(ct).ConfigureAwait(false);
        var snapshot = localNotes.ToDictionary(n => n.Id);

        var downloads = new List<RemoteApplyItem>();
        var uploads = new List<Note>();

        foreach (var id in snapshot.Keys.Union(remote.Keys))
        {
            snapshot.TryGetValue(id, out var localNote);
            remote.TryGetValue(id, out var remoteDto);

            if (localNote == null && remoteDto != null)
            {
                // 远端新便签（含其他设备的墓碑，回流入库为不可见行）
                downloads.Add(new RemoteApplyItem(ToNote(remoteDto), null));
            }
            else if (localNote != null && remoteDto == null)
            {
                // 本地新便签（含尚未上传过的墓碑）
                uploads.Add(localNote);
            }
            else if (localNote != null && remoteDto != null)
            {
                // 两边都有：updatedAt 大者胜；相等本地胜（与导入服务规则一致，确定性）
                if (localNote.UpdatedAt >= remoteDto.UpdatedAt)
                {
                    var localDto = SyncNoteDto.FromNote(localNote, deviceId);
                    if (!localDto.BusinessEquals(remoteDto))
                    {
                        uploads.Add(localNote);
                    }
                }
                else
                {
                    if (!SyncNoteDto.FromNote(localNote, deviceId).BusinessEquals(remoteDto))
                    {
                        downloads.Add(new RemoteApplyItem(ToNote(remoteDto), localNote.UpdatedAt));
                    }
                }
            }
        }

        // 3. 下行应用（条件更新守卫，见 NoteRepository.ApplyRemoteBatchAsync）
        int applied = 0;
        int guardedSkipped = 0;
        if (downloads.Count > 0)
        {
            var applyResult = await _repository.ApplyRemoteBatchAsync(downloads, ct).ConfigureAwait(false);
            applied = applyResult.Applied;
            guardedSkipped = applyResult.GuardedSkipped;
            if (guardedSkipped > 0)
            {
                AppLog.Info($"[SyncEngine] 下行守卫跳过 {guardedSkipped} 条（对账窗口内被本地编辑，下轮重新对账）");
            }
        }

        // 4. 上行（失败即整轮终止；已上传文件有效，下轮按差量续传）
        int uploaded = 0;
        foreach (var note in uploads)
        {
            var json = SyncProtocol.Serialize(SyncNoteDto.FromNote(note, deviceId));
            await backend.PutTextAsync(SyncProtocol.NoteKey(note.Id), json, ct).ConfigureAwait(false);
            uploaded++;
        }

        return new SyncRoundSummary(
            Listed: remoteItems.Count,
            Downloaded: applied,
            Uploaded: uploaded,
            SkippedInvalid: skippedInvalid,
            GuardedSkipped: guardedSkipped,
            AppliedIds: downloads.Select(d => d.Note.Id).ToList());
    }

    /// <summary>远端 DTO → 本地实体：几何默认落点、IsOpen=false（不自动弹窗，协议设计 §3.2）</summary>
    private static Note ToNote(SyncNoteDto dto) => new()
    {
        Id = dto.Id,
        Content = SyncProtocol.NormalizeContent(dto.Content),
        Color = dto.Color,
        IsPinnedInList = dto.IsPinnedInList,
        AlwaysOnTop = dto.AlwaysOnTop,
        IsDeleted = dto.IsDeleted,
        WindowX = Note.DefaultWindowX,
        WindowY = Note.DefaultWindowY,
        WindowWidth = Note.DefaultWindowWidth,
        WindowHeight = Note.DefaultWindowHeight,
        IsOpen = false,
        CreatedAt = SyncProtocol.EnsureUtc(dto.CreatedAt),
        UpdatedAt = SyncProtocol.EnsureUtc(dto.UpdatedAt)
    };
}
