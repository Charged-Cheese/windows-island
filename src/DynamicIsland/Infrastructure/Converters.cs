using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Infrastructure;

/// <summary>byte[]（PNG/JPG）→ BitmapImage。</summary>
public sealed class BytesToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes)
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → Visibility（true 显示）。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>bool → Visibility（true 隐藏）。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not Visibility.Visible;
}

/// <summary>bool 取反。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>0-1 比例 + 容器宽度 → 实际宽度（用于进度条 / 环形条）。</summary>
public sealed class RatioToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double ratio || values[1] is not double width)
        {
            return 0d;
        }

        if (double.IsNaN(width) || double.IsNaN(ratio))
        {
            return 0d;
        }

        return Math.Clamp(ratio, 0, 1) * width;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 0-1 比例 + 容器宽度 → 滑块左边距（Thickness），让滑块中心落在进度位置。
/// 参数为滑块直径（默认 8），两端自动夹住，避免圆点越出轨道。
/// </summary>
public sealed class RatioToThumbMarginConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double ratio || values[1] is not double width)
        {
            return new Thickness(0);
        }

        if (double.IsNaN(width) || double.IsNaN(ratio) || width <= 0)
        {
            return new Thickness(0);
        }

        double diameter = 8;
        if (parameter is string text &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            diameter = parsed;
        }

        double left = Math.Clamp(ratio, 0, 1) * width - diameter / 2;
        left = Math.Clamp(left, 0, Math.Max(0, width - diameter));
        return new Thickness(left, 0, 0, 0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>比例 0-1 → 角度（0-360），用于环形进度。</summary>
public sealed class RatioToAngleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double ratio ? Math.Clamp(ratio, 0, 1) * 360 : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>比例 → 告警色（<75% 正常，<90% 橙，其余红）。</summary>
public sealed class RatioToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Normal = Freeze(Color.FromRgb(0x6C, 0x8C, 0xFF));
    private static readonly SolidColorBrush Warning = Freeze(Color.FromRgb(0xFF, 0xB0, 0x4A));
    private static readonly SolidColorBrush Danger = Freeze(Color.FromRgb(0xFF, 0x5A, 0x5A));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double ratio = value is double d ? d : 0;
        return ratio switch
        {
            >= 0.9 => Danger,
            >= 0.75 => Warning,
            _ => Normal
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>ImageSource 为空时返回 false，便于隐藏占位。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
