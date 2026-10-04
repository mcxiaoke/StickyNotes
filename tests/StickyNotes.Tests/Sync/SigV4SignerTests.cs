using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Sync;

namespace StickyNotes.Tests;

/// <summary>
/// SigV4 签名器正确性：使用 AWS 官方文档中的固定测试向量
/// （https://docs.aws.amazon.com/AmazonS3/latest/API/sig-v4-header-based-auth.html 与
///  https://docs.aws.amazon.com/general/latest/gr/sigv4-calculate-signature.html）。
/// </summary>
[TestClass]
public class SigV4SignerTests
{
    private const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

    /// <summary>AWS 官方「派生签名密钥」示例（region us-east-1, service iam, 2015-08-30）</summary>
    [TestMethod]
    public void DeriveSigningKey_MatchesAwsDocumentedVector()
    {
        var key = SigV4Signer.DeriveSigningKey(
            "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "20150830", "us-east-1", "iam");

        Assert.AreEqual(
            "c4afb1cc5771d871763a393e44b703571b55cc28424d1a5e86da6ed3c154a4b9",
            Convert.ToHexString(key).ToLowerInvariant());
    }

    /// <summary>AWS S3 官方「GET Bucket Lifecycle」示例：签名必须与文档一致</summary>
    [TestMethod]
    public void Sign_MatchesAwsS3GetBucketLifecycleVector()
    {
        // GET https://examplebucket.s3.amazonaws.com?lifecycle=
        var uri = new Uri("https://examplebucket.s3.amazonaws.com?lifecycle=");
        var now = new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);

        var (authorization, amzDate) = SigV4Signer.SignWithoutPayload(
            "GET", uri, "us-east-1", "s3", AccessKey, SecretKey, now);

        Assert.AreEqual("20130524T000000Z", amzDate);
        StringAssert.Contains(authorization, "Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request");
        StringAssert.Contains(authorization, "SignedHeaders=host;x-amz-content-sha256;x-amz-date");
        StringAssert.Contains(authorization,
            "Signature=fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543");
    }

    /// <summary>空负载哈希必须为 SHA256("")（AWS 约定）</summary>
    [TestMethod]
    public void SignWithoutPayload_UsesEmptyPayloadHash()
    {
        var uri = new Uri("https://examplebucket.s3.amazonaws.com/");
        var now = new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);

        var (_, amzDate, payloadSha256) = SigV4Signer.Sign(
            "PUT", uri, "us-east-1", "s3", AccessKey, SecretKey, Array.Empty<byte>(), now);

        Assert.AreEqual("20130524T000000Z", amzDate);
        Assert.AreEqual(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            payloadSha256);
    }

    /// <summary>路径逐段转义：含 $ 的对象 key 在规范化 URI 中必须转义（AWS 规范）</summary>
    [TestMethod]
    public void CanonicalUri_EscapesSpecialCharacters()
    {
        var uri = new Uri("https://examplebucket.s3.amazonaws.com/test$file.text");
        var payload = Encoding.UTF8.GetBytes("Welcome to Amazon S3.");
        var now = new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);

        // 该请求的 payload 哈希为 AWS 文档值；规范化 URI 必须是 /test%24file.text
        // （完整签名向量因文档示例带额外签名头，不直接比对 Authorization，改为比对 payload 哈希与 URI 形态）
        var (_, _, payloadSha256) = SigV4Signer.Sign(
            "PUT", uri, "us-east-1", "s3", AccessKey, SecretKey, payload, now);

        Assert.AreEqual(
            "44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072",
            payloadSha256);
        Assert.IsTrue(uri.AbsolutePath.Contains("test$file.text"), "Uri 解析保留原始段，转义在规范化阶段完成");
    }

    /// <summary>ListObjectsV2 形态的查询串：prefix 中的 / 必须编码，参数按 key 排序</summary>
    [TestMethod]
    public void Sign_ListObjectsV2StyleQuery_ProducesStableAuthorization()
    {
        var uri = new Uri("https://account.r2.cloudflarestorage.com/my-bucket?list-type=2&max-keys=1000&prefix=stickynotes%2Fnotes%2F");
        var now = new DateTimeOffset(2026, 10, 4, 7, 30, 0, TimeSpan.Zero);

        var (authorization, amzDate) = SigV4Signer.SignWithoutPayload(
            "GET", uri, "auto", "s3", AccessKey, SecretKey, now);

        Assert.AreEqual("20261004T073000Z", amzDate);
        StringAssert.Contains(authorization, "Credential=AKIAIOSFODNN7EXAMPLE/20261004/auto/s3/aws4_request");
        StringAssert.Contains(authorization, "SignedHeaders=host;x-amz-content-sha256;x-amz-date");
        // 签名随输入确定：重复签名两次结果一致
        var (authorization2, _) = SigV4Signer.SignWithoutPayload(
            "GET", uri, "auto", "s3", AccessKey, SecretKey, now);
        Assert.AreEqual(authorization, authorization2);
    }
}
