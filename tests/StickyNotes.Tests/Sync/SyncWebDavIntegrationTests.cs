using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Data;
using StickyNotes.Infrastructure;
using StickyNotes.Models;
using StickyNotes.Sync;

namespace StickyNotes.Tests;

/// <summary>
/// 真实 WebDAV 服务器集成验证（协议设计 §11 Phase 1 手动清单的自动化版）：
/// 用本机 webdav.exe（hacdias/webdav v5）起隔离实例，两个仓储模拟两台设备，
/// 走真实 HTTP 完成对账、LWW、删除传播。服务器缺失时用例按 Inconclusive 跳过。
/// 服务器路径优先读环境变量 STICKYNOTES_WEBDAV_EXE。
/// </summary>
[TestClass]
public class SyncWebDavIntegrationTests
{
    private static string? FindWebDavExecutable()
    {
        var fromEnv = Environment.GetEnvironmentVariable("STICKYNOTES_WEBDAV_EXE");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        const string defaultPath = @"C:\Home\Develop\tools\webdav.exe";
        return File.Exists(defaultPath) ? defaultPath : null;
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static NoteRepository CreateRepository()
    {
        var directory = Path.Combine(TestEnvironment.TempRoot, "wdav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var context = new SqliteDatabaseContext(Path.Combine(directory, "notes.db"));
        context.InitializeAndMigrateAsync().GetAwaiter().GetResult();
        return new NoteRepository(context);
    }

    [TestMethod]
    public async Task RealWebDavServer_TwoDevicesSync_DeletePropagation()
    {
        var exePath = FindWebDavExecutable();
        if (exePath == null)
        {
            Assert.Inconclusive("未找到 webdav.exe（设置 STICKYNOTES_WEBDAV_EXE 可启用本用例）");
            return;
        }

        var workDir = Path.Combine(TestEnvironment.TempRoot, "wdav-server-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var storageDir = Path.Combine(workDir, "storage");
        Directory.CreateDirectory(storageDir);

        var port = FindFreePort();
        var username = "tester";
        var password = "pass-" + Guid.NewGuid().ToString("N")[..8];
        var configFile = Path.Combine(workDir, "config.yaml");
        await File.WriteAllTextAsync(configFile,
            $"address: 127.0.0.1\nport: {port}\ndirectory: {storageDir.Replace('\\', '/')}\n" +
            $"permissions: CRUD\nusers:\n  - username: {username}\n    password: {password}\n");

        var server = Process.Start(new ProcessStartInfo(exePath, $"--config \"{configFile}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;

        try
        {
            // 等待端口就绪（进程崩溃则直接失败并带上输出）
            var baseUrl = $"http://127.0.0.1:{port}/";
            var ready = false;
            for (var i = 0; i < 50; i++)
            {
                if (server.HasExited)
                {
                    var stderr = await server.StandardError.ReadToEndAsync();
                    Assert.Fail($"WebDAV 服务器启动失败: {stderr}");
                }

                try
                {
                    using var backend = new WebDavBackend(baseUrl, username, password, allowInsecureHttp: true);
                    await backend.TestAsync();
                    ready = true;
                    break;
                }
                catch when (i < 49)
                {
                    await Task.Delay(100);
                }
            }

            Assert.IsTrue(ready, "服务器在 5 秒内未就绪");

            // 「测试连接」语义：真实服务器 + 凭据验证
            using (var backend = new WebDavBackend(baseUrl, username, password, allowInsecureHttp: true))
            {
                await backend.TestAsync();
            }

            var repoA = CreateRepository();
            var repoB = CreateRepository();
            var engineA = new SyncEngine(repoA);
            var engineB = new SyncEngine(repoB);
            const string deviceA = "win-intgra";
            const string deviceB = "win-intgrb";

            using (var backend = new WebDavBackend(baseUrl, username, password, allowInsecureHttp: true))
            {
                // 1. A 创建两条 → 同步上行
                var n1 = new Note { Content = "first\r\nline", UpdatedAt = DateTime.UtcNow.AddMinutes(-5) };
                var n2 = new Note { Content = "second", UpdatedAt = DateTime.UtcNow.AddMinutes(-4) };
                await repoA.SaveAsync(n1);
                await repoA.SaveAsync(n2);
                var s1 = await engineA.RunAsync(backend, deviceA);
                Assert.IsNotNull(s1);
                Assert.AreEqual(2, s1!.Uploaded, "首轮应上行两条");
                Assert.AreEqual(0, s1.SkippedInvalid);

                // 2. B 首轮 → 全部下行（几何默认、IsOpen=false）
                var s2 = await engineB.RunAsync(backend, deviceB);
                Assert.IsNotNull(s2);
                Assert.AreEqual(2, s2!.Downloaded);
                var b1 = (await repoB.GetByIdAsync(n1.Id))!;
                Assert.AreEqual("first\nline", b1.Content, "下行内容为协议规范化换行");
                Assert.IsFalse(b1.IsOpen);

                // 3. B 编辑 n2（时间戳更新）→ 上行；A 对账后收到 B 的新内容
                var b2 = (await repoB.GetByIdAsync(n2.Id))!;
                b2.Content = "second-edited-by-b";
                b2.UpdatedAt = DateTime.UtcNow;
                await repoB.SaveAsync(b2);
                var s3 = await engineB.RunAsync(backend, deviceB);
                Assert.AreEqual(1, s3!.Uploaded);
                var s4 = await engineA.RunAsync(backend, deviceA);
                Assert.AreEqual(1, s4!.Downloaded);
                Assert.AreEqual("second-edited-by-b", (await repoA.GetByIdAsync(n2.Id))!.Content);

                // 4. A 删除 n1（墓碑）→ 传播到 B；B 的活动视图为空
                await repoA.ArchiveNoteAsync(n1.Id);
                var s5 = await engineA.RunAsync(backend, deviceA);
                Assert.AreEqual(1, s5!.Uploaded, "墓碑作为文件更新上行");
                var s6 = await engineB.RunAsync(backend, deviceB);
                Assert.AreEqual(1, s6!.Downloaded);
                Assert.IsFalse((await repoB.GetAllActiveAsync()).Any(n => n.Id == n1.Id));
                Assert.IsTrue((await repoB.GetAllAsync()).First(n => n.Id == n1.Id).IsDeleted);

                // 5. 幂等：双方再各跑一轮，零传输
                var s7 = await engineA.RunAsync(backend, deviceA);
                var s8 = await engineB.RunAsync(backend, deviceB);
                Assert.AreEqual(0, s7!.Uploaded + s7.Downloaded, "A 幂等");
                Assert.AreEqual(0, s8!.Uploaded + s8.Downloaded, "B 幂等");
            }

            AppLog.Info("[SyncWebDavIntegrationTests] 真实 WebDAV 全链路用例通过");
        }
        finally
        {
            try
            {
                if (!server.HasExited) server.Kill(entireProcessTree: true);
                server.Dispose();
            }
            catch
            {
                // 清理失败不影响判定
            }
        }
    }
}
