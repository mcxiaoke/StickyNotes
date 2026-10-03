using System;
using System.Collections.Generic;
using System.Linq;
using StickyNotes.Infrastructure;
using StickyNotes.Models;

namespace StickyNotes.Services;

/// <summary>
/// 纯内存高性能搜索与 1~3 行智能上下文提取服务
/// 支持单词/多词 AND 匹配、连续短语匹配与紧凑去空格匹配
/// </summary>
public sealed class SearchService : ISearchService
{
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

        var results = new List<SearchHit>();

        foreach (var note in notes.Where(n => !n.IsDeleted))
        {
            var content = note.Content;
            if (string.IsNullOrEmpty(content)) continue;

            // 1. 便签匹配判定规则：
            // A. 正文直接包含完整 trimmed 短语；
            // B. 正文包含紧凑连写词 compact（例如 open router 匹配 openrouter）；
            // C. 正文包含全部 tokens（AND 模式，跨行/跨词组合匹配）；
            bool isDirectMatch = content.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
            bool isCompactMatch = compact.Length > 0 && content.Contains(compact, StringComparison.OrdinalIgnoreCase);
            bool isAllTokensMatch = tokens.All(t => content.Contains(t, StringComparison.OrdinalIgnoreCase));

            if (!isDirectMatch && !isCompactMatch && !isAllTokensMatch)
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

            // 3. 寻找第一个命中行及该行中的首个命中位置
            int hitLineIndex = -1;
            int firstHitAbsCharIndex = 0;
            int firstHitLength = trimmed.Length;
            string firstHitText = string.Empty;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrEmpty(line)) continue;

                // 在该行寻找任一有效关键词的命中
                int earliestInLine = int.MaxValue;
                string? matchedKeywordInLine = null;

                foreach (var kw in sortedKeywords)
                {
                    int pos = line.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
                    if (pos >= 0 && pos < earliestInLine)
                    {
                        earliestInLine = pos;
                        matchedKeywordInLine = kw;
                    }
                }

                if (matchedKeywordInLine != null)
                {
                    hitLineIndex = i;
                    firstHitAbsCharIndex = lineStartOffsets[i] + earliestInLine;
                    firstHitLength = matchedKeywordInLine.Length;
                    firstHitText = line.Substring(earliestInLine, matchedKeywordInLine.Length);
                    break;
                }
            }

            // 兜底保护：若行内未逐行搜出（例如跨换行匹配），则采用全文首个命中
            if (hitLineIndex < 0)
            {
                hitLineIndex = 0;
                firstHitAbsCharIndex = 0;
                firstHitLength = Math.Min(trimmed.Length, content.Length);
                firstHitText = content[..firstHitLength];
            }

            // 4. 提取首个命中行的“前一行 + 命中行 + 后一行”共三行（不足三行显示实际行数）
            int startLine = Math.Max(0, hitLineIndex - 1);
            int endLine = Math.Min(lines.Length - 1, hitLineIndex + 1);

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

            // 5. 统计便签全文总命中处数
            int totalMatches = CountTotalMatches(content, sortedKeywords);

            results.Add(new SearchHit(
                NoteId: note.Id,
                NoteTitle: note.DisplayTitle,
                Color: note.Color,
                LineNumber: hitLineIndex + 1,
                CharIndex: firstHitAbsCharIndex,
                Length: firstHitLength,
                LineSnippet: lines[hitLineIndex],
                HighlightText: firstHitText,
                Segments: allSegments,
                UpdatedAt: note.UpdatedAt,
                Lines: snippetLines,
                TotalMatches: Math.Max(1, totalMatches)
            ));
        }

        sw.Stop();
        // 仅在耗时明显偏高（>100ms）时记录，避免高频搜索把日志刷爆；
        // 阈值参考产品文档 NFR（<30ms 达标）与本报告实测的退化拐点。
        if (sw.ElapsedMilliseconds >= 100)
        {
            AppLog.Warn($"[SearchService] 搜索耗时偏高 {sw.ElapsedMilliseconds}ms（词=\"{trimmed}\"，扫描便签数={results.Count}，命中={results.Count}）");
        }

        return results;
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
    /// 统计关键词在全文中的非重叠总出现次数
    /// </summary>
    private static int CountTotalMatches(string content, IReadOnlyList<string> keywords)
    {
        int count = 0;
        int idx = 0;
        while (idx < content.Length)
        {
            int earliest = int.MaxValue;
            int advance = 1;
            foreach (var kw in keywords)
            {
                int pos = content.IndexOf(kw, idx, StringComparison.OrdinalIgnoreCase);
                if (pos >= 0 && pos < earliest)
                {
                    earliest = pos;
                    advance = Math.Max(1, kw.Length);
                }
            }

            if (earliest == int.MaxValue) break;
            count++;
            idx = earliest + advance;
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
