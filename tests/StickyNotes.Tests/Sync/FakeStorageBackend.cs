using StickyNotes.Sync;

namespace StickyNotes.Tests;

/// <summary>
/// 同步引擎测试用内存存储后端：字典即桶，支持故障注入（全部 GET 失败、逐对象改写、操作计数）。
/// </summary>
public sealed class FakeStorageBackend : IStorageBackend
{
    /// <summary>对象存储（key → 文本内容），测试可直接读写以构造远端现场</summary>
    public Dictionary<string, string> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>GET 前钩子：返回异常则该次下载按网络失败处理（注入断网/超时）</summary>
    public Func<string, Exception?>? GetFault { get; set; }

    /// <summary>GET 前异步闸门（单飞测试用：卡住第一轮）</summary>
    public Func<string, Task>? GetDelay { get; set; }

    /// <summary>PUT 钩子：返回异常则该次上传失败</summary>
    public Func<string, string, Exception?>? PutFault { get; set; }

    public int GetCount { get; private set; }
    public int PutCount { get; private set; }
    public List<string> DeletedKeys { get; } = new();
    public List<string> PutKeys { get; } = new();

    public Task<IReadOnlyList<RemoteItem>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<RemoteItem> items = Objects.Keys
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => new RemoteItem(k, null, null))
            .ToList();
        return Task.FromResult(items);
    }

    public async Task<string?> GetTextAsync(string key, CancellationToken ct = default)
    {
        await Task.Yield();
        GetCount++;

        if (GetDelay != null) await GetDelay(key).ConfigureAwait(false);

        var fault = GetFault?.Invoke(key);
        if (fault != null) throw fault;

        return Objects.TryGetValue(key, out var text) ? text : null;
    }

    public async Task PutTextAsync(string key, string content, CancellationToken ct = default)
    {
        await Task.Yield();
        PutCount++;

        var fault = PutFault?.Invoke(key, content);
        if (fault != null) throw fault;

        PutKeys.Add(key);
        Objects[key] = content;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        Objects.Remove(key);
        DeletedKeys.Add(key);
        return Task.CompletedTask;
    }

    public Task TestAsync(CancellationToken ct = default) => Task.CompletedTask;

    public void Dispose()
    {
    }
}
