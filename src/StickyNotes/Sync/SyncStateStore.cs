using System.IO;
using System.Text.Json;
using StickyNotes.Infrastructure;

namespace StickyNotes.Sync;

/// <summary>
/// 同步运行时状态存取（数据目录 sync_state.json，与用户设置分离，协议设计 §6）。
/// 状态丢失只是 UI 展示退化，不影响同步正确性。
/// </summary>
public sealed class SyncStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();

    public SyncStateStore(string? path = null)
    {
        _path = path ?? AppPaths.SyncStatePath;
    }

    public SyncState Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return new SyncState();
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<SyncState>(json, JsonOptions) ?? new SyncState();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[SyncStateStore] 读取同步状态失败（按空状态处理）: {ex.Message}");
                return new SyncState();
            }
        }
    }

    public void Save(SyncState state)
    {
        lock (_gate)
        {
            try
            {
                var json = JsonSerializer.Serialize(state, JsonOptions);
                File.WriteAllText(_path, json);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[SyncStateStore] 写入同步状态失败: {ex.Message}");
            }
        }
    }

    /// <summary>带锁更新：读旧状态 → 应用变更 → 落盘</summary>
    public void Update(Action<SyncState> mutate)
    {
        lock (_gate)
        {
            var state = Load();
            mutate(state);
            Save(state);
        }
    }
}
