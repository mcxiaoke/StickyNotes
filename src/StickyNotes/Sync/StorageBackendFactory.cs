namespace StickyNotes.Sync;

/// <summary>
/// 按当前 SyncSettings 构建存储后端（协议设计 §2.2）。
/// 凭据在此处统一解密（DPAPI），后端实现只接触明文。
/// 根据 EnableEncryption 自动路由子目录（明文 stickynotes-data / 密文 stickynotes-vault）。
/// </summary>
public static class StorageBackendFactory
{
    public static string GetEffectiveWebDavUrl(string webDavUrl, bool enableEncryption)
    {
        if (string.IsNullOrWhiteSpace(webDavUrl)) return string.Empty;
        var trimmed = webDavUrl.Trim().TrimEnd('/');
        var targetSubdir = SyncProtocol.GetEffectiveSubdirectory(enableEncryption).Trim('/');
        var otherSubdir = SyncProtocol.GetEffectiveSubdirectory(!enableEncryption).Trim('/');

        if (trimmed.EndsWith("/" + otherSubdir, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^(otherSubdir.Length + 1)];
        }

        if (!trimmed.EndsWith("/" + targetSubdir, StringComparison.OrdinalIgnoreCase))
        {
            trimmed += "/" + targetSubdir;
        }

        return trimmed + "/";
    }

    public static string GetEffectiveS3Prefix(string? basePrefix, bool enableEncryption)
    {
        var targetSubdir = SyncProtocol.GetEffectiveSubdirectory(enableEncryption).Trim('/');
        var otherSubdir = SyncProtocol.GetEffectiveSubdirectory(!enableEncryption).Trim('/');

        if (string.IsNullOrWhiteSpace(basePrefix))
        {
            return targetSubdir + "/";
        }

        var trimmed = basePrefix.Trim().TrimStart('/').TrimEnd('/');

        if (trimmed.Equals("stickynotes", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals(targetSubdir, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals(otherSubdir, StringComparison.OrdinalIgnoreCase))
        {
            return targetSubdir + "/";
        }

        if (trimmed.EndsWith("/" + otherSubdir, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^(otherSubdir.Length + 1)];
        }

        if (!trimmed.EndsWith("/" + targetSubdir, StringComparison.OrdinalIgnoreCase))
        {
            trimmed += "/" + targetSubdir;
        }

        return trimmed + "/";
    }

    public static IStorageBackend Create(SyncSettings settings) => settings.BackendType switch
    {
        SyncBackendType.WebDav => new WebDavBackend(
            GetEffectiveWebDavUrl(settings.WebDavUrl, settings.EnableEncryption),
            settings.WebDavUsername,
            CredentialProtector.Unprotect(settings.WebDavPassword),
            settings.WebDavAllowInsecureHttp),
        SyncBackendType.S3 => new S3Backend(
            settings.S3Endpoint,
            settings.S3Bucket,
            GetEffectiveS3Prefix(settings.S3BasePrefix, settings.EnableEncryption),
            settings.S3AccessKey,
            CredentialProtector.Unprotect(settings.S3SecretKey)),
        _ => throw new InvalidOperationException($"未知的同步后端类型: {settings.BackendType}")
    };

    /// <summary>
    /// 设置页「测试连接」：构建临时后端并探测连通性（用后即弃）。
    /// 若启用加密，还会自举或验证远端口令校验探针（.auth_verifier）。
    /// </summary>
    public static async Task TestConnectionAsync(SyncSettings settings, CancellationToken ct = default)
    {
        using var backend = Create(settings);
        await backend.TestAsync(ct).ConfigureAwait(false);

        if (settings.EnableEncryption)
        {
            var secret = VaultSecret.GetSecret();
            var verifierJson = await backend.GetTextAsync(SyncProtocol.VerifierKey, ct).ConfigureAwait(false);
            if (verifierJson == null)
            {
                var newVerifier = AuthVerifierDto.Create(secret);
                await backend.PutTextAsync(SyncProtocol.VerifierKey, newVerifier, ct).ConfigureAwait(false);
            }
            else if (!AuthVerifierDto.Verify(verifierJson, secret))
            {
                throw new InvalidOperationException("远端加密保险箱口令校验失败（.auth_verifier 无法解密或密钥不匹配）。");
            }
        }
    }
}
