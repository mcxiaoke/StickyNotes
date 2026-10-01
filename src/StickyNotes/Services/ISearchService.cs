using StickyNotes.Models;

namespace StickyNotes.Services;

/// <summary>
/// 搜索定位服务契约
/// </summary>
public interface ISearchService
{
    /// <summary>
    /// 在指定便签集合中检索关键字，返回包含绝对字符偏移与逻辑行号的命中集合
    /// </summary>
    IReadOnlyList<SearchHit> Search(IEnumerable<Note> notes, string keyword);
}
