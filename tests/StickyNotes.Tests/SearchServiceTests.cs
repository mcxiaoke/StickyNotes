using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Data;
using StickyNotes.Models;
using StickyNotes.Services;
using StickyNotes.ViewModels;

namespace StickyNotes.Tests;

[TestClass]
public class SearchServiceTests
{
    private readonly SearchService _searchService = new();

    [TestMethod]
    public void Search_WithEmptyOrWhitespace_ShouldReturnEmpty()
    {
        var notes = new[] { new Note { Content = "测试便签" } };
        Assert.AreEqual(0, _searchService.Search(notes, "").Count);
        Assert.AreEqual(0, _searchService.Search(notes, "   ").Count);
    }

    [TestMethod]
    public void Search_CaseInsensitive_And_Substring_ShouldMatch()
    {
        var notes = new[]
        {
            new Note { Id = Guid.NewGuid(), Content = "Hello WPF world\nToday is a great day" }
        };

        var hits = _searchService.Search(notes, "wpf");
        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual(1, hits[0].LineNumber);
        Assert.AreEqual(6, hits[0].CharIndex);
        Assert.AreEqual(3, hits[0].Length);
        Assert.AreEqual("WPF", hits[0].HighlightText);
    }

    [TestMethod]
    public void Search_ChineseCharacters_ShouldCalculateCorrectLineNumber()
    {
        var content = "这是第一行标题\n第二行介绍 SQLite 数据库持久化\n第三行说明文本搜索跳行定位算法\n第四行总结";
        var note = new Note { Id = Guid.NewGuid(), Content = content };

        // 搜索“跳行”位于第 3 行
        var hits = _searchService.Search(new[] { note }, "跳行");

        Assert.AreEqual(1, hits.Count);
        var hit = hits[0];
        Assert.AreEqual(3, hit.LineNumber);
        Assert.AreEqual(content.IndexOf("跳行"), hit.CharIndex);
        Assert.AreEqual(2, hit.Length);
        Assert.IsTrue(hit.LineSnippet.Contains("第三行说明文本搜索跳行定位算法"));
    }

    [TestMethod]
    public void Search_MultipleHits_InSameNote_ShouldReturnOneCardWithTotalMatchesCount()
    {
        var content = "苹果 香蕉 苹果 橘子 苹果";
        var note = new Note { Id = Guid.NewGuid(), Content = content };

        var hits = _searchService.Search(new[] { note }, "苹果");

        Assert.AreEqual(1, hits.Count, "每张匹配便签对应一个卡片");
        var hit = hits[0];
        Assert.AreEqual(1, hit.LineNumber);
        Assert.AreEqual(0, hit.CharIndex, "首个命中词索引应为 0");
        Assert.AreEqual(3, hit.TotalMatches, "应正确统计该便签内总命中 3 处");
    }

    [TestMethod]
    public void Search_MultiWord_OpenRouter_ShouldMatch_CompactAndTokens()
    {
        // 场景 1：用户搜 "open router" (有空格)，便签内容是 "OpenRouter" (连写)
        var note1 = new Note
        {
            Id = Guid.NewGuid(),
            Content = "OpenRouter imkey1\n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"
        };

        var hits1 = _searchService.Search(new[] { note1 }, "open router");
        Assert.AreEqual(1, hits1.Count, "输入 open router 应成功匹配包含 OpenRouter 连写的便签");
        Assert.AreEqual(1, hits1[0].LineNumber);
        Assert.IsTrue(hits1[0].Lines[0].Segments.Any(s => s.IsHit && s.Text.Contains("OpenRouter", StringComparison.OrdinalIgnoreCase)),
            "OpenRouter 应被标记为高亮");

        // 场景 2：用户搜 "open router"，便签不同行分别包含 "open" 与 "router" (AND 模式)
        var note2 = new Note
        {
            Id = Guid.NewGuid(),
            Content = "First line with OPEN\nSecond line is empty\nThird line has ROUTER config"
        };
        var hits2 = _searchService.Search(new[] { note2 }, "open router");
        Assert.AreEqual(1, hits2.Count, "多词 AND 组合应成功匹配包含全部词项的便签");

        // 场景 3：便签只包含 open 不包含 router，不应误命中
        var note3 = new Note { Id = Guid.NewGuid(), Content = "only open here" };
        var hits3 = _searchService.Search(new[] { note3 }, "open router");
        Assert.AreEqual(0, hits3.Count, "缺少 router 词项的便签不应被命中");
    }

    [TestMethod]
    public void Search_ThreeLineContext_Extraction_ShouldReturnBeforeAndAfterLines()
    {
        // 场景 1：命中第 3 行 (共 5 行)，应返回第 2、3、4 行 (共 3 行)，且首行带省略号
        var note1 = new Note
        {
            Id = Guid.NewGuid(),
            Content = "Line 1 Header\nLine 2 Above\nLine 3 Target Keyword Here\nLine 4 Below\nLine 5 Footer"
        };

        var hits1 = _searchService.Search(new[] { note1 }, "Target");
        Assert.AreEqual(1, hits1.Count);
        var hit1 = hits1[0];
        Assert.AreEqual(3, hit1.LineNumber, "逻辑行号应为 3");
        Assert.AreEqual(3, hit1.Lines.Count, "中间行命中时应正好返回 3 行");
        // 第一行带省略号前缀
        var line0Text = string.Concat(hit1.Lines[0].Segments.Select(s => s.Text));
        Assert.IsTrue(line0Text.StartsWith("..."), "非首行开始的上下文首行应带省略号前缀");
        Assert.IsTrue(line0Text.Contains("Line 2 Above"));
        // 第二行是命中行
        var line1Text = string.Concat(hit1.Lines[1].Segments.Select(s => s.Text));
        Assert.IsTrue(line1Text.Contains("Line 3 Target"));
        Assert.IsTrue(hit1.Lines[1].Segments.Any(s => s.IsHit && s.Text.Equals("Target", StringComparison.OrdinalIgnoreCase)));
        // 第三行是后一行
        var line2Text = string.Concat(hit1.Lines[2].Segments.Select(s => s.Text));
        Assert.IsTrue(line2Text.Contains("Line 4 Below"));

        // 场景 2：首行命中 (无前一行)，应返回实际的 2 行
        var note2 = new Note
        {
            Id = Guid.NewGuid(),
            Content = "First Line Target\nSecond Line Context\nThird Line Other"
        };
        var hits2 = _searchService.Search(new[] { note2 }, "Target");
        Assert.AreEqual(1, hits2.Count);
        Assert.AreEqual(2, hits2[0].Lines.Count, "首行命中时应返回实际的 2 行 (命中行 + 后一行)");
        var firstLineText = string.Concat(hits2[0].Lines[0].Segments.Select(s => s.Text));
        Assert.IsFalse(firstLineText.StartsWith("..."), "第一行起始时不应带有前导省略号");

        // 场景 3：单行便签，应返回实际的 1 行
        var note3 = new Note { Id = Guid.NewGuid(), Content = "Only One Line Target" };
        var hits3 = _searchService.Search(new[] { note3 }, "Target");
        Assert.AreEqual(1, hits3.Count);
        Assert.AreEqual(1, hits3[0].Lines.Count, "单行便签命中时应返回实际的 1 行");

        // 场景 4：末行命中 (无后一行)，应返回实际的 2 行 (前一行 + 命中行)
        var note4 = new Note { Id = Guid.NewGuid(), Content = "Line 1 Intro\nLine 2 Ending Target" };
        var hits4 = _searchService.Search(new[] { note4 }, "Target");
        Assert.AreEqual(1, hits4.Count);
        Assert.AreEqual(2, hits4[0].Lines.Count, "末行命中时应返回实际的 2 行");
    }

    [TestMethod]
    public void Search_BoundaryAtStartAndEnd_ShouldLocateAccurately()
    {
        var content = "START_TOKEN\n中间内容\nEND_TOKEN";
        var note = new Note { Id = Guid.NewGuid(), Content = content };

        var startHits = _searchService.Search(new[] { note }, "START_TOKEN");
        Assert.AreEqual(1, startHits.Count);
        Assert.AreEqual(1, startHits[0].LineNumber);
        Assert.AreEqual(0, startHits[0].CharIndex);

        var endHits = _searchService.Search(new[] { note }, "END_TOKEN");
        Assert.AreEqual(1, endHits.Count);
        Assert.AreEqual(3, endHits[0].LineNumber);
        Assert.AreEqual(content.IndexOf("END_TOKEN"), endHits[0].CharIndex);
    }

    [TestMethod]
    public void Search_ShouldIgnoreDeletedNotes()
    {
        var noteActive = new Note { Content = "测试便签活跃", IsDeleted = false };
        var noteDeleted = new Note { Content = "测试便签已删除", IsDeleted = true };

        var hits = _searchService.Search(new[] { noteActive, noteDeleted }, "测试");
        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual("测试便签活跃", hits[0].LineSnippet);
    }

    [TestMethod]
    public void NotesListViewModel_SequentialSearch_AfterClear_ShouldSucceed()
    {
        TestEnvironment.RunInSta(() =>
        {
            var note1 = new Note { Id = Guid.NewGuid(), Content = "Hello Apple" };
            var note2 = new Note { Id = Guid.NewGuid(), Content = "World Banana" };
            var repo = new FakeSearchRepo(new[] { note1, note2 });
            var search = new SearchService();
            var wm = new WindowManager(TestEnvironment.CreateWindowManagerContainer(repo), repo);

            using var vm = new NotesListViewModel(repo, search, wm);
            vm.LoadNotesAsync().GetAwaiter().GetResult();

            // 第一次搜索：输入 "Apple"
            vm.SearchText = "Apple";
            PumpDispatcher(450);
            Assert.AreEqual(1, vm.SearchHitCount, "第一次搜索应命中 1 条");

            // 用户清空搜索框（或按 Esc、或点 ClearButton）
            vm.SearchText = string.Empty;
            Assert.AreEqual(0, vm.SearchHitCount, "清空后命中数应重置为 0");
            Assert.IsFalse(vm.IsSearching, "清空后 IsSearching 应为 false");

            // 第二次搜索：用户输入 "Banana"
            // 彻底杜绝 CTS Disposed 缺陷，第二次搜索正常执行
            vm.SearchText = "Banana";
            PumpDispatcher(450);
            Assert.AreEqual(1, vm.SearchHitCount, "第二次搜索应命中 1 条");
        });
    }

    /// <summary>
    /// F-P3-20：摘要单行截断只能由 SearchService 一处决定。
    /// 卡片此前套用 <c>Type.Body</c>（含 TextTrimming）会把已截断的行再省略一次，
    /// 出现非预期「…」。此用例锁定「超长命中行只被截断一次且长度有界」。
    /// </summary>
    [TestMethod]
    public void Search_LongHitLine_SnippetIsTruncatedExactlyOnce()
    {
        var service = new SearchService();
        var longLine = "前缀填充文字" + new string('甲', 120) + "关键词" + new string('乙', 120);
        var note = new Note { Id = Guid.NewGuid(), Content = longLine };

        var hit = service.Search(new[] { note }, "关键词").Single();

        // 渲染用的分段摘要（高亮渲染的真正输入）长度有界，不会把整行 250+ 字符塞进卡片
        var rendered = string.Concat(hit.Segments.Select(x => x.Text));
        Assert.IsTrue(rendered.Length <= 100,
            $"渲染摘要应被截断到有界长度，实际 {rendered.Length}");
        // 截断结果仍包含关键词，说明截取窗口定位正确
        Assert.IsTrue(rendered.Contains("关键词"), "截断后的摘要仍应包含关键词");
        // 不应出现「……」，即截断只发生一次
        Assert.IsFalse(rendered.Contains("……"), "不应出现重复省略号（二次截断）");
    }

    [TestMethod]
    public void NotesListViewModel_RapidSearchInput_ShouldNotThrowObjectDisposedException()
    {
        TestEnvironment.RunInSta(() =>
        {
            var note1 = new Note { Id = Guid.NewGuid(), Content = "Hello Apple Pie" };
            var repo = new FakeSearchRepo(new[] { note1 });
            var search = new SearchService();
            var wm = new WindowManager(TestEnvironment.CreateWindowManagerContainer(repo), repo);

            using var vm = new NotesListViewModel(repo, search, wm);
            vm.LoadNotesAsync().GetAwaiter().GetResult();

            // 模拟用户极速打字：A -> Ap -> App -> (清空) -> Ban -> Banana -> Apple
            vm.SearchText = "A";
            PumpDispatcher(40);
            vm.SearchText = "Ap";
            PumpDispatcher(40);
            vm.SearchText = "App";
            PumpDispatcher(40);
            vm.SearchText = "";
            PumpDispatcher(40);
            vm.SearchText = "Ban";
            PumpDispatcher(40);
            vm.SearchText = "Apple";
            PumpDispatcher(450);

            Assert.AreEqual(1, vm.SearchHitCount);
        });
    }

    [TestMethod]
    public void NotesListViewModel_Dispose_ShouldNotThrowIfAlreadyDisposedCts()
    {
        var repo = new FakeSearchRepo(Array.Empty<Note>());
        var search = new SearchService();
        var wm = new WindowManager(TestEnvironment.CreateWindowManagerContainer(repo), repo);

        var vm = new NotesListViewModel(repo, search, wm);
        vm.SearchText = "Apple";
        vm.SearchText = ""; // 触发清空
        // 销毁 ViewModel，验证退出时不报 ObjectDisposedException
        vm.Dispose();
    }

    private static void PumpDispatcher(int delayMilliseconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < delayMilliseconds)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                (Action)(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(15);
        }
    }

    private class FakeSearchRepo : INoteRepository
    {
        private readonly List<Note> _notes;
        public FakeSearchRepo(IEnumerable<Note> notes) => _notes = notes.ToList();
        public Task<IReadOnlyList<Note>> GetAllActiveAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Note>>(_notes);
        public Task<IReadOnlyList<Note>> GetAllArchivedAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Note>>(Array.Empty<Note>());
        public Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Note>>(_notes);
        public Task<Note?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_notes.FirstOrDefault(n => n.Id == id));
        public Task SaveAsync(Note note, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveBatchAsync(IEnumerable<Note> notes, CancellationToken ct = default) => Task.CompletedTask;
        public Task SoftDeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ArchiveNoteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreNoteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task HardDeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAllArchivedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateWindowBoundsAsync(Guid id, double x, double y, double width, double height, bool isOpen, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateWindowPlacementAsync(Guid id, double x, double y, double width, double height, CancellationToken ct = default) => Task.CompletedTask;
    }
}
