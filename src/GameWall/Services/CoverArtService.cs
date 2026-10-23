using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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
    /// <summary>
    /// 是否允许联网补封面（settings.json 的 <c>DownloadCoverFromWeb</c>，默认开）。
    /// 启动时由 <see cref="SettingsService.Load"/> 写入，界面上改完设置会立即刷新。
    ///
    /// 关掉之后行为回到「纯本地」：只认 Steam 自己缓存下来的图，没缓存就显示占位封面。
    /// </summary>
    public static bool OnlineEnabled { get; set; } = true;

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
    ///
    /// 两级来源：先吃 Steam 自己的本地缓存（快、完全离线）；
    /// 缓存里没有就用 appid 去 Steam 的图片 CDN 取官方竖版封面（见 <see cref="TryDownloadSteamCover"/>）。
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

            return TryFetchSteamCoverByAppId(appId, gameId);
        }
        catch (Exception ex)
        {
            Logger.Error($"取 Steam 封面失败: {launchUri}", ex);
            return null;
        }
    }

    /// <summary>
    /// 按 appid 取封面：本地缓存优先，缓存没命中再联网。
    ///
    /// 之所以要联网兜底：用户的机器上经常只装了游戏本体，
    /// <c>appcache\librarycache</c> 里压根没有这个 appid 的目录
    /// （Steam 只在你打开过商店页 / 库页面之后才缓存）。这时候卡片就只能显示占位图了。
    /// </summary>
    public static string? TryFetchSteamCoverByAppId(string appId, string gameId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        var source = FindSteamCoverFile(appId);
        if (!string.IsNullOrEmpty(source))
        {
            return SaveCover(source, gameId, $"已取到 Steam 封面：{appId} → {Path.GetFileName(source)}");
        }

        // 本地缓存没有 → 联网取
        var downloaded = TryDownloadSteamCover(appId, gameId);
        if (!string.IsNullOrEmpty(downloaded))
        {
            return downloaded;
        }

        Logger.Info($"Steam 缓存与 CDN 都没有 {appId} 的封面，改用占位图");
        return null;
    }

    private static string? SaveCover(string sourcePath, string gameId, string logMessage)
    {
        AppPaths.EnsureCreated();
        var ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = ".jpg";
        }

        var dest = Path.Combine(AppPaths.Covers, $"{IconService.Sanitize(gameId)}{ext}");
        File.Copy(sourcePath, dest, overwrite: true);
        Logger.Info(logMessage);
        return dest;
    }

    /// <summary>
    /// 直连 Steam 的图片 CDN 取官方封面。
    ///
    /// 这是整个程序里**唯一**会主动联网的地方，只在「本地缓存找不到封面」时才触发，
    /// 而且只请求图片、不上传任何东西。设置里的「下载游戏封面」开关可以关掉它。
    ///
    /// 实测这几条 CDN 在国内都还算通（cloudflare / akamai / steamstatic 三条互为备份）。
    /// </summary>
    private static string? TryDownloadSteamCover(string appId, string gameId)
    {
        if (!OnlineEnabled)
        {
            return null;
        }

        AppPaths.EnsureCreated();
        var dest = Path.Combine(AppPaths.Covers, $"{IconService.Sanitize(gameId)}.jpg");

        foreach (var host in CdnHosts)
        {
            foreach (var name in DownloadCandidateNames)
            {
                var url = $"{host}/steam/apps/{appId}/{name}";
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    var bytes = GetHttpClient()
                        .GetByteArrayAsync(url, cts.Token)
                        .GetAwaiter()
                        .GetResult();

                    if (bytes is null || bytes.Length < 1024)
                    {
                        continue;
                    }

                    File.WriteAllBytes(dest, bytes);
                    Logger.Info($"已从 Steam CDN 下载封面：{appId} → {name}（{bytes.Length / 1024} KB）");
                    return dest;
                }
                catch (OperationCanceledException)
                {
                    // 超时就换下一个源，不刷日志（一次扫描几十个游戏会刷屏）
                }
                catch (Exception ex)
                {
                    Logger.Info($"从 CDN 取封面失败（{name}）：{ex.GetType().Name} {ex.Message}");
                }
            }
        }

        return null;
    }

    /// <summary>Steam 图片 CDN，按实测速度排序，互为备份。</summary>
    private static readonly string[] CdnHosts =
    {
        "https://cdn.cloudflare.steamstatic.com",
        "https://cdn.akamai.steamstatic.com",
        "https://cdn.steamstatic.com",
    };

    /// <summary>
    /// CDN 上要试的文件名。和本地缓存那套不完全一样：
    /// 网络请求每次都几十毫秒，只试真正像封面的三档，不试 header / hero 这种横图。
    /// </summary>
    private static readonly string[] DownloadCandidateNames =
    {
        "library_600x900_2x.jpg",
        "library_600x900.jpg",
        "library_capsule.jpg",
        "header.jpg",
    };

    private static HttpClient? _httpClient;
    private static readonly object HttpClientGate = new();

    /// <summary>共用一个 HttpClient：每次 new 会耗尽 socket，这是 .NET 上的老坑了。</summary>
    private static HttpClient GetHttpClient()
    {
        lock (HttpClientGate)
        {
            if (_httpClient is not null)
            {
                return _httpClient;
            }

            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                ConnectTimeout = TimeSpan.FromSeconds(8),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20),
            };

            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GameWall/" + AppInfo.Version);
            return _httpClient;
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
