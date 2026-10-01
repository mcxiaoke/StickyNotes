using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using StickyNotes.Models;

namespace StickyNotes.Converters;

/// <summary>
/// 将 NoteColor 枚举转换为对应背景色 Brush
/// </summary>
public sealed class NoteColorToBackgroundBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is NoteColor color)
        {
            var (bgHex, _, _) = color.GetThemeColors();
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(bgHex));
        }
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF7D1"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw freshNotSupported();

    private static NotSupportedException freshNotSupported() => new();
}

/// <summary>
/// 将 NoteColor 枚举转换为工具栏色 Brush
/// </summary>
public sealed class NoteColorToToolbarBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is NoteColor color)
        {
            var (_, tbHex, _) = color.GetThemeColors();
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(tbHex));
        }
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFEE9D"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw freshNotSupported();

    private static NotSupportedException freshNotSupported() => new();
}

/// <summary>
/// 将 NoteColor 枚举转换为文本前景色 Brush
/// </summary>
public sealed class NoteColorToTextBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is NoteColor color)
        {
            var (_, _, textHex) = color.GetThemeColors();
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(textHex));
        }
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C1C1C"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw freshNotSupported();

    private static NotSupportedException freshNotSupported() => new();
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
            return b ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        }
        return System.Windows.Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

