using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameWall.Services;

namespace GameWall.Services;

/// <summary>
/// 生成占位封面：当游戏没有自定义封面、也提取不到图标时，
/// 用「名字首字 + 渐变底色」拼一张 3:4 的卡片图，保证游戏墙视觉统一、不出现空洞。
/// </summary>
public static class CoverFactory
{
    private const int Width = 480;
    private const int Height = 640;

    /// <summary>按游戏名生成（并缓存）占位封面，返回 PNG 路径。</summary>
    public static string? CreatePlaceholder(string gameName, string gameId)
    {
        try
        {
            AppPaths.EnsureCreated();
            var path = Path.Combine(AppPaths.Covers, $"placeholder_{IconService.Sanitize(gameId)}.png");
            if (File.Exists(path))
            {
                return path;
            }

            var bmp = Render(gameName);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            encoder.Save(fs);
            return path;
        }
        catch (Exception ex)
        {
            Logger.Error($"生成占位封面失败: {gameName}", ex);
            return null;
        }
    }

    private static BitmapSource Render(string gameName)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // 依据名字哈希挑选一组稳定的配色，保证同一游戏每次颜色一致
            var hue = StableHash(gameName) % 360;
            var top = FromHsl(hue, 0.45, 0.32);
            var bottom = FromHsl((hue + 40) % 360, 0.55, 0.12);

            dc.DrawRectangle(
                new LinearGradientBrush(top, bottom, 90),
                null,
                new Rect(0, 0, Width, Height));

            // 顶部高光
            dc.DrawRectangle(
                new RadialGradientBrush(Color.FromArgb(60, 255, 255, 255), Colors.Transparent)
                {
                    Center = new Point(0.5, 0.18),
                    GradientOrigin = new Point(0.5, 0.18),
                    RadiusX = 0.85,
                    RadiusY = 0.5,
                },
                null,
                new Rect(0, 0, Width, Height));

            // 中央首字
            var initial = FirstGlyph(gameName);
            var text = new FormattedText(
                initial,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                230,
                new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
                1.0);

            dc.DrawText(text, new Point((Width - text.Width) / 2, (Height - text.Height) / 2 - 40));
        }

        var rtb = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static string FirstGlyph(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var trimmed = name.Trim();
        return trimmed[..1].ToUpperInvariant();
    }

    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = 23;
            foreach (var c in text ?? string.Empty)
            {
                hash = hash * 31 + c;
            }

            return Math.Abs(hash);
        }
    }

    private static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360.0;
        double r, g, b;

        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3);
        }

        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
