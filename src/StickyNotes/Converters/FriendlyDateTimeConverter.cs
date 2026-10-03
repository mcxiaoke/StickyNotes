using System.Globalization;
using System.Windows.Data;

namespace StickyNotes.Converters;

/// <summary>
/// 将 UTC DateTime 转换为符合用户习惯的本地化友好时间描述
/// </summary>
public sealed class FriendlyDateTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTime dt)
        {
            var local = dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
            var now = DateTime.Now;
            var diff = now - local;

            if (diff.TotalMinutes < 1)
                return "刚刚";
            if (diff.TotalMinutes < 60)
                return $"{(int)diff.TotalMinutes} 分钟前";
            if (diff.TotalHours < 24 && now.Date == local.Date)
                return $"今天 {local:HH:mm}";
            if (now.Date.AddDays(-1) == local.Date)
                return $"昨天 {local:HH:mm}";
            if (now.Year == local.Year)
                return local.ToString("MM-dd HH:mm");

            return local.ToString("yyyy-MM-dd");
        }

        return string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
