using StickyNotes.Models;

namespace StickyNotes.Data;

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
    Task SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task ArchiveNoteAsync(Guid id, CancellationToken cancellationToken = default);
    Task RestoreNoteAsync(Guid id, CancellationToken cancellationToken = default);
    Task HardDeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task ClearAllArchivedAsync(CancellationToken cancellationToken = default);
    Task UpdateWindowBoundsAsync(Guid id, double x, double y, double width, double height, bool isOpen, CancellationToken cancellationToken = default);
}

