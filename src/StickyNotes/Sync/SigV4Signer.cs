using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace StickyNotes.Sync;

/// <summary>
/// 最小 AWS Signature V4 签名器（协议设计 §5.2：为保持 Costura 单文件打包不引入 AWSSDK）。
/// 只覆盖本工程用到的形态：path-style S3 请求 + x-amz-content-sha256 + x-amz-date 两个签名头。
/// region 可为 "auto"（R2 约定）。
/// </summary>
public static class SigV4Signer
{
    private const string EmptyPayloadSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>带 payload 的请求签名（PUT）</summary>
    public static (string Authorization, string XAmzDate, string PayloadSha256) Sign(
        string method, Uri uri, string region, string service,
        string accessKey, string secretKey, byte[] payload, DateTimeOffset nowUtc)
    {
        var payloadHash = payload.Length == 0 ? EmptyPayloadSha256 : HexSha256(payload);
        return (SignCore(method, uri, region, service, accessKey, secretKey, payloadHash, nowUtc, out var amzDate),
            amzDate, payloadHash);
    }

    /// <summary>无 body 请求签名（GET/DELETE）</summary>
    public static (string Authorization, string XAmzDate) SignWithoutPayload(
        string method, Uri uri, string region, string service,
        string accessKey, string secretKey, DateTimeOffset nowUtc)
    {
        var authorization = SignCore(method, uri, region, service, accessKey, secretKey, EmptyPayloadSha256, nowUtc, out var amzDate);
        return (authorization, amzDate);
    }

    private static string SignCore(
        string method, Uri uri, string region, string service,
        string accessKey, string secretKey, string payloadHash, DateTimeOffset nowUtc, out string amzDate)
    {
        var dateStamp = nowUtc.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        amzDate = nowUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        var canonicalUri = CanonicalUri(uri);
        var canonicalQuery = CanonicalQuery(uri.Query);
        var canonicalHeaders =
            $"host:{CanonicalHost(uri)}\n" +
            $"x-amz-content-sha256:{payloadHash}\n" +
            $"x-amz-date:{amzDate}\n";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";

        var canonicalRequest = string.Join("\n",
            method, canonicalUri, canonicalQuery, canonicalHeaders, signedHeaders, payloadHash);

        var scope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = string.Join("\n",
            Algorithm, amzDate, scope, HexSha256(Encoding.UTF8.GetBytes(canonicalRequest)));

        var signingKey = DeriveSigningKey(secretKey, dateStamp, region, service);
        var signature = Hex(HmacSha256(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        return $"{Algorithm} Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    /// <summary>规范化 URI：逐段 RFC3986 转义，保留路径分隔符</summary>
    private static string CanonicalUri(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/');
        if (segments.Length == 0) return "/";

        return string.Join("/", segments.Select(s => s.Length == 0 ? string.Empty : Rfc3986Escape(s)));
    }

    /// <summary>规范化查询串：按 key 再按 value 排序，重新编码（query 中保留 '=' 关系）</summary>
    private static string CanonicalQuery(string rawQuery)
    {
        if (string.IsNullOrEmpty(rawQuery)) return string.Empty;

        var pairs = new List<(string Key, string Value)>();
        foreach (var part in rawQuery.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = part.IndexOf('=');
            var rawKey = separatorIndex < 0 ? part : part[..separatorIndex];
            var rawValue = separatorIndex < 0 ? string.Empty : part[(separatorIndex + 1)..];
            pairs.Add((Rfc3986Escape(Uri.UnescapeDataString(rawKey)), Rfc3986Escape(Uri.UnescapeDataString(rawValue))));
        }

        return string.Join("&",
            pairs.OrderBy(p => p.Key, StringComparer.Ordinal)
                 .ThenBy(p => p.Value, StringComparer.Ordinal)
                 .Select(p => $"{p.Key}={p.Value}"));
    }

    private static string CanonicalHost(Uri uri) =>
        uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

    /// <summary>RFC 3986 编码（EscapeDataString 在 .NET 中即符合：仅保留 A-Z a-z 0-9 - _ . ~）</summary>
    private static string Rfc3986Escape(string value) => Uri.EscapeDataString(value);

    internal static byte[] DeriveSigningKey(string secretKey, string dateStamp, string region, string service)
    {
        var kDate = HmacSha256(Encoding.UTF8.GetBytes($"AWS4{secretKey}"), Encoding.UTF8.GetBytes(dateStamp));
        var kRegion = HmacSha256(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HmacSha256(kRegion, Encoding.UTF8.GetBytes(service));
        return HmacSha256(kService, Encoding.UTF8.GetBytes("aws4_request"));
    }

    private static byte[] HmacSha256(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    private static string HexSha256(byte[] data) => Hex(SHA256.HashData(data));

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
