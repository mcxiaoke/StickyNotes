using Microsoft.Data.Sqlite;
using StickyNotes.Infrastructure;

namespace StickyNotes.Data;

/// <summary>
/// SQLite 数据库上下文与自适应增量迁移引擎
/// </summary>
public sealed class SqliteDatabaseContext
{
    public const int TargetSchemaVersion = 2;
    private readonly string _connectionString;

    public SqliteDatabaseContext(string? dbPath = null)
    {
        var targetDb = dbPath ?? AppPaths.DatabasePath;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = targetDb,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            DefaultTimeout = 5
        };
        _connectionString = builder.ToString();
    }

    /// <summary>
    /// 创建新的 SQLite 连接（已配置 ForeignKeys 与 DefaultTimeout）
    /// </summary>
    public SqliteConnection CreateConnection() => new(_connectionString);

    /// <summary>
    /// 初始化数据库模式并执行版本化增量迁移 (PRAGMA user_version)
    /// </summary>
    public async Task InitializeAndMigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        // 1. 开启 WAL 预写日志与 NORMAL 同步模式（抗进程突发中断，减少写磨损）
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

        if (currentVersion > TargetSchemaVersion)
        {
            AppLog.Warn($"[SqliteDatabaseContext] 检测到数据库架构版本 (v{currentVersion}) 高于应用程序当前支持的版本 (v{TargetSchemaVersion})");
            return;
        }

        // 3. 执行 V1 初始建表迁移
        if (currentVersion < 1)
        {
            await using var tx = connection.BeginTransaction();
            await using var migrateCmd = connection.CreateCommand();
            migrateCmd.Transaction = tx;
            migrateCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS Notes (
                    Id             TEXT PRIMARY KEY,
                    Content        TEXT NOT NULL DEFAULT '',
                    Color          INTEGER NOT NULL DEFAULT 0,
                    IsPinned       INTEGER NOT NULL DEFAULT 0,
                    IsPinnedInList INTEGER NOT NULL DEFAULT 0,
                    AlwaysOnTop    INTEGER NOT NULL DEFAULT 0,
                    IsDeleted      INTEGER NOT NULL DEFAULT 0,
                    WindowX        REAL NOT NULL DEFAULT 150,
                    WindowY        REAL NOT NULL DEFAULT 150,
                    WindowWidth    REAL NOT NULL DEFAULT 320,
                    WindowHeight   REAL NOT NULL DEFAULT 360,
                    IsOpen         INTEGER NOT NULL DEFAULT 1,
                    CreatedAt      TEXT NOT NULL,
                    UpdatedAt      TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_Notes_IsDeleted ON Notes(IsDeleted);
                CREATE INDEX IF NOT EXISTS IX_Notes_UpdatedAt ON Notes(UpdatedAt DESC);
                CREATE INDEX IF NOT EXISTS IX_Notes_IsPinned ON Notes(IsPinned DESC);
                CREATE INDEX IF NOT EXISTS IX_Notes_IsPinnedInList ON Notes(IsPinnedInList DESC);

                PRAGMA user_version = 1;
                """;
            await migrateCmd.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            currentVersion = 1;
        }

        // 4. 执行 V2 迁移：拆分 IsPinnedInList 与 AlwaysOnTop 列
        if (currentVersion < 2)
        {
            await using var tx = connection.BeginTransaction();
            await using var migrateCmd = connection.CreateCommand();
            migrateCmd.Transaction = tx;
            migrateCmd.CommandText = """
                -- 检查并安全增加 V2 拆分字段
                ALTER TABLE Notes ADD COLUMN IsPinnedInList INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE Notes ADD COLUMN AlwaysOnTop INTEGER NOT NULL DEFAULT 0;

                -- 从旧 IsPinned 继承并同步
                UPDATE Notes SET IsPinnedInList = IsPinned, AlwaysOnTop = IsPinned;

                CREATE INDEX IF NOT EXISTS IX_Notes_IsPinnedInList ON Notes(IsPinnedInList DESC);
                PRAGMA user_version = 2;
                """;
            try
            {
                await migrateCmd.ExecuteNonQueryAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                // 若列已存在（如 V1 新建表已带列），直接提升 user_version 为 2
                await tx.RollbackAsync(cancellationToken);
                await using var fallbackCmd = connection.CreateCommand();
                fallbackCmd.CommandText = "PRAGMA user_version = 2;";
                await fallbackCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            AppLog.Info("[SqliteDatabaseContext] 成功升级数据库模式至 Schema V2");
        }
    }
}
