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

    /// <summary>
    /// 直接在内存里造一个和占位封面同色系的渐变画刷，给卡片的背景层用。
    ///
    /// 相比 CreatePlaceholder 要走 RenderTargetBitmap + PNG 编码（单张 20ms 上下），
    /// 这里只是算几个颜色再 new 一个画刷，几十张卡片也就是零点几毫秒，
    /// 首屏再也不会因为这层兜底视觉卡住。
    /// </summary>
    public static Brush CreatePlaceholderBrush(string gameName)
    {
        var (top, bottom) = PickColors(gameName);

        var brush = new LinearGradientBrush(top, bottom, 90);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 按名字哈希从一组「像游戏封面」的暗色里挑一对颜色。
    ///
    /// 早先的实现是「哈希取色相」，结果大约四分之一的游戏会落到紫色区间，
    /// 一整屏看过去全是青紫渐变，特别像 AI 生成的图。这里改成从手工挑过的
    /// 色板里选：深海蓝、青、苔绿、琥珀、暗红、石板灰，色相刻意避开 260~330
    /// 的紫区，饱和度压低、明度压暗，摆在深色背景上不刺眼也不脏。
    /// </summary>
    private static (Color Top, Color Bottom) PickColors(string gameName)
    {
        var palette = Palette;
        var index = StableHash(gameName) % palette.Length;
        return palette[index];
    }

    /// <summary>占位封面色板（上亮下暗，每项都在深色底上验证过可读性）。</summary>
    private static readonly (Color Top, Color Bottom)[] Palette =
    {
        (Rgb(0x1E, 0x3A, 0x5F), Rgb(0x09, 0x12, 0x1F)), // 深海蓝
        (Rgb(0x14, 0x4A, 0x52), Rgb(0x07, 0x16, 0x1B)), // 幽青
        (Rgb(0x1F, 0x4A, 0x3C), Rgb(0x08, 0x17, 0x12)), // 苔绿
        (Rgb(0x5A, 0x3E, 0x14), Rgb(0x1B, 0x11, 0x06)), // 琥珀
        (Rgb(0x53, 0x22, 0x22), Rgb(0x1A, 0x0A, 0x0A)), // 暗红
        (Rgb(0x2E, 0x33, 0x42), Rgb(0x0D, 0x0F, 0x15)), // 石板灰
        (Rgb(0x22, 0x2C, 0x4E), Rgb(0x0B, 0x0E, 0x1A)), // 靛夜
        (Rgb(0x39, 0x33, 0x22), Rgb(0x12, 0x10, 0x0A)), // 古铜
        (Rgb(0x16, 0x3E, 0x4A), Rgb(0x06, 0x12, 0x17)), // 冷杉
        (Rgb(0x44, 0x28, 0x3C), Rgb(0x16, 0x0C, 0x14)), // 暗莓（偏红，不是紫）
    };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static BitmapSource Render(string gameName)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // 依据名字哈希挑选一组稳定的配色，保证同一游戏每次颜色一致
            var (top, bottom) = PickColors(gameName);

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

            // 中央标牌：原来只画一个光秃秃的大字母，摆在渐变底上像是渲染坏了；
            // 这里补一圈淡描边和底板，做成杂志刊头字那种感觉，整墙看过去更统一。
            var glyph = BuildGlyph(gameName);
            const double box = 300;
            var boxLeft = (Width - box) / 2;
            var boxTop = (Height - box) / 2 - 40;

            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
                new Pen(new SolidColorBrush(Color.FromArgb(61, 255, 255, 255)), 3),
                new Rect(boxLeft, boxTop, box, box),
                54, 54);

            // 字长不同（1 字 / 2 字母）时自动缩放字号，保证不溢出方框
            var fontSize = glyph.Length >= 2 ? 168.0 : 210.0;
            var text = new FormattedText(
                glyph,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                fontSize,
                new SolidColorBrush(Color.FromArgb(242, 255, 255, 255)),
                1.0);

            dc.DrawText(text, new Point((Width - text.Width) / 2, boxTop + (box - text.Height) / 2));
        }

        var rtb = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// 占位封面上显示的字。规则与 GameCardViewModel.InitialGlyph 保持一致：
    /// 中文取第一个字；英文取前两个单词首字母（Sniper Elite → SE）；
    /// 单个单词取前两个字母（JAPANSTIGMATIZED → JA）。
    /// </summary>
    private static string BuildGlyph(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var trimmed = name.Trim();
        if (trimmed[0] > 0x7F)
        {
            return trimmed[..1];
        }

        var words = trimmed.Split(new[] { ' ', '-', '_', ':', '.', '\'' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && words[0].Length > 0 && words[1].Length > 0)
        {
            return (char.ToUpperInvariant(words[0][0]).ToString() + char.ToUpperInvariant(words[1][0])).ToUpperInvariant();
        }

        var word = words.Length > 0 ? words[0] : trimmed;
        return word.Length >= 2
            ? (char.ToUpperInvariant(word[0]).ToString() + char.ToUpperInvariant(word[1]))
            : char.ToUpperInvariant(word[0]).ToString();
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

}
