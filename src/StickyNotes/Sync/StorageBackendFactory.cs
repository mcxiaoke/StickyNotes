namespace StickyNotes.Sync;

/// <summary>
/// 按当前 SyncSettings 构建存储后端（协议设计 §2.2）。
/// 凭据在此处统一解密（DPAPI），后端实现只接触明文。
/// </summary>
public static class StorageBackendFactory
{
    public static IStorageBackend Create(SyncSettings settings) => settings.BackendType switch
    {
        SyncBackendType.WebDav => new WebDavBackend(
            settings.WebDavUrl,
            settings.WebDavUsername,
            CredentialProtector.Unprotect(settings.WebDavPassword),
            settings.WebDavAllowInsecureHttp),
        SyncBackendType.S3 => new S3Backend(
            settings.S3Endpoint,
            settings.S3Bucket,
            settings.S3BasePrefix,
            settings.S3AccessKey,
            CredentialProtector.Unprotect(settings.S3SecretKey)),
        _ => throw new InvalidOperationException($"未知的同步后端类型: {settings.BackendType}")
    };

    /// <summary>设置页「测试连接」：构建临时后端并探测连通性（用后即弃）</summary>
    public static async Task TestConnectionAsync(SyncSettings settings, CancellationToken ct = default)
    {
        using var backend = Create(settings);
        await backend.TestAsync(ct).ConfigureAwait(false);
    }
}
