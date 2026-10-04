using StickyNotes.Models;

namespace StickyNotes.Data;

/// <summary>
/// 远端同步下行条目：<see cref="Note"/> 为远端最新状态，<see cref="SnapshotUpdatedAt"/>
/// 为对账快照时刻本地该便签的 UpdatedAt（下行守卫基准；null 表示本地无此行、直接插入）。
/// </summary>
public sealed record RemoteApplyItem(Note Note, DateTime? SnapshotUpdatedAt);

/// <summary>远端批次应用结果</summary>
public sealed record ApplyRemoteResult(int Applied, int GuardedSkipped);

/// <summary>
/// 便签持久化仓储契约
/// </summary>
public interface INoteRepository
{
    Task<IReadOnlyList<Note>> GetAllActiveAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Note>> GetAllArchivedAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Note?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(Note note, CancellationToken cancellationToken = default);
    Task SaveBatchAsync(IEnumerable<Note> notes, CancellationToken cancellationToken = default);
    Task SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task ArchiveNoteAsync(Guid id, CancellationToken cancellationToken = default);
    Task RestoreNoteAsync(Guid id, CancellationToken cancellationToken = default);
    Task HardDeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task ClearAllArchivedAsync(CancellationToken cancellationToken = default);
    Task UpdateWindowBoundsAsync(Guid id, double x, double y, double width, double height, bool isOpen, CancellationToken cancellationToken = default);

    /// <summary>
    /// 仅更新窗口坐标与尺寸，不触碰 IsOpen
    /// </summary>
    Task UpdateWindowPlacementAsync(Guid id, double x, double y, double width, double height, CancellationToken cancellationToken = default);

    /// <summary>
    /// 应用远端同步批次（单事务，下行守卫）：本地不存在 → 插入（几何默认落点、IsOpen=false）；
    /// 本地存在且 UpdatedAt 仍等于快照值 → 覆盖业务字段（窗口几何与 IsOpen 保持本地现状）；
    /// 快照后被本地编辑过（UpdatedAt 不匹配）→ 跳过并计入 GuardedSkipped，下一轮对账自动收敛。
    /// </summary>
    Task<ApplyRemoteResult> ApplyRemoteBatchAsync(IEnumerable<RemoteApplyItem> items, CancellationToken cancellationToken = default);
}
