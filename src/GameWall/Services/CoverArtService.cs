using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GameWall.Services;

/// <summary>
/// 从本地平台缓存里捞真正的竖版封面。
///
/// 为什么不去网上下：GameWall 的定位就是纯本地工具（README 里写死了不联网）。
/// 好消息是 Steam 自己会把商店封面缓存到本地 —— 只要你在这台机器上打开过
/// 该游戏的商店页或库页面，<c>appcache\librarycache\&lt;appid&gt;</c> 下就会有一堆
/// 官方尺寸的图：
///   library_600x900.jpg          600×900  竖版网格封面（最合适）
///   library_capsule(_schinese)   300×450  竖版小胶囊
///   library_header(_schinese)    460×215  横版头图
///   library_hero.jpg            1920×620  横版大图
/// 实测本机 Steam 缓存里 323 个 appid 有 151 个带 600×900 竖版封面。
/// </summary>
public static class CoverArtService
{
    /// <summary>按优先级排列的候选文件名（越靠前越适合做卡片封面）。</summary>
    private static readonly string[] CandidateNames =
    {
        "library_600x900.jpg",
        "library_600x900.png",
        "library_600x900_schinese.jpg",
        "library_600x900_english.jpg",
        "library_capsule.jpg",
        "library_capsule_schinese.jpg",
        "library_capsule_english.jpg",
        "library_header.jpg",
        "library_header_schinese.jpg",
        "header.jpg",
        "library_hero.jpg",
    };

    private static readonly Regex SteamAppIdPattern = new(@"steam://(?:rungameid|run)/(\d+)", RegexOptions.IgnoreCase);

    /// <summary>从 steam:// 协议地址里取出 appid。</summary>
    public static string? GetSteamAppId(string? launchUri)
    {
        if (string.IsNullOrWhiteSpace(launchUri))
        {
            return null;
        }

        var match = SteamAppIdPattern.Match(launchUri);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// 尝试给一个 Steam 游戏补上封面，成功返回本地封面路径（已复制进数据目录）。
    /// 找不到就返回 null，调用方继续用占位图。
    /// </summary>
    public static string? TryFetchSteamCover(string? launchUri, string gameId)
    {
        try
        {
            var appId = GetSteamAppId(launchUri);
            if (string.IsNullOrEmpty(appId))
            {
                return null;
            }

            var source = FindSteamCoverFile(appId);
            if (string.IsNullOrEmpty(source))
            {
                Logger.Info($"Steam 缓存里没有 {appId} 的封面，改用占位图");
                return null;
            }

            AppPaths.EnsureCreated();
            var ext = Path.GetExtension(source);
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = ".jpg";
            }

            var dest = Path.Combine(AppPaths.Covers, $"{gameId}{ext}");
            File.Copy(source, dest, overwrite: true);
            Logger.Info($"已取到 Steam 封面：{appId} → {Path.GetFileName(source)}");
            return dest;
        }
        catch (Exception ex)
        {
            Logger.Error($"取 Steam 封面失败: {launchUri}", ex);
            return null;
        }
    }

    /// <summary>在 Steam 安装目录的 appcache 里找某个 appid 的封面文件。</summary>
    private static string? FindSteamCoverFile(string appId)
    {
        foreach (var cacheRoot in EnumerateLibraryCacheRoots())
        {
            var appDir = Path.Combine(cacheRoot, appId);
            if (!Directory.Exists(appDir))
            {
                continue;
            }

            // Steam 会把每个资源放进一层哈希子目录，所以文件名要递归找。
            var files = Directory.EnumerateFiles(appDir, "*.*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f);
                    return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".png", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            foreach (var name in CandidateNames)
            {
                var hit = files.FirstOrDefault(f =>
                    string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(hit))
                {
                    return hit;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 所有可能藏着 librarycache 的目录：Steam 客户端自己 + 每个游戏库。
    /// 新版 Steam 只把缓存放在客户端根目录，老版本会放在每个库里。
    ///
    /// 结果缓存 2 分钟：扫描几十个游戏时每条都要查一次，反复解析
    /// libraryfolders.vdf 纯属浪费。进程内缓存足够，不用做失效。
    /// </summary>
    private static List<string>? _cacheRoots;
    private static DateTime _cacheRootsAt;
    private static readonly TimeSpan CacheRootsLifetime = TimeSpan.FromMinutes(2);
    private static readonly object CacheRootsGate = new();

    private static List<string> EnumerateLibraryCacheRoots()
    {
        lock (CacheRootsGate)
        {
            if (_cacheRoots is not null && DateTime.UtcNow - _cacheRootsAt < CacheRootsLifetime)
            {
                return _cacheRoots;
            }

            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var steamRoot = PlatformImportService.ReadSteamRootPublic();

            if (!string.IsNullOrEmpty(steamRoot))
            {
                var root = Path.Combine(steamRoot, "appcache", "librarycache");
                if (Directory.Exists(root) && seen.Add(root))
                {
                    roots.Add(root);
                }

                foreach (var library in PlatformImportService.ReadSteamLibrariesPublic(steamRoot))
                {
                    var libRoot = Path.Combine(library, "appcache", "librarycache");
                    if (Directory.Exists(libRoot) && seen.Add(libRoot))
                    {
                        roots.Add(libRoot);
                    }
                }
            }

            _cacheRoots = roots;
            _cacheRootsAt = DateTime.UtcNow;
            return roots;
        }
    }
}
