using System.Globalization;
using System.Windows;
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
            var theme = color.GetTheme();
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.BackgroundHex));
        }
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF7D1"));
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
        if (value is NoteColor color)
        {
            var theme = color.GetTheme();
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.ToolbarHex));
        }
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFEE9D"));
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
        if (value is NoteColor color)
        {
            var theme = color.GetTheme();
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.TextHex));
        }
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202020"));
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
        if (value is NoteColor color)
        {
            var theme = color.GetTheme();
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.BorderHex));
        }
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E6D77D"));
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
        if (value is NoteColor color)
        {
            var theme = color.GetTheme();
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.AccentHex));
        }
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0A800"));
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
        if (value is NoteColor color)
        {
            var theme = color.GetTheme();
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.SecondaryTextHex));
        }
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6C6546"));
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

