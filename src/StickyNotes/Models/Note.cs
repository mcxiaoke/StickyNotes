using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StickyNotes.Models;

/// <summary>
/// 便签核心业务实体（支持 INotifyPropertyChanged，提供流畅的局部 UI 绑定更新）
/// </summary>
public sealed partial class Note : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    private string _content = string.Empty;
    /// <summary>
    /// 便签文本内容（纯文本，统一以 \n 或系统换行符存储）
    /// </summary>
    public string Content
    {
        get => _content;
        set
        {
            if (SetProperty(ref _content, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(PreviewText));
                OnPropertyChanged(nameof(Snippet));
            }
        }
    }

    private NoteColor _color = NoteColor.Yellow;
    /// <summary>
    /// 主题色彩
    /// </summary>
    public NoteColor Color
    {
        get => _color;
        set => SetProperty(ref _color, value);
    }

    private bool _isPinnedInList;
    /// <summary>
    /// 是否在便签列表中置顶排序（Pin to notes list）
    /// </summary>
    public bool IsPinnedInList
    {
        get => _isPinnedInList;
        set => SetProperty(ref _isPinnedInList, value);
    }

    private bool _alwaysOnTop;
    /// <summary>
    /// 是否在桌面最顶层悬浮（Always on top）
    /// </summary>
    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set => SetProperty(ref _alwaysOnTop, value);
    }

    /// <summary>
    /// 兼容旧版置顶属性：读返回 IsPinnedInList，写同时赋给 IsPinnedInList 和 AlwaysOnTop
    /// </summary>
    public bool IsPinned
    {
        get => _isPinnedInList;
        set
        {
            IsPinnedInList = value;
            AlwaysOnTop = value;
            OnPropertyChanged(nameof(IsPinned));
        }
    }

    private bool _isDeleted;
    /// <summary>
    /// 是否软删除（回收站机制）
    /// </summary>
    public bool IsDeleted
    {
        get => _isDeleted;
        set => SetProperty(ref _isDeleted, value);
    }

    public double WindowX { get; set; } = 150;
    public double WindowY { get; set; } = 150;
    public double WindowWidth { get; set; } = 380;
    public double WindowHeight { get; set; } = 420;
    public bool IsOpen { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    private DateTime _updatedAt = DateTime.UtcNow;
    /// <summary>
    /// 最后修改时间（统一 UTC）
    /// </summary>
    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set => SetProperty(ref _updatedAt, value);
    }

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
