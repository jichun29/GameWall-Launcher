using System;
using Microsoft.Win32;
using GameWall.Models;

namespace GameWall.Services;

/// <summary>
/// 读取并监听 Windows 的「应用模式」深浅色设置。
///
/// 数据来源：HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize
///   AppsUseLightTheme = 1 → 应用使用浅色；0（或不存在）→ 应用使用深色。
/// 注意这里读的是 AppsUseLightTheme 而不是 SystemUsesLightTheme：
/// 前者代表「应用」模式，后者代表「Windows 模式」，用户可以把两者设成不同的值。
///
/// 监听方式：Microsoft.Win32.SystemEvents.UserPreferenceChanged。
/// 用户在 设置 → 个性化 → 颜色 里切换模式时，系统会广播 WM_SETTINGCHANGE，
/// SystemEvents 会把它翻译成 UserPreferenceChanged，我们就能实时跟着换肤。
/// </summary>
public static class SystemThemeWatcher
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private static bool _hooked;

    /// <summary>系统主题变化时触发（已切到 UI 线程）。参数是 ThemeModes.Light 或 ThemeModes.Dark。</summary>
    public static event Action<string>? SystemThemeChanged;

    /// <summary>当前系统实际使用的主题（ThemeModes.Light / ThemeModes.Dark）。</summary>
    public static string CurrentSystemTheme => IsSystemLight() ? ThemeModes.Light : ThemeModes.Dark;

    /// <summary>把「主题模式」解析成实际要用的主题：System 时去问系统，其余原样返回。</summary>
    public static string Resolve(string? themeMode)
        => string.Equals(ThemeModes.Normalize(themeMode), ThemeModes.System, StringComparison.Ordinal)
            ? CurrentSystemTheme
            : ThemeModes.Normalize(themeMode);

    /// <summary>读注册表判断系统应用模式是否为浅色。</summary>
    public static bool IsSystemLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            var raw = key?.GetValue(AppsUseLightThemeValue);

            // 值不存在时按 Windows 的默认行为当作深色
            if (raw is int i)
            {
                return i != 0;
            }

            return false;
        }
        catch (Exception ex)
        {
            // 极端情况下（策略锁定、注册表不可读）退回深色，不影响主流程
            Logger.Error("读取系统主题失败", ex);
            return false;
        }
    }

    /// <summary>开始监听系统主题变化。重复调用安全。</summary>
    public static void Start()
    {
        if (_hooked)
        {
            return;
        }

        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _hooked = true;
        }
        catch (Exception ex)
        {
            Logger.Error("注册系统主题监听失败", ex);
        }
    }

    /// <summary>停止监听（退出时调用，避免静态事件把已关闭的窗口吊住）。</summary>
    public static void Stop()
    {
        if (!_hooked)
        {
            return;
        }

        try
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
        catch (Exception ex)
        {
            Logger.Error("注销系统主题监听失败", ex);
        }

        _hooked = false;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // General / VisualStyle / Color 都可能伴随深浅色切换，统一当作「可能变了」处理；
        // 真正的判断交给订阅方去比对实际主题，避免误报导致频繁重载资源字典。
        if (e.Category != UserPreferenceCategory.General
            && e.Category != UserPreferenceCategory.VisualStyle
            && e.Category != UserPreferenceCategory.Color)
        {
            return;
        }

        var theme = CurrentSystemTheme;

        // SystemEvents 的回调在后台线程，必须切回 UI 线程再动资源字典
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                SystemThemeChanged?.Invoke(theme);
            }
            catch (Exception ex)
            {
                Logger.Error("处理系统主题变化失败", ex);
            }
        }));
    }
}
