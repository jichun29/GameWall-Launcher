using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GameWall.Models;

/// <summary>
/// 视图模式。
///
/// 历史包袱：1.5 及以前有三种视图（Wall 大图墙 / List 列表 / Compact 紧凑卡片）。
/// 1.6 把「大图墙」删掉了——它和「卡片式」的区别只是卡片更宽，信息量一样却更占地方；
/// 现在的两种视图就是「卡片式」（默认，像 Steam 库）和「列表式」。
///
/// 枚举值故意没有重排：Compact 仍保留 2，把旧配置里的 Wall(0) 在读取时归一到 Card(2)，
/// 这样老用户升级后不会看到一个空白的视图。
/// </summary>
public enum WallViewMode
{
    /// <summary>已废弃的大图墙（仅用于兼容旧配置，读取时会被换成 <see cref="Card"/>）</summary>
    Wall = 0,

    /// <summary>列表式</summary>
    List = 1,

    /// <summary>卡片式（默认，像 Steam 的游戏库）</summary>
    Card = 2,
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

    /// <summary>当前视图模式（默认卡片式）</summary>
    public WallViewMode ViewMode { get; set; } = WallViewMode.Card;

    /// <summary>
    /// 卡片宽度（像素）。封面按 3:4 的竖版比例跟着变高（见 <see cref="CardHeight"/>）。
    /// 默认 150 —— 一屏能放下 6~8 张，接近 Steam 库的密度；
    /// 想更大气可以拖到 260。
    /// </summary>
    public double CardWidth { get; set; } = 150;

    /// <summary>
    /// 封面高宽比（高 ÷ 宽）= 3/2。
    /// 取 2:3 的竖版而不是正方形：Steam / GOG 的库封面本身就是 600×900 的竖图，
    /// 正方形要么把两边裁掉、要么上下留黑边；横屏宣传图也不会被拉成方块。
    /// </summary>
    public const double CoverAspectRatio = 3.0 / 2.0;

    /// <summary>
    /// 封面高度（只读派生值）= 卡片宽度 × 4/3。
    /// 界面直接绑这个，改宽度时高度自动跟着走，不用在两处各调一次。
    /// 派生值不进 settings.json —— 反序列化时它也不能赋值，写进去只会造成一份会和
    /// CardWidth 不一致的冗余数据。
    /// </summary>
    [JsonIgnore]
    public double CardHeight => Math.Round(CardWidth * CoverAspectRatio);

    /// <summary>可选的卡片宽度下限</summary>
    public const double MinCardWidth = 110;

    /// <summary>可选的卡片宽度上限</summary>
    public const double MaxCardWidth = 260;

    /// <summary>旧版本用的默认卡片宽度，读配置时识别并迁移成新默认值</summary>
    public const double LegacyCardWidth = 208;

    /// <summary>卡片悬停时的放大动画是否开启（低配机器可以关掉省 GPU）</summary>
    public bool EnableCardAnimations { get; set; } = true;

    /// <summary>卡片是否显示名称文字</summary>
    public bool ShowLabels { get; set; } = true;

    /// <summary>自定义背景图片路径（留空则使用纯色 / 渐变）</summary>
    public string BackgroundImagePath { get; set; } = string.Empty;

    /// <summary>
    /// 背景纯色（HEX，如 #07080C）。留空 = 跟随主题默认底色，
    /// 这样切到浅色模式时背景会一起变亮，不会出现「浅色界面 + 深色底」的割裂感。
    /// </summary>
    public string BackgroundColor { get; set; } = string.Empty;

    /// <summary>背景图片不透明度 0~1</summary>
    public double BackgroundImageOpacity { get; set; } = 0.35;

    /// <summary>
    /// 强调色（HEX）。留空 = 跟随主题默认强调色（深色 #3DDBFF / 浅色 #1668E3）。
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

    // ── 桌面整理 ────────────────────────────────────────────
    /// <summary>
    /// 扫描 / 导入之后，如果发现桌面上还有「已收录但没挪走」的快捷方式，
    /// 是否主动弹出整理窗口。默认开着——大多数人就是因为桌面太乱才来用这个工具的。
    /// </summary>
    public bool AskTidyAfterImport { get; set; } = true;

    /// <summary>上次整理桌面后是否还提示（用户点过「以后别问了」就关掉）</summary>
    public bool ShowTidyHint { get; set; } = true;

    // ── 游戏工具箱 ──────────────────────────────────────────
    /// <summary>
    /// 用户点过「以后别提示了」就置 true，工具箱相关的一次性引导不再弹。
    /// </summary>
    public bool HardwareToolboxSnoozed { get; set; }

    /// <summary>已应用的优化项 Id 列表，用于界面上打勾与批量还原</summary>
    public List<string> AppliedTweaks { get; set; } = new();

    /// <summary>硬件信息页上次停留的页签索引</summary>
    public int ToolboxTabIndex { get; set; }

    // ── 更新 ────────────────────────────────────────────────
    /// <summary>启动时自动检查更新（每天最多真正请求一次 GitHub）</summary>
    public bool AutoCheckUpdate { get; set; } = true;

    /// <summary>
    /// 允许联网下载游戏封面（默认开）。
    ///
    /// 本地 Steam 缓存里没有封面时，用 appid 去 Steam 的图片 CDN 取官方竖版封面。
    /// 这是程序里唯一会主动联网的功能，只下载图片、不上传任何数据；
    /// 想完全离线就把这个开关关掉，届时没缓存的游戏会显示首字母封面。
    /// </summary>
    public bool DownloadCoverFromWeb { get; set; } = true;

    /// <summary>
    /// 更新下载源。<c>auto</c> = 直连失败时自动切换到内置加速源，
    /// 也可以填 <c>github</c> / <c>ghproxy</c> / <c>ghfast</c> / <c>moeyy</c> 强制指定。
    /// </summary>
    public string UpdateSource { get; set; } = "auto";
}
