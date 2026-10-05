using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Data;
using StickyNotes.Models;
using StickyNotes.Sync;

namespace StickyNotes.Tests;

/// <summary>
/// 端到端防偷窥加密同步单元测试：
/// 1. 专属密钥与混淆解混淆
/// 2. AES-256 加解密与 SN1: 魔数守卫
/// 3. DTO 序列化互斥（密文模式绝无 content）
/// 4. 口令探针 .auth_verifier 校验与防篡改
/// 5. 存储后端子目录路由（stickynotes-data / stickynotes-vault）
/// 6. SyncEngine 加密全链路（多端加密同步、差量对账、错误密钥中止、坏密文隔离跳过）
/// </summary>
[TestClass]
public class SyncCryptoTests
{
    private const string DeviceA = "win-crypto-a";
    private const string DeviceB = "win-crypto-b";

    private static NoteRepository CreateRepository(out string directory)
    {
        directory = Path.Combine(TestEnvironment.TempRoot, "sync-crypto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var context = new SqliteDatabaseContext(Path.Combine(directory, "notes.db"));
        context.InitializeAndMigrateAsync().GetAwaiter().GetResult();
        return new NoteRepository(context);
    }

    [TestMethod]
    public void VaultSecret_GetSecret_ReturnsValid64CharSecret()
    {
        var secret = VaultSecret.GetSecret();
        Assert.IsFalse(string.IsNullOrWhiteSpace(secret));
        Assert.AreEqual(64, secret.Length, "生成的密钥应为 64 位字符");
    }

    [TestMethod]
    public void CryptoHelper_CreateAndUnwrapMagicPayload_Roundtrips()
    {
        var secret = VaultSecret.GetSecret();
        var plain = "测试便签正文\r\nHello World! 12345 special: @#$%^&*()_+";

        var (iv, payload) = CryptoHelper.CreateMagicPayload(plain, secret);
        Assert.IsFalse(string.IsNullOrWhiteSpace(iv));
        Assert.IsFalse(string.IsNullOrWhiteSpace(payload));

        var unwrapped = CryptoHelper.UnwrapMagicPayload(iv, payload, secret);
        Assert.AreEqual(plain, unwrapped);
    }

    [TestMethod]
    public void CryptoHelper_WrongSecret_ThrowsException()
    {
        var secret1 = VaultSecret.GetSecret();
        var secret2 = "wrong-secret-0000000000000000000000000000000000000000000000000000";
        var (iv, payload) = CryptoHelper.CreateMagicPayload("secret message", secret1);

        Assert.ThrowsException<System.Security.SecurityException>(() =>
        {
            CryptoHelper.UnwrapMagicPayload(iv, payload, secret2);
        });
    }

    [TestMethod]
    public void AuthVerifierDto_CreateAndVerify_Succeeds()
    {
        var secret = VaultSecret.GetSecret();
        var verifierJson = AuthVerifierDto.Create(secret);
        Assert.IsFalse(string.IsNullOrWhiteSpace(verifierJson));

        var ok = AuthVerifierDto.Verify(verifierJson, secret);
        Assert.IsTrue(ok, "相同密钥校验必须通过");

        var badSecret = "fake-secret-9999999999999999999999999999999999999999999999999999";
        var failed = AuthVerifierDto.Verify(verifierJson, badSecret);
        Assert.IsFalse(failed, "错误密钥校验必须失败");

        var garbageFailed = AuthVerifierDto.Verify("invalid json string", secret);
        Assert.IsFalse(garbageFailed, "非法 JSON 校验必须返回 false");
    }

    [TestMethod]
    public void SyncNoteDto_Encrypted_ExcludesContentInJson()
    {
        var secret = VaultSecret.GetSecret();
        var note = new Note
        {
            Id = Guid.NewGuid(),
            Content = "高度机密便签内容",
            UpdatedAt = DateTime.UtcNow
        };

        var dto = SyncNoteDto.FromNoteEncrypted(note, "win-test", secret);
        Assert.IsTrue(dto.IsEncrypted);
        Assert.IsNull(dto.Content);

        var json = SyncProtocol.Serialize(dto);
        Assert.IsFalse(json.Contains("高度机密便签内容"), "JSON 绝不能包含明文正文");
        Assert.IsFalse(json.Contains("\"content\""), "JSON 绝不能出现 content 键名");
        StringAssert.Contains(json, "\"iv\":");
        StringAssert.Contains(json, "\"payload\":");

        var deserialized = SyncProtocol.TryDeserialize(json, SyncProtocol.NoteKey(note.Id));
        Assert.IsNotNull(deserialized);
        Assert.IsTrue(deserialized!.IsEncrypted);
        Assert.IsNull(deserialized.Content);

        // 解密还原
        var restored = CryptoHelper.UnwrapMagicPayload(deserialized.Iv!, deserialized.Payload!, secret);
        Assert.AreEqual(note.Content, restored);
    }

    [TestMethod]
    public void StorageBackendFactory_EffectiveUrlAndPrefix_RoutesCorrectly()
    {
        // WebDAV
        var plainDav = StorageBackendFactory.GetEffectiveWebDavUrl("https://dav.example.com/dav/", enableEncryption: false);
        Assert.AreEqual("https://dav.example.com/dav/stickynotes-data/", plainDav);

        var vaultDav = StorageBackendFactory.GetEffectiveWebDavUrl("https://dav.example.com/dav/", enableEncryption: true);
        Assert.AreEqual("https://dav.example.com/dav/stickynotes-vault/", vaultDav);

        // 切换模式自动替换
        var switchedToVault = StorageBackendFactory.GetEffectiveWebDavUrl(plainDav, enableEncryption: true);
        Assert.AreEqual("https://dav.example.com/dav/stickynotes-vault/", switchedToVault);

        var switchedToPlain = StorageBackendFactory.GetEffectiveWebDavUrl(vaultDav, enableEncryption: false);
        Assert.AreEqual("https://dav.example.com/dav/stickynotes-data/", switchedToPlain);

        // S3 BasePrefix
        var plainS3 = StorageBackendFactory.GetEffectiveS3Prefix("", enableEncryption: false);
        Assert.AreEqual("stickynotes-data/", plainS3);

        var vaultS3 = StorageBackendFactory.GetEffectiveS3Prefix("", enableEncryption: true);
        Assert.AreEqual("stickynotes-vault/", vaultS3);

        var defaultS3 = StorageBackendFactory.GetEffectiveS3Prefix("stickynotes/", enableEncryption: true);
        Assert.AreEqual("stickynotes-vault/", defaultS3);

        var customS3 = StorageBackendFactory.GetEffectiveS3Prefix("backup/", enableEncryption: true);
        Assert.AreEqual("backup/stickynotes-vault/", customS3);

        var switchedCustom = StorageBackendFactory.GetEffectiveS3Prefix("backup/stickynotes-data/", enableEncryption: true);
        Assert.AreEqual("backup/stickynotes-vault/", switchedCustom);
    }

    [TestMethod]
    public async Task SyncEngine_EncryptedSync_FullRoundtripBetweenTwoDevices()
    {
        var repoA = CreateRepository(out var dirA);
        var repoB = CreateRepository(out var dirB);
        var backend = new FakeStorageBackend();
        var engineA = new SyncEngine(repoA);
        var engineB = new SyncEngine(repoB);

        try
        {
            // 1. Device A 创建便签并进行加密同步
            var note = new Note
            {
                Content = "设备 A 编写的私密便签\n换行第二行",
                Color = NoteColor.Purple,
                AlwaysOnTop = true,
                UpdatedAt = DateTime.UtcNow.AddMinutes(-10)
            };
            await repoA.SaveAsync(note);

            var summaryA = await engineA.RunAsync(backend, DeviceA, enableEncryption: true);
            Assert.IsNotNull(summaryA);
            Assert.AreEqual(1, summaryA!.Uploaded);

            // 验证远端存储中的内容：必须有探针，且便签必须为密文
            Assert.IsTrue(backend.Objects.ContainsKey(SyncProtocol.VerifierKey), "远端必须自举生成 .auth_verifier");
            var remoteKey = SyncProtocol.NoteKey(note.Id);
            Assert.IsTrue(backend.Objects.ContainsKey(remoteKey));
            var remoteRawJson = backend.Objects[remoteKey];
            Assert.IsFalse(remoteRawJson.Contains("设备 A 编写的私密便签"), "远端 JSON 不能有明文");
            Assert.IsFalse(remoteRawJson.Contains("\"content\""));
            StringAssert.Contains(remoteRawJson, "\"payload\"");

            // 2. Device B 进行加密同步下行
            var summaryB = await engineB.RunAsync(backend, DeviceB, enableEncryption: true);
            Assert.IsNotNull(summaryB);
            Assert.AreEqual(1, summaryB!.Downloaded);

            var noteOnB = await repoB.GetByIdAsync(note.Id);
            Assert.IsNotNull(noteOnB);
            Assert.AreEqual("设备 A 编写的私密便签\n换行第二行", noteOnB!.Content, "Device B 数据库中还原为完全一致的明文");
            Assert.AreEqual(NoteColor.Purple, noteOnB.Color);
            Assert.IsTrue(noteOnB.AlwaysOnTop);

            // 3. Device B 编辑便签 → 同步上行
            noteOnB.Content = "设备 B 修改了此私密便签";
            noteOnB.UpdatedAt = DateTime.UtcNow.AddMinutes(-5);
            await repoB.SaveAsync(noteOnB);

            var summaryB2 = await engineB.RunAsync(backend, DeviceB, enableEncryption: true);
            Assert.AreEqual(1, summaryB2!.Uploaded);

            // 4. Device A 接收更新
            var summaryA2 = await engineA.RunAsync(backend, DeviceA, enableEncryption: true);
            Assert.AreEqual(1, summaryA2!.Downloaded);
            var noteOnA2 = await repoA.GetByIdAsync(note.Id);
            Assert.AreEqual("设备 B 修改了此私密便签", noteOnA2!.Content);

            // 5. 墓碑同步：Device A 归档删除
            await repoA.ArchiveNoteAsync(note.Id);
            var summaryA3 = await engineA.RunAsync(backend, DeviceA, enableEncryption: true);
            Assert.AreEqual(1, summaryA3!.Uploaded);

            var summaryB3 = await engineB.RunAsync(backend, DeviceB, enableEncryption: true);
            Assert.AreEqual(1, summaryB3!.Downloaded);
            var activeB = await repoB.GetAllActiveAsync();
            Assert.AreEqual(0, activeB.Count, "Device B 上该便签应进入归档墓碑状态");
        }
        finally
        {
            try { Directory.Delete(dirA, true); } catch { }
            try { Directory.Delete(dirB, true); } catch { }
        }
    }

    [TestMethod]
    public async Task SyncEngine_BadVerifier_AbortsSyncImmediately()
    {
        var repo = CreateRepository(out var dir);
        var backend = new FakeStorageBackend();
        var engine = new SyncEngine(repo);

        try
        {
            // 在远端故意写入一个不同密钥生成的探针
            var otherSecret = "other-secret-1111111111111111111111111111111111111111111111111111";
            backend.Objects[SyncProtocol.VerifierKey] = AuthVerifierDto.Create(otherSecret);

            await repo.SaveAsync(new Note { Content = "测试便签", UpdatedAt = DateTime.UtcNow });

            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            {
                await engine.RunAsync(backend, DeviceA, enableEncryption: true);
            });

            StringAssert.Contains(ex.Message, "口令校验失败");
            Assert.AreEqual(0, backend.PutCount, "校验失败必须立即中止，绝不上载任何便签");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task SyncEngine_TamperedCiphertext_IsSkippedSafely()
    {
        var repo = CreateRepository(out var dir);
        var backend = new FakeStorageBackend();
        var engine = new SyncEngine(repo);

        try
        {
            var noteId = Guid.NewGuid();
            // 上传一个篡改了 payload 的非法密文 JSON
            backend.Objects[SyncProtocol.VerifierKey] = AuthVerifierDto.Create(VaultSecret.GetSecret());
            backend.Objects[SyncProtocol.NoteKey(noteId)] = "{\"schemaVersion\":1,\"id\":\"" + noteId + "\",\"iv\":\"AAECAwQFBgcICQoLDA0ODw==\",\"payload\":\"dGFtcGVyZWQtZGF0YQ==\",\"updatedAt\":\"2026-10-05T12:00:00Z\"}";

            var summary = await engine.RunAsync(backend, DeviceA, enableEncryption: true);
            Assert.IsNotNull(summary);
            Assert.AreEqual(1, summary!.SkippedInvalid, "损坏密文应被记录为 SkippedInvalid");
            Assert.AreEqual(0, summary.Downloaded, "损坏密文绝不落盘进本地库");

            var all = await repo.GetAllAsync();
            Assert.AreEqual(0, all.Count);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
