using System.Globalization;
using Microsoft.Data.Sqlite;
using StickyNotes.Infrastructure;
using StickyNotes.Models;

namespace StickyNotes.Data;

/// <summary>
/// 基于原生 Microsoft.Data.Sqlite 的高性能便签仓储实现（支持 Schema V2 拆分置顶字段）
/// </summary>
public sealed class NoteRepository : INoteRepository
{
    private readonly SqliteDatabaseContext _context;

    public NoteRepository(SqliteDatabaseContext context)
    {
        _context = context;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // Microsoft.Data.Sqlite 连接串不支持同步级别，只能在建连后单独下发（缓存为 NORMAL，兼顾性能与抗突发中断）
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous = NORMAL;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    public async Task<IReadOnlyList<Note>> GetAllActiveAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<Note>();
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                Id, Content, Color, IsPinned, IsDeleted,
                WindowX, WindowY, WindowWidth, WindowHeight, IsOpen,
                CreatedAt, UpdatedAt,
                IsPinnedInList, AlwaysOnTop
            FROM Notes
            WHERE IsDeleted = 0
            ORDER BY IsPinnedInList DESC, UpdatedAt DESC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(ReadNote(reader));
        }

        return list;
    }

    public async Task<IReadOnlyList<Note>> GetAllArchivedAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<Note>();
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                Id, Content, Color, IsPinned, IsDeleted,
                WindowX, WindowY, WindowWidth, WindowHeight, IsOpen,
                CreatedAt, UpdatedAt,
                IsPinnedInList, AlwaysOnTop
            FROM Notes
            WHERE IsDeleted = 1
            ORDER BY UpdatedAt DESC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(ReadNote(reader));
        }

        return list;
    }

    public async Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<Note>();
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                Id, Content, Color, IsPinned, IsDeleted,
                WindowX, WindowY, WindowWidth, WindowHeight, IsOpen,
                CreatedAt, UpdatedAt,
                IsPinnedInList, AlwaysOnTop
            FROM Notes
            ORDER BY UpdatedAt DESC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(ReadNote(reader));
        }

        return list;
    }

    public async Task<Note?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                Id, Content, Color, IsPinned, IsDeleted,
                WindowX, WindowY, WindowWidth, WindowHeight, IsOpen,
                CreatedAt, UpdatedAt,
                IsPinnedInList, AlwaysOnTop
            FROM Notes
            WHERE Id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadNote(reader);
        }

        return null;
    }

    public async Task SaveAsync(Note note, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Notes (
                Id, Content, Color, IsPinned, IsPinnedInList, AlwaysOnTop, IsDeleted,
                WindowX, WindowY, WindowWidth, WindowHeight, IsOpen,
                CreatedAt, UpdatedAt
            ) VALUES (
                $id, $content, $color, $isPinned, $isPinnedInList, $alwaysOnTop, $isDeleted,
                $windowX, $windowY, $windowWidth, $windowHeight, $isOpen,
                $createdAt, $updatedAt
            )
            ON CONFLICT(Id) DO UPDATE SET
                Content = excluded.Content,
                Color = excluded.Color,
                IsPinned = excluded.IsPinned,
                IsPinnedInList = excluded.IsPinnedInList,
                AlwaysOnTop = excluded.AlwaysOnTop,
                IsDeleted = excluded.IsDeleted,
                WindowX = excluded.WindowX,
                WindowY = excluded.WindowY,
                WindowWidth = excluded.WindowWidth,
                WindowHeight = excluded.WindowHeight,
                IsOpen = excluded.IsOpen,
                UpdatedAt = excluded.UpdatedAt;
            """;

        BindNoteParameters(command, note);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveBatchAsync(IEnumerable<Note> notes, CancellationToken cancellationToken = default)
    {
        var noteList = notes.ToList();
        if (noteList.Count == 0) return;
        AppLog.Info($"[NoteRepository] 批量写入 {noteList.Count} 条便签（单事务）");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        const string sql = """
            INSERT INTO Notes (
                Id, Content, Color, IsPinned, IsPinnedInList, AlwaysOnTop, IsDeleted,
                WindowX, WindowY, WindowWidth, WindowHeight, IsOpen,
                CreatedAt, UpdatedAt
            ) VALUES (
                $id, $content, $color, $isPinned, $isPinnedInList, $alwaysOnTop, $isDeleted,
                $windowX, $windowY, $windowWidth, $windowHeight, $isOpen,
                $createdAt, $updatedAt
            )
            ON CONFLICT(Id) DO UPDATE SET
                Content = excluded.Content,
                Color = excluded.Color,
                IsPinned = excluded.IsPinned,
                IsPinnedInList = excluded.IsPinnedInList,
                AlwaysOnTop = excluded.AlwaysOnTop,
                IsDeleted = excluded.IsDeleted,
                WindowX = excluded.WindowX,
                WindowY = excluded.WindowY,
                WindowWidth = excluded.WindowWidth,
                WindowHeight = excluded.WindowHeight,
                IsOpen = excluded.IsOpen,
                UpdatedAt = excluded.UpdatedAt;
            """;

        foreach (var note in noteList)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)tx;
            command.CommandText = sql;

            BindNoteParameters(command, note);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await ArchiveNoteAsync(id, cancellationToken);
    }

    public async Task ArchiveNoteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Notes
            SET IsDeleted = 1, IsOpen = 0, UpdatedAt = $updatedAt
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        AppLog.Info($"[NoteRepository] 归档便签 {id}，受影响行数={affected}");
    }

    public async Task RestoreNoteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Notes
            SET IsDeleted = 0, UpdatedAt = $updatedAt
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        AppLog.Info($"[NoteRepository] 恢复便签 {id}，受影响行数={affected}");
    }

    public async Task HardDeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Notes WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        AppLog.Warn($"[NoteRepository] 彻底删除便签 {id}（不可撤销），受影响行数={affected}");
    }

    public async Task ClearAllArchivedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Notes WHERE IsDeleted = 1;";

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        AppLog.Warn($"[NoteRepository] 清空全部已归档便签（不可撤销），受影响行数={affected}");
    }

    public async Task UpdateWindowBoundsAsync(
        Guid id,
        double x,
        double y,
        double width,
        double height,
        bool isOpen,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Notes
            SET
                WindowX = $x,
                WindowY = $y,
                WindowWidth = $width,
                WindowHeight = $height,
                IsOpen = $isOpen
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$x", x);
        command.Parameters.AddWithValue("$y", y);
        command.Parameters.AddWithValue("$width", width);
        command.Parameters.AddWithValue("$height", height);
        command.Parameters.AddWithValue("$isOpen", isOpen ? 1 : 0);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 仅更新便签的窗口坐标与尺寸，不触碰 IsOpen（用于「仅桌面置顶便签记忆坐标」的单一职责写入口）。
    /// 与 UpdateWindowBoundsAsync 分离，避免坐标写入顺带改变打开状态。
    /// </summary>
    public async Task UpdateWindowPlacementAsync(
        Guid id,
        double x,
        double y,
        double width,
        double height,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Notes
            SET
                WindowX = $x,
                WindowY = $y,
                WindowWidth = $width,
                WindowHeight = $height
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$x", x);
        command.Parameters.AddWithValue("$y", y);
        command.Parameters.AddWithValue("$width", width);
        command.Parameters.AddWithValue("$height", height);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            // 目标行不存在通常意味着便签已被删除，需要留痕以便排查「坐标丢失」
            AppLog.Warn($"[NoteRepository] 保存窗口坐标未命中任何行，便签 {id} 可能已不存在");
        }
    }

    private static void BindNoteParameters(SqliteCommand command, Note note)
    {
        command.Parameters.AddWithValue("$id", note.Id.ToString());
        command.Parameters.AddWithValue("$content", note.Content);
        command.Parameters.AddWithValue("$color", (int)note.Color);
        command.Parameters.AddWithValue("$isPinned", note.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$isPinnedInList", note.IsPinnedInList ? 1 : 0);
        command.Parameters.AddWithValue("$alwaysOnTop", note.AlwaysOnTop ? 1 : 0);
        command.Parameters.AddWithValue("$isDeleted", note.IsDeleted ? 1 : 0);
        command.Parameters.AddWithValue("$windowX", note.WindowX);
        command.Parameters.AddWithValue("$windowY", note.WindowY);
        command.Parameters.AddWithValue("$windowWidth", note.WindowWidth);
        command.Parameters.AddWithValue("$windowHeight", note.WindowHeight);
        command.Parameters.AddWithValue("$isOpen", note.IsOpen ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", note.CreatedAt.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updatedAt", note.UpdatedAt.ToString("o", CultureInfo.InvariantCulture));
    }

    private static Note ReadNote(SqliteDataReader reader)
    {
        var note = new Note
        {
            Id = Guid.Parse(reader.GetString(0)),
            Content = reader.GetString(1),
            Color = (NoteColor)reader.GetInt32(2),
            IsDeleted = reader.GetInt32(4) == 1,
            WindowX = reader.GetDouble(5),
            WindowY = reader.GetDouble(6),
            WindowWidth = reader.GetDouble(7),
            WindowHeight = reader.GetDouble(8),
            IsOpen = reader.GetInt32(9) == 1,
            CreatedAt = DateTime.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            UpdatedAt = DateTime.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };

        if (reader.FieldCount >= 14)
        {
            note.IsPinnedInList = reader.GetInt32(12) == 1;
            note.AlwaysOnTop = reader.GetInt32(13) == 1;
        }
        else
        {
            bool isPinned = reader.GetInt32(3) == 1;
            note.IsPinnedInList = isPinned;
            note.AlwaysOnTop = isPinned;
        }

        return note;
    }
}
