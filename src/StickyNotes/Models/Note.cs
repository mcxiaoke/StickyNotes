using System.IO;

namespace StickyNotes.Models;

/// <summary>
/// 便签核心业务实体
/// </summary>
public sealed class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 便签文本内容（纯文本，统一以 \n 或系统换行符存储）
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 主题色彩
    /// </summary>
    public NoteColor Color { get; set; } = NoteColor.Yellow;

    /// <summary>
    /// 是否在桌面置顶（Topmost）
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// 是否软删除（回收站机制）
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// 独立窗口在屏幕上的 X 坐标
    /// </summary>
    public double WindowX { get; set; } = 150;

    /// <summary>
    /// 独立窗口在屏幕上的 Y 坐标
    /// </summary>
    public double WindowY { get; set; } = 150;

    /// <summary>
    /// 独立窗口宽度
    /// </summary>
    public double WindowWidth { get; set; } = 380;

    /// <summary>
    /// 独立窗口高度
    /// </summary>
    public double WindowHeight { get; set; } = 420;

    /// <summary>
    /// 上次退出时贴纸窗口是否处于打开状态
    /// </summary>
    public bool IsOpen { get; set; } = true;

    /// <summary>
    /// 创建时间（统一 UTC）
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 最后修改时间（统一 UTC）
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 纯文本卡片显示：去除首尾空白，按需显示1~4行；无内容时显示“（空白便签）”
    /// </summary>
    public string PreviewText => string.IsNullOrWhiteSpace(Content) ? "（空白便签）" : Content.Trim();

    /// <summary>
    /// 动态计算标题：智能扫描第一行非空白文本，截取最多 40 字符；无内容时显示“（空白便签）”
    /// </summary>
    public string DisplayTitle
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Content))
                return "（空白便签）";

            using var reader = new StringReader(Content);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed.Length <= 40 ? trimmed : trimmed[..40] + "...";
                }
            }

            return "（空白便签）";
        }
    }

    /// <summary>
    /// 动态提取正文摘要（第 2~4 行内容）
    /// </summary>
    public string Snippet
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Content))
                return string.Empty;

            using var reader = new StringReader(Content);
            // 跳过首行非空标题
            bool skippedTitle = false;
            var snippetLines = new List<string>(3);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (!skippedTitle)
                {
                    if (trimmed.Length > 0)
                    {
                        skippedTitle = true;
                    }
                    continue;
                }

                if (trimmed.Length > 0)
                {
                    snippetLines.Add(trimmed);
                    if (snippetLines.Count >= 3) break;
                }
            }

            return snippetLines.Count > 0 ? string.Join("  ", snippetLines) : string.Empty;
        }
    }
}
