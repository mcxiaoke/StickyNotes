using System.IO;
using System.Text.Json;
using StickyNotes.Infrastructure;

namespace StickyNotes.Services;

/// <summary>
/// 硬删除台账（P2-1）：记录本机「彻底删除」过的便签 id 与删除时刻，
/// 存为数据目录下的 <c>hard_deleted.json</c>（id → UTC 删除时间的扁平字典）。
/// <para>
/// 同步对账用它否决云端回流：彻底删除不产生云端副作用，若不记台账，
/// 云端残留对象会在下一轮被判为「远端新便签」重新插回本地（回收站反复回填）。
/// 台账时间戳还参与裁决——远端版本不新于删除时刻则推送墓碑覆盖云端，
/// 远端在删除后被其他设备编辑过则跳过下载（编辑胜过删除，与 LWW 一致）。
/// </para>
/// <para>
/// 已知取舍：文件不进每日 DB 冷备份（丢失的最坏结果是回收站回填一次）；
/// DB 删除与台账写入不原子（中间崩溃的最坏结果同上，均无害）。
/// 超过 <see cref="RetentionDays"/> 的条目在加载时顺手清理。
/// </para>
/// </summary>
public sealed class HardDeleteLedger
{
    /// <summary>台账条目保留天数：超过即视为无需继续否决回流（极老条目清理后最坏回填一次）</summary>
    public const int RetentionDays = 365;

    private readonly string _filePath;
    private readonly object _gate = new();
    private Dictionary<Guid, DateTime>? _entries;

    public HardDeleteLedger(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppPaths.DataDirectory, "hard_deleted.json");
    }

    /// <summary>记录一次硬删除（可批量，供「清空归档」使用）；同一 id 以较新时间为准</summary>
    public void Record(IEnumerable<Guid> noteIds, DateTime deletedAtUtc)
    {
        lock (_gate)
        {
            var entries = LoadLocked();
            var changed = false;
            foreach (var id in noteIds)
            {
                if (entries.TryGetValue(id, out var existing) && existing >= deletedAtUtc) continue;
                entries[id] = deletedAtUtc;
                changed = true;
            }

            if (changed)
            {
                PersistLocked(entries);
            }
        }
    }

    /// <summary>获取台账快照（id → UTC 删除时刻），对账每轮读取一次</summary>
    public IReadOnlyDictionary<Guid, DateTime> GetEntries()
    {
        lock (_gate)
        {
            return new Dictionary<Guid, DateTime>(LoadLocked());
        }
    }

    /// <summary>锁内惰性加载；文件缺失/损坏视为空台账，加载时顺手做保留期清理</summary>
    private Dictionary<Guid, DateTime> LoadLocked()
    {
        if (_entries != null) return _entries;

        _entries = new Dictionary<Guid, DateTime>();
        try
        {
            if (!File.Exists(_filePath)) return _entries;

            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<Guid, DateTime>>(json);
            if (loaded != null)
            {
                foreach (var (id, deletedAt) in loaded)
                {
                    _entries[id] = deletedAt;
                }
            }
        }
        catch (Exception ex)
        {
            // 损坏文件降级为空台账：最坏结果是回收站回填一次，绝不能阻断启动或同步
            AppLog.Warn($"[HardDeleteLedger] 台账文件读取失败，按空台账处理: {ex.Message}");
            return _entries;
        }

        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        var expired = _entries.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
        if (expired.Count > 0)
        {
            foreach (var id in expired)
            {
                _entries.Remove(id);
            }
            AppLog.Info($"[HardDeleteLedger] 已清理 {expired.Count} 条超过 {RetentionDays} 天的台账条目");
            PersistLocked(_entries);
        }

        return _entries;
    }

    /// <summary>锁内原子落盘：tmp + move，进程中断不会留下半个文件</summary>
    private void PersistLocked(Dictionary<Guid, DateTime> entries)
    {
        try
        {
            var json = JsonSerializer.Serialize(entries, JsonOptions);
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 落盘失败只降级为「本轮删除未记台账」（回收站可能回填一次），不抛出阻断删除流程
            AppLog.Warn($"[HardDeleteLedger] 台账写入失败: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
