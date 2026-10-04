using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using StickyNotes.Models;

namespace StickyNotes.Sync;

/// <summary>
/// 远端存储对象条目（<see cref="IStorageBackend.ListAsync"/> 结果）。
/// <see cref="Key"/> 为相对后端根的路径（如 "notes/&lt;uuid&gt;.json"），统一使用 '/' 分隔。
/// </summary>
public sealed record RemoteItem(string Key, long? Size, DateTimeOffset? LastModified);

/// <summary>
/// 存储后端抽象（协议设计见 docs/SYNC-PROTOCOL-DESIGN-20261004.md §2.2）。
/// <para>
/// 同步引擎只依赖此接口，不感知 WebDAV 还是 S3。实现约定：
/// 幂等；失败抛异常；全部方法支持取消；对象不存在不算错误（Get 返回 null，Delete 视为成功）。
/// 实现通常持有 HttpClient 等非托管资源，须正确实现 <see cref="IDisposable"/>。
/// </para>
/// </summary>
public interface IStorageBackend : IDisposable
{
    /// <summary>列出 notes/ 前缀下全部对象（扁平列表）</summary>
    Task<IReadOnlyList<RemoteItem>> ListAsync(CancellationToken ct = default);

    /// <summary>读取文本，对象不存在返回 null</summary>
    Task<string?> GetTextAsync(string key, CancellationToken ct = default);

    /// <summary>写入/覆盖文本（小文件单请求覆盖，直接 PUT，不做 tmp+rename）</summary>
    Task PutTextAsync(string key, string content, CancellationToken ct = default);

    /// <summary>删除对象（仅墓碑 GC 使用，v1 不调用；对象不存在视为成功）</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>连通性测试（设置页「测试连接」）</summary>
    Task TestAsync(CancellationToken ct = default);
}

/// <summary>一轮同步的摘要（日志与 UI 状态用）</summary>
public sealed record SyncRoundSummary(
    int Listed,
    int Downloaded,
    int Uploaded,
    int SkippedInvalid,
    int GuardedSkipped,
    IReadOnlyList<Guid> AppliedIds)
{
    public SyncRoundSummary(int Listed, int Downloaded, int Uploaded, int SkippedInvalid, int GuardedSkipped)
        : this(Listed, Downloaded, Uploaded, SkippedInvalid, GuardedSkipped, Array.Empty<Guid>())
    {
    }
}

/// <summary>
/// 同步协议 v1 的常量、wire 格式（schemaVersion=1）与序列化约定。
/// </summary>
public static class SyncProtocol
{
    public const int SchemaVersion = 1;

    /// <summary>便签对象统一存放的 key 前缀</summary>
    public const string NotesPrefix = "notes/";

    /// <summary>单文件防呆上限：超过视为异常数据，跳过并记日志</summary>
    public const int MaxNoteFileBytes = 4 * 1024 * 1024;

    /// <summary>远端文件名：小写 Guid "D" 格式 + .json</summary>
    public static string NoteKey(Guid id) => $"{NotesPrefix}{id.ToString("D")}.json";

    /// <summary>判断 key 是否为便签对象，并解析出便签 Id（大小写不敏感）</summary>
    public static bool TryGetNoteId(string key, out Guid id)
    {
        id = Guid.Empty;
        if (!key.StartsWith(NotesPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var name = key[NotesPrefix.Length..];
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
        return Guid.TryParseExact(name[..^5], "D", out id) && id != Guid.Empty;
    }

    /// <summary>协议层换行规范化：wire 与比对一律 \n（协议设计 §3.2）</summary>
    public static string NormalizeContent(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

    /// <summary>把任意 Kind 的 DateTime 规范为 UTC（Kind 丢失时按 UTC 处理——本地库全链路存 UTC）</summary>
    public static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    /// <summary>同步 DTO 序列化选项：camelCase + 字符串枚举 + 强制 UTC "o" 时间戳</summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new UtcRoundtripDateTimeConverter());
        return options;
    }

    public static string Serialize(SyncNoteDto dto) => JsonSerializer.Serialize(dto, JsonOptions);

    /// <summary>
    /// 防御性解析：坏文件/陌生格式返回 null（调用方跳过并记日志，绝不中断整轮）。
    /// 校验：schemaVersion 已知、id 与（可选传入的）key 一致。
    /// </summary>
    public static SyncNoteDto? TryDeserialize(string json, string? expectedKey = null)
    {
        SyncNoteDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SyncNoteDto>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (dto == null) return null;
        if (dto.Id == Guid.Empty) return null;
        if (dto.SchemaVersion > SchemaVersion) return null;
        if (expectedKey != null && !string.Equals(NoteKey(dto.Id), expectedKey, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return dto;
    }
}

/// <summary>
/// wire 格式的 DateTime 转换器：一律 ISO-8601 UTC round-trip（"o"），
/// 读取时把 Local/Unspecified 规范为 UTC（协议设计 §3.2 的跨平台硬约定）。
/// </summary>
public sealed class UtcRoundtripDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => EnsureUtc(reader.GetDateTime());

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(EnsureUtc(value).ToString("o", CultureInfo.InvariantCulture));

    private static DateTime EnsureUtc(DateTime value) => SyncProtocol.EnsureUtc(value);
}

/// <summary>同步协议 v1 便签 DTO（wire 格式字段与 docs/SYNC-PROTOCOL-DESIGN-20261004.md §3.2 一致）</summary>
public sealed class SyncNoteDto
{
    public int SchemaVersion { get; set; } = SyncProtocol.SchemaVersion;

    public Guid Id { get; set; }

    public string Content { get; set; } = string.Empty;

    public NoteColor Color { get; set; } = NoteColor.Yellow;

    public bool IsPinnedInList { get; set; }

    public bool AlwaysOnTop { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? DeviceId { get; set; }

    /// <summary>从本地实体构造 DTO（内容按协议规范化为 \n；本地不迁移换行）</summary>
    public static SyncNoteDto FromNote(Note note, string deviceId) => new()
    {
        Id = note.Id,
        Content = SyncProtocol.NormalizeContent(note.Content),
        Color = note.Color,
        IsPinnedInList = note.IsPinnedInList,
        AlwaysOnTop = note.AlwaysOnTop,
        IsDeleted = note.IsDeleted,
        CreatedAt = note.CreatedAt,
        UpdatedAt = note.UpdatedAt,
        DeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId
    };

    /// <summary>
    /// 业务语义相等（决定「是否需要传输」防乒乓）：比对规范化后的内容与全部参与同步的标志位；
    /// 时间戳是裁决输入而非载荷，不参与相等判断。
    /// </summary>
    public bool BusinessEquals(SyncNoteDto other) =>
        string.Equals(SyncProtocol.NormalizeContent(Content), SyncProtocol.NormalizeContent(other.Content), StringComparison.Ordinal)
        && Color == other.Color
        && IsPinnedInList == other.IsPinnedInList
        && AlwaysOnTop == other.AlwaysOnTop
        && IsDeleted == other.IsDeleted;
}
