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
        IStorageBackend backend, string deviceId, bool enableEncryption = false, CancellationToken ct = default)
    {
        if (!await _singleFlight.WaitAsync(0, ct).ConfigureAwait(false))
        {
            AppLog.Info("[SyncEngine] 上一轮同步尚未结束，放弃本轮触发");
            return null;
        }

        try
        {
            return await RunCoreAsync(backend, deviceId, enableEncryption, ct).ConfigureAwait(false);
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    private async Task<SyncRoundSummary> RunCoreAsync(
        IStorageBackend backend, string deviceId, bool enableEncryption, CancellationToken ct)
    {
        // 0. 若启用加密，首先校验远端探针（.auth_verifier）
        if (enableEncryption)
        {
            var secret = VaultSecret.GetSecret();
            var verifierJson = await backend.GetTextAsync(SyncProtocol.VerifierKey, ct).ConfigureAwait(false);
            if (verifierJson == null)
            {
                var newVerifier = AuthVerifierDto.Create(secret);
                await backend.PutTextAsync(SyncProtocol.VerifierKey, newVerifier, ct).ConfigureAwait(false);
                AppLog.Info("[SyncEngine] 远端保险箱未发现校验探针，已自动初始化 .auth_verifier");
            }
            else if (!AuthVerifierDto.Verify(verifierJson, secret))
            {
                throw new InvalidOperationException("远端加密保险箱口令校验失败（.auth_verifier 无法解密或密钥不匹配），同步已安全中止。");
            }
            else
            {
                AppLog.Info("[SyncEngine] 远端加密保险箱校验探针（.auth_verifier）验证通过");
            }
        }

        // 1. 列出远端并全量下载解析（坏文件隔离，绝不中断整轮）
        var remoteItems = await backend.ListAsync(ct).ConfigureAwait(false);
        AppLog.Info($"[SyncEngine] 远端列表拉取完成，共 {remoteItems.Count} 个远端对象");
        var remote = new ConcurrentDictionary<Guid, SyncNoteDto>();
        // 读取失败/无效的远端对象集合（P0-1）：这些 id 本轮**不参与对账**——
        // 既不上传也不下载。若不隔离，「部分读取失败」会被对账误判为「远端不存在」，
        // 本地旧版本随即 PUT 上去静默覆盖云端更新数据，且后续每轮都因本地时间戳更大而无法纠正。
        var incompleteIds = new ConcurrentDictionary<Guid, byte>();
        int skippedInvalid = 0;
        int failedDownloads = 0;
        Exception? firstFailure = null;

        using (var gate = new SemaphoreSlim(DownloadConcurrency, DownloadConcurrency))
        {
            var tasks = remoteItems.Select(async item =>
            {
                if (string.Equals(item.Key, SyncProtocol.VerifierKey, StringComparison.OrdinalIgnoreCase))
                {
                    return; // 内部口令探针不是便签，静默忽略
                }

                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var text = await backend.GetTextAsync(item.Key, ct).ConfigureAwait(false);
                    if (text == null || text.Length > SyncProtocol.MaxNoteFileBytes)
                    {
                        Interlocked.Increment(ref skippedInvalid);
                        AppLog.Warn($"[SyncEngine] 跳过异常远端对象 {item.Key}（空/超过防呆上限）");
                        MarkIncomplete(incompleteIds, item.Key, Guid.Empty);
                        return;
                    }

                    var dto = SyncProtocol.TryDeserialize(text, item.Key);
                    if (dto == null)
                    {
                        Interlocked.Increment(ref skippedInvalid);
                        AppLog.Warn($"[SyncEngine] 跳过无法解析的远端对象 {item.Key}（坏 JSON/版本未知/id 不符）");
                        MarkIncomplete(incompleteIds, item.Key, Guid.Empty);
                        return;
                    }

                    if (dto.IsEncrypted)
                    {
                        try
                        {
                            var secret = VaultSecret.GetSecret();
                            dto.Content = CryptoHelper.UnwrapMagicPayload(dto.Iv!, dto.Payload!, secret);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref skippedInvalid);
                            AppLog.Warn($"[SyncEngine] 跳过解密失败或魔数不符的密文对象 {item.Key}: {ex.Message}");
                            MarkIncomplete(incompleteIds, item.Key, dto.Id);
                            return;
                        }
                    }

                    remote.TryAdd(dto.Id, dto);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failedDownloads);
                    Interlocked.CompareExchange(ref firstFailure, ex, null);
                    AppLog.Error($"[SyncEngine] 下载 {item.Key} 失败: {ex.Message}", ex);
                    MarkIncomplete(incompleteIds, item.Key, Guid.Empty);
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

        // 2. 全量对账（读取失败/无效的 id 已隔离，不进入差量推导）
        var localNotes = await _repository.GetAllAsync(ct).ConfigureAwait(false);
        var snapshot = localNotes.ToDictionary(n => n.Id);

        var downloads = new List<RemoteApplyItem>();
        var uploads = new List<Note>();

        foreach (var id in snapshot.Keys.Union(remote.Keys))
        {
            if (incompleteIds.ContainsKey(id))
            {
                // 该 id 本轮远端状态未知（下载失败/对象无效），跳过对账并在摘要中留痕，
                // 下一轮重新拉取后再裁决，绝不能把「读不到」当成「云端没有」
                continue;
            }

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

        AppLog.Info($"[SyncEngine] 全量对账完成: 本地便签={snapshot.Count}, 远端便签={remote.Count} => 待下行下载={downloads.Count}, 待上行上传={uploads.Count}");

        // 3. 下行应用（条件更新守卫，见 NoteRepository.ApplyRemoteBatchAsync）
        int applied = 0;
        int guardedSkipped = 0;
        if (downloads.Count > 0)
        {
            AppLog.Info($"[SyncEngine] 开始下行应用 {downloads.Count} 条便签...");
            var applyResult = await _repository.ApplyRemoteBatchAsync(downloads, ct).ConfigureAwait(false);
            applied = applyResult.Applied;
            guardedSkipped = applyResult.GuardedSkipped;
            if (guardedSkipped > 0)
            {
                AppLog.Info($"[SyncEngine] 下行守卫跳过 {guardedSkipped} 条（对账窗口内被本地编辑，下轮重新对账）");
            }
        }

        // 4. 上行（逐条容错，P2-4：单条失败不再终止整轮——已应用的下行与已成功上传的
        // 对象照常计入摘要，失败的 id 随摘要返回，下轮差量对账自动补传）
        int uploaded = 0;
        var uploadFailedIds = new List<Guid>();
        Exception? firstUploadFailure = null;
        if (uploads.Count > 0)
        {
            AppLog.Info($"[SyncEngine] 开始上行上传 {uploads.Count} 条便签（加密={enableEncryption}）...");
        }
        foreach (var note in uploads)
        {
            try
            {
                var dto = enableEncryption
                    ? SyncNoteDto.FromNoteEncrypted(note, deviceId, VaultSecret.GetSecret())
                    : SyncNoteDto.FromNote(note, deviceId);
                var json = SyncProtocol.Serialize(dto);
                await backend.PutTextAsync(SyncProtocol.NoteKey(note.Id), json, ct).ConfigureAwait(false);
                uploaded++;
            }
            catch (Exception ex)
            {
                uploadFailedIds.Add(note.Id);
                firstUploadFailure ??= ex;
                AppLog.Error($"[SyncEngine] 上传便签 {note.Id} 失败: {ex.Message}", ex);
            }
        }

        // 全部上传失败视为本轮失败（典型为认证/断网/配额耗尽），上行语义退化为整体失败；
        // 已应用的下行变更已落库不受影响，下一轮差量对账自动收敛
        if (uploads.Count > 0 && uploadFailedIds.Count == uploads.Count && firstUploadFailure != null)
        {
            throw new InvalidOperationException($"远端上传全部失败（共 {uploads.Count} 个对象），本轮中止", firstUploadFailure);
        }

        if (incompleteIds.IsEmpty)
        {
            AppLog.Info($"[SyncEngine] 本轮对账完整: 下行应用={downloads.Count}, 上行成功={uploaded}, 上行失败={uploadFailedIds.Count}");
        }
        else
        {
            AppLog.Warn($"[SyncEngine] 本轮存在 {incompleteIds.Count} 个远端对象未完成对账（下载失败/无效，已隔离跳过），下一轮重新裁决");
        }

        return new SyncRoundSummary(
            Listed: remoteItems.Count,
            Downloaded: applied,
            Uploaded: uploaded,
            SkippedInvalid: skippedInvalid,
            GuardedSkipped: guardedSkipped,
            AppliedIds: downloads.Select(d => d.Note.Id).ToList(),
            IncompleteCount: incompleteIds.Count,
            UploadFailedIds: uploadFailedIds);
    }

    /// <summary>
    /// 把读取失败/无效的远端对象 id 记入隔离集合（P0-1）。
    /// 优先用 DTO 携带的 id；DTO 不可得时从对象 key 解析（协议 key 固定为 notes/&lt;uuid&gt;.json）。
    /// </summary>
    private static void MarkIncomplete(ConcurrentDictionary<Guid, byte> incompleteIds, string key, Guid knownId)
    {
        var id = knownId != Guid.Empty
            ? knownId
            : SyncProtocol.TryGetNoteId(key, out var parsed) ? parsed : Guid.Empty;

        if (id != Guid.Empty)
        {
            incompleteIds.TryAdd(id, 0);
        }
    }

    /// <summary>远端 DTO → 本地实体：几何默认落点、IsOpen=false（不自动弹窗，协议设计 §3.2）</summary>
    private static Note ToNote(SyncNoteDto dto) => new()
    {
        Id = dto.Id,
        Content = SyncProtocol.NormalizeContent(dto.Content ?? string.Empty),
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
