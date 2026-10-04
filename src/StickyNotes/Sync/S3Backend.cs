using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using StickyNotes.Infrastructure;

namespace StickyNotes.Sync;

/// <summary>
/// S3 兼容对象存储后端（Cloudflare R2 为第一目标，协议设计 §5.2）。
/// path-style 端点 + AWS SigV4（手写签名器，region 固定 "auto"）。
/// key 形态：{BasePrefix}notes/&lt;uuid&gt;.json（BasePrefix 必填，评审决议 4）。
/// </summary>
public sealed class S3Backend : IStorageBackend, IDisposable
{
    private const string Service = "s3";
    private const string Region = "auto";

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _bucket;
    private readonly string _basePrefix;
    private readonly string _accessKey;
    private readonly string _secretKey;

    /// <param name="endpoint">S3 API 端点（如 https://&lt;account&gt;.r2.cloudflarestorage.com）</param>
    /// <param name="bucket">桶名</param>
    /// <param name="basePrefix">必填前缀子目录（空串表示桶根，设置页默认 stickynotes/）</param>
    /// <param name="accessKey">访问密钥 ID</param>
    /// <param name="secretKey">秘密访问密钥（明文，调用方负责解密）</param>
    /// <param name="handler">测试注入用消息处理器</param>
    public S3Backend(string endpoint, string bucket, string basePrefix,
        string accessKey, string secretKey, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("S3 端点未配置或不合法（必须为 https://）。");
        }

        if (string.IsNullOrWhiteSpace(bucket))
        {
            throw new InvalidOperationException("S3 桶名未配置。");
        }

        _endpoint = parsed.AbsoluteUri.TrimEnd('/');
        _bucket = bucket.Trim();
        _basePrefix = NormalizePrefix(basePrefix);
        _accessKey = accessKey ?? throw new InvalidOperationException("S3 AccessKey 未配置。");
        _secretKey = secretKey ?? throw new InvalidOperationException("S3 SecretKey 未配置。");

        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private static string NormalizePrefix(string prefix)
    {
        var trimmed = (prefix ?? string.Empty).Trim().TrimStart('/').TrimEnd('/');
        return trimmed.Length == 0 ? string.Empty : trimmed + "/";
    }

    /// <summary>相对 key（"notes/x.json"）→ 完整对象 key（"{prefix}notes/x.json"）</summary>
    private string FullKey(string key) => _basePrefix + key;

    /// <summary>构造 path-style 请求 URI（/bucket/fullKey，逐段转义）</summary>
    private Uri ObjectUri(string fullKey)
    {
        var path = $"/{_bucket}/" + string.Join("/", fullKey.Split('/').Select(Uri.EscapeDataString));
        return new Uri(_endpoint + path);
    }

    private Uri BucketUri(string canonicalQuery)
    {
        var query = string.IsNullOrEmpty(canonicalQuery) ? string.Empty : "?" + canonicalQuery;
        return new Uri($"{_endpoint}/{Uri.EscapeDataString(_bucket)}{query}");
    }

    private void Sign(HttpRequestMessage request, byte[] payload)
    {
        // payload 为空字节时签名器自动使用空串哈希（GET/DELETE 与 PUT 统一走这里）
        var (authorization, amzDate, payloadSha256) =
            SigV4Signer.Sign(request.Method.Method, request.RequestUri!, Region, Service, _accessKey, _secretKey, payload, DateTimeOffset.UtcNow);

        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadSha256);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
    }

    public async Task TestAsync(CancellationToken ct = default)
    {
        // 以「列出前缀下 1 个对象」作为连通性测试：同时验证端点、凭据、桶与前缀
        await ListPageAsync(1, null, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(CancellationToken ct = default)
    {
        var items = new List<RemoteItem>();
        string? continuation = null;

        do
        {
            var (contents, next) = await ListPageAsync(1000, continuation, ct).ConfigureAwait(false);

            foreach (var (key, size, modified) in contents)
            {
                // 只认本协议前缀下的便签对象；其他对象（包括同桶他人数据）一律无视
                if (key.Length > _basePrefix.Length
                    && key.StartsWith(_basePrefix, StringComparison.Ordinal)
                    && key.StartsWith(_basePrefix + SyncProtocol.NotesPrefix, StringComparison.Ordinal))
                {
                    var relative = key[_basePrefix.Length..];
                    var name = relative[SyncProtocol.NotesPrefix.Length..];
                    if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        && Guid.TryParseExact(name[..^5], "D", out _))
                    {
                        items.Add(new RemoteItem(relative, size, modified));
                    }
                }
            }

            continuation = next;
        }
        while (continuation != null);

        return items;
    }

    /// <summary>单页 ListObjectsV2。返回 (Contents[relative? 原始 key], NextContinuationToken)</summary>
    private async Task<(List<(string Key, long? Size, DateTimeOffset? Modified)> Contents, string? Next)> ListPageAsync(
        int maxKeys, string? continuationToken, CancellationToken ct)
    {
        var query = $"list-type=2&max-keys={maxKeys.ToString(CultureInfo.InvariantCulture)}" +
                    $"&prefix={Uri.EscapeDataString(_basePrefix + SyncProtocol.NotesPrefix)}";
        if (!string.IsNullOrEmpty(continuationToken))
        {
            query += $"&continuation-token={Uri.EscapeDataString(continuationToken)}";
        }

        var uri = BucketUri(query);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        Sign(request, Array.Empty<byte>());

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "列出对象").ConfigureAwait(false);

        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new InvalidOperationException("ListObjectsV2 响应缺少根元素。");

        var contents = new List<(string, long?, DateTimeOffset?)>();
        foreach (var element in root.Elements().Where(e => e.Name.LocalName == "Contents"))
        {
            var key = element.Elements().FirstOrDefault(e => e.Name.LocalName == "Key")?.Value;
            if (string.IsNullOrEmpty(key)) continue;

            long? size = long.TryParse(
                element.Elements().FirstOrDefault(e => e.Name.LocalName == "Size")?.Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSize) ? parsedSize : null;

            DateTimeOffset? modified = null;
            var modifiedText = element.Elements().FirstOrDefault(e => e.Name.LocalName == "LastModified")?.Value;
            if (modifiedText != null
                && DateTimeOffset.TryParse(modifiedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedModified))
            {
                modified = parsedModified;
            }

            contents.Add((key, size, modified));
        }

        var isTruncated = string.Equals(
            root.Elements().FirstOrDefault(e => e.Name.LocalName == "IsTruncated")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        var next = isTruncated
            ? root.Elements().FirstOrDefault(e => e.Name.LocalName == "NextContinuationToken")?.Value
            : null;

        return (contents, string.IsNullOrEmpty(next) ? null : next);
    }

    public async Task<string?> GetTextAsync(string key, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ObjectUri(FullKey(key)));
        Sign(request, Array.Empty<byte>());

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, $"读取 {key}").ConfigureAwait(false);

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public async Task PutTextAsync(string key, string content, CancellationToken ct = default)
    {
        var payload = Encoding.UTF8.GetBytes(content);
        using var request = new HttpRequestMessage(HttpMethod.Put, ObjectUri(FullKey(key)))
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        Sign(request, payload);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"上传 {key}").ConfigureAwait(false);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, ObjectUri(FullKey(key)));
        Sign(request, Array.Empty<byte>());

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return; // S3 语义：不存在视为已删除
        await EnsureSuccessAsync(response, $"删除 {key}").ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;

        string detail;
        try
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            detail = ParseErrorCode(body) ?? $"HTTP {(int)response.StatusCode}";
        }
        catch
        {
            detail = $"HTTP {(int)response.StatusCode}";
        }

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"S3 认证/权限失败（{detail}）：请检查访问密钥与令牌权限",
            HttpStatusCode.NotFound => $"{operation} 失败：对象或桶不存在（{detail}）",
            _ => $"{operation} 失败: {detail}"
        };

        throw new HttpRequestException(message);
    }

    /// <summary>从 S3 错误响应体提取 Code/Message（如 NoSuchKey / InvalidAccessKeyId）</summary>
    internal static string? ParseErrorCode(string errorBody)
    {
        try
        {
            var doc = XDocument.Parse(errorBody);
            if (doc.Root?.Name.LocalName != "Error") return null;

            var code = doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "Code")?.Value;
            var message = doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value;
            return string.IsNullOrEmpty(code) ? null : (string.IsNullOrEmpty(message) ? code : $"{code}: {message}");
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
