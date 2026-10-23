using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GameWall.Models;

namespace GameWall.Services;

/// <summary>扫描 / 搜索得到的一个候选 exe。</summary>
public sealed class GameCandidate
{
    public string FilePath { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string FolderName { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public DateTime ModifiedAt { get; init; }

    /// <summary>建议使用的显示名（目录名通常比 exe 名更像游戏名）</summary>
    public string SuggestedName { get; init; } = string.Empty;

    /// <summary>扫描时命中的根目录，用来推断「这个 exe 属于哪个游戏」</summary>
    public string ScanRoot { get; init; } = string.Empty;

    public string SizeText => SizeBytes >= 1024L * 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024 / 1024:F1} GB"
        : $"{Math.Max(1, SizeBytes / 1024 / 1024)} MB";

    public override string ToString() => $"{SuggestedName}  ·  {FilePath}";
}

/// <summary>
/// 本地硬盘游戏扫描器。
///
/// 这里最难的不是「找到 exe」，而是「把不是游戏的东西排除掉」。
/// 经验教训：Steam 客户端自己就有 30 多个 exe（steam.exe / steamwebhelper.exe /
/// steamerrorreporter.exe …），各种平台的启动器、反作弊、运行库也全都是 exe。
/// 如果不做过滤，扫出来的「游戏」会全是这些辅助程序。
///
/// 所以过滤分四层：
///   1) 目录黑名单 —— 整个目录跳过（Steam 的 bin/ 和 steamapps/、node_modules 等）
///   2) 文件名黑名单 —— uninstall / vcredist / crashhandler / EasyAntiCheat 之类
///   3) 名字白名单加分 —— 叫 steam / launcher / client 的降权而不是直接丢，
///      因为确实存在叫 launcher.exe 的游戏主程序
///   4) 体积与位置加权 —— 主程序通常体积大，且跟游戏目录同名
/// 打分低于阈值的候选直接不返回。
/// </summary>
public sealed class GameScanner
{
    /// <summary>
    /// 目录黑名单：整个目录都不进去。
    /// Steam 的 bin/ 里全是客户端组件，steamapps/ 里除了 common 都是清单和缓存。
    /// </summary>
    private static readonly string[] BlockedDirNames =
    {
        "windows", "$recycle.bin", "system volume information", "node_modules",
        "redist", "_commonredist", "__installer", "installers", "directx", "dotnet",
        "vcredist", "vc_redist", "support", "docs", "documentation", "manual",
        "soundtrack", "ost", "artbook", "wallpaper", "editor", "sdk", "tools",
        "benchmark", "crashreports", "crashdumps", "logs", "cache", "temp", "tmp",
        "shadercache", "downloading", "workshop", "modding", "mods_tools",
        // 腾讯 WeGame 把运行库、WebView2、DirectX 等公共组件全塞在这里，
        // 里面一个游戏都没有（Deploy-Application.exe / vcredist_x64.exe 之类）
        "common_apps", "rail_apps", "rail_user_data", "toolkit",
        // 破解组织 / 补丁 / 汉化包目录：里面是补丁程序，不是游戏本体。
        // 下载站的散装游戏几乎都会带一个这样的目录，进去只会扫出一堆 loader。
        "codex", "plaza", "skidrow", "reloaded", "prophet", "steampunks",
        "elamigos", "fitgirl", "dodi", "rune", "tenoke", "goldberg",
        "empress", "flt", "razor1911", "hoodlum", "crack", "cracked",
        "crackfix", "patch", "patches", "no-dvd", "nodvd", "keygen",
        "trainer", "trainers", "cheats", "cheat", "修改器", "汉化",
        "汉化补丁", "破解补丁", "免cd补丁", "补丁",
    };

    /// <summary>
    /// 平台客户端的目录：这些目录里的 exe 是「平台本体」，不是游戏。
    /// 只有它们的 steamapps\common 子目录才是游戏。
    /// </summary>
    private static readonly string[] StoreClientDirHints =
    {
        "steam", "epic games", "epicgameslauncher", "gog galaxy", "ubisoft",
        "ea games", "origin", "battle.net", "wegame", "riot client",
        "riot games", "xboxgames", "amazon games", "bethesda.net launcher",
    };

    /// <summary>
    /// 文件名片段黑名单。命中即排除。
    /// 这些名字在真实机器上大量出现在游戏目录里，但全都不是「游戏主程序」。
    /// </summary>
    private static readonly string[] NoiseFileFragments =
    {
        // 安装 / 卸载 / 更新
        "unins", "uninstall", "setup", "install", "update", "updater", "patch",
        "repair", "cleanup", "register", "activation", "touchup",
        // 中文命名的安装器（国产游戏目录里非常常见，如「XX卸载.exe」）
        "卸载", "安装", "修复", "更新器", "自检", "诊断工具",
        // 崩溃 / 诊断 / 上报
        "crash", "report", "bugreport", "dump", "diagnostics", "diag", "telemetry",
        "sysinfo", "errorreport", "minidump",
        // 运行库 / 驱动 / 预装
        "vcredist", "vc_redist", "dxsetup", "directx", "dotnet", "redist",
        "prereq", "ue4prereq", "ue5prereq", "oalinst", "physx",
        // 反作弊
        "easyanticheat", "eac_", "battleye", "be_", "anticheat", "vanguard",
        // 辅助进程
        "helper", "service", "daemon", "watchdog", "monitor", "agent", "broker",
        "overlay", "hook", "injector", "proxy",
        // 厂商 / 平台组件
        "steamwebhelper", "steamerrorreporter", "steamservice", "steamtours",
        "steamxboxutil", "steam_monitor", "gameoverlayui", "streaming_client",
        "hardwareupdater", "fossilize", "gldriverquery", "vulkandriverquery",
        "x64launcher", "x86launcher", "steamsysinfo", "writeminidump",
        "secure_desktop_capture", "drivers",
        // 游戏目录里的录制 / 直播 / 教练 / 网页壳（LoL、WeGame 这类客户端特别多）
        "coach", "recorder", "ffmpeg", "ffprobe", "crashpad", "tinydl",
        "minibrowser", "qbblink", "tcls", "tenprotect",
        // 配置 / 编辑器
        "config", "settings", "options", "editor", "tool", "benchmark",
        // 部署 / 打包脚本（WeGame 的公共组件目录里一抓一把）
        "deploy",
        // 破解补丁 / 汉化补丁 / 修改器 / 存档工具
        // 下载站把游戏本体和这些塞在同一个目录里，补丁 exe 常被误当成主程序
        "crack", "crackfix", "keygen", "patcher", "trainer", "cheat", "cheatengine",
        "破解", "汉化补丁", "汉化", "修改器", "免CD", "免cd", "存档修改", "补丁",
        "greenfix", "regfix", "注册表修复",
    };

    /// <summary>
    /// 名字里含这些词时视为「平台本体」，直接排除。
    /// 注意用整词匹配（见 IsPlatformClientName），避免误伤 "SteamWorld Dig" 这类真游戏。
    /// </summary>
    private static readonly string[] PlatformClientNames =
    {
        "steam", "steamwebhelper", "epicgameslauncher", "epicwebhelper",
        "goggalaxy", "galaxyclient", "ubisoftconnect", "upc", "eadesktop",
        "origin", "battle.net", "wegame", "riotclientservices", "riotclientux",
        "xboxapp", "amazon games", "bethesdanetlauncher",
    };

    /// <summary>常见游戏目录的默认候选（用户可在设置里增删）。</summary>
    public static List<string> DefaultScanRoots()
    {
        var roots = new List<string>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                var root = drive.RootDirectory.FullName;
                foreach (var name in new[]
                         {
                             "Games", "Game", "SteamLibrary", "游戏", "单机游戏",
                             // 国内平台常把游戏直接装到盘符根下
                             "WeGameApps", "WeGame", "Tencent Games", "GOG Games",
                             // Epic 装在非系统盘时也是 <盘符>\Epic Games\<游戏名>
                             "Epic Games", "EpicGames",
                             // 下载站/破解版最常见的几个落点：很多人直接把解压出来的
                             // 游戏丢在 D:\游戏库、D:\PC游戏、D:\Downloads\Games 下面
                             "游戏库", "PC游戏", "电脑游戏", "我的游戏", "游戏下载",
                             "Downloads\\Games", "Downloads\\游戏", "Gamer", "Gaming",
                         })
                {
                    var candidate = Path.Combine(root, name);
                    if (Directory.Exists(candidate))
                    {
                        roots.Add(candidate);
                    }
                }

                // steamapps\common 才是 Steam 游戏的真正落点，不要整个 steamapps
                var steamCommon = Path.Combine(root, "SteamLibrary", "steamapps", "common");
                if (Directory.Exists(steamCommon))
                {
                    roots.Add(steamCommon);
                }
            }
            catch
            {
                // 某些驱动器不可访问，跳过
            }
        }

        // 各平台客户端的游戏安装目录（而不是客户端本体目录）
        var steamRoots = SteamLibraryRoots();
        roots.AddRange(steamRoots.Select(lib => Path.Combine(lib, "steamapps", "common")).Where(Directory.Exists));

        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrWhiteSpace(programFiles))
            {
                continue;
            }

            foreach (var name in new[] { "Epic Games", "GOG Galaxy\\Games", "Ubisoft\\Ubisoft Game Launcher\\games", "EA Games" })
            {
                var candidate = Path.Combine(programFiles, name);
                if (Directory.Exists(candidate))
                {
                    roots.Add(candidate);
                }
            }
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 从注册表 + libraryfolders.vdf 找出所有 Steam 库根目录。
    /// 用户把游戏装在 E:\SteamLibrary 这种非默认位置时，只靠盘符猜是猜不到的。
    /// </summary>
    private static List<string> SteamLibraryRoots()
    {
        var result = new List<string>();
        try
        {
            var steamRoot = PlatformImportService.ReadSteamRootPublic();
            if (string.IsNullOrEmpty(steamRoot))
            {
                return result;
            }

            result.Add(steamRoot);
            result.AddRange(PlatformImportService.ReadSteamLibrariesPublic(steamRoot));
        }
        catch
        {
            // 读不到就走默认路径
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 扫描给定目录下的 exe。
    /// </summary>
    /// <param name="roots">要扫描的根目录</param>
    /// <param name="maxDepth">递归深度上限</param>
    /// <param name="minSize">exe 最小体积，过滤小工具</param>
    /// <param name="keyword">非空时只保留文件名/目录名包含关键字的候选</param>
    /// <param name="progress">进度回调</param>
    public Task<List<GameCandidate>> ScanAsync(
        IEnumerable<string> roots,
        int maxDepth,
        long minSize,
        string? keyword,
        IProgress<string>? progress,
        CancellationToken token)
    {
        return Task.Run(() =>
        {
            var results = new List<GameCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // exe 路径 → 它所属的「游戏根目录」（用于归并同一游戏的多个 exe）
            var gameRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var visitedDirs = 0;

            foreach (var root in roots)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    continue;
                }

                Walk(root, root, 0);
            }

            void Walk(string dir, string root, int depth)
            {
                if (token.IsCancellationRequested || depth > maxDepth)
                {
                    return;
                }

                visitedDirs++;
                if (visitedDirs % 25 == 0)
                {
                    progress?.Report($"正在扫描：{Shorten(dir)}（已发现 {results.Count} 个候选）");
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    return;
                }

                foreach (var file in files)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    var candidate = TryBuild(file, root, minSize, keyword);
                    if (candidate is not null && seen.Add(candidate.FilePath))
                    {
                        results.Add(candidate);
                    }
                }

                IEnumerable<string> subDirs;
                try
                {
                    subDirs = Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    return;
                }

                foreach (var sub in subDirs)
                {
                    if (ShouldSkipDirectory(sub, depth))
                    {
                        continue;
                    }

                    Walk(sub, root, depth + 1);
                }
            }

            // 同一个游戏目录里往往有好几个 exe（主程序 + 启动器 + shipping 版 + 录制器），
            // 全收进来会让墙上出现两张一模一样的卡片。
            // 这里按「这款游戏自己的目录」归并，只留最像主程序的那个。
            //
            // 注意归并键不是 ResolveGameRoot 的结果：用户把游戏塞进自建分类文件夹时，
            // 那个结果会是分类文件夹（XIAORIZI），于是整个文件夹里的游戏都被算成一款。
            var byGameDir = new Dictionary<string, GameCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in results)
            {
                var root = ResolveGameRoot(c.FilePath, c.ScanRoot);
                var exeDir = Path.GetDirectoryName(c.FilePath) ?? root;
                var identity = PickGameIdentity(root, exeDir);
                var key = identity.Dir;

                if (byGameDir.TryGetValue(key, out var existing))
                {
                    // MainExecutableRank 越小越像主程序，所以新来的分数不占优就直接跳过，
                    // 占优则用新候选覆盖掉旧的那个。
                    if (MainExecutableRank(c, root) >= MainExecutableRank(existing, root))
                    {
                        continue;
                    }
                }

                byGameDir[key] = c;
            }

            var deduped = new List<GameCandidate>(byGameDir.Count);
            foreach (var pair in byGameDir)
            {
                deduped.Add(RenameByGameRoot(pair.Key, pair.Value));
            }

            return deduped
                .OrderByDescending(c => Score(c))
                .ThenBy(c => c.SuggestedName, StringComparer.CurrentCulture)
                .ToList();
        }, token);
    }

    /// <summary>
    /// 同一游戏目录里挑主程序的排序键，**越小越优先**。
    ///
    /// 真实的游戏目录里通常塞了一堆 exe：
    ///   《英雄联盟》  Game\League of Legends.exe   ← 这才是游戏本体
    ///                LeagueClient\LeagueClient.exe ← 大厅客户端
    ///                Launcher\Client.exe           ← 启动器
    ///                Cross\icreate\recorder-release\ffmpeg.exe ← 录制器
    /// 光比体积会选到 200MB 的录制器，所以先按「所在子目录像不像客户端/工具」定档，
    /// 再看是否直接在游戏根目录，然后看深度，最后才用体积做微调。
    /// </summary>
    private static long MainExecutableRank(GameCandidate candidate, string gameRoot)
    {
        var exeDir = Path.GetDirectoryName(candidate.FilePath) ?? string.Empty;

        var inRoot = string.Equals(
            Path.GetFullPath(exeDir).TrimEnd('\\'),
            Path.GetFullPath(gameRoot).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

        var depth = candidate.FilePath.Count(ch => ch == '\\');
        var sizeMb = candidate.SizeBytes / (1024 * 1024);

        long rank = 0;

        // ── 第一档：路径里只要有一层是「客户端 / 启动器 / 工具」目录，就判为次要候选 ──
        //
        // 不能只看 exe 所在的直接父目录：腾讯系游戏把东西埋在
        //   WeGameLauncher\TenioDL\TenioDL.exe
        //   Cross\Core\Stable\Tenio\TenioDL\TenioDL.exe
        //   Cross\icreate\icreate-lol\icreateLol.exe
        // 里，父目录叫 TenioDL / icreate-lol，而真正说明问题的是中间的 WeGameLauncher / Cross。
        if (HasClientSegment(exeDir, gameRoot))
        {
            rank += 1_000_000L;
        }
        else if (!inRoot)
        {
            rank += 200_000L;
        }

        // ── 第二档：埋得越深越不像主程序 ──
        rank += Math.Min(depth, 40) * 5_000L;

        // ── 第三档：主程序通常体积大（这一项只在同一档内起作用）──
        rank -= Math.Min(sizeMb, 2_000L) * 100L;

        return rank;
    }

    /// <summary>
    /// 判断 exe 所在路径（相对于游戏根目录）里是否出现过「客户端 / 启动器 / 工具」目录名。
    ///
    /// 只看相对路径，避免游戏根目录自己在 E:\Games\Client 之类的位置时被误判。
    /// </summary>
    private static bool HasClientSegment(string exeDir, string gameRoot)
    {
        var relative = exeDir;
        if (relative.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
        {
            relative = relative.Substring(gameRoot.Length);
        }

        foreach (var segment in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ClientSubDirNames.Contains(segment.ToLowerInvariant()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把 exe 埋得很深的候选改名为「游戏根目录名」。
    ///
    /// A Pond Hunt Demo 的真实主程序是
    /// steamapps\common\A Pond Hunt Demo\PumpFish\Binaries\Win64\PumpFish-Win64-Shipping.exe，
    /// 直接用 exe 名会显示成 "PumpFish-Win64-Shipping" —— 玩家根本认不出是哪个游戏。
    /// </summary>
    private static GameCandidate RenameByGameRoot(string gameRoot, GameCandidate candidate)
    {
        var exeDir = Path.GetDirectoryName(candidate.FilePath) ?? string.Empty;

        // exe 就摆在游戏根目录下，而且不是商店托管的目录：
        // 说明这个目录本身就是游戏目录，ChooseName 已经在「目录名 vs exe 名」之间做过取舍，
        // 这里再覆盖一次只会把好不容易选对的 exe 名换回目录名。
        var rootFull = Path.GetFullPath(gameRoot).TrimEnd('\\');
        var exeFull = Path.GetFullPath(exeDir).TrimEnd('\\');
        if (string.Equals(rootFull, exeFull, StringComparison.OrdinalIgnoreCase)
            && !IsStoreManagedGameRoot(gameRoot))
        {
            return candidate;
        }

        var name = PickGameName(gameRoot, exeDir);
        if (string.IsNullOrWhiteSpace(name)
            || name.Equals(candidate.SuggestedName, StringComparison.Ordinal))
        {
            return candidate;
        }

        return new GameCandidate
        {
            FilePath = candidate.FilePath,
            FileName = candidate.FileName,
            FolderName = candidate.FolderName,
            SizeBytes = candidate.SizeBytes,
            ModifiedAt = candidate.ModifiedAt,
            SuggestedName = name,
        };
    }

    /// <summary>
    /// 挑出「这个 exe 属于哪款游戏」的显示名。
    ///
    /// 这里踩过一个坑：用户把游戏放在自己建的分类文件夹里，例如
    ///   E:\Games\XIAORIZI\JAPANSTIGMATIZED\uuu.exe
    /// 早先的实现把「游戏根目录」当成第一层子目录（XIAORIZI），再把名字无条件
    /// 覆盖成根目录名，结果墙上显示的是 XIAORIZI 而不是 JAPANSTIGMATIZED ——
    /// 名字错了，后面按名字匹配封面也就全废了。
    ///
    /// 现在的规则：
    ///  - 商店托管的目录（steamapps\common\&lt;游戏&gt; 等）以商店目录名为准，
    ///    因为 ...\A Pond Hunt Demo\PumpFish\Binaries\Win64\ 里最深那层叫 Win64，
    ///    再往上是 Binaries / PumpFish，都不如商店名可信。
    ///  - 其余情况从 exe 所在目录往上走，取**第一层不是通用容器名的目录**
    ///    （bin / win64 / binaries / game 这类不算），走到游戏根目录还没有就用根目录名。
    /// </summary>
    private static string PickGameName(string gameRoot, string exeDir)
    {
        var identity = PickGameIdentity(gameRoot, exeDir);
        return identity.Name;
    }

    /// <summary>
    /// 同 <see cref="PickGameName"/>，但同时给出承载这个名字的目录。
    ///
    /// 目录有两个用途：
    ///  1. 当归并键 —— 同一款游戏的多个 exe 必须落到同一张卡片上；
    ///  2. 后面按名字去 Steam 库里模糊匹配封面时，用它当第二判据。
    /// </summary>
    private static (string Name, string Dir) PickGameIdentity(string gameRoot, string exeDir)
    {
        var rootFull = Path.GetFullPath(gameRoot).TrimEnd('\\');
        var rootName = Path.GetFileName(rootFull);

        if (IsStoreManagedGameRoot(gameRoot))
        {
            return (rootName, rootFull);
        }

        // 只要路径中间出现过「客户端 / 启动器 / 工具」这一层，这个 exe 就是某个游戏的组件，
        // 身份必须归到游戏根目录。
        //
        // 这一条是《英雄联盟》踩出来的：往上找「第一个不是通用容器名的目录」时，
        //   LeagueClient\FeedBack\FeedBack.exe      → 认成 FeedBack（墙上多一张 FE 卡）
        //   Launcher\qbblinktrial\browser.exe       → 认成 qbblinktrial（QB）
        //   Cross\icreate\cut\cut-cli.exe           → 认成 cut（CC）
        //   Cross\icreate\recorder-release\...      → 认成 recorder-release（RR）
        // 这些目录自己不在黑名单里，但它们的**父级**（leagueclient / launcher / cross）
        // 全在，所以真正说明问题的是祖先而不是直接父目录。
        if (HasClientSegment(exeDir, gameRoot))
        {
            return (rootName, rootFull);
        }

        var current = Path.GetFullPath(exeDir).TrimEnd('\\');

        // 从最深处往上走，最多走到游戏根目录为止
        for (var guard = 0; guard < 8; guard++)
        {
            if (current.Length < rootFull.Length
                || !current.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var name = Path.GetFileName(current);
            if (!string.IsNullOrWhiteSpace(name)
                && !string.Equals(current, rootFull, StringComparison.OrdinalIgnoreCase)
                && !RedundantFolderNames.Contains(name.ToLowerInvariant())
                && !ClientSubDirNames.Contains(name.ToLowerInvariant()))
            {
                return (name, current);
            }

            if (string.Equals(current, rootFull, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent))
            {
                break;
            }

            current = parent.TrimEnd('\\');
        }

        return (rootName, rootFull);
    }

    /// <summary>
    /// 求一个 exe 所属的「游戏根目录」。
    ///
    /// 三种情况：
    ///  - steamapps\common\&lt;游戏&gt;\...   → 归到 &lt;游戏&gt; 这一层
    ///  - Epic Games\&lt;游戏&gt;\...          → 同上
    ///  - 其余（WeGame / 自建游戏目录）    → 取扫描根目录下的第一层子目录
    ///
    /// 不这么做的话，一个《英雄联盟》里的 20 多个 exe（客户端、录制器、崩溃上报）
    /// 会被当成 20 多个不同的游戏。
    /// </summary>
    private static string ResolveGameRoot(string exePath, string scanRoot)
    {
        var dir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(dir))
        {
            return exePath;
        }

        var lower = dir.ToLowerInvariant();
        const string marker = "\\steamapps\\common\\";
        var at = lower.IndexOf(marker, StringComparison.Ordinal);
        if (at >= 0)
        {
            var rest = dir[(at + marker.Length)..];
            var gameDir = rest.Split('\\')[0];
            if (!string.IsNullOrWhiteSpace(gameDir))
            {
                return Path.Combine(dir[..(at + marker.Length)], gameDir);
            }
        }

        // Epic 的安装目录形如 ...\Epic Games\<游戏>
        const string epicMarker = "\\epic games\\";
        at = lower.IndexOf(epicMarker, StringComparison.Ordinal);
        if (at >= 0)
        {
            var rest = dir[(at + epicMarker.Length)..];
            var gameDir = rest.Split('\\')[0];
            if (!string.IsNullOrWhiteSpace(gameDir)
                && !gameDir.Equals("Launcher", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(dir[..(at + epicMarker.Length)], gameDir);
            }
        }

        // 通用情况：扫描根目录下第一层子目录就是「一个游戏」
        if (!string.IsNullOrWhiteSpace(scanRoot))
        {
            var rootFull = Path.GetFullPath(scanRoot).TrimEnd('\\');
            var dirFull = Path.GetFullPath(dir).TrimEnd('\\');

            if (dirFull.Length > rootFull.Length
                && dirFull.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase))
            {
                var rest = dirFull[(rootFull.Length + 1)..];
                var first = rest.Split('\\')[0];

                // 第一层就是 bin/win64 这种容器目录，说明根目录本身才是游戏目录
                if (RedundantFolderNames.Contains(first.ToLowerInvariant()))
                {
                    return rootFull;
                }

                return Path.Combine(rootFull, first);
            }

            if (string.Equals(dirFull, rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return dirFull;
            }
        }

        return dir;
    }

    /// <summary>判断一个子目录该不该进去扫。</summary>
    private static bool ShouldSkipDirectory(string dir, int depth)
    {
        var name = Path.GetFileName(dir).ToLowerInvariant();

        if (BlockedDirNames.Contains(name))
        {
            return true;
        }

        // Steam 客户端目录里的 bin/、steamapps/ 全是客户端组件和清单，
        // 真正的游戏在 steamapps\common\ 下，只有那个要继续往下走。
        if (name == "bin" || name == "steamapps")
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(dir)) ?? string.Empty;
            if (IsSteamClientDir(parent))
            {
                return name == "bin";
            }
        }

        // "Steamworks Shared" / "Steamworks Common Redistributables" 是运行库合集
        if (name.StartsWith("steamworks", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsSteamClientDir(string dirName) =>
        dirName.Equals("Steam", StringComparison.OrdinalIgnoreCase)
        || dirName.Equals("SteamLibrary", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 这个「游戏根目录」是不是商店托管出来的（steamapps\common\&lt;游戏&gt; 之类）。
    ///
    /// 是的话，目录名就是商店里的游戏名，比 exe 名可信得多：
    ///   steamapps\common\A Pond Hunt Demo\PumpFish.exe  → 应该显示 A Pond Hunt Demo
    /// 而自建目录（E:\Game\Sekiro\sekiro.exe）里 exe 名同样可信，不必强行覆盖。
    /// </summary>
    private static bool IsStoreManagedGameRoot(string gameRoot)
    {
        var lower = gameRoot.ToLowerInvariant();
        return lower.Contains("\\steamapps\\common\\", StringComparison.Ordinal)
               || lower.Contains("\\epic games\\", StringComparison.Ordinal)
               || lower.Contains("\\gog galaxy\\games\\", StringComparison.Ordinal)
               || lower.Contains("\\ubisoft game launcher\\games\\", StringComparison.Ordinal)
               || lower.Contains("\\ea games\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// 判断一个 exe 是不是「平台本体 / 工具程序」而不是游戏。
    ///
    /// 手动添加时不拦用户（用户想加什么就加什么），但拖放这种「一次拖一堆」
    /// 的场景需要给出提示，否则很容易把 steam.exe、vcredist 拖进游戏墙。
    /// </summary>
    public static bool IsLikelyPlatformOrTool(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        var lowerName = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        if (NoiseFileFragments.Any(n => lowerName.Contains(n)) || IsPlatformClientName(lowerName))
        {
            return true;
        }

        // 位于平台客户端安装目录、但不在 steamapps\common 下 → 平台自身
        var dir = Path.GetDirectoryName(exePath) ?? string.Empty;
        var dirLower = dir.ToLowerInvariant();

        foreach (var hint in StoreClientDirHints)
        {
            var marker = "\\" + hint + "\\";
            if (dirLower.Contains(marker) || dirLower.EndsWith("\\" + hint, StringComparison.Ordinal))
            {
                // 游戏目录例外：steamapps\common\<游戏名>
                if (dirLower.Contains("\\steamapps\\common\\")
                    || dirLower.Contains("\\epic games\\") && !dirLower.Contains("\\epic games\\launcher"))
                {
                    return false;
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 这个 exe 是不是躺在「平台客户端自己的安装目录」里（而不是游戏目录里）。
    ///
    /// 用户抱怨的「扫出来全是游戏平台」就是这里漏的：
    ///   C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\UbisoftExtension.exe
    ///   Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe
    /// 这些 exe 的名字（UbisoftExtension / EpicGamesLauncher）不在黑名单里，
    /// 只能靠路径判断：在平台目录下、又不在该平台的「游戏」子目录里，就是平台本体。
    /// </summary>
    private static bool IsInsideStoreClient(string exePath)
    {
        var lower = (Path.GetDirectoryName(exePath) ?? string.Empty).ToLowerInvariant();

        // 先认领「这里是游戏目录」的情况，命中就直接放行
        if (lower.Contains("\\steamapps\\common\\", StringComparison.Ordinal)
            || lower.Contains("\\gog galaxy\\games\\", StringComparison.Ordinal)
            || lower.Contains("\\ubisoft game launcher\\games\\", StringComparison.Ordinal)
            || lower.Contains("\\xboxgames\\", StringComparison.Ordinal))
        {
            return false;
        }

        // Epic 的游戏目录是 Epic Games\<游戏名>，Launcher 子目录才是平台本体
        if (lower.Contains("\\epic games\\", StringComparison.Ordinal)
            && !lower.Contains("\\epic games\\launcher", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var hint in StoreClientDirHints)
        {
            if (lower.Contains("\\" + hint + "\\", StringComparison.Ordinal)
                || lower.EndsWith("\\" + hint, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static GameCandidate? TryBuild(string file, string scanRoot, long minSize, string? keyword)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length < minSize)
            {
                return null;
            }

            var baseName = Path.GetFileNameWithoutExtension(file);
            var lower = baseName.ToLowerInvariant();

            // ── 第 2 层：文件名黑名单 ──
            if (NoiseFileFragments.Any(n => lower.Contains(n)))
            {
                return null;
            }

            // ── 第 3 层：平台本体 ──
            if (IsPlatformClientName(lower))
            {
                return null;
            }

            // ── 第 4 层：躺在平台客户端安装目录里（不是它的游戏目录）──
            if (IsInsideStoreClient(file))
            {
                return null;
            }

            var folder = info.Directory?.Name ?? string.Empty;
            var suggested = ChooseName(baseName, folder);

            // 目录名本身是平台/运行库名（如 Steamworks Shared）时也排除
            var folderLower = folder.ToLowerInvariant();
            if (BlockedDirNames.Contains(folderLower)
                || folderLower.StartsWith("steamworks", StringComparison.OrdinalIgnoreCase)
                || IsPlatformClientName(folderLower))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                var kw = keyword.Trim();
                var hit = suggested.Contains(kw, StringComparison.OrdinalIgnoreCase)
                          || baseName.Contains(kw, StringComparison.OrdinalIgnoreCase)
                          || folder.Contains(kw, StringComparison.OrdinalIgnoreCase);
                if (!hit)
                {
                    return null;
                }
            }

            return new GameCandidate
            {
                FilePath = info.FullName,
                FileName = info.Name,
                FolderName = folder,
                SizeBytes = info.Length,
                ModifiedAt = info.LastWriteTime,
                SuggestedName = suggested,
                ScanRoot = scanRoot,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 名字是不是「平台客户端本体」。
    /// 用整词判断而不是 Contains：否则 "SteamWorld Dig" 会被误杀。
    /// </summary>
    private static bool IsPlatformClientName(string lowerName)
    {
        foreach (var client in PlatformClientNames)
        {
            if (lowerName.Equals(client, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // steam.exe / steam64.exe / galaxyclient.exe 这类
            if (lowerName.StartsWith(client + ".", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 「容器目录」名：这些目录名不承载游戏身份信息，
    /// 看到它们要把游戏名往上一层找。
    /// </summary>
    private static readonly string[] RedundantFolderNames =
    {
        "bin", "bin64", "binaries", "win64", "win32", "x64", "x86", "game",
        "release", "retail", "system", "engine", "common", "build", "launcher",
        "shipping", "content", "app",
    };

    /// <summary>
    /// 游戏安装目录里常见的「非游戏本体」子目录。
    /// 这些目录下的 exe 一律排在主程序之后（不直接删，因为有些游戏本体就放在 client 里）。
    /// </summary>
    private static readonly string[] ClientSubDirNames =
    {
        "launcher", "client", "leagueclient", "launchers", "cross", "tcls",
        "tenio", "teniodl", "wecamelauncher", "rail", "rail_apps", "update",
        "updater", "redist", "support", "tools",
    };

    /// <summary>
    /// 目录名往往比 exe 名更像游戏名（"Sekiro" 目录里是 sekiro.exe）。
    /// 规则：目录名不是通用词、且比 exe 名信息量更大时用目录名。
    /// </summary>
    private static string ChooseName(string exeBaseName, string folderName)
    {
        // 目录名先洗一遍：下载站目录常带「免安装绿色版 v1.10 全DLC」这类尾巴，
        // 直接拿来当游戏名既不好看也匹配不到封面。
        var folder = CleanFolderName(folderName);

        if (string.IsNullOrWhiteSpace(folder)
            || RedundantFolderNames.Contains(folder.ToLowerInvariant()))
        {
            return exeBaseName;
        }

        // 两者只差大小写时用目录名 —— 玩家建目录时一般保留了官方写法
        // （"Sekiro" 而不是 sekiro.exe 里的 sekiro），显示出来更像游戏名。
        if (folder.Equals(exeBaseName, StringComparison.OrdinalIgnoreCase))
        {
            return folder;
        }

        // exe 名带版本号/后缀（如 Game-Win64-Shipping）时优先目录名
        var exeLower = exeBaseName.ToLowerInvariant();
        if (exeLower.Contains("-win64") || exeLower.Contains("-win32") || exeLower.Contains("shipping"))
        {
            return folder;
        }

        // exe 名只是 launcher / start / play / game 这类无信息量的词时，用目录名
        if (VagueExeNames.Contains(exeLower))
        {
            return folder;
        }

        // 目录名带中文、exe 名是纯英文时优先目录名。
        // 下载站习惯把目录写成中文名、exe 保留英文（「赛博朋克2077」目录里是
        // Cyberpunk2077.exe）。中文用户看中文名更认得出，去 Steam 库/封面库
        // 模糊匹配时中文名也更准。
        if (HasChineseChar(folder) && !HasChineseChar(exeBaseName))
        {
            return folder;
        }

        return exeBaseName.Length >= folder.Length ? exeBaseName : folder;
    }

    /// <summary>是否含中日韩表意字符（用来判断「这个字符串是不是中文名」）。</summary>
    private static bool HasChineseChar(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (ch >= 0x4E00 && ch <= 0x9FFF)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// exe 名毫无信息量的情况：目录名才是游戏名。
    /// 下载站/破解版里 launcher.exe、start.exe、game.exe 满地都是。
    /// </summary>
    private static readonly HashSet<string> VagueExeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "game", "start", "play", "launcher", "run", "main", "app", "client",
        "loader", "application", "setup", "begin", "starter", "go",
    };

    /// <summary>
    /// 把「下载站风格」的目录名洗成像样的游戏名。
    ///
    /// 典型输入：
    ///   【3DM 首发】艾尔登法环 v1.10 全DLC 免安装绿色版
    ///   赛博朋克2077 [v2.21][全DLC][简体中文][免安装]
    ///   Hades.v1.38281.Cracked-CODEX
    /// 期望输出：
    ///   艾尔登法环 / 赛博朋克2077 / Hades
    ///
    /// 做法保守：只剥「一定不是游戏名」的部分，括注、版本号、发行版修饰词。
    /// 剥完不足两个字符就退回原名，宁可难看也不要丢名字。
    /// </summary>
    private static string CleanFolderName(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return string.Empty;
        }

        var text = folderName.Trim();

        // 1) 去掉成对括注：[] 【】 () （） —— 站点前缀、语言、版本几乎都在里面
        text = Regex.Replace(text, @"[\[【（(][^\]】）)]*[\]】）)]", " ");
        // 去掉落单的右括号（"-CODEX]" 这类不配对写法）
        text = text.Replace("]", " ").Replace("】", " ").Replace("）", " ").Replace(")", " ");

        // 2) 去掉发行版修饰词。逐个替换成空格而不是直接删，
        //    免得 "Hades免安装" 粘成 "Hades免安装"→"Hades" 之后又和后面的词连上。
        foreach (var noise in ReleaseNameNoise)
        {
            text = Regex.Replace(text, Regex.Escape(noise), " ", RegexOptions.IgnoreCase);
        }

        // 3) 去掉版本号：v1.10 / v1.38281 / 1.0.3 / Version 2.1
        text = Regex.Replace(text, @"\bversion\b", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bv?\d+(?:\.\d+){1,4}\b", " ");
        // "Hades.v1.38281" 这种点号粘连版：上一条已处理，这里清理残留的点
        text = Regex.Replace(text, @"\.(?=\s|$)", " ");

        // 4) 去掉破解组名（独立成词时才删，避免把 "CODEX" 之外的正常词切坏）
        foreach (var group in SceneGroupNames)
        {
            text = Regex.Replace(text, @"\b" + Regex.Escape(group) + @"\b", " ", RegexOptions.IgnoreCase);
        }

        // 5) 收尾：合并空白、去掉首尾的连字符/下划线/点/空格
        text = Regex.Replace(text, @"\s+", " ");
        text = text.Replace('_', ' ').Trim(' ', '-', '.', '·', '—');

        // 洗完太短（比如整个名字都是修饰词）就退回原名，别把游戏名洗没了
        return text.Length >= 2 ? text : folderName;
    }

    /// <summary>发行版修饰词：剥掉之后剩下的才是游戏名。</summary>
    private static readonly string[] ReleaseNameNoise =
    {
        "免安装", "绿色版", "硬盘版", "中文版", "简体中文", "繁体中文", "官方中文",
        "豪华版", "终极版", "决定版", "完整版", "正式版", "数字版", "标准版",
        "全DLC", "全dlc", "含DLC", "整合版", "整合", "已汉化", "汉化版", "汉化",
        "破解版", "未加密", "免CD", "免cd", "绿色", "解压即玩", "一键安装",
        "repack", "cracked", "crack", "multi", "multilanguage", "eng", "cht", "chs",
        "rip", "portable", "update", "all dlc", "goty",
    };

    /// <summary>常见破解/打包组名，出现在目录名里时是噪音。</summary>
    private static readonly string[] SceneGroupNames =
    {
        "codex", "plaza", "skidrow", "reloaded", "prophet", "steampunks",
        "elamigos", "fitgirl", "dodi", "rune", "tenoke", "empress", "flt",
        "razor1911", "hoodlum", "goldberg", "cpy", "3dm", "游侠", "游民", "ali213",
    };

    private static long Score(GameCandidate c)
    {
        long score = 0;

        // 主程序通常较大
        score += Math.Min(c.SizeBytes / (1024 * 1024), 2000);

        var lower = c.FileName.ToLowerInvariant();

        // 叫 launcher / config 的即便是游戏主程序，也大概率不是用户想双击的那个
        if (lower.Contains("launcher"))
        {
            score -= 500;
        }

        // exe 名和目录名一致 —— 典型的「游戏根目录下的主程序」
        if (c.FolderName.Equals(Path.GetFileNameWithoutExtension(c.FileName), StringComparison.OrdinalIgnoreCase))
        {
            score += 300;
        }

        // 埋在很深目录里的 exe 多半是组件
        var depth = c.FilePath.Count(ch => ch == '\\');
        score -= depth * 20;

        return score;
    }

    private static string Shorten(string path) => path.Length <= 52 ? path : "…" + path[^51..];
}
