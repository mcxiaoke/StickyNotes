namespace StickyNotes.Models;

/// <summary>
/// 便签经典 7 色主题定义
/// </summary>
public enum NoteColor
{
    Yellow = 0,
    Green = 1,
    Pink = 2,
    Purple = 3,
    Blue = 4,
    Gray = 5,
    Charcoal = 6
}

/// <summary>
/// 便签颜色值转换与元数据
/// </summary>
public static class NoteColorExtensions
{
    public static (string BackgroundHex, string ToolbarHex, string TextHex) GetThemeColors(this NoteColor color) =>
        color switch
        {
            NoteColor.Yellow => ("#FFF7D1", "#FFEE9D", "#1C1C1C"),
            NoteColor.Green => ("#E4F9E0", "#C8F2C2", "#1C1C1C"),
            NoteColor.Pink => ("#FFE4EF", "#FFC7DE", "#1C1C1C"),
            NoteColor.Purple => ("#F2E6FF", "#E4CCFF", "#1C1C1C"),
            NoteColor.Blue => ("#E1F3FE", "#C3E8FD", "#1C1C1C"),
            NoteColor.Gray => ("#F5F5F5", "#E8E8E8", "#1C1C1C"),
            NoteColor.Charcoal => ("#2D2D2D", "#222222", "#F0F0F0"),
            _ => ("#FFF7D1", "#FFEE9D", "#1C1C1C")
        };
}
