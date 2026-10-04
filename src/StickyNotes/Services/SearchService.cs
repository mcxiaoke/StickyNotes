using System;
using System.Collections.Generic;
using System.Linq;
using StickyNotes.Infrastructure;
using StickyNotes.Models;

namespace StickyNotes.Services;

/// <summary>
/// 纯内存高性能搜索与 1~3 行智能上下文提取服务
/// 支持单词/多词 AND 匹配、连续短语匹配与紧凑去空格匹配；
/// 命中行全部收集并按匹配质量分级（Tier 1~3），相邻命中行就近合并，
/// 便签内卡片按质量截断，全局以「便签最高质量 → 更新时间」分组排序输出扁平多卡片结果
/// </summary>
public sealed class SearchService : ISearchService
{
    /// <summary>匹配质量分级：1 完整短语 > 2 同行全词 > 3 部分词/跨行/兜底</summary>
    private const int TierPhrase = 1;
    private const int TierAllTokensInLine = 2;
    private const int TierPartial = 3;

    /// <summary>单便签最多产出的命中卡片数（合并后计数），避免长文本/日志便签刷屏</summary>
    private const int MaxCardsPerNote = 3;

    /// <summary>相邻命中行行距不超过该值时链式合并为一张卡片</summary>
    private const int MergeLineGap = 2;

    public IReadOnlyList<SearchHit> Search(IEnumerable<Note> notes, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return Array.Empty<SearchHit>();

        var trimmed = keyword.Trim();
        var tokens = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return Array.Empty<SearchHit>();

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 紧凑词（去除所有空格，用于“open router”匹配“openrouter”等连写场景）
        var compact = string.Concat(tokens);

        // 收集待匹配与待高亮的有效关键词集合（按长度降序排列，优先长匹配）
        var highlightKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(compact)) highlightKeywords.Add(compact);
        if (!string.IsNullOrEmpty(trimmed)) highlightKeywords.Add(trimmed);
        foreach (var t in tokens)
        {
            if (!string.IsNullOrEmpty(t)) highlightKeywords.Add(t);
        }
        var sortedKeywords = highlightKeywords.OrderByDescending(k => k.Length).ToList();

        // 便签级结果：卡片列表 + 便签最高匹配质量 + 更新时间，供全局分组排序
        var noteResults = new List<(List<SearchHit> Cards, int BestTier, DateTime UpdatedAt)>();
        var results = new List<SearchHit>();
        int scannedNotes = 0;

        foreach (var note in notes.Where(n => !n.IsDeleted))
        {
            scannedNotes++;
            var content = note.Content;
            if (string.IsNullOrEmpty(content)) continue;

            // 1. 便签匹配判定规则（短路求值，高频搜索下避免对长文本重复全文扫描）：
            // A. 正文直接包含完整 trimmed 短语；
            // B. 正文包含紧凑连写词 compact（例如 open router 匹配 openrouter）；
            // C. 正文包含全部 tokens（AND 模式，跨行/跨词组合匹配）；
            bool noteMatches = content.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                || (compact.Length > 0 && content.Contains(compact, StringComparison.OrdinalIgnoreCase))
                || tokens.All(t => content.Contains(t, StringComparison.OrdinalIgnoreCase));

            if (!noteMatches)
            {
                continue;
            }

            // 2. 切分便签文本为物理行并计算行偏移
            var rawLines = content.Split('\n');
            var lines = new string[rawLines.Length];
            var lineStartOffsets = new int[rawLines.Length];

            int runningOffset = 0;
            for (int i = 0; i < rawLines.Length; i++)
            {
                lineStartOffsets[i] = runningOffset;
                lines[i] = rawLines[i].TrimEnd('\r');
                runningOffset += rawLines[i].Length + 1; // +1 为 '\n' 换行符
            }

            // 3. 全行扫描：收集每个命中行的分级与选区信息（不再找到首行即 break）
            var hitLines = new List<HitLine>();
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrEmpty(line)) continue;

                if (ClassifyLine(line, trimmed, compact, tokens, out int tier, out int relStart, out int relEnd))
                {
                    hitLines.Add(new HitLine(i, tier, relStart, relEnd, lineStartOffsets[i] + relStart, lineStartOffsets[i] + relEnd));
                }
            }

            var cards = new List<SearchHit>();
            if (hitLines.Count > 0)
            {
                // 4. 相邻命中行就近合并（行距 ≤ MergeLineGap 链式并入同组），每组一张卡片
                int groupStart = 0;
                for (int i = 1; i <= hitLines.Count; i++)
                {
                    bool flush = i == hitLines.Count
                        || hitLines[i].LineIndex - hitLines[i - 1].LineIndex > MergeLineGap;
                    if (!flush) continue;

                    cards.Add(BuildHitCard(note, lines, sortedKeywords, hitLines, groupStart, i - 1));
                    groupStart = i;
                }

                // 5. 便签内卡片排序：匹配质量优先，同质量按行号；
                //    Cap 截断采用同一顺序，保证保留的永远是质量最高的命中项
                cards.Sort((a, b) => a.Tier != b.Tier
                    ? a.Tier.CompareTo(b.Tier)
                    : a.LineNumber.CompareTo(b.LineNumber));
                if (cards.Count > MaxCardsPerNote)
                {
                    cards.RemoveRange(MaxCardsPerNote, cards.Count - MaxCardsPerNote);
                }
            }
            else
            {
                // 兜底保护：行级逐行未搜出（例如关键词本身跨换行的极端输入）时，
                // 采用全文首个命中生成一张最低质量卡片，禁止移除（防空列表与越界）
                int fallbackLength = Math.Min(trimmed.Length, content.Length);
                var (fallbackSnippetLines, fallbackSegments) = BuildContextWindow(lines, 0, Math.Min(lines.Length - 1, 2), sortedKeywords);
                cards.Add(new SearchHit(
                    NoteId: note.Id,
                    NoteTitle: note.DisplayTitle,
                    Color: note.Color,
                    LineNumber: 1,
                    CharIndex: 0,
                    Length: fallbackLength,
                    LineSnippet: content[..fallbackLength],
                    HighlightText: content[..fallbackLength],
                    Segments: fallbackSegments,
                    UpdatedAt: note.UpdatedAt,
                    Lines: fallbackSnippetLines,
                    TotalMatches: Math.Max(1, CountTotalMatches(content, sortedKeywords)),
                    DisplayLineNumber: "1",
                    Tier: TierPartial));
            }

            int bestTier = cards.Min(c => c.Tier);
            noteResults.Add((cards, bestTier, note.UpdatedAt));
        }

        // 6. 全局分组排序：便签最高质量优先，同质量按更新时间新→旧；
        //    搜索排序不考虑置顶（IsPinnedInList 不参与，非搜索列表行为不变）；
        //    同便签卡片保持相邻，组内次序已在截断前排定（Tier → 行号）
        foreach (var group in noteResults.OrderBy(n => n.BestTier).ThenByDescending(n => n.UpdatedAt))
        {
            results.AddRange(group.Cards);
        }

        sw.Stop();
        // 仅在耗时明显偏高（>100ms）时记录，避免高频搜索把日志刷爆；
        // 阈值参考产品文档 NFR（<30ms 达标）与本报告实测的退化拐点。
        if (sw.ElapsedMilliseconds >= 100)
        {
            AppLog.Warn($"[SearchService] 搜索耗时偏高 {sw.ElapsedMilliseconds}ms（词=\"{trimmed}\"，扫描便签数={scannedNotes}，结果卡片数={results.Count}）");
        }

        return results;
    }

    /// <summary>单个命中行的分级与选区记录（行号 0 基；相对/绝对选区均为 [Start, End)）</summary>
    private readonly record struct HitLine(int LineIndex, int Tier, int RelStart, int RelEnd, int AbsStart, int AbsEnd);

    /// <summary>
    /// 对单行做匹配质量分级，并给出命中选区（行内相对 [Start, End)）：
    /// Tier 1：行内包含完整短语 trimmed 或紧凑连写词 compact（取行内最早出现者）；
    /// Tier 2：短语未连续出现，但全部 tokens 同时出现在本行（选区覆盖首 token 至末 token）；
    /// Tier 3：仅命中部分 tokens（跨行组合匹配场景，取行内最早出现者）。
    /// 短语/连写命中必然蕴含全部 tokens 命中（二者都是 tokens 的拼接，包含每个 token 为子串），
    /// 故先扫描 tokens 位置，仅在全部命中时才进一步扫描短语/连写，
    /// 未命中行只承担 tokens 数量的扫描成本（避免逐级判定对整行重复扫描）。
    /// </summary>
    private static bool ClassifyLine(
        string line,
        string trimmed,
        string compact,
        string[] tokens,
        out int tier,
        out int relStart,
        out int relEnd)
    {
        relStart = 0;
        relEnd = 0;
        tier = TierPartial;

        int minStart = int.MaxValue;
        int minStartLen = 0;
        int maxEnd = -1;
        int hitCount = 0;
        foreach (var t in tokens)
        {
            int pos = line.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) continue;

            hitCount++;
            if (pos < minStart)
            {
                minStart = pos;
                minStartLen = t.Length;
            }
            if (pos + t.Length > maxEnd) maxEnd = pos + t.Length;
        }

        if (hitCount == 0) return false;

        if (hitCount == tokens.Length)
        {
            bool singleToken = string.Equals(compact, trimmed, StringComparison.Ordinal);
            int phrasePos = singleToken
                ? minStart
                : (trimmed.Length > 0 ? line.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) : -1);
            int compactPos = singleToken
                ? phrasePos
                : (compact.Length > 0 ? line.IndexOf(compact, StringComparison.OrdinalIgnoreCase) : -1);

            if (phrasePos >= 0 || compactPos >= 0)
            {
                // Tier 1：完整短语 / 连写词，取行内最早出现者
                if (phrasePos < 0 || (compactPos >= 0 && compactPos < phrasePos))
                {
                    relStart = compactPos;
                    relEnd = compactPos + compact.Length;
                }
                else
                {
                    relStart = phrasePos;
                    relEnd = phrasePos + trimmed.Length;
                }
                tier = TierPhrase;
                return true;
            }

            // Tier 2：全部 tokens 同行（不连续），选区从首 token 起点覆盖到末 token 终点
            relStart = minStart;
            relEnd = maxEnd;
            tier = TierAllTokensInLine;
            return true;
        }

        // Tier 3：部分 token 命中（跨行组合匹配，此时短语/连写必然未命中），取最早出现者
        relStart = minStart;
        relEnd = minStart + minStartLen;
        tier = TierPartial;
        return true;
    }

    /// <summary>
    /// 将一组相邻命中行（下标 [from, to]，已按行号连续排列）合成为一张搜索卡片：
    /// Tier 取组内最高；跳转选区覆盖组内首个至末个命中词（跨行选区，居中锚点为首个命中词）；
    /// 上下文窗口为组内首末行各外扩一行；摘要行取组内质量最高且行号最早的代表行。
    /// </summary>
    private static SearchHit BuildHitCard(
        Note note,
        string[] lines,
        IReadOnlyList<string> sortedKeywords,
        List<HitLine> hitLines,
        int from,
        int to)
    {
        int firstLine = hitLines[from].LineIndex;
        int lastLine = hitLines[to].LineIndex;

        int tier = hitLines[from].Tier;
        for (int i = from + 1; i <= to; i++)
        {
            if (hitLines[i].Tier < tier) tier = hitLines[i].Tier;
        }

        // 行号升序扫描保证 AbsStart 严格递增，组内首命中词即首行的 AbsStart
        int absStart = hitLines[from].AbsStart;
        int absEnd = hitLines[from].AbsEnd;
        for (int i = from + 1; i <= to; i++)
        {
            if (hitLines[i].AbsEnd > absEnd) absEnd = hitLines[i].AbsEnd;
        }

        int repIdx = from;
        for (int i = from + 1; i <= to; i++)
        {
            if (hitLines[i].Tier < hitLines[repIdx].Tier) repIdx = i;
        }
        int repLine = hitLines[repIdx].LineIndex;
        var rep = hitLines[repIdx];

        int startLine = Math.Max(0, firstLine - 1);
        int endLine = Math.Min(lines.Length - 1, lastLine + 1);
        var (snippetLines, allSegments) = BuildContextWindow(lines, startLine, endLine, sortedKeywords);

        string displayLineNumber = firstLine == lastLine
            ? (firstLine + 1).ToString()
            : $"{firstLine + 1}-{lastLine + 1}";

        return new SearchHit(
            NoteId: note.Id,
            NoteTitle: note.DisplayTitle,
            Color: note.Color,
            LineNumber: firstLine + 1,
            CharIndex: absStart,
            Length: absEnd - absStart,
            LineSnippet: lines[repLine],
            HighlightText: lines[repLine].Substring(rep.RelStart, rep.RelEnd - rep.RelStart),
            Segments: allSegments,
            UpdatedAt: note.UpdatedAt,
            Lines: snippetLines,
            TotalMatches: Math.Max(1, CountTotalMatches(note.Content, sortedKeywords)),
            DisplayLineNumber: displayLineNumber,
            Tier: tier);
    }

    /// <summary>
    /// 提取 [startLine, endLine] 区间的上下文行（窗口范围由调用方决定），
    /// 含前导省略号提示、超长单行智能截取与关键词高亮分段
    /// </summary>
    private static (List<SnippetLine> Lines, List<SnippetSegment> Segments) BuildContextWindow(
        string[] lines, int startLine, int endLine, IReadOnlyList<string> sortedKeywords)
    {
        var snippetLines = new List<SnippetLine>();
        var allSegments = new List<SnippetSegment>();

        for (int lineIdx = startLine; lineIdx <= endLine; lineIdx++)
        {
            string text = lines[lineIdx];

            // 首行若上方还有更多内容，添加前导省略号提示
            if (lineIdx == startLine && startLine > 0 && !text.StartsWith("..."))
            {
                text = "..." + text;
            }

            // 超长单行智能截取（避免宽卡片溢出）
            text = TruncateLineSafely(text, sortedKeywords);

            // 生成行内关键词高亮分段
            var segments = BuildMultiKeywordsSegments(text, sortedKeywords);
            snippetLines.Add(new SnippetLine(segments));

            if (allSegments.Count > 0)
            {
                allSegments.Add(new SnippetSegment("\n", false));
            }
            allSegments.AddRange(segments);
        }

        return (snippetLines, allSegments);
    }

    /// <summary>
    /// 对单行过长文本围绕关键词智能截取，保证卡片显示精简整洁
    /// </summary>
    private static string TruncateLineSafely(string line, IReadOnlyList<string> keywords)
    {
        if (line.Length <= MaxSnippetLineLength) return line;

        // 寻找行内第一个命中位置
        int earliest = -1;
        int kwLen = 0;
        foreach (var kw in keywords)
        {
            int pos = line.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
            if (pos >= 0 && (earliest < 0 || pos < earliest))
            {
                earliest = pos;
                kwLen = kw.Length;
            }
        }

        if (earliest >= 0)
        {
            int subStart = Math.Max(0, earliest - 30);
            int subLen = Math.Min(line.Length - subStart, kwLen + 60);
            string snippet = line.Substring(subStart, subLen);
            if (subStart > 0 && !snippet.StartsWith("...")) snippet = "..." + snippet;
            if (subStart + subLen < line.Length && !snippet.EndsWith("...")) snippet += "...";
            return snippet;
        }

        // 无命中的上下文行截取前 75 字符
        return line[..75] + "...";
    }

    /// <summary>
    /// 摘要单行最大长度。调用方（搜索卡片）已改用不裁剪的正文样式，
    /// 因此这里的截断是摘要行长的<b>唯一</b>决定者；此前调用方叠加
    /// <c>Type.Body</c> 的 <c>TextTrimming</c> 会把已截断的行再省略一次，
    /// 出现非预期「…」（原 F-P3-20）。
    /// </summary>
    private const int MaxSnippetLineLength = 85;

    /// <summary>
    /// 统计关键词在全文中的非重叠总出现次数。
    /// 每个关键词只独立扫描全文一遍收集出现区间，再合并计数；
    /// 原实现每命中一次就对剩余全文重扫 O(命中数 × 全文长度)，
    /// 在超长日志便签（数千行、上百处命中）下退化到 100ms+ 量级。
    /// 贪心语义与原实现一致：同起点取最长命中并跳过其覆盖范围。
    /// </summary>
    private static int CountTotalMatches(string content, IReadOnlyList<string> keywords)
    {
        var intervals = new List<(int Start, int End)>();
        foreach (var kw in keywords)
        {
            int idx = 0;
            while (idx < content.Length)
            {
                int pos = content.IndexOf(kw, idx, StringComparison.OrdinalIgnoreCase);
                if (pos < 0) break;
                intervals.Add((pos, pos + kw.Length));
                idx = pos + Math.Max(1, kw.Length);
            }
        }

        if (intervals.Count == 0) return 0;

        intervals.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));

        int count = 0;
        int curEnd = -1;
        foreach (var (start, end) in intervals)
        {
            if (start >= curEnd)
            {
                count++;
                curEnd = end;
            }
        }
        return count;
    }

    /// <summary>
    /// 多关键词高亮分段切分算法（支持多词同时高亮、自动区间合并与贪婪匹配）
    /// </summary>
    public static List<SnippetSegment> BuildMultiKeywordsSegments(string snippet, IEnumerable<string> keywords)
    {
        var segments = new List<SnippetSegment>();
        if (string.IsNullOrEmpty(snippet)) return segments;

        var validKeywords = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validKeywords.Count == 0)
        {
            segments.Add(new SnippetSegment(snippet, false));
            return segments;
        }

        // 收集文本中所有命中的区间 [Start, End)
        var intervals = new List<(int Start, int End)>();
        foreach (var kw in validKeywords)
        {
            int searchIdx = 0;
            while (searchIdx < snippet.Length)
            {
                int pos = snippet.IndexOf(kw, searchIdx, StringComparison.OrdinalIgnoreCase);
                if (pos < 0) break;
                intervals.Add((pos, pos + kw.Length));
                searchIdx = pos + Math.Max(1, kw.Length);
            }
        }

        if (intervals.Count == 0)
        {
            segments.Add(new SnippetSegment(snippet, false));
            return segments;
        }

        // 按起点升序、终点降序排序并合并重叠区间
        intervals.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        var merged = new List<(int Start, int End)>();
        var cur = intervals[0];
        for (int i = 1; i < intervals.Count; i++)
        {
            var next = intervals[i];
            if (next.Start <= cur.End)
            {
                if (next.End > cur.End)
                {
                    cur = (cur.Start, next.End);
                }
            }
            else
            {
                merged.Add(cur);
                cur = next;
            }
        }
        merged.Add(cur);

        // 按照合并后的区间切分 snippet
        int cursor = 0;
        foreach (var (start, end) in merged)
        {
            if (start > cursor)
            {
                segments.Add(new SnippetSegment(snippet[cursor..start], false));
            }
            segments.Add(new SnippetSegment(snippet[start..end], true));
            cursor = end;
        }
        if (cursor < snippet.Length)
        {
            segments.Add(new SnippetSegment(snippet[cursor..], false));
        }

        return segments;
    }

    /// <summary>
    /// 兼容旧单关键词分段切分接口
    /// </summary>
    public static List<SnippetSegment> BuildSegments(string snippet, string keyword)
    {
        return BuildMultiKeywordsSegments(snippet, new[] { keyword });
    }
}
