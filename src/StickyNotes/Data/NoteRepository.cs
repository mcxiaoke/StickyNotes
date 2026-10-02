using System.Globalization;
using Microsoft.Data.Sqlite;
using StickyNotes.Models;

namespace StickyNotes.Data;

/// <summary>
/// 基于原生 Microsoft.Data.Sqlite 的高性能便签仓储实现
/// </summary>
public sealed class NoteRepository : INoteRepository
{
    private readonly SqliteDatabaseContext _context;

    public NoteRepository(SqliteDatabaseContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<Note>> GetAllActiveAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<Note>();
        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                Id, Content, Color, IsPinned, IsDeleted,
                WindowX, WindowY, WindowWidth, WindowHeight, IsOpen,
                CreatedAt, UpdatedAt
            FROM Notes
            WHERE IsDeleted = 0
            ORDER BY IsPinned DESC, UpdatedAt DESC;
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
        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                Id, Content, Color, IsPinned, IsDeleted,
                WindowX, WindowY, WindowWidth, WindowHeight, IsOpen,
                CreatedAt, UpdatedAt
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
        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                Id, Content, Color, IsPinned, IsDeleted,
                WindowX, WindowY, WindowWidth, WindowHeight, IsOpen,
                CreatedAt, UpdatedAt
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
    {
        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                Id, Content, Color, IsPinned, IsDeleted,
                WindowX, WindowY, WindowWidth, WindowHeight, IsOpen,
                CreatedAt, UpdatedAt
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
        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Notes (
                Id, Content, Color, IsPinned, IsDeleted,
                WindowX, WindowY, WindowWidth, WindowHeight, IsOpen,
                CreatedAt, UpdatedAt
            ) VALUES (
                $id, $content, $color, $isPinned, $isDeleted,
                $windowX, $windowY, $windowWidth, $windowHeight, $isOpen,
                $createdAt, $updatedAt
            )
            ON CONFLICT(Id) DO UPDATE SET
                Content = excluded.Content,
                Color = excluded.Color,
                IsPinned = excluded.IsPinned,
                IsDeleted = excluded.IsDeleted,
                WindowX = excluded.WindowX,
                WindowY = excluded.WindowY,
                WindowWidth = excluded.WindowWidth,
                WindowHeight = excluded.WindowHeight,
                IsOpen = excluded.IsOpen,
                UpdatedAt = excluded.UpdatedAt;
            """;

        command.Parameters.AddWithValue("$id", note.Id.ToString());
        command.Parameters.AddWithValue("$content", note.Content);
        command.Parameters.AddWithValue("$color", (int)note.Color);
        command.Parameters.AddWithValue("$isPinned", note.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$isDeleted", note.IsDeleted ? 1 : 0);
        command.Parameters.AddWithValue("$windowX", note.WindowX);
        command.Parameters.AddWithValue("$windowY", note.WindowY);
        command.Parameters.AddWithValue("$windowWidth", note.WindowWidth);
        command.Parameters.AddWithValue("$windowHeight", note.WindowHeight);
        command.Parameters.AddWithValue("$isOpen", note.IsOpen ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", note.CreatedAt.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updatedAt", note.UpdatedAt.ToString("o", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveBatchAsync(IEnumerable<Note> notes, CancellationToken cancellationToken = default)
    {
        var noteList = notes.ToList();
        if (noteList.Count == 0) return;

        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        const string sql = """
            INSERT INTO Notes (
                Id, Content, Color, IsPinned, IsDeleted,
                WindowX, WindowY, WindowWidth, WindowHeight, IsOpen,
                CreatedAt, UpdatedAt
            ) VALUES (
                $id, $content, $color, $isPinned, $isDeleted,
                $windowX, $windowY, $windowWidth, $windowHeight, $isOpen,
                $createdAt, $updatedAt
            )
            ON CONFLICT(Id) DO UPDATE SET
                Content = excluded.Content,
                Color = excluded.Color,
                IsPinned = excluded.IsPinned,
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

            command.Parameters.AddWithValue("$id", note.Id.ToString());
            command.Parameters.AddWithValue("$content", note.Content);
            command.Parameters.AddWithValue("$color", (int)note.Color);
            command.Parameters.AddWithValue("$isPinned", note.IsPinned ? 1 : 0);
            command.Parameters.AddWithValue("$isDeleted", note.IsDeleted ? 1 : 0);
            command.Parameters.AddWithValue("$windowX", note.WindowX);
            command.Parameters.AddWithValue("$windowY", note.WindowY);
            command.Parameters.AddWithValue("$windowWidth", note.WindowWidth);
            command.Parameters.AddWithValue("$windowHeight", note.WindowHeight);
            command.Parameters.AddWithValue("$isOpen", note.IsOpen ? 1 : 0);
            command.Parameters.AddWithValue("$createdAt", note.CreatedAt.ToString("o", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$updatedAt", note.UpdatedAt.ToString("o", CultureInfo.InvariantCulture));

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
        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Notes
            SET IsDeleted = 1, IsOpen = 0, UpdatedAt = $updatedAt
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RestoreNoteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Notes
            SET IsDeleted = 0, UpdatedAt = $updatedAt
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task HardDeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Notes WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearAllArchivedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Notes WHERE IsDeleted = 1;";

        await command.ExecuteNonQueryAsync(cancellationToken);
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
        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Notes
            SET
                WindowX = $x,
                WindowY = $y,
                WindowWidth = $width,
                WindowHeight = $height,
                IsOpen = $isOpen,
                UpdatedAt = $updatedAt
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$x", x);
        command.Parameters.AddWithValue("$y", y);
        command.Parameters.AddWithValue("$width", width);
        command.Parameters.AddWithValue("$height", height);
        command.Parameters.AddWithValue("$isOpen", isOpen ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Note ReadNote(SqliteDataReader reader)
    {
        return new Note
        {
            Id = Guid.Parse(reader.GetString(0)),
            Content = reader.GetString(1),
            Color = (NoteColor)reader.GetInt32(2),
            IsPinned = reader.GetInt32(3) == 1,
            IsDeleted = reader.GetInt32(4) == 1,
            WindowX = reader.GetDouble(5),
            WindowY = reader.GetDouble(6),
            WindowWidth = reader.GetDouble(7),
            WindowHeight = reader.GetDouble(8),
            IsOpen = reader.GetInt32(9) == 1,
            CreatedAt = DateTime.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            UpdatedAt = DateTime.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };
    }
}
