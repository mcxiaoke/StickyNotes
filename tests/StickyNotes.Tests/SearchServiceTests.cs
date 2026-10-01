using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Models;
using StickyNotes.Services;

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
    public void Search_MultipleHits_InSameLine_ShouldReturnAllWithAscendingCharIndex()
    {
        var content = "苹果 香蕉 苹果 橘子 苹果";
        var note = new Note { Id = Guid.NewGuid(), Content = content };

        var hits = _searchService.Search(new[] { note }, "苹果");

        Assert.AreEqual(3, hits.Count);
        foreach (var h in hits)
        {
            Assert.AreEqual(1, h.LineNumber);
        }
        Assert.IsTrue(hits[0].CharIndex < hits[1].CharIndex);
        Assert.IsTrue(hits[1].CharIndex < hits[2].CharIndex);
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
}
