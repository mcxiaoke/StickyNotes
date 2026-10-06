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
    IReadOnlyList<Guid> AppliedIds,
    int IncompleteCount = 0,
    IReadOnlyList<Guid>? UploadFailedIds = null)
{
    /// <summary>本轮因下载失败/对象无效而被隔离、未参与对账的远端对象数（P0-1）</summary>
    public int IncompleteCount { get; init; } = IncompleteCount;

    /// <summary>本轮上传失败的便签 id（逐条容错，P2-4）；空列表表示上传全部成功</summary>
    public IReadOnlyList<Guid> UploadFailedIds { get; init; } = UploadFailedIds ?? Array.Empty<Guid>();

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

    /// <summary>明文同步模式子目录前缀</summary>
    public const string DataPrefix = "stickynotes-data/";

    /// <summary>密文保险箱同步模式子目录前缀</summary>
    public const string VaultPrefix = "stickynotes-vault/";

    /// <summary>远端口令校验探针文件名称</summary>
    public const string VerifierKey = ".auth_verifier";

    /// <summary>探针内固定的成功魔数标识</summary>
    public const string AuthVerifierMagic = "STICKYNOTES_AUTH_OK";

    /// <summary>便签对象统一存放的 key 前缀</summary>
    public const string NotesPrefix = "notes/";

    /// <summary>单文件防呆上限：超过视为异常数据，跳过并记日志</summary>
    public const int MaxNoteFileBytes = 4 * 1024 * 1024;

    /// <summary>获取当前加密模式对应的存储子目录</summary>
    public static string GetEffectiveSubdirectory(bool enableEncryption) =>
        enableEncryption ? VaultPrefix : DataPrefix;

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
    /// 校验：schemaVersion 已知、id 与（可选传入的）key 一致、明密文互斥合法。
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
        if (!string.IsNullOrEmpty(dto.Iv) ^ !string.IsNullOrEmpty(dto.Payload)) return null; // IV 与 Payload 必须成对出现
        if (dto.Content != null && dto.IsEncrypted) return null;  // 明密文同时存在（非法格式）
        if (!dto.IsEncrypted)
        {
            dto.Content ??= string.Empty; // 明文模式若正文字段缺失/为 null 则规范为空串
        }
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

/// <summary>远端口令校验探针文件模型（.auth_verifier）</summary>
public sealed class AuthVerifierDto
{
    public int Version { get; set; } = 1;
    public string Iv { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;

    public static string Create(string secret)
    {
        var (iv, payload) = CryptoHelper.CreateMagicPayload(SyncProtocol.AuthVerifierMagic, secret);
        return JsonSerializer.Serialize(new AuthVerifierDto { Iv = iv, Payload = payload }, SyncProtocol.JsonOptions);
    }

    public static bool Verify(string json, string secret)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<AuthVerifierDto>(json, SyncProtocol.JsonOptions);
            if (dto == null || string.IsNullOrEmpty(dto.Iv) || string.IsNullOrEmpty(dto.Payload)) return false;
            var unwrapped = CryptoHelper.UnwrapMagicPayload(dto.Iv, dto.Payload, secret);
            return string.Equals(unwrapped, SyncProtocol.AuthVerifierMagic, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>同步协议 v1 便签 DTO（支持明文与 stickynotes-vault 密文两种 wire 格式）</summary>
public sealed class SyncNoteDto
{
    public int SchemaVersion { get; set; } = SyncProtocol.SchemaVersion;

    public Guid Id { get; set; }

    /// <summary>明文便签正文（密文模式为 null）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    /// <summary>密文模式 IV 向量 Base64（明文模式为 null）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Iv { get; set; }

    /// <summary>密文模式载荷 Base64（明文模式为 null）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Payload { get; set; }

    public NoteColor Color { get; set; } = NoteColor.Yellow;

    public bool IsPinnedInList { get; set; }

    public bool AlwaysOnTop { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? DeviceId { get; set; }

    /// <summary>是否为密文便签（包含有效的 Iv 与 Payload，且 Content 为 null）</summary>
    [JsonIgnore]
    public bool IsEncrypted => !string.IsNullOrEmpty(Payload) && !string.IsNullOrEmpty(Iv);

    /// <summary>从本地实体构造明文 DTO</summary>
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

    /// <summary>从本地实体构造密文 DTO（正文经 AES-256 加密存入 Payload，Content 置 null）</summary>
    public static SyncNoteDto FromNoteEncrypted(Note note, string deviceId, string secret, byte[]? fixedIv = null)
    {
        var normalized = SyncProtocol.NormalizeContent(note.Content);
        var (iv, payload) = CryptoHelper.CreateMagicPayload(normalized, secret, fixedIv);
        return new()
        {
            Id = note.Id,
            Content = null,
            Iv = iv,
            Payload = payload,
            Color = note.Color,
            IsPinnedInList = note.IsPinnedInList,
            AlwaysOnTop = note.AlwaysOnTop,
            IsDeleted = note.IsDeleted,
            CreatedAt = note.CreatedAt,
            UpdatedAt = note.UpdatedAt,
            DeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId
        };
    }

    /// <summary>
    /// 业务语义相等（决定「是否需要传输」防乒乓）：比对规范化后的内容与全部参与同步的标志位；
    /// 时间戳是裁决输入而非载荷，不参与相等判断。
    /// </summary>
    public bool BusinessEquals(SyncNoteDto other) =>
        string.Equals(SyncProtocol.NormalizeContent(Content ?? string.Empty), SyncProtocol.NormalizeContent(other.Content ?? string.Empty), StringComparison.Ordinal)
        && Color == other.Color
        && IsPinnedInList == other.IsPinnedInList
        && AlwaysOnTop == other.AlwaysOnTop
        && IsDeleted == other.IsDeleted;
}
