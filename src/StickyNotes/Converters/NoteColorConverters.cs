using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using StickyNotes.Models;

namespace StickyNotes.Converters;

/// <summary>
/// 便签主题色 Frozen 笔刷缓存。
/// 背景：原先每个转换器在每次 Convert 时都执行 ColorConverter.ConvertFromString + new SolidColorBrush，
/// 既产生字符串解析开销与对象分配，又因未 Freeze 而每次返回新实例，导致 WPF 认为画刷发生变化而触发重绘
/// （NoteWindow.xaml 单次换色即绑定 9 处）。此处改为按颜色预构建并 Freeze 的静态缓存，转换时仅做字典查表。
/// </summary>
internal static class ThemeBrushCache
{
    internal sealed record FrozenBrushes(
        SolidColorBrush Background,
        SolidColorBrush Toolbar,
        SolidColorBrush Text,
        SolidColorBrush Border,
        SolidColorBrush Accent,
        SolidColorBrush SecondaryText);

    private static readonly Dictionary<NoteColor, FrozenBrushes> Cache = Build();

    private static Dictionary<NoteColor, FrozenBrushes> Build()
    {
        var dict = new Dictionary<NoteColor, FrozenBrushes>();
        foreach (var color in Enum.GetValues<NoteColor>())
        {
            var theme = color.GetTheme();
            dict[color] = new FrozenBrushes(
                CreateFrozen(theme.BackgroundHex),
                CreateFrozen(theme.ToolbarHex),
                CreateFrozen(theme.TextHex),
                CreateFrozen(theme.BorderHex),
                CreateFrozen(theme.AccentHex),
                CreateFrozen(theme.SecondaryTextHex));
        }
        return dict;
    }

    private static SolidColorBrush CreateFrozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 取指定颜色的主题笔刷；未知值回退到经典黄，保证绑定永不返回 null。
    /// </summary>
    internal static FrozenBrushes Get(NoteColor color) =>
        Cache.TryGetValue(color, out var brushes) ? brushes : Cache[NoteColor.Yellow];
}

/// <summary>
/// 将 NoteColor 枚举转换为对应背景色 Brush
/// </summary>
public sealed class NoteColorToBackgroundBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = value is NoteColor c ? c : NoteColor.Yellow;
        return ThemeBrushCache.Get(color).Background;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 将 NoteColor 枚举转换为工具栏色 Brush
/// </summary>
public sealed class NoteColorToToolbarBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = value is NoteColor c ? c : NoteColor.Yellow;
        return ThemeBrushCache.Get(color).Toolbar;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 将 NoteColor 枚举转换为文本前景色 Brush
/// </summary>
public sealed class NoteColorToTextBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = value is NoteColor c ? c : NoteColor.Yellow;
        return ThemeBrushCache.Get(color).Text;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 将 NoteColor 枚举转换为边框画刷 Brush
/// </summary>
public sealed class NoteColorToBorderBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = value is NoteColor c ? c : NoteColor.Yellow;
        return ThemeBrushCache.Get(color).Border;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 将 NoteColor 枚举转换为强调饱和色画刷 Brush (用于左侧色彩标识或圆点)
/// </summary>
public sealed class NoteColorToAccentBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = value is NoteColor c ? c : NoteColor.Yellow;
        return ThemeBrushCache.Get(color).Accent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 将 NoteColor 枚举转换为次要辅助文本 Brush
/// </summary>
public sealed class NoteColorToSecondaryTextBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = value is NoteColor c ? c : NoteColor.Yellow;
        return ThemeBrushCache.Get(color).SecondaryText;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 判断 NoteColor 是否等于参数指定的颜色，返回 Visible / Collapsed (用于调色盘 Checkmark 指示)
/// </summary>
public sealed class NoteColorEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is NoteColor color)
        {
            if (parameter is NoteColor targetColor && color == targetColor)
                return Visibility.Visible;
            if (parameter is string paramStr && Enum.TryParse<NoteColor>(paramStr, true, out var parsed) && color == parsed)
                return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 反转布尔值到可见性转换器 (True -> Collapsed, False -> Visible)
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return b ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
