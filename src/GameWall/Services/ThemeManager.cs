using System;
using System.Windows;
using System.Windows.Media;
using GameWall.Models;

namespace GameWall.Services;

/// <summary>
/// 主题切换：替换 App 资源字典里的一整套颜色。
///
/// 所有控件样式都用 DynamicResource 引用 Brush.*，所以替换字典后界面会立即刷新，
/// 不需要重启窗口。
///
/// Theme 设置支持三态：System（跟随系统）/ Dark / Light。
/// 真正落到界面上的永远是 Dark 或 Light —— System 会先被 SystemThemeWatcher 解析成实际值。
/// </summary>
public static class ThemeManager
{
    private const string DarkUri = "pack://application:,,,/Themes/Dark.xaml";
    private const string LightUri = "pack://application:,,,/Themes/Light.xaml";

    /// <summary>当前实际生效的主题（ThemeModes.Dark / ThemeModes.Light）。</summary>
    public static string CurrentTheme { get; private set; } = ThemeModes.Dark;

    /// <summary>应用某个主题。传入 "System" 时会自动解析成当前系统主题。</summary>
    public static void Apply(string theme)
    {
        var actual = SystemThemeWatcher.Resolve(theme);
        var uri = string.Equals(actual, ThemeModes.Light, StringComparison.Ordinal) ? LightUri : DarkUri;
        CurrentTheme = actual;

        try
        {
            var dict = new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) };

            var app = Application.Current;
            if (app is null)
            {
                return;
            }

            // 主题字典固定插到第 0 位，保证 Controls.xaml 里的 DynamicResource 能解析到它
            for (var i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
            {
                var existing = app.Resources.MergedDictionaries[i];
                var src = existing.Source?.OriginalString ?? string.Empty;
                if (src.Contains("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase)
                    || src.Contains("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase))
                {
                    app.Resources.MergedDictionaries.RemoveAt(i);
                }
            }

            app.Resources.MergedDictionaries.Insert(0, dict);
            Logger.Info($"主题已应用：{theme} → {actual}");
        }
        catch (Exception ex)
        {
            Logger.Error($"切换主题失败: {theme}", ex);
        }
    }

    /// <summary>
    /// 把用户配置的强调色 / 背景色写进资源，覆盖主题默认值。
    /// 传入空字符串表示「跟随主题默认色」——此时直接清掉覆盖项，让主题字典里的值生效。
    /// </summary>
    public static void ApplyAccent(string? accentHex)
    {
        try
        {
            var app = Application.Current;
            if (app is null)
            {
                return;
            }

            // 空值 = 用主题自带的强调色（深色 #4F8CFF，浅色 #2563EB）
            if (string.IsNullOrWhiteSpace(accentHex))
            {
                app.Resources.Remove("Brush.Accent");
                app.Resources.Remove("Brush.AccentSoft");
                return;
            }

            var color = (Color)ColorConverter.ConvertFromString(accentHex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();

            var soft = new SolidColorBrush(Color.FromArgb(0x38, color.R, color.G, color.B));
            soft.Freeze();

            app.Resources["Brush.Accent"] = brush;
            app.Resources["Brush.AccentSoft"] = soft;
        }
        catch (Exception ex)
        {
            Logger.Error($"设置强调色失败: {accentHex}", ex);
        }
    }

    /// <summary>一次性应用全部外观设置。</summary>
    public static void ApplyAll(AppSettings settings)
    {
        Apply(settings.Theme);
        ApplyAccent(settings.AccentColor);
    }
}
