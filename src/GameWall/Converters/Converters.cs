using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameWall.Converters;

/// <summary>
/// 路径 → BitmapImage。
///
/// 走 ImageCache 复用解码结果：卡片模板会反复问同一个路径，
/// 每次都重新解码 PNG 是启动慢的主因。缓存键含文件修改时间，
/// 换了封面会自动重新读；同时用 OnLoad 让文件句柄立刻释放，
/// 用户可以随时替换封面文件而不会报「被另一进程占用」。
/// </summary>
public sealed class PathToImageConverter : IValueConverter
{
    /// <summary>默认解码宽度。卡片最宽也就两百多像素，解码到 640 已经富余。</summary>
    private const int DefaultDecodeWidth = 320;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        // 允许在 XAML 里用 ConverterParameter 指定解码宽度
        var width = DefaultDecodeWidth;
        if (parameter is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            width = parsed;
        }

        return ImageCache.Get(path, width);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → Visibility，ConverterParameter="invert" 可反转。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>非空字符串 → Visible，ConverterParameter="invert" 反转。</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasText = !string.IsNullOrWhiteSpace(value as string);
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
        {
            hasText = !hasText;
        }

        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>#RRGGBB / #AARRGGBB → Brush。空字符串表示「跟随主题」，返回透明占位。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as string)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return Brushes.Transparent;
        }

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(text);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>枚举与 ConverterParameter 相等时返回 true（视图分段按钮用）。</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
        {
            return false;
        }

        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>收藏状态 → 右键菜单文案。</summary>
public sealed class FavoriteMenuTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool on && on ? "取消收藏" : "加入收藏";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 宽 + 高 + 圆角半径 → 圆角矩形 Clip。
///
/// 为什么需要它：WPF 的 <c>Border.CornerRadius</c> **只影响自己那条描边**，
/// 不会把子元素裁成圆角（<c>ClipToBounds</c> 裁的是矩形）。
/// 于是封面图 / 渐变矩形这些方形内容会把四个角的圆弧整片盖掉，
/// 平时描边颜色和背景接近看不出来，一旦「选中」变成 2px 电光青，
/// 就只剩四条直边、四个角是空的 —— 看起来像直角边框，很突兀。
///
/// 把它挂到 Border 里那层内容容器的 Clip 上，圆弧才会真的露出来。
/// 用法（MultiBinding，第三个值是圆角半径）：
///   &lt;Grid.Clip&gt;&lt;MultiBinding Converter="{StaticResource RoundedClip}"&gt;
///     &lt;Binding Path="ActualWidth" RelativeSource="{RelativeSource Self}" /&gt;
///     &lt;Binding Path="ActualHeight" RelativeSource="{RelativeSource Self}" /&gt;
///     &lt;Binding Source="14" /&gt;
///   &lt;/MultiBinding&gt;&lt;/Grid.Clip&gt;
/// </summary>
public sealed class RoundedClipConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2
            || values[0] is not double width || values[1] is not double height
            || double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0)
        {
            // 尺寸还没算出来：给一个空裁剪，避免画出半张图
            return new RectangleGeometry(Rect.Empty);
        }

        var radius = 14.0;
        if (values.Length > 2 && values[2] is not null)
        {
            if (values[2] is double d)
            {
                radius = d;
            }
            else if (double.TryParse(values[2].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                radius = parsed;
            }
        }

        // 半径不能超过短边的一半，否则 RectangleGeometry 会退化成奇怪的形状
        var limit = Math.Min(width, height) / 2.0;
        if (radius > limit)
        {
            radius = limit;
        }

        var geometry = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
        geometry.Freeze();
        return geometry;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
