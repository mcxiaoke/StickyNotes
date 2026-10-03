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
        // 注意：Microsoft.Data.Sqlite 的 SqliteConnectionStringBuilder 不支持 "synchronous" 关键字
        // （仅支持 Data Source / Mode / Cache / Password / Foreign Keys / Recursive Triggers /
        //  Default Timeout / Pooling），因此 synchronous 只能在每次建连后以 PRAGMA 单独下发，
        // 见 NoteRepository.OpenConnectionAsync。审查报告 F-P2-4 建议的「并入连接串」在本 provider 下不可行。
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
            AppLog.Error($"[SqliteDatabaseContext] 数据库架构版本 (v{currentVersion}) 高于应用程序支持的版本 (v{TargetSchemaVersion})，为保证数据安全已中止初始化");
            throw new InvalidOperationException(
                $"数据库架构版本 (v{currentVersion}) 高于当前应用支持的版本 (v{TargetSchemaVersion})。" +
                "请升级应用，或从 backups/ 目录恢复一份匹配的备份后再启动。");
        }

        AppLog.Info($"[SqliteDatabaseContext] 数据库初始化开始：当前版本 v{currentVersion}，目标版本 v{TargetSchemaVersion}");

        // 3. 执行 V1 初始建表迁移
        // 注意「每个版本只做自己的事」：V1 建表**只包含** v1 时期的列（LiveNote 相关），
        // IsPinnedInList / AlwaysOnTop 两列属于 V2，必须由 V2 迁移通过 ALTER 添加。
        // 若 V1 建表就带上这两列，V2 的 ALTER TABLE ADD COLUMN 必然报「duplicate column name」，
        // 只能靠 catch 掩盖，导致「新建库走 catch 路径、旧库升级走正常路径」两条不同代码路径
        // 且测试只覆盖其中一条（原 F-P1-5）。
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

                PRAGMA user_version = 1;
                """;
            await migrateCmd.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            currentVersion = 1;
            AppLog.Info("[SqliteDatabaseContext] 已应用 Schema V1（基础建表）");
        }

        // 4. 执行 V2 迁移：拆分 IsPinnedInList 与 AlwaysOnTop 列
        // 采用「先探测列是否存在、再决定是否 ALTER」的幂等写法，使以下两类库都走**同一条**代码路径：
        //   a) 全新库 / 纯 v1 库：表内无这两列 → 执行 ALTER 补齐；
        //   b) 历史遗留库：早期版本曾在 V1 建表时误带上这两列 → 跳过 ALTER，仅补数据与索引。
        // 这样既不需要、也绝不允许用 catch 掩盖失败（原 F-P1-5）。
        if (currentVersion < 2)
        {
            var existingColumns = await GetColumnNamesAsync(connection, cancellationToken);

            await using var tx = connection.BeginTransaction();

            if (!existingColumns.Contains("IsPinnedInList"))
            {
                await ExecuteAsync(connection, tx, "ALTER TABLE Notes ADD COLUMN IsPinnedInList INTEGER NOT NULL DEFAULT 0;", cancellationToken);
            }

            if (!existingColumns.Contains("AlwaysOnTop"))
            {
                await ExecuteAsync(connection, tx, "ALTER TABLE Notes ADD COLUMN AlwaysOnTop INTEGER NOT NULL DEFAULT 0;", cancellationToken);
            }

            // 从旧 IsPinned 继承并同步（仅在首次补齐时才有意义，但重复执行结果一致，天然幂等）
            await ExecuteAsync(connection, tx, "UPDATE Notes SET IsPinnedInList = IsPinned, AlwaysOnTop = IsPinned;", cancellationToken);
            await ExecuteAsync(connection, tx, "CREATE INDEX IF NOT EXISTS IX_Notes_IsPinnedInList ON Notes(IsPinnedInList DESC);", cancellationToken);
            await ExecuteAsync(connection, tx, "PRAGMA user_version = 2;", cancellationToken);

            try
            {
                await tx.CommitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(cancellationToken);
                AppLog.Error($"[SqliteDatabaseContext] Schema V2 迁移提交失败，数据库保持 v{currentVersion} 未变更: {ex.Message}", ex);
                throw new InvalidOperationException(
                    $"数据库从 v{currentVersion} 升级到 v2 失败，已回滚且未修改版本号。原始错误：{ex.Message}", ex);
            }

            AppLog.Info($"[SqliteDatabaseContext] 已应用 Schema V2（IsPinnedInList 补齐={!existingColumns.Contains("IsPinnedInList")}, AlwaysOnTop 补齐={!existingColumns.Contains("AlwaysOnTop")}）");
        }
        else
        {
            AppLog.Info($"[SqliteDatabaseContext] 数据库架构已是最新 (v{currentVersion})，无需迁移");
        }
    }

    /// <summary>
    /// 读取 Notes 表现有列名（用于幂等迁移判断，避免靠 catch 掩盖 ALTER 失败）
    /// </summary>
    private static async Task<HashSet<string>> GetColumnNamesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(Notes);";

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // PRAGMA table_info 的第 2 列（索引 1）为列名
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction tx,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
