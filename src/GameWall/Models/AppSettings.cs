using System;
using System.Collections.Generic;

namespace GameWall.Models;

/// <summary>视图模式</summary>
public enum WallViewMode
{
    /// <summary>大图墙（默认）</summary>
    Wall = 0,

    /// <summary>列表模式</summary>
    List = 1,

    /// <summary>紧凑模式</summary>
    Compact = 2,
}

/// <summary>
/// 主题模式常量（settings.json 里 Theme 字段的合法取值）。
/// 用字符串而不是枚举，是为了让用户手改配置文件时写 "Dark"/"Light"/"System" 都直观。
/// </summary>
public static class ThemeModes
{
    /// <summary>跟随系统：系统是浅色就用浅色，是深色就用深色（并实时跟随切换）</summary>
    public const string System = "System";

    /// <summary>始终深色</summary>
    public const string Dark = "Dark";

    /// <summary>始终浅色</summary>
    public const string Light = "Light";

    /// <summary>把任意大小写的输入归一成上面三个常量之一，非法值回退到 Dark。</summary>
    public static string Normalize(string? value)
    {
        if (string.Equals(value, Light, StringComparison.OrdinalIgnoreCase))
        {
            return Light;
        }

        if (string.Equals(value, System, StringComparison.OrdinalIgnoreCase))
        {
            return System;
        }

        return Dark;
    }
}

/// <summary>
/// 全局配置，序列化到 %AppData%\GameWall\settings.json。
/// </summary>
public sealed class AppSettings
{
    // ── 外观 ────────────────────────────────────────────────
    /// <summary>
    /// 主题模式：System = 跟随系统（浅色/深色自动切换），Dark = 深色，Light = 浅色。
    /// 默认深色 —— 游戏封面墙在深色底下观感最好。
    /// </summary>
    public string Theme { get; set; } = ThemeModes.Dark;

    /// <summary>当前视图模式</summary>
    public WallViewMode ViewMode { get; set; } = WallViewMode.Wall;

    /// <summary>大图墙卡片宽度（像素），高度按 3:4 自动计算</summary>
    public double CardWidth { get; set; } = 208;

    /// <summary>卡片是否显示名称文字</summary>
    public bool ShowLabels { get; set; } = true;

    /// <summary>自定义背景图片路径（留空则使用纯色 / 渐变）</summary>
    public string BackgroundImagePath { get; set; } = string.Empty;

    /// <summary>
    /// 背景纯色（HEX，如 #101318）。留空 = 跟随主题默认底色，
    /// 这样切到浅色模式时背景会一起变亮，不会出现「浅色界面 + 深色底」的割裂感。
    /// </summary>
    public string BackgroundColor { get; set; } = string.Empty;

    /// <summary>背景图片不透明度 0~1</summary>
    public double BackgroundImageOpacity { get; set; } = 0.35;

    /// <summary>
    /// 强调色（HEX）。留空 = 跟随主题默认强调色（深色 #4F8CFF / 浅色 #2563EB）。
    /// </summary>
    public string AccentColor { get; set; } = string.Empty;

    // ── 兼容旧配置 ──────────────────────────────────────────
    /// <summary>
    /// 旧版本把强调色/背景色写死成上面这两个值当作「默认」，
    /// 读取配置时如果发现还是这两个老默认值，就当成「跟随主题」处理。
    /// </summary>
    public const string LegacyAccentColor = "#4F8CFF";

    /// <summary>旧版本写死的默认背景色</summary>
    public const string LegacyBackgroundColor = "#101318";

    // ── 窗口行为 ────────────────────────────────────────────
    public bool Topmost { get; set; }

    public bool StartFullscreen { get; set; }

    /// <summary>关闭窗口时最小化到托盘而不是退出</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>开机自启</summary>
    public bool AutoStart { get; set; }

    /// <summary>移除游戏前二次确认</summary>
    public bool ConfirmOnRemove { get; set; } = true;

    /// <summary>启动游戏后自动最小化自己</summary>
    public bool MinimizeOnGameLaunch { get; set; }

    // ── 窗口位置 ────────────────────────────────────────────
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;

    // 用可空类型而不是 double.NaN：JSON 里 NaN 不是合法数字，
    // 而 null 能被正常序列化/反序列化，手改配置文件也不会读崩。
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    // ── 扫描 ────────────────────────────────────────────────
    /// <summary>自动扫描 / 搜索时优先遍历的目录</summary>
    public List<string> ScanPaths { get; set; } = new();

    /// <summary>扫描时递归的最大深度</summary>
    public int ScanDepth { get; set; } = 4;

    /// <summary>扫描时忽略小于该大小的 exe（字节），过滤掉辅助小工具</summary>
    public long MinExecutableSize { get; set; } = 40 * 1024;

    // ── 状态 ────────────────────────────────────────────────
    /// <summary>是否已经完成首次运行引导</summary>
    public bool FirstRunCompleted { get; set; }

    /// <summary>上次检查更新的时间（预留）</summary>
    public DateTime? LastCheckAt { get; set; }
}
