using System;
using System.Collections.Generic;

namespace StickyNotes.Models;

/// <summary>
/// 文本分段（用于搜索高亮渲染）
/// </summary>
public sealed record SnippetSegment(string Text, bool IsHit);

/// <summary>
/// 搜索预览行（用于 1~3 行上下文独立渲染，避免跨行折行错乱）
/// </summary>
public sealed record SnippetLine(IReadOnlyList<SnippetSegment> Segments);

/// <summary>
/// 搜索命中项模型（每张命中便签对应一个卡片，展示前后各一行共 1~3 行上下文与更新日期）
/// </summary>
public sealed record SearchHit(
    Guid NoteId,
    string NoteTitle,
    NoteColor Color,
    int LineNumber,        // 逻辑行号（从 1 开始计）
    int CharIndex,         // 命中词在 Content 中的绝对起始字符索引
    int Length,            // 命中关键字长度
    string LineSnippet,    // 所在行前后摘要
    string HighlightText,  // 实际命中的文字
    IReadOnlyList<SnippetSegment>? Segments = null, // 兼容扁平分段
    DateTime UpdatedAt = default,                   // 便签更新时间（用于右上角日期展示）
    IReadOnlyList<SnippetLine>? Lines = null,       // 1~3 行上下文独立行列表
    int TotalMatches = 1                            // 该便签总命中数
)
{
    public IReadOnlyList<SnippetSegment> Segments { get; init; } = Segments ?? Array.Empty<SnippetSegment>();
    public IReadOnlyList<SnippetLine> Lines { get; init; } = Lines ?? Array.Empty<SnippetLine>();
    public DateTime UpdatedAt { get; init; } = UpdatedAt == default ? DateTime.UtcNow : UpdatedAt;
}
