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
    private readonly StickyNotes.Services.HardDeleteLedger? _hardDeleteLedger;

    public NoteRepository(SqliteDatabaseContext context, StickyNotes.Services.HardDeleteLedger? hardDeleteLedger = null)
    {
        _context = context;
        _hardDeleteLedger = hardDeleteLedger;
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
                UpdatedAt = excluded.UpdatedAt
            -- 条件守卫（P1-4）：仅当待写入版本比库内新时才覆盖。
            -- 本方法当前唯一调用方是 JSON 导入：裁决若只靠 Service 层「先读后写」，
            -- 读与写之间没有事务边界，防抖窗口内的用户编辑会被旧备份静默覆盖。
            WHERE excluded.UpdatedAt > Notes.UpdatedAt;
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

        // 记入硬删除台账（P2-1）：否则云端残留对象会在下轮同步被判为「远端新便签」回流复活
        if (affected > 0)
        {
            _hardDeleteLedger?.Record(new[] { id }, DateTime.UtcNow);
        }
    }

    public async Task ClearAllArchivedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        // 先取待删 id 再删除：清空归档同样要记台账（P2-1），否则全部墓碑都会回流
        var ids = new List<Guid>();
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.CommandText = "SELECT Id FROM Notes WHERE IsDeleted = 1;";
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Notes WHERE IsDeleted = 1;";

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        AppLog.Warn($"[NoteRepository] 清空全部已归档便签（不可撤销），受影响行数={affected}");

        if (affected > 0)
        {
            _hardDeleteLedger?.Record(ids, DateTime.UtcNow);
        }
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

    /// <summary>
    /// 应用远端同步批次（单事务，下行守卫）。
    /// 守卫语义：快照后本地又被编辑（UpdatedAt 变化）的行本轮跳过，下一轮对账重新裁决，
    /// 保证「对账读快照 → 下载解析 → 写库」窗口内的用户输入不被远端覆盖（协议设计 §4.1）。
    /// </summary>
    public async Task<ApplyRemoteResult> ApplyRemoteBatchAsync(IEnumerable<RemoteApplyItem> items, CancellationToken cancellationToken = default)
    {
        var list = items.ToList();
        if (list.Count == 0) return new ApplyRemoteResult(0, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        int applied = 0;
        int guarded = 0;

        try
        {
            foreach (var item in list)
            {
                var note = item.Note;
                var currentUpdatedAt = await TryGetUpdatedAtAsync(connection, tx, note.Id, cancellationToken).ConfigureAwait(false);

                if (currentUpdatedAt == null)
                {
                    // 本地无此行：按协议强制「几何默认落点 + IsOpen=false」插入（不自动弹窗），
                    // 不信任调用方携带的几何/打开状态（协议设计 §3.2）
                    var insertNote = new Note
                    {
                        Id = note.Id,
                        Content = note.Content,
                        Color = note.Color,
                        IsPinnedInList = note.IsPinnedInList,
                        AlwaysOnTop = note.AlwaysOnTop,
                        IsDeleted = note.IsDeleted,
                        IsOpen = false,
                        CreatedAt = note.CreatedAt,
                        UpdatedAt = note.UpdatedAt
                    };
                    await UpsertNoteCommandAsync(connection, tx, insertNote, cancellationToken).ConfigureAwait(false);
                    applied++;
                }
                else if (item.SnapshotUpdatedAt != null && currentUpdatedAt.Value.Equals(item.SnapshotUpdatedAt.Value))
                {
                    // 快照后未被本地编辑：覆盖业务字段，窗口几何与 IsOpen 保持本地现状
                    var command = connection.CreateCommand();
                    command.Transaction = (SqliteTransaction)tx;
                    command.CommandText = """
                        UPDATE Notes SET
                            Content = $content,
                            Color = $color,
                            IsPinned = $isPinned,
                            IsPinnedInList = $isPinnedInList,
                            AlwaysOnTop = $alwaysOnTop,
                            IsDeleted = $isDeleted,
                            UpdatedAt = $updatedAt
                        WHERE Id = $id;
                        """;
                    command.Parameters.AddWithValue("$content", note.Content);
                    command.Parameters.AddWithValue("$color", (int)note.Color);
                    command.Parameters.AddWithValue("$isPinned", note.IsPinnedInList ? 1 : 0);
                    command.Parameters.AddWithValue("$isPinnedInList", note.IsPinnedInList ? 1 : 0);
                    command.Parameters.AddWithValue("$alwaysOnTop", note.AlwaysOnTop ? 1 : 0);
                    command.Parameters.AddWithValue("$isDeleted", note.IsDeleted ? 1 : 0);
                    command.Parameters.AddWithValue("$updatedAt", SyncProtocolEnsureUtc(note.UpdatedAt).ToString("o", CultureInfo.InvariantCulture));
                    command.Parameters.AddWithValue("$id", note.Id.ToString());
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    applied++;
                }
                else
                {
                    guarded++;
                    AppLog.Info($"[NoteRepository] 下行守卫生效：便签 {note.Id} 在对账窗口内被本地编辑，本轮跳过远端覆盖");
                }
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        return new ApplyRemoteResult(applied, guarded);
    }

    /// <summary>读取行内 UpdatedAt（round-trip 解析）；行不存在返回 null</summary>
    private static async Task<DateTime?> TryGetUpdatedAtAsync(
        SqliteConnection connection, System.Data.Common.DbTransaction tx, Guid id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)tx;
        command.CommandText = "SELECT UpdatedAt FROM Notes WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result == null || result == DBNull.Value) return null;

        return DateTime.Parse((string)result, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    /// <summary>整行 Upsert（与 SaveBatchAsync 相同的 SQL，供插入远端新便签复用）</summary>
    private static async Task UpsertNoteCommandAsync(
        SqliteConnection connection, System.Data.Common.DbTransaction tx, Note note, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)tx;
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
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>确保 Kind 为 UTC（库内时间戳全链路 "o" + UTC，解析/比较前统一口径）</summary>
    private static DateTime SyncProtocolEnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

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

        // 四个查询均显式列出 IsPinnedInList/AlwaysOnTop（含由迁移补齐的旧库），
        // 因此此处直接读取第 13/14 列；原 `FieldCount >= 14` 分支恒为真，已删除。
        note.IsPinnedInList = reader.GetInt32(12) == 1;
        note.AlwaysOnTop = reader.GetInt32(13) == 1;

        return note;
    }
}
