using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GameWall.Models;

/// <summary>
/// 游戏的启动方式。不同平台导入的游戏并不都是一条 exe 路径，
/// 例如 Steam 需要通过 steam:// 协议唤起客户端。
/// </summary>
public enum LaunchKind
{
    /// <summary>直接启动 exe 文件</summary>
    Executable = 0,

    /// <summary>通过 steam://rungameid/{appid} 启动</summary>
    Steam = 1,

    /// <summary>通过 com.epicgames.launcher:// 启动</summary>
    Epic = 2,

    /// <summary>通过 GOG 的 galaxy2:// 或本地 exe 启动</summary>
    Gog = 3,

    /// <summary>自定义 URI / 命令</summary>
    Uri = 4,
}

/// <summary>
/// 一款游戏的全部持久化信息。序列化到 %AppData%\GameWall\library.json。
/// </summary>
public sealed class GameEntry
{
    /// <summary>稳定唯一标识，用于拖拽排序、封面文件命名等</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>启动方式</summary>
    public LaunchKind LaunchKind { get; set; } = LaunchKind.Executable;

    /// <summary>可执行文件完整路径（平台导入的游戏可能为空）</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>平台启动 URI（Steam/Epic 等使用）</summary>
    public string LaunchUri { get; set; } = string.Empty;

    /// <summary>命令行参数</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>工作目录，留空则取 exe 所在目录</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>自定义封面图片路径（指向 %AppData%\GameWall\covers 下的副本）</summary>
    public string CoverPath { get; set; } = string.Empty;

    /// <summary>从 exe 提取出来的图标缓存路径（PNG）</summary>
    public string IconPath { get; set; } = string.Empty;

    /// <summary>存档目录，右键菜单「打开存档目录」使用</summary>
    public string SaveDirectory { get; set; } = string.Empty;

    /// <summary>标签 / 分类</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>备注</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>是否收藏（收藏的卡片有角标）</summary>
    public bool IsFavorite { get; set; }

    /// <summary>添加时间</summary>
    public DateTime AddedAt { get; set; } = DateTime.Now;

    /// <summary>最后启动时间</summary>
    public DateTime? LastLaunchAt { get; set; }

    /// <summary>累计启动次数</summary>
    public int LaunchCount { get; set; }

    /// <summary>拖拽排序用的序号，越小越靠前</summary>
    public int SortIndex { get; set; }

    /// <summary>来源平台（手动 / Steam / Epic / GOG / 扫描），仅用于展示</summary>
    public string Source { get; set; } = "手动添加";

    /// <summary>文件是否仍然存在（运行时计算，不参与序列化）</summary>
    [JsonIgnore]
    public bool Exists =>
        LaunchKind != LaunchKind.Executable
        || (!string.IsNullOrWhiteSpace(ExecutablePath) && System.IO.File.Exists(ExecutablePath));
}
