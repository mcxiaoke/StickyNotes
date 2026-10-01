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
/// 便签主题色彩完整定义
/// </summary>
public sealed record NoteThemeColors(
    string BackgroundHex,
    string ToolbarHex,
    string TextHex,
    string BorderHex,
    string AccentHex,
    string SecondaryTextHex
);

/// <summary>
/// 便签颜色值转换与元数据
/// </summary>
public static class NoteColorExtensions
{
    public static NoteThemeColors GetTheme(this NoteColor color) =>
        color switch
        {
            NoteColor.Yellow => new("#FFF7D1", "#FFEE9D", "#202020", "#E6D77D", "#E0A800", "#6C6546"),
            NoteColor.Green => new("#E4F9E0", "#C8F2C2", "#202020", "#BCE5B6", "#209E35", "#476A42"),
            NoteColor.Pink => new("#FFE4EF", "#FFC7DE", "#202020", "#F5BCCE", "#DB3374", "#774457"),
            NoteColor.Purple => new("#F2E6FF", "#E4CCFF", "#202020", "#D5BAFA", "#7F3CD8", "#594575"),
            NoteColor.Blue => new("#E1F3FE", "#C3E8FD", "#202020", "#B7DAF5", "#1079D1", "#425C70"),
            NoteColor.Gray => new("#F6F6F8", "#E8E8EB", "#202020", "#DCDCE0", "#636366", "#616166"),
            NoteColor.Charcoal => new("#292929", "#1E1E1E", "#F5F5F5", "#3D3D3D", "#4CC2FF", "#A6A6A6"),
            _ => new("#FFF7D1", "#FFEE9D", "#202020", "#E6D77D", "#E0A800", "#6C6546")
        };

    public static (string BackgroundHex, string ToolbarHex, string TextHex) GetThemeColors(this NoteColor color)
    {
        var theme = color.GetTheme();
        return (theme.BackgroundHex, theme.ToolbarHex, theme.TextHex);
    }
}
