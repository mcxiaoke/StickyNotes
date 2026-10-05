using System.Security.Cryptography;

namespace StickyNotes.Sync;

/// <summary>存储后端类型</summary>
public enum SyncBackendType
{
    WebDav = 0,
    S3 = 1
}

/// <summary>
/// 同步配置（序列化进 settings.json 的 <c>Sync</c> 节，协议设计 §6）。
/// 密码/SecretKey 一律以 DPAPI 密文落盘（"dpapi:" 前缀），见 <see cref="CredentialProtector"/>。
/// </summary>
public sealed class SyncSettings
{
    public bool Enabled { get; set; }

    /// <summary>是否启用端到端防偷窥加密（密文存入 stickynotes-vault/，明文存入 stickynotes-data/）</summary>
    public bool EnableEncryption { get; set; }

    public SyncBackendType BackendType { get; set; } = SyncBackendType.WebDav;

    /// <summary>设备标识（平台前缀 + 6 位随机串，如 win-8xf7ad），首启生成，评审决议 5</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>后台定时同步间隔（分钟，5–120，默认 15，评审决议 1）</summary>
    public int IntervalMinutes { get; set; } = 15;

    // ---- WebDAV ----
    public string WebDavUrl { get; set; } = string.Empty;
    public string WebDavUsername { get; set; } = string.Empty;

    /// <summary>WebDAV 应用专用密码（DPAPI 密文）</summary>
    public string WebDavPassword { get; set; } = string.Empty;

    /// <summary>显式允许明文 http://（内网 NAS 自担风险，协议设计 §5.1）</summary>
    public bool WebDavAllowInsecureHttp { get; set; }

    // ---- S3 / Cloudflare R2 ----
    /// <summary>S3 API 端点（如 https://&lt;account&gt;.r2.cloudflarestorage.com）</summary>
    public string S3Endpoint { get; set; } = string.Empty;

    public string S3Bucket { get; set; } = string.Empty;

    /// <summary>必填对象前缀子目录（如 stickynotes/，评审决议 4），实际 key = {prefix}notes/&lt;uuid&gt;.json</summary>
    public string S3BasePrefix { get; set; } = "stickynotes/";

    public string S3AccessKey { get; set; } = string.Empty;

    /// <summary>S3 秘密访问密钥（DPAPI 密文）</summary>
    public string S3SecretKey { get; set; } = string.Empty;

    /// <summary>约定区间内的有效间隔（默认/越界回 15 分钟）</summary>
    public int EffectiveIntervalMinutes =>
        IntervalMinutes is >= 5 and <= 120 ? IntervalMinutes : 15;

    /// <summary>配置是否齐备到可以尝试同步（不验证连通性）</summary>
    public bool IsConfigured => BackendType switch
    {
        SyncBackendType.WebDav => !string.IsNullOrWhiteSpace(WebDavUrl),
        SyncBackendType.S3 => !string.IsNullOrWhiteSpace(S3Endpoint) && !string.IsNullOrWhiteSpace(S3Bucket),
        _ => false
    };

    /// <summary>生成设备标识：win-xxxxxx（6 位小写去混淆字符集）</summary>
    public static string GenerateDeviceId()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789"; // 去除 i l o 0 1 等易混淆字符
        var bytes = RandomNumberGenerator.GetBytes(6);
        var chars = bytes.Select(b => alphabet[b % alphabet.Length]).ToArray();
        return $"win-{new string(chars)}";
    }
}

/// <summary>
/// 同步运行时状态（sync_state.json，与用户设置分离）：最近成功时间、最近错误、mtime 跳过缓存预留位。
/// </summary>
public sealed class SyncState
{
    public DateTime? LastSuccessAt { get; set; }

    public string? LastError { get; set; }

    /// <summary>最近一次尝试（含失败）时间，用于设置页展示「正在重试」语义</summary>
    public DateTime? LastAttemptAt { get; set; }
}
