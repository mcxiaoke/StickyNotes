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
            while (searchStart < content.Length)
            {
                int matchIndex = content.IndexOf(trimmed, searchStart, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0) break;

                // 1. 统计命中位置前面的换行符数量，计算 1-based 逻辑行号
                int lineNumber = 1;
                for (int i = 0; i < matchIndex; i++)
                {
                    if (content[i] == '\n') lineNumber++;
                }

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

                results.Add(new SearchHit(
                    NoteId: note.Id,
                    NoteTitle: note.DisplayTitle,
                    Color: note.Color,
                    LineNumber: lineNumber,
                    CharIndex: matchIndex,
                    Length: trimmed.Length,
                    LineSnippet: snippet,
                    HighlightText: content.Substring(matchIndex, trimmed.Length)
                ));

                // 推进游标，保证支持重叠词和同行多次命中
                searchStart = matchIndex + Math.Max(1, trimmed.Length);
            }
        }

        return results;
    }
}
