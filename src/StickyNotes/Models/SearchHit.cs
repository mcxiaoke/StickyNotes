namespace StickyNotes.Models;

/// <summary>
/// 搜索命中项模型（用于搜索卡片呈现与跳行定位，不持久化）
/// </summary>
public sealed record SearchHit(
    Guid NoteId,
    string NoteTitle,
    NoteColor Color,
    int LineNumber,        // 逻辑行号（从 1 开始计）
    int CharIndex,         // 命中词在 Content 中的绝对起始字符索引
    int Length,            // 命中关键字长度
    string LineSnippet,    // 所在行前后摘要
    string HighlightText   // 实际命中的文字
);
