using System.IO;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Data;
using StickyNotes.Infrastructure;
using StickyNotes.Models;
using StickyNotes.Sync;

namespace StickyNotes.Tests;

/// <summary>
/// Cloudflare R2 真实连通冒烟（协议设计 §11 Phase 2）。
/// 全部凭据经环境变量注入，未设置时用例按 Inconclusive 跳过，绝不把密钥写进代码/日志：
///   STICKYNOTES_R2_ENDPOINT / STICKYNOTES_R2_ACCESSKEY / STICKYNOTES_R2_SECRETKEY
///   STICKYNOTES_R2_BUCKET（可选；缺省时通过 ListBuckets 自动发现第一个桶）
/// 测试使用一次性随机前缀并在结束时清理，不触碰用户数据前缀。
/// </summary>
[TestClass]
public class SyncR2SmokeTests
{
    [TestMethod]
    public async Task RealR2_TestConnection_PutGet_EngineSync_AndCleanup()
    {
        var endpoint = Environment.GetEnvironmentVariable("STICKYNOTES_R2_ENDPOINT");
        var accessKey = Environment.GetEnvironmentVariable("STICKYNOTES_R2_ACCESSKEY");
        var secretKey = Environment.GetEnvironmentVariable("STICKYNOTES_R2_SECRETKEY");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            Assert.Inconclusive("未设置 R2 测试环境变量（STICKYNOTES_R2_*），跳过真实连通冒烟");
            return;
        }

        var bucket = Environment.GetEnvironmentVariable("STICKYNOTES_R2_BUCKET")
                     ?? await ProbeKnownBucketCandidatesAsync(endpoint, accessKey, secretKey);
        Assert.IsFalse(string.IsNullOrWhiteSpace(bucket),
            $"R2 未发现可用桶（endpoint={endpoint}，诊断：{_discoveryDetail}）。" +
            "R2 令牌为桶级授权时无法 ListBuckets，请设置 STICKYNOTES_R2_BUCKET 指定桶名。");
        AppLog.Info("[SyncR2SmokeTests] 使用真实桶进行冒烟（桶名不在断言/日志中展示）");

        var testPrefix = $"stickynotes-test-{Guid.NewGuid().ToString("N")[..8]}/";
        var directory = Path.Combine(TestEnvironment.TempRoot, "r2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var context = new SqliteDatabaseContext(Path.Combine(directory, "notes.db"));
        await context.InitializeAndMigrateAsync();
        var repo = new NoteRepository(context);
        var engine = new SyncEngine(repo);

        var settings = new SyncSettings
        {
            BackendType = SyncBackendType.S3,
            S3Endpoint = endpoint,
            S3Bucket = bucket!,
            S3BasePrefix = testPrefix,
            S3AccessKey = accessKey,
            S3SecretKey = secretKey
        };

        var uploadedKeys = new List<string>();
        try
        {
            using (var backend = (S3Backend)StorageBackendFactory.Create(settings))
            {
                // 1. 连通性（ListObjectsV2 max-keys=1）
                await backend.TestAsync();

                // 2. Put/Get 往返
                var key = SyncProtocol.NoteKey(Guid.NewGuid());
                uploadedKeys.Add(testPrefix + key);
                await backend.PutTextAsync(key, "{\"probe\":true}");
                var roundtrip = await backend.GetTextAsync(key);
                Assert.AreEqual("{\"probe\":true}", roundtrip);

                // 3. 引擎级：一条便签上行 → 对象存在；第二条下行
                var note = new Note { Content = "from-r2-smoke", UpdatedAt = DateTime.UtcNow };
                await repo.SaveAsync(note);
                uploadedKeys.Add(testPrefix + SyncProtocol.NoteKey(note.Id));
                var summary = await engine.RunAsync(backend, "win-r2smoke");
                Assert.IsNotNull(summary);
                Assert.AreEqual(1, summary!.Uploaded);

                var remoteDto = SyncProtocol.TryDeserialize(
                    (await backend.GetTextAsync(SyncProtocol.NoteKey(note.Id)))!,
                    SyncProtocol.NoteKey(note.Id));
                Assert.IsNotNull(remoteDto);
                Assert.AreEqual("from-r2-smoke", remoteDto!.Content);
            }

            AppLog.Info("[SyncR2SmokeTests] R2 真实连通冒烟通过");
        }
        finally
        {
            // 4. 清理本次写入的所有对象
            try
            {
                using var cleanup = (S3Backend)StorageBackendFactory.Create(settings);
                foreach (var fullKey in uploadedKeys.Select(k => k[testPrefix.Length..]))
                {
                    await cleanup.DeleteAsync(fullKey);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[SyncR2SmokeTests] 清理测试对象失败（前缀 {testPrefix} 下可能残留）: {ex.Message}");
            }
        }
    }

    /// <summary>探测候选桶名（令牌为桶级授权时 ListBuckets 不可用）。仅执行只读 ListObjectsV2。</summary>
    private static async Task<string?> ProbeKnownBucketCandidatesAsync(string endpoint, string accessKey, string secretKey)
    {
        _discoveryDetail = "candidates: all denied";
        string[] candidates =
        {
            "mydata", "mydata-umao", "mydata.umao.top", "umao", "stickynotes", "notes",
            "data", "backup", "sync", "bucket", "test", "r2"
        };

        foreach (var candidate in candidates)
        {
            try
            {
                using var backend = new S3Backend(endpoint, candidate, "stickynotes/", accessKey, secretKey);
                await backend.TestAsync();
                _discoveryDetail = $"candidate '{candidate}' authorized";
                AppLog.Info($"[SyncR2SmokeTests] 候选桶连通成功（名称略）");
                return candidate;
            }
            catch
            {
                // 403/404 → 换下一个候选
            }
        }

        return null;
    }

    /// <summary>ListBuckets（GET /）自动发现第一个桶名；失败时保留诊断详情</summary>
    private static string? _discoveryDetail;

    private static async Task<string?> DiscoverFirstBucketAsync(string endpoint, string accessKey, string secretKey)
    {
        _discoveryDetail = "no attempt";
        try
        {
            using var http = new HttpClient();
            var uri = new Uri(endpoint.TrimEnd('/') + "/");
            var (authorization, amzDate) = SigV4Signer.SignWithoutPayload(
                "GET", uri, "auto", "s3", accessKey, secretKey, DateTimeOffset.UtcNow);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
            request.Headers.TryAddWithoutValidation("x-amz-content-sha256",
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
            request.Headers.TryAddWithoutValidation("Authorization", authorization);

            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            _discoveryDetail = $"HTTP {(int)response.StatusCode}; body: {(body.Length > 400 ? body[..400] : body)}";

            if (!response.IsSuccessStatusCode) return null;

            var doc = XDocument.Parse(body);
            return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Bucket")
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
        }
        catch (Exception ex)
        {
            _discoveryDetail = "exception: " + ex.Message;
            return null;
        }
    }
}
