using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Data;
using StickyNotes.Models;
using StickyNotes.Sync;

namespace StickyNotes.Tests;

/// <summary>
/// 协议 wire 格式测试：序列化约定、防御性解析、ApplyRemoteBatch 下行守卫。
/// </summary>
[TestClass]
public class SyncProtocolTests
{
    [TestMethod]
    public void Serialize_UsesCamelCase_StringEnums_AndIsoUtcTimestamps()
    {
        var dto = new SyncNoteDto
        {
            Id = Guid.Parse("9f3c8a4e-1b2d-4c3e-a5f6-0123456789ab"),
            Content = "hello",
            Color = NoteColor.Purple,
            IsPinnedInList = true,
            AlwaysOnTop = false,
            IsDeleted = false,
            CreatedAt = new DateTime(2026, 10, 4, 3, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 10, 4, 7, 30, 0, DateTimeKind.Utc),
            DeviceId = "win-8xf7ad"
        };

        var json = SyncProtocol.Serialize(dto);

        StringAssert.Contains(json, "\"schemaVersion\":1");
        StringAssert.Contains(json, "\"color\":\"purple\"");
        StringAssert.Contains(json, "\"isPinnedInList\":true");
        StringAssert.Contains(json, "\"createdAt\":\"2026-10-04T03:00:00.0000000Z\"");
        StringAssert.Contains(json, "\"updatedAt\":\"2026-10-04T07:30:00.0000000Z\"");
        StringAssert.Contains(json, "\"deviceId\":\"win-8xf7ad\"");
    }

    [TestMethod]
    public void Serialize_NonUtcKinds_AreNormalizedToUtc()
    {
        var dto = new SyncNoteDto
        {
            Id = Guid.NewGuid(),
            UpdatedAt = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Local),
            CreatedAt = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Unspecified)
        };

        var json = SyncProtocol.Serialize(dto);

        StringAssert.Contains(json, "\"createdAt\":\"2026-10-04T12:00:00");
        Assert.IsTrue(json.Contains('Z'), "时间戳必须带 Z（Unspecified 按 UTC 处理）");

        var parsed = SyncProtocol.TryDeserialize(json, null);
        Assert.IsNotNull(parsed);
        Assert.AreEqual(DateTimeKind.Utc, parsed.UpdatedAt.Kind);
    }

    [TestMethod]
    public void Roundtrip_PreservesFields()
    {
        var dto = new SyncNoteDto
        {
            Id = Guid.NewGuid(),
            Content = "多行\n文本",
            Color = NoteColor.Blue,
            IsPinnedInList = true,
            AlwaysOnTop = true,
            IsDeleted = true,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow,
            DeviceId = "win-abcdef"
        };

        var parsed = SyncProtocol.TryDeserialize(SyncProtocol.Serialize(dto), SyncProtocol.NoteKey(dto.Id));

        Assert.IsNotNull(parsed);
        Assert.AreEqual(dto.Id, parsed.Id);
        Assert.AreEqual(dto.Content, parsed.Content);
        Assert.AreEqual(dto.Color, parsed.Color);
        Assert.AreEqual(dto.IsPinnedInList, parsed.IsPinnedInList);
        Assert.AreEqual(dto.AlwaysOnTop, parsed.AlwaysOnTop);
        Assert.AreEqual(dto.IsDeleted, parsed.IsDeleted);
        Assert.AreEqual(dto.DeviceId, parsed.DeviceId);
        Assert.AreEqual(DateTimeKind.Utc, parsed.UpdatedAt.Kind);
    }

    [TestMethod]
    public void TryDeserialize_RejectsBadPayloads()
    {
        var id = Guid.NewGuid();
        var key = SyncProtocol.NoteKey(id);

        Assert.IsNull(SyncProtocol.TryDeserialize("{ not json", key), "坏 JSON");
        Assert.IsNull(SyncProtocol.TryDeserialize("{}", key), "缺字段（id 为空）");

        var mismatch = SyncProtocol.Serialize(RemoteNote(Guid.NewGuid(), "x", DateTime.UtcNow));
        Assert.IsNull(SyncProtocol.TryDeserialize(mismatch, key), "id 与文件名不符");

        var future = RemoteNote(id, "future", DateTime.UtcNow);
        future.SchemaVersion = 2;
        Assert.IsNull(SyncProtocol.TryDeserialize(SyncProtocol.Serialize(future), key), "未知更高 schemaVersion");

        var jsonText = "{\"schemaVersion\":1,\"id\":\"" + id + "\",\"content\":\"ok\",\"color\":\"yellow\"," +
                       "\"isPinnedInList\":false,\"alwaysOnTop\":false,\"isDeleted\":false," +
                       "\"createdAt\":\"2026-10-04T03:00:00Z\",\"updatedAt\":\"2026-10-04T03:00:00Z\"}";
        Assert.IsNotNull(SyncProtocol.TryDeserialize(jsonText, key), "小写枚举值与无毫秒 ISO 时间戳应可解析");
    }

    [TestMethod]
    public void NoteKey_IsLowercaseGuidDFormat()
    {
        var id = Guid.NewGuid();
        var key = SyncProtocol.NoteKey(id);

        Assert.AreEqual("notes/" + id.ToString("D") + ".json", key);
        Assert.IsTrue(SyncProtocol.TryGetNoteId(key, out var parsed));
        Assert.AreEqual(id, parsed);
        Assert.IsFalse(SyncProtocol.TryGetNoteId("notes/readme.txt", out _));
        Assert.IsFalse(SyncProtocol.TryGetNoteId("meta/index.json", out _));
    }

    [TestMethod]
    public void BusinessEquals_IgnoresTimestampsAndDeviceId_ButComparesFlags()
    {
        var time = DateTime.UtcNow;
        var a = RemoteNote(Guid.NewGuid(), "same", time, isDeleted: false);
        var b = RemoteNote(a.Id, "same", time.AddHours(1), isDeleted: false);
        b.DeviceId = "other";

        Assert.IsTrue(a.BusinessEquals(b), "仅时间戳/设备标识不同 → 语义相等");

        b.IsDeleted = true;
        Assert.IsFalse(a.BusinessEquals(b), "删除标志不同 → 语义不等");
    }

    // ---- ApplyRemoteBatchAsync 下行守卫 ----

    private static NoteRepository CreateRepository(out string directory)
    {
        directory = Path.Combine(TestEnvironment.TempRoot, "syncapply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var context = new SqliteDatabaseContext(Path.Combine(directory, "notes.db"));
        context.InitializeAndMigrateAsync().GetAwaiter().GetResult();
        return new NoteRepository(context);
    }

    [TestMethod]
    public async Task ApplyRemote_MissingRow_InsertsWithDefaults()
    {
        var repo = CreateRepository(out _);
        var remote = new Note
        {
            Id = Guid.NewGuid(),
            Content = "fresh",
            UpdatedAt = DateTime.UtcNow,
            WindowX = 999, // 引擎构造的默认几何之外值应被忽略（插入即默认）
            WindowY = 999
        };

        var result = await repo.ApplyRemoteBatchAsync(new[] { new RemoteApplyItem(remote, null) });

        Assert.AreEqual(1, result.Applied);
        var stored = (await repo.GetByIdAsync(remote.Id))!;
        Assert.AreEqual("fresh", stored.Content);
        Assert.IsFalse(stored.IsOpen);
        Assert.AreEqual(Note.DefaultWindowX, stored.WindowX);
    }

    [TestMethod]
    public async Task ApplyRemote_GuardMatch_UpdatesBusinessFields_KeepsGeometry()
    {
        var repo = CreateRepository(out _);
        var id = Guid.NewGuid();
        var snapshotTime = DateTime.UtcNow.AddMinutes(-5);
        await repo.SaveAsync(new Note
        {
            Id = id,
            Content = "old",
            UpdatedAt = snapshotTime,
            WindowX = 321,
            WindowY = 111,
            WindowWidth = 500,
            WindowHeight = 300,
            IsOpen = true
        });

        var remote = new Note { Id = id, Content = "new", UpdatedAt = DateTime.UtcNow };

        var result = await repo.ApplyRemoteBatchAsync(new[] { new RemoteApplyItem(remote, snapshotTime) });

        Assert.AreEqual(1, result.Applied);
        Assert.AreEqual(0, result.GuardedSkipped);
        var stored = (await repo.GetByIdAsync(id))!;
        Assert.AreEqual("new", stored.Content);
        Assert.AreEqual(321, stored.WindowX, "窗口几何为设备本地属性，更新时保持本地现状");
        Assert.IsTrue(stored.IsOpen, "IsOpen 保持本地现状");
    }

    [TestMethod]
    public async Task ApplyRemote_GuardMismatch_SkipsOverwrite()
    {
        var repo = CreateRepository(out _);
        var id = Guid.NewGuid();
        var snapshotTime = DateTime.UtcNow.AddMinutes(-5);
        await repo.SaveAsync(new Note { Id = id, Content = "user-typed-during-sync", UpdatedAt = snapshotTime });

        // 快照之后用户又编辑了（UpdatedAt 前进）→ 远端旧内容不得覆盖
        var remote = new Note { Id = id, Content = "remote", UpdatedAt = DateTime.UtcNow };

        var result = await repo.ApplyRemoteBatchAsync(new[] { new RemoteApplyItem(remote, snapshotTime.AddMinutes(-1)) });

        Assert.AreEqual(0, result.Applied);
        Assert.AreEqual(1, result.GuardedSkipped);
        Assert.AreEqual("user-typed-during-sync", (await repo.GetByIdAsync(id))!.Content);
    }

    private static SyncNoteDto RemoteNote(Guid id, string content, DateTime updatedAt, bool isDeleted = false) => new()
    {
        Id = id,
        Content = content,
        UpdatedAt = updatedAt,
        CreatedAt = updatedAt,
        IsDeleted = isDeleted
    };
}
