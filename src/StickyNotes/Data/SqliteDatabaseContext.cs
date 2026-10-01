using Microsoft.Data.Sqlite;
using StickyNotes.Infrastructure;

namespace StickyNotes.Data;

/// <summary>
/// SQLite 数据库上下文与自适应迁移引擎
/// </summary>
public sealed class SqliteDatabaseContext
{
    private readonly string _connectionString;

    public SqliteDatabaseContext(string? dbPath = null)
    {
        var targetDb = dbPath ?? AppPaths.DatabasePath;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = targetDb,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _connectionString = builder.ToString();
    }

    /// <summary>
    /// 创建新的 SQLite 连接
    /// </summary>
    public SqliteConnection CreateConnection() => new(_connectionString);

    /// <summary>
    /// 初始化数据库模式并执行版本化迁移
    /// </summary>
    public async Task InitializeAndMigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        // 1. 开启 WAL 预写日志与同步模式，抗进程突发中断和损坏
        await using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            await pragmaCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 2. 检查当前数据库架构版本 (PRAGMA user_version)
        int currentVersion = 0;
        await using (var verCmd = connection.CreateCommand())
        {
            verCmd.CommandText = "PRAGMA user_version;";
            var res = await verCmd.ExecuteScalarAsync(cancellationToken);
            if (res != null && res != DBNull.Value)
            {
                currentVersion = Convert.ToInt32(res);
            }
        }

        // 3. 执行 V1 初始建表迁移
        if (currentVersion < 1)
        {
            await using var tx = connection.BeginTransaction();
            await using var migrateCmd = connection.CreateCommand();
            migrateCmd.Transaction = tx;
            migrateCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS Notes (
                    Id           TEXT PRIMARY KEY,
                    Content      TEXT NOT NULL DEFAULT '',
                    Color        INTEGER NOT NULL DEFAULT 0,
                    IsPinned     INTEGER NOT NULL DEFAULT 0,
                    IsDeleted    INTEGER NOT NULL DEFAULT 0,
                    WindowX      REAL NOT NULL DEFAULT 150,
                    WindowY      REAL NOT NULL DEFAULT 150,
                    WindowWidth  REAL NOT NULL DEFAULT 320,
                    WindowHeight REAL NOT NULL DEFAULT 360,
                    IsOpen       INTEGER NOT NULL DEFAULT 1,
                    CreatedAt    TEXT NOT NULL,
                    UpdatedAt    TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_Notes_IsDeleted ON Notes(IsDeleted);
                CREATE INDEX IF NOT EXISTS IX_Notes_UpdatedAt ON Notes(UpdatedAt DESC);
                CREATE INDEX IF NOT EXISTS IX_Notes_IsPinned ON Notes(IsPinned DESC);

                PRAGMA user_version = 1;
                """;
            await migrateCmd.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
    }
}
