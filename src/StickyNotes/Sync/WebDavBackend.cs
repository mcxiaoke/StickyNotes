using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using StickyNotes.Infrastructure;

namespace StickyNotes.Sync;

/// <summary>
/// WebDAV 存储后端（协议设计 §5.1）。
/// 仅使用 PROPFIND / GET / PUT / DELETE / MKCOL 五个动词，不使用 LOCK 与条件写。
/// 兼容目标：坚果云、Nextcloud、Alist、Apache mod_dav、rclone serve webdav。
/// </summary>
public sealed class WebDavBackend : IStorageBackend
{
    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly HttpMethod MkColMethod = new("MKCOL");

    private const string PropFindBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <d:propfind xmlns:d="DAV:"><d:prop><d:getlastmodified/><d:getcontentlength/><d:resourcetype/></d:prop></d:propfind>
        """;

    private readonly HttpClient _http;
    private readonly string _rootUrl;

    /// <param name="rootUrl">用户配置的根目录 URL（notes/ 集合将位于其下）</param>
    /// <param name="username">用户名（可为空，匿名服务器）</param>
    /// <param name="password">应用专用密码</param>
    /// <param name="allowInsecureHttp">显式允许明文 http://（内网 NAS 自担风险）</param>
    /// <param name="handler">测试注入用消息处理器；为 null 时使用默认处理器</param>
    public WebDavBackend(string rootUrl, string? username, string? password,
        bool allowInsecureHttp = false, HttpMessageHandler? handler = null)
    {
        _rootUrl = NormalizeRootUrl(rootUrl, allowInsecureHttp);

        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        if (!string.IsNullOrEmpty(username))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
    }

    private static string NormalizeRootUrl(string rootUrl, bool allowInsecureHttp)
    {
        if (string.IsNullOrWhiteSpace(rootUrl))
        {
            throw new InvalidOperationException("WebDAV 服务器地址未配置。");
        }

        if (!Uri.TryCreate(rootUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"WebDAV 地址不合法: {rootUrl}");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !allowInsecureHttp)
        {
            throw new InvalidOperationException("明文 HTTP 已被拒绝；如确为内网 NAS，请在设置中显式允许明文 HTTP。");
        }

        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }

    private Uri BuildUri(string key)
    {
        // key 形如 "notes/<uuid>.json"，逐段转义（文件名本身是受限字符集，转义是防御性的）
        var escaped = string.Join("/", key.Split('/').Select(Uri.EscapeDataString));
        return new Uri(_rootUrl + escaped);
    }

    public async Task TestAsync(CancellationToken ct = default)
    {
        // PROPFIND 根集合 Depth:0：同时验证可达性、认证与根目录存在
        using var request = new HttpRequestMessage(PropFindMethod, new Uri(_rootUrl));
        request.Headers.Add("Depth", "0");
        FillPropFindContent(request);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // 首次使用根集合尚不存在，尝试创建
            await MkColAsync(new Uri(_rootUrl), ct).ConfigureAwait(false);
            using var retryRequest = new HttpRequestMessage(PropFindMethod, new Uri(_rootUrl));
            retryRequest.Headers.Add("Depth", "0");
            FillPropFindContent(retryRequest);
            using var retryResponse = await _http.SendAsync(retryRequest, ct).ConfigureAwait(false);
            EnsureSuccess(retryResponse, "测试连接");
            return;
        }
        EnsureSuccess(response, "测试连接");
    }

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(CancellationToken ct = default)
    {
        var items = await PropFindCollectionAsync(BuildUri(SyncProtocol.NotesPrefix), Depth.One, ct).ConfigureAwait(false);

        if (items == null)
        {
            // 首次使用远端可能还没有 notes/ 集合：尝试创建后重列，仍不存在则按空集合处理
            await EnsureNotesCollectionAsync(ct).ConfigureAwait(false);
            items = await PropFindCollectionAsync(BuildUri(SyncProtocol.NotesPrefix), Depth.One, ct).ConfigureAwait(false);
            if (items == null)
            {
                AppLog.Warn("[WebDavBackend] notes/ 集合创建后仍无法列出（服务器可能禁止 MKCOL），按空集合处理");
                return Array.Empty<RemoteItem>();
            }
        }

        var result = new List<RemoteItem>();
        foreach (var (name, size, modified) in items)
        {
            // 陌生文件/子目录一律忽略（协议设计 §3.1 向前兼容规则），绝不删除
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(name[..^5], "D", out _))
            {
                result.Add(new RemoteItem(SyncProtocol.NotesPrefix + name, size, modified));
            }
        }

        return result;
    }

    public async Task<string?> GetTextAsync(string key, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(BuildUri(key), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(response, $"读取 {key}");
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public async Task PutTextAsync(string key, string content, CancellationToken ct = default)
    {
        using var payload = new StringContent(content, Encoding.UTF8, "application/json");
        using var response = await _http.PutAsync(BuildUri(key), payload, ct).ConfigureAwait(false);
        EnsureSuccess(response, $"上传 {key}");
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync(BuildUri(key), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        EnsureSuccess(response, $"删除 {key}");
    }

    // ---- 内部实现 ----

    private enum Depth { Zero, One }

    private static void FillPropFindContent(HttpRequestMessage request) =>
        request.Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");

    /// <summary>
    /// PROPFIND 指定集合。返回集合直接子项 (文件名, 大小, 修改时间) 列表；
    /// 集合不存在（404）返回 null；其他错误抛异常。
    /// </summary>
    private async Task<List<(string Name, long? Size, DateTimeOffset? Modified)>?> PropFindCollectionAsync(
        Uri collectionUri, Depth depth, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(PropFindMethod, collectionUri);
        request.Headers.Add("Depth", depth == Depth.One ? "1" : "0");
        FillPropFindContent(request);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(response, $"列出 {collectionUri}");

        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseMultistatus(xml);
    }

    private async Task EnsureNotesCollectionAsync(CancellationToken ct)
    {
        // 先保证根存在，再建 notes/ 子集合；服务器拒绝 MKCOL 不视为致命（下轮重试）
        try
        {
            await MkColAsync(new Uri(_rootUrl), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Info($"[WebDavBackend] 根集合 MKCOL 跳过（通常已存在）: {ex.Message}");
        }

        try
        {
            await MkColAsync(BuildUri(SyncProtocol.NotesPrefix), ct).ConfigureAwait(false);
            AppLog.Info("[WebDavBackend] 已创建远端 notes/ 集合");
        }
        catch (Exception ex)
        {
            AppLog.Info($"[WebDavBackend] notes/ 集合 MKCOL 未成功（可能已存在）: {ex.Message}");
        }
    }

    private async Task MkColAsync(Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(MkColMethod, uri);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.MethodNotAllowed
            || response.StatusCode == HttpStatusCode.Conflict
            || response.StatusCode == HttpStatusCode.Forbidden)
        {
            // 已存在/服务器不支持建集合：留痕后继续
            AppLog.Info($"[WebDavBackend] MKCOL {uri} 返回 {response.StatusCode}，视为已存在或不可创建");
            return;
        }
        EnsureSuccess(response, $"创建集合 {uri}");
    }

    /// <summary>
    /// 解析 207 multistatus。对命名空间不规范的服务器做 LocalName 兜底；
    /// 返回 (文件名, 大小, 修改时间) 列表，跳过集合自身与其余集合项。
    /// </summary>
    internal static List<(string Name, long? Size, DateTimeOffset? Modified)>? ParseMultistatus(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("WebDAV PROPFIND 响应不是合法 XML。", ex);
        }

        var list = new List<(string, long?, DateTimeOffset?)>();

        foreach (var responseElement in doc.Descendants().Where(e => e.Name.LocalName == "response"))
        {
            var href = responseElement.Descendants().FirstOrDefault(e => e.Name.LocalName == "href")?.Value;
            if (string.IsNullOrEmpty(href)) continue;

            var decoded = Uri.UnescapeDataString(href);
            if (decoded.EndsWith("/", StringComparison.Ordinal)) continue; // 集合项

            var name = decoded.TrimEnd('/').Split('/').LastOrDefault();
            if (string.IsNullOrEmpty(name)) continue;

            long? size = null;
            DateTimeOffset? modified = null;

            foreach (var propElement in responseElement.Descendants().Where(e => e.Name.LocalName == "prop"))
            {
                foreach (var child in propElement.Elements())
                {
                    if (child.Name.LocalName == "getcontentlength" && long.TryParse(child.Value, out var parsedSize))
                    {
                        size = parsedSize;
                    }
                    else if (child.Name.LocalName == "getlastmodified" && TryParseHttpDate(child.Value, out var parsedModified))
                    {
                        modified = parsedModified;
                    }
                }
            }

            list.Add((name, size, modified));
        }

        return list;
    }

    private static bool TryParseHttpDate(string value, out DateTimeOffset result)
    {
        // RFC 1123（HTTP date）为主，部分服务器返回 ISO 8601
        if (DateTimeOffset.TryParseExact(value, "R", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result))
        {
            return true;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result);
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "认证失败（401）：请检查用户名与应用专用密码",
            HttpStatusCode.Forbidden => "访问被拒绝（403）：请检查账号权限",
            _ => $"{operation} 失败: HTTP {(int)response.StatusCode} {response.StatusCode}"
        };

        throw new HttpRequestException(message);
    }

    public void Dispose() => _http.Dispose();
}
