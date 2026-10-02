using StickyNotes.Models;

namespace StickyNotes.Services;

/// <summary>
/// 纯内存高性能搜索与字符定位服务
/// </summary>
public sealed class SearchService : ISearchService
{
    public IReadOnlyList<SearchHit> Search(IEnumerable<Note> notes, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return Array.Empty<SearchHit>();

        var results = new List<SearchHit>();
        var trimmed = keyword.Trim();

        foreach (var note in notes.Where(n => !n.IsDeleted))
        {
            var content = note.Content;
            if (string.IsNullOrEmpty(content)) continue;

            int searchStart = 0;
            int lastNewlineScanIndex = 0;
            int currentLineNumber = 1;

            while (searchStart < content.Length)
            {
                int matchIndex = content.IndexOf(trimmed, searchStart, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0) break;

                // 1. 增量统计换行符，将复杂度降至单次 O(n)
                for (int i = lastNewlineScanIndex; i < matchIndex; i++)
                {
                    if (content[i] == '\n') currentLineNumber++;
                }
                lastNewlineScanIndex = matchIndex;
                int lineNumber = currentLineNumber;

                // 2. 提取命中所在行的完整文本
                int lineStart = content.LastIndexOf('\n', Math.Max(matchIndex - 1, 0));
                lineStart = (lineStart < 0) ? 0 : lineStart + 1;

                int lineEnd = content.IndexOf('\n', matchIndex);
                if (lineEnd < 0) lineEnd = content.Length;

                string lineText = content[lineStart..lineEnd].Trim('\r');

                // 3. 构建该行预览（若该行过长则围绕命中词截取前后 40 字符）
                string snippet = lineText;
                if (lineText.Length > 80)
                {
                    int relPos = matchIndex - lineStart;
                    int subStart = Math.Max(0, relPos - 30);
                    int subLen = Math.Min(lineText.Length - subStart, trimmed.Length + 60);
                    snippet = "..." + lineText.Substring(subStart, subLen) + "...";
                }

                var segments = BuildSegments(snippet, trimmed);

                results.Add(new SearchHit(
                    NoteId: note.Id,
                    NoteTitle: note.DisplayTitle,
                    Color: note.Color,
                    LineNumber: lineNumber,
                    CharIndex: matchIndex,
                    Length: trimmed.Length,
                    LineSnippet: snippet,
                    HighlightText: content.Substring(matchIndex, trimmed.Length),
                    Segments: segments
                ));

                // 推进游标，保证支持重叠词和同行多次命中
                searchStart = matchIndex + Math.Max(1, trimmed.Length);
            }
        }

        return results;
    }

    public static List<SnippetSegment> BuildSegments(string snippet, string keyword)
    {
        var segments = new List<SnippetSegment>();
        if (string.IsNullOrEmpty(snippet)) return segments;
        if (string.IsNullOrEmpty(keyword))
        {
            segments.Add(new SnippetSegment(snippet, false));
            return segments;
        }

        int start = 0;
        while (start < snippet.Length)
        {
            int idx = snippet.IndexOf(keyword, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                segments.Add(new SnippetSegment(snippet[start..], false));
                break;
            }

            if (idx > start)
            {
                segments.Add(new SnippetSegment(snippet[start..idx], false));
            }

            segments.Add(new SnippetSegment(snippet.Substring(idx, keyword.Length), true));
            start = idx + keyword.Length;
        }

        return segments;
    }
}
