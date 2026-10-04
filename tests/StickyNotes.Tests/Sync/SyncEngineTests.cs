using System.IO;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Data;
using StickyNotes.Models;
using StickyNotes.Sync;

namespace StickyNotes.Tests;

/// <summary>
/// 同步引擎全量对账规则测试（协议设计 §4 / §8 清单）。
/// 每个用例使用独立隔离的 SQLite 数据目录，两台设备 = 两个仓储 + 共享一个 FakeStorageBackend。
/// </summary>
[TestClass]
public class SyncEngineTests
{
    private const string DeviceA = "win-aaaaaa";
    private const string DeviceB = "win-bbbbbb";

    private static NoteRepository CreateRepository(out string directory)
    {
        directory = Path.Combine(TestEnvironment.TempRoot, "sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var context = new SqliteDatabaseContext(Path.Combine(directory, "notes.db"));
        context.InitializeAndMigrateAsync().GetAwaiter().GetResult();
        return new NoteRepository(context);
    }

    /// <summary>以指定仓储为「一台设备」跑一轮同步</summary>
    private static SyncRoundSummary Run(NoteRepository repo, FakeStorageBackend backend, string deviceId)
        => new SyncEngine(repo).RunAsync(backend, deviceId).GetAwaiter().GetResult()!;

    private static Note NewNote(string content, DateTime? updatedAt = null, bool isDeleted = false) => new()
    {
        Content = content,
        UpdatedAt = updatedAt ?? DateTime.UtcNow,
        IsDeleted = isDeleted
    };

    private static SyncNoteDto RemoteNote(Guid id, string content, DateTime updatedAt, bool isDeleted = false) => new()
    {
        Id = id,
        Content = content,
        UpdatedAt = updatedAt,
        CreatedAt = updatedAt,
        IsDeleted = isDeleted
    };

    private static void SeedRemote(FakeStorageBackend backend, params SyncNoteDto[] dtos)
    {
        foreach (var dto in dtos)
        {
            backend.Objects[SyncProtocol.NoteKey(dto.Id)] = SyncProtocol.Serialize(dto);
        }
    }

    // ---- 首次同步与幂等 ----

    [TestMethod]
    public async Task FirstSync_UploadsAllLocalNotes()
    {
        var repo = CreateRepository(out _);
        var backend = new FakeStorageBackend();
        var n1 = NewNote("alpha");
        var n2 = NewNote("beta");
        await repo.SaveAsync(n1);
        await repo.SaveAsync(n2);

        var summary = Run(repo, backend, DeviceA);

        Assert.AreEqual(2, summary.Uploaded);
        Assert.AreEqual(2, backend.PutCount);
        Assert.IsTrue(backend.Objects.ContainsKey(SyncProtocol.NoteKey(n1.Id)));
        Assert.IsTrue(backend.Objects.ContainsKey(SyncProtocol.NoteKey(n2.Id)));
    }

    [TestMethod]
    public async Task FirstSync_DownloadsRemoteWithDefaults()
    {
        var repo = CreateRepository(out _);
        var backend = new FakeStorageBackend();
        var remote = RemoteNote(Guid.NewGuid(), "from-cloud", DateTime.UtcNow);
        SeedRemote(backend, remote);

        var summary = Run(repo, backend, DeviceA);

        Assert.AreEqual(1, summary.Downloaded);
        var local = (await repo.GetByIdAsync(remote.Id))!;
        Assert.AreEqual("from-cloud", local.Content);
        Assert.IsFalse(local.IsOpen, "远端来的便签不得自动弹窗");
        Assert.AreEqual(Note.DefaultWindowX, local.WindowX);
        Assert.AreEqual(Note.DefaultWindowWidth, local.WindowWidth);
    }

    [TestMethod]
    public async Task SecondRun_NoChanges_NoTransfer()
    {
        var repo = CreateRepository(out _);
        var backend = new FakeStorageBackend();
        await repo.SaveAsync(NewNote("stable"));

        Run(repo, backend, DeviceA);
        var putsAfterFirst = backend.PutCount;

        var summary = Run(repo, backend, DeviceA);

        Assert.AreEqual(0, summary.Uploaded, "内容无差异不得重传（防乒乓）");
        Assert.AreEqual(0, summary.Downloaded);
        Assert.AreEqual(putsAfterFirst, backend.PutCount);
    }

    // ---- LWW 裁决 ----

    [TestMethod]
    public async Task Lww_LocalNewerWins_Uploads()
    {
        var repo = CreateRepository(out _);
        var backend = new FakeStorageBackend();
        var id = Guid.NewGuid();
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        await repo.SaveAsync(new Note { Id = id, Content = "local-new", UpdatedAt = baseTime.AddMinutes(5) });
        SeedRemote(backend, RemoteNote(id, "remote-old", baseTime));

        var summary = Run(repo, backend, DeviceA);

        Assert.AreEqual(1, summary.Uploaded);
        Assert.AreEqual(0, summary.Downloaded);
        Assert.AreEqual("local-new", (await repo.GetByIdAsync(id))!.Content);
        StringAssert.Contains(backend.Objects[SyncProtocol.NoteKey(id)], "local-new");
    }

    [TestMethod]
    public async Task Lww_RemoteNewerWins_Downloads()
    {
        var repo = CreateRepository(out _);
        var backend = new FakeStorageBackend();
        var id = Guid.NewGuid();
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        await repo.SaveAsync(new Note { Id = id, Content = "local-old", UpdatedAt = baseTime });
        SeedRemote(backend, RemoteNote(id, "remote-new", baseTime.AddMinutes(5)));

        var summary = Run(repo, backend, DeviceA);

        Assert.AreEqual(1, summary.Downloaded);
        Assert.AreEqual(0, summary.Uploaded);
        Assert.AreEqual("remote-new", (await repo.GetByIdAsync(id))!.Content);
    }

    [TestMethod]
    public async Task Lww_EqualTimestampDifferentContent_LocalWins()
    {
        var repo = CreateRepository(out _);
        var backend = new FakeStorageBackend();
        var id = Guid.NewGuid();
        var sameTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await repo.SaveAsync(new Note { Id = id, Content = "local-version", UpdatedAt = sameTime });
        SeedRemote(backend, RemoteNote(id, "remote-version", sameTime));

        var summary = Run(repo, backend, DeviceA);

        Assert.AreEqual(1, summary.Uploaded, "时间戳相等且内容不同 → 本地胜（与导入规则 >= 一致）");
        Assert.AreEqual(0, summary.Downloaded);
    }

    [TestMethod]
    public async Task SameContentDifferentTimestamp_NoTransfer()
    {
        var repo = CreateRepository(out _);
        var backend = new FakeStorageBackend();
        var id = Guid.NewGuid();
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        await repo.SaveAsync(new Note { Id = id, Content = "same\ncontent", UpdatedAt = baseTime.AddMinutes(5) });
        SeedRemote(backend, RemoteNote(id, "same\ncontent", baseTime));

        var summary = Run(repo, backend, DeviceA);

        Assert.AreEqual(0, summary.Uploaded, "仅时间戳不同而内容相同不触发上传");
        Assert.AreEqual(0, summary.Downloaded);
        Assert.AreEqual(0, summary.GuardedSkipped);
    }

    // ---- 删除与墓碑 ----

    [TestMethod]
    public async Task Delete_PropagatesAsTombstone()
    {
        var repoA = CreateRepository(out var dirA);
        var repoB = CreateRepository(out var dirB);
        var backend = new FakeStorageBackend();

        var note = NewNote("to-be-deleted");
        await repoA.SaveAsync(note);
        Run(repoA, backend, DeviceA);
        Run(repoB, backend, DeviceB);
        Assert.IsNotNull(await repoB.GetByIdAsync(note.Id));

        await repoA.ArchiveNoteAsync(note.Id);
        Run(repoA, backend, DeviceA);
        var summaryB = Run(repoB, backend, DeviceB);

        Assert.AreEqual(1, summaryB.Downloaded, "墓碑应下行到 B");
        Assert.AreEqual(0, (await repoB.GetAllActiveAsync()).Count(n => n.Id == note.Id), "活动视图中不可见");
        var tombstoned = (await repoB.GetAllAsync()).First(n => n.Id == note.Id);
        Assert.IsTrue(tombstoned.IsDeleted);
    }

    [TestMethod]
    public async Task HardDeleteLocally_TombstoneReflowsButNeverResurrects()
    {
        var repoA = CreateRepository(out _);
        var repoB = CreateRepository(out _);
        var backend = new FakeStorageBackend();

        var note = NewNote("content-must-not-return");
        await repoA.SaveAsync(note);
        Run(repoA, backend, DeviceA);
        Run(repoB, backend, DeviceB);

        await repoA.ArchiveNoteAsync(note.Id);
        Run(repoA, backend, DeviceA);
        Run(repoB, backend, DeviceB);

        // B 清空回收站：本地行消失，远端墓碑仍在
        await repoB.HardDeleteAsync(note.Id);
        Run(repoB, backend, DeviceB);

        // 墓碑回流为不可见行，内容不得复活
        Assert.AreEqual(0, (await repoB.GetAllActiveAsync()).Count(n => n.Id == note.Id));
        var row = (await repoB.GetAllAsync()).FirstOrDefault(n => n.Id == note.Id);
        Assert.IsNotNull(row, "远端墓碑以 IsDeleted 行回流（无害且幂等）");
        Assert.IsTrue(row.IsDeleted);
    }

    [TestMethod]
    public async Task EditBeatsDelete_WhenEditIsNewer()
    {
        var repoA = CreateRepository(out _);
        var repoB = CreateRepository(out _);
        var backend = new FakeStorageBackend();

        var note = NewNote("original");
        await repoA.SaveAsync(note);
        Run(repoA, backend, DeviceA);
        Run(repoB, backend, DeviceB);

        await repoA.ArchiveNoteAsync(note.Id);          // A 删除于 T1
        Thread.Sleep(20);                                // 确保 B 的编辑时间戳严格更新
        var localB = (await repoB.GetByIdAsync(note.Id))!;
        localB.Content = "edited-after-delete";
        localB.IsDeleted = false;
        localB.UpdatedAt = DateTime.UtcNow;
        await repoB.SaveAsync(localB);                   // B 编辑于 T2 > T1

        Run(repoB, backend, DeviceB);
        Run(repoA, backend, DeviceA);

        var onA = (await repoA.GetAllAsync()).First(n => n.Id == note.Id);
        Assert.IsFalse(onA.IsDeleted, "编辑时间戳更新 → 编辑胜过删除，便签复活");
        Assert.AreEqual("edited-after-delete", onA.Content);
    }

    [TestMethod]
    public async Task DeleteBeatsEdit_WhenDeleteIsNewer()
    {
        var repoA = CreateRepository(out _);
        var repoB = CreateRepository(out _);
        var backend = new FakeStorageBackend();

        var note = NewNote("original");
        await repoA.SaveAsync(note);
        Run(repoA, backend, DeviceA);
        Run(repoB, backend, DeviceB);

        // B 先编辑（旧），A 后删除（新），A 必须先把墓碑推上远端，B 才能在对账中输给墓碑
        var localB = (await repoB.GetByIdAsync(note.Id))!;
        localB.Content = "edited-before-delete";
        localB.UpdatedAt = DateTime.UtcNow;
        await repoB.SaveAsync(localB);
        Thread.Sleep(20);
        await repoA.ArchiveNoteAsync(note.Id);
        Run(repoA, backend, DeviceA);

        Run(repoB, backend, DeviceB);

        Assert.AreEqual(0, (await repoB.GetAllActiveAsync()).Count(n => n.Id == note.Id), "删除时间戳更新 → 墓碑胜出");
    }

    // ---- 防御性解析 ----

    [TestMethod]
    public async Task BadFiles_AreSkipped_WithoutBreakingRound()
    {
        var repo = CreateRepository(out _);
        var backend = new FakeStorageBackend();

        var good = RemoteNote(Guid.NewGuid(), "good", DateTime.UtcNow);
        SeedRemote(backend, good);

        backend.Objects["notes/not-a-guid.json"] = "{}";
        backend.Objects["notes/00000000-0000-0000-0000-000000000000.json"] = "{ broken json";
        var mismatched = RemoteNote(Guid.NewGuid(), "mismatch", DateTime.UtcNow);
        backend.Objects[SyncProtocol.NoteKey(Guid.NewGuid())] = SyncProtocol.Serialize(mismatched); // id 与文件名不符
        var futureSchema = RemoteNote(Guid.NewGuid(), "future", DateTime.UtcNow);
        futureSchema.SchemaVersion = 99;
        backend.Objects[SyncProtocol.NoteKey(futureSchema.Id)] = SyncProtocol.Serialize(futureSchema);
        backend.Objects["notes/readme.txt"] = "foreign file";
        backend.Objects["meta/index.json"] = "{ \"future\": true }";

        var summary = Run(repo, backend, DeviceA);

        // 6 个非本协议/非法对象全部隔离：not-a-guid、坏 JSON、id 不符、schema 过高、
        // readme.txt（陌生扩展名）、meta/index.json（陌生前缀）
        Assert.AreEqual(6, summary.SkippedInvalid, "坏文件/陌生文件全部隔离");
        Assert.IsNotNull(await repo.GetByIdAsync(good.Id));
        Assert.IsFalse(backend.DeletedKeys.Any(), "陌生文件绝不被清理（向前兼容）");
    }

    [TestMethod]
    public async Task OversizedNote_IsSkipped()
    {
        var repo = CreateRepository(out _);
        var backend = new FakeStorageBackend();

        var huge = RemoteNote(Guid.NewGuid(), new string('a', SyncProtocol.MaxNoteFileBytes + 1024), DateTime.UtcNow);
        SeedRemote(backend, huge);

        var summary = Run(repo, backend, DeviceA);

        Assert.AreEqual(1, summary.SkippedInvalid);
        Assert.IsNull(await repo.GetByIdAsync(huge.Id));
    }

    // ---- 故障处理 ----

    [TestMethod]
    public async Task AllDownloadsFail_RoundFails()
    {
        var repo = CreateRepository(out _);
        var backend = new FakeStorageBackend();
        SeedRemote(backend, RemoteNote(Guid.NewGuid(), "x", DateTime.UtcNow));
        backend.GetFault = _ => new HttpRequestException("offline");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => new SyncEngine(repo).RunAsync(backend, DeviceA));
    }

    [TestMethod]
    public async Task PartialDownloadFail_StillAppliesGoodFiles()
    {
        var repo = CreateRepository(out _);
        var backend = new FakeStorageBackend();
        var good = RemoteNote(Guid.NewGuid(), "good", DateTime.UtcNow);
        SeedRemote(backend, good, RemoteNote(Guid.NewGuid(), "bad", DateTime.UtcNow));
        var badKey = backend.Objects.Keys.First(k => k != SyncProtocol.NoteKey(good.Id));
        backend.GetFault = key => key == badKey ? new HttpRequestException("flaky") : null;

        var summary = Run(repo, backend, DeviceA);

        // 网络失败计入 failedDownloads（不等于解析类 skip），但整轮继续，合法文件照常应用
        Assert.IsNotNull(await repo.GetByIdAsync(good.Id));
        Assert.AreEqual(1, summary.Downloaded);
        Assert.AreEqual(1, backend.GetCount - 1, "除失败对象外，其余对象均已下载");
    }

    [TestMethod]
    public async Task SingleFlight_SecondRunIsSkippedNotQueued()
    {
        var repo = CreateRepository(out _);
        var backend = new FakeStorageBackend();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.GetDelay = async _ => await gate.Task.ConfigureAwait(false);
        SeedRemote(backend, RemoteNote(Guid.NewGuid(), "slow", DateTime.UtcNow));

        var engine = new SyncEngine(repo);
        var first = Task.Run(() => engine.RunAsync(backend, DeviceA));

        // 等第一轮确实卡在下载
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (backend.GetCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        var second = await engine.RunAsync(backend, DeviceB);
        Assert.IsNull(second, "单飞：上一轮未结束本轮直接放弃");

        gate.SetResult();
        var firstResult = await first;
        Assert.IsNotNull(firstResult);
        Assert.AreEqual(1, firstResult.Downloaded);
    }

    // ---- 换行规范化（协议设计 §3.2）----

    [TestMethod]
    public async Task WireContent_IsNewlineNormalized()
    {
        var repo = CreateRepository(out _);
        var backend = new FakeStorageBackend();
        var note = NewNote("line1\r\nline2\rline3\nline4");
        await repo.SaveAsync(note);

        Run(repo, backend, DeviceA);

        var wire = backend.Objects[SyncProtocol.NoteKey(note.Id)];
        Assert.IsFalse(wire.Contains('\r'), "wire 内容必须只含 \\n");
        StringAssert.Contains(wire, "line1\\nline2\\nline3\\nline4");
    }

    [TestMethod]
    public async Task NewlineOnlyDifference_DoesNotTriggerTransfer()
    {
        var repo = CreateRepository(out _);
        var backend = new FakeStorageBackend();
        var id = Guid.NewGuid();
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        await repo.SaveAsync(new Note { Id = id, Content = "a\r\nb", UpdatedAt = baseTime.AddMinutes(5) });
        SeedRemote(backend, RemoteNote(id, "a\nb", baseTime));

        var summary = Run(repo, backend, DeviceA);

        Assert.AreEqual(0, summary.Uploaded);
        Assert.AreEqual(0, summary.Downloaded);
    }

    [TestMethod]
    public async Task DownloadedContent_KeepsUnixNewlines()
    {
        var repo = CreateRepository(out _);
        var backend = new FakeStorageBackend();
        var remote = RemoteNote(Guid.NewGuid(), "x\ny", DateTime.UtcNow);
        SeedRemote(backend, remote);

        Run(repo, backend, DeviceA);

        Assert.AreEqual("x\ny", (await repo.GetByIdAsync(remote.Id))!.Content);
    }

    // ---- 多设备收敛 ----

    [TestMethod]
    public async Task TwoDevices_ExchangeChanges_AndConverge()
    {
        var repoA = CreateRepository(out _);
        var repoB = CreateRepository(out _);
        var backend = new FakeStorageBackend();

        var a1 = NewNote("from-a");
        await repoA.SaveAsync(a1);
        Run(repoA, backend, DeviceA);
        Run(repoB, backend, DeviceB);

        var b1 = NewNote("from-b");
        await repoB.SaveAsync(b1);
        Run(repoB, backend, DeviceB);
        Run(repoA, backend, DeviceA);
        Run(repoB, backend, DeviceB);

        var aAll = (await repoA.GetAllAsync()).Where(n => !n.IsDeleted).OrderBy(n => n.Content).ToList();
        var bAll = (await repoB.GetAllAsync()).Where(n => !n.IsDeleted).OrderBy(n => n.Content).ToList();

        Assert.AreEqual(2, aAll.Count);
        CollectionAssert.AreEqual(aAll.Select(n => (n.Id, n.Content)).ToList(), bAll.Select(n => (n.Id, n.Content)).ToList());
    }
}
