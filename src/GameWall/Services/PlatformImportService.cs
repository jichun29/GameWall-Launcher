using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using GameWall.Models;

namespace GameWall.Services;

/// <summary>一个平台的读取结果状态。用来在导入窗口里告诉用户「为什么没读到」。</summary>
public enum PlatformState
{
    /// <summary>读到了游戏</summary>
    Ok = 0,

    /// <summary>客户端在，但库里一个游戏都没有</summary>
    NoGames = 1,

    /// <summary>本机没检测到该平台客户端</summary>
    ClientMissing = 2,

    /// <summary>客户端装了，但本地清单还没生成（没启动过 / 没登录过）</summary>
    ManifestMissing = 3,

    /// <summary>读取过程出错（权限、文件损坏等）</summary>
    Error = 4,
}

/// <summary>
/// 单个平台的读取诊断。
///
/// 平台导入最常被问的两句是「为什么一个游戏都没有」和「我明明装了」，
/// 光写日志没用——用户不会去翻日志。所以这里每个平台都给一句话现状 + 一句可操作的建议，
/// 直接画在导入窗口里。
/// </summary>
public sealed class PlatformStatus
{
    public string Platform { get; init; } = string.Empty;

    public PlatformState State { get; init; }

    public int Count { get; init; }

    /// <summary>现状，例如「未检测到 Steam 客户端」。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>下一步能做什么，例如「先启动一次 Steam 客户端再回来点重新扫描」。</summary>
    public string Hint { get; init; } = string.Empty;

    /// <summary>这条状态是否需要用户在界面上看到（全部正常时不占地方）。</summary>
    public bool HasProblem => State != PlatformState.Ok;

    public bool IsReadable => State == PlatformState.Ok || State == PlatformState.NoGames;

    public static PlatformStatus Ok(string platform, int count, string where) => new()
    {
        Platform = platform,
        State = count > 0 ? PlatformState.Ok : PlatformState.NoGames,
        Count = count,
        Message = count > 0 ? $"读到 {count} 个游戏（{where}）" : $"客户端在（{where}），但库里没有已安装的游戏",
        Hint = count > 0 ? string.Empty : "在平台里装好游戏后再点「重新扫描」。",
    };
}

/// <summary>平台导入的完整结果：游戏 + 每个平台的诊断。</summary>
public sealed class PlatformImportResult
{
    public List<PlatformGame> Games { get; init; } = new();

    public List<PlatformStatus> Statuses { get; init; } = new();
}

/// <summary>从平台导入的候选游戏。</summary>
public sealed class PlatformGame
{
    public string Platform { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string AppId { get; init; } = string.Empty;

    public LaunchKind Kind { get; init; } = LaunchKind.Steam;

    public string LaunchUri { get; init; } = string.Empty;

    /// <summary>能定位到本地 exe 时填上，便于「打开安装目录」</summary>
    public string ExecutablePath { get; init; } = string.Empty;

    public string InstallDir { get; init; } = string.Empty;

    public string SuggestedName { get; init; } = string.Empty;
}

/// <summary>
/// 平台导入：读取 Steam / Epic / GOG 的本地安装清单。
///
/// 只读本地文件，不做任何网络请求：
///  - Steam：libraryfolders.vdf + steamapps/appmanifest_*.acf
///  - Epic ：%ProgramData%\Epic\UnrealEngineLauncher\LauncherInstalled.dat
///  - GOG  ：注册表 HKCU\Software\GOG.com\Games\<id> 下的 gameName / path
/// </summary>
public static class PlatformImportService
{
    // ── Steam ────────────────────────────────────────────────
    public static List<PlatformGame> ReadSteam() => ReadSteamCore(out _);

    private static List<PlatformGame> ReadSteamCore(out PlatformStatus status)
    {
        var list = new List<PlatformGame>();
        var steamRoot = string.Empty;

        status = new PlatformStatus
        {
            Platform = "Steam",
            State = PlatformState.ClientMissing,
            Message = "没有检测到 Steam 客户端",
            Hint = "Steam 装在非默认位置时，先启动一次 Steam 客户端，再回来点「重新扫描」。",
        };

        try
        {
            steamRoot = ReadSteamRoot() ?? string.Empty;
            if (steamRoot.Length == 0)
            {
                return list;
            }

            // 根目录找到了但 libraryfolders.vdf 还没生成 —— 客户端装过但没登录过
            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
            {
                status = new PlatformStatus
                {
                    Platform = "Steam",
                    State = PlatformState.ManifestMissing,
                    Message = "找到了 Steam（" + steamRoot + "），但本地库清单还没生成",
                    Hint = "启动一次 Steam 客户端并登录，等库列表加载出来后回来点「重新扫描」。",
                };
                return list;
            }

            var libraries = new List<string> { steamRoot };
            libraries.AddRange(ReadSteamLibraries(steamRoot));

            foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var steamApps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(steamApps))
                {
                    continue;
                }

                foreach (var acf in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
                {
                    try
                    {
                        var text = File.ReadAllText(acf);
                        var appId = Match(text, "\"appid\"\\s+\"(\\d+)\"");
                        var name = Match(text, "\"name\"\\s+\"([^\"]+)\"");
                        var installDir = Match(text, "\"installdir\"\\s+\"([^\"]+)\"");

                        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        // Steam 自己会在库里放两样「不是游戏」的东西：
                        //   - 228980 Steamworks Common Redistributables（运行库合集）
                        //   - 名字里带 "Steamworks" 的各种共享组件
                        if (appId == "228980"
                            || name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var fullInstall = string.IsNullOrWhiteSpace(installDir)
                            ? string.Empty
                            : Path.Combine(steamApps, "common", installDir);

                        // Steam 清单里只有英文名；本地的 appinfo.vdf 里带着官方中文名，
                        // 能查到就用中文，国内用户看着舒服（纯本地读取，不联网）。
                        var displayName = SteamNameService.GetChineseName(appId) ?? name;

                        list.Add(new PlatformGame
                        {
                            Platform = "Steam",
                            Name = displayName,
                            AppId = appId,
                            Kind = LaunchKind.Steam,
                            LaunchUri = $"steam://rungameid/{appId}",
                            InstallDir = fullInstall,
                            ExecutablePath = FindMainExe(fullInstall, name),
                            SuggestedName = displayName,
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"解析 Steam 清单失败: {acf}", ex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("读取 Steam 游戏失败", ex);
            status = new PlatformStatus
            {
                Platform = "Steam",
                State = PlatformState.Error,
                Message = "读取 Steam 库时出错：" + ex.Message,
                Hint = "确认 Steam 客户端没有被杀进程，或稍后重试；持续失败请把日志导出发给开发者。",
            };
            return list;
        }

        var ordered = list.OrderBy(g => g.Name, StringComparer.CurrentCulture).ToList();
        status = PlatformStatus.Ok("Steam", ordered.Count, steamRoot);
        return ordered;
    }

    private static string? ReadSteamRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string path && Directory.Exists(path))
            {
                return path.Replace('/', '\\');
            }
        }
        catch
        {
            // 注册表不可读时继续用默认路径探测
        }

        foreach (var guess in new[]
                 {
                     @"C:\Program Files (x86)\Steam",
                     @"C:\Program Files\Steam",
                     @"D:\Steam",
                     @"D:\Program Files (x86)\Steam",
                 })
        {
            if (Directory.Exists(guess))
            {
                return guess;
            }
        }

        return null;
    }

    /// <summary>供 GameScanner 复用：找到 Steam 安装根目录。</summary>
    public static string? ReadSteamRootPublic() => ReadSteamRoot();

    /// <summary>供 GameScanner 复用：读取所有 Steam 库根目录。</summary>
    public static List<string> ReadSteamLibrariesPublic(string steamRoot) =>
        ReadSteamLibraries(steamRoot).ToList();

    private static IEnumerable<string> ReadSteamLibraries(string steamRoot)
    {
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
        {
            yield break;
        }

        string text;
        try
        {
            text = File.ReadAllText(vdf);
        }
        catch
        {
            yield break;
        }

        foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
        {
            var path = m.Groups[1].Value.Replace("\\\\", "\\");
            if (Directory.Exists(path))
            {
                yield return path;
            }
        }
    }

    // ── Epic ─────────────────────────────────────────────────
    public static List<PlatformGame> ReadEpic() => ReadEpicCore(out _);

    private static List<PlatformGame> ReadEpicCore(out PlatformStatus status)
    {
        var list = new List<PlatformGame>();
        var dat = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");

        status = new PlatformStatus
        {
            Platform = "Epic",
            State = PlatformState.ClientMissing,
            Message = "没有检测到 Epic 客户端",
            Hint = "没装 Epic 就不用管。装了的话启动一次 Epic Games Launcher 再回来点「重新扫描」。",
        };

        try
        {
            if (!File.Exists(dat))
            {
                // 注册表里有 Epic 说明客户端装过，只是清单还没落地
                if (EpicRegistryExists())
                {
                    status = new PlatformStatus
                    {
                        Platform = "Epic",
                        State = PlatformState.ManifestMissing,
                        Message = "装了 Epic，但安装清单还没生成",
                        Hint = "启动 Epic Games Launcher 并登录一次，让它写出 LauncherInstalled.dat 后再「重新扫描」。",
                    };
                }
                return list;
            }

            // 该文件是标准 JSON，直接借用 System.Text.Json 解析
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(dat));
            if (!doc.RootElement.TryGetProperty("InstallationList", out var arr))
            {
                status = new PlatformStatus
                {
                    Platform = "Epic",
                    State = PlatformState.ManifestMissing,
                    Message = "Epic 安装清单存在，但里面没有游戏列表",
                    Hint = "在 Epic 里装好游戏后，重新打开一次启动器再「重新扫描」。",
                };
                return list;
            }

            foreach (var item in arr.EnumerateArray())
            {
                var name = item.TryGetProperty("AppName", out var n) ? n.GetString() : null;
                var dir = item.TryGetProperty("InstallLocation", out var d) ? d.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                list.Add(new PlatformGame
                {
                    Platform = "Epic",
                    Name = name!,
                    AppId = name!,
                    Kind = LaunchKind.Epic,
                    LaunchUri = $"com.epicgames.launcher://apps/{name}?action=launch&silent=true",
                    InstallDir = dir ?? string.Empty,
                    ExecutablePath = FindMainExe(dir, name!),
                    SuggestedName = name!,
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error("读取 Epic 游戏失败", ex);
            status = new PlatformStatus
            {
                Platform = "Epic",
                State = PlatformState.Error,
                Message = "读取 Epic 清单时出错：" + ex.Message,
                Hint = "清单文件可能正在被客户端写入，等 Epic 完全启动后再试一次。",
            };
            return list;
        }

        var ordered = list.OrderBy(g => g.Name, StringComparer.CurrentCulture).ToList();
        status = PlatformStatus.Ok("Epic", ordered.Count, "LauncherInstalled.dat");
        return ordered;
    }

    /// <summary>注册表里有没有 Epic 的痕迹（判断「确实装了客户端」）。</summary>
    private static bool EpicRegistryExists()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher")
                            ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Epic Games\EpicGamesLauncher");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    // ── GOG ──────────────────────────────────────────────────
    public static List<PlatformGame> ReadGog() => ReadGogCore(out _);

    private static List<PlatformGame> ReadGogCore(out PlatformStatus status)
    {
        var list = new List<PlatformGame>();
        status = new PlatformStatus
        {
            Platform = "GOG",
            State = PlatformState.ClientMissing,
            Message = "没有检测到 GOG Galaxy",
            Hint = "没装 GOG 就不用管。GOG 的游戏也可以直接用「扫描」或「手动添加」。",
        };

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Software\GOG.com\Games");
            if (root is null)
            {
                return list;
            }

            foreach (var subName in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(subName);
                if (sub is null)
                {
                    continue;
                }

                var name = sub.GetValue("gameName") as string ?? sub.GetValue("GAMENAME") as string;
                var path = sub.GetValue("path") as string ?? sub.GetValue("PATH") as string;
                var exe = sub.GetValue("exe") as string ?? sub.GetValue("EXE") as string;

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var fullExe = string.Empty;
                if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(exe))
                {
                    var candidate = Path.Combine(path, exe);
                    if (File.Exists(candidate))
                    {
                        fullExe = candidate;
                    }
                }

                list.Add(new PlatformGame
                {
                    Platform = "GOG",
                    Name = name!,
                    AppId = subName,
                    Kind = LaunchKind.Gog,
                    LaunchUri = $"galaxy2://runGame/{subName}",
                    InstallDir = path ?? string.Empty,
                    ExecutablePath = fullExe,
                    SuggestedName = name!,
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error("读取 GOG 游戏失败", ex);
            status = new PlatformStatus
            {
                Platform = "GOG",
                State = PlatformState.Error,
                Message = "读取 GOG 注册表时出错：" + ex.Message,
                Hint = "注册表可能被安全软件拦住，可改用「扫描」找 GOG 的游戏目录。",
            };
            return list;
        }

        var ordered = list.OrderBy(g => g.Name, StringComparer.CurrentCulture).ToList();
        status = PlatformStatus.Ok("GOG", ordered.Count, @"HKCU\Software\GOG.com\Games");
        return ordered;
    }

    /// <summary>批量导入（去重交给调用方，这里只负责读取）。</summary>
    public static List<PlatformGame> ReadAll()
    {
        var all = new List<PlatformGame>();
        all.AddRange(ReadSteam());
        all.AddRange(ReadEpic());
        all.AddRange(ReadGog());
        return all;
    }

    /// <summary>批量读取，同时带回每个平台的诊断信息（导入窗口用这个）。</summary>
    public static PlatformImportResult ReadAllWithStatus()
    {
        var result = new PlatformImportResult();

        result.Games.AddRange(ReadSteamCore(out var steam));
        result.Games.AddRange(ReadEpicCore(out var epic));
        result.Games.AddRange(ReadGogCore(out var gog));

        result.Statuses.Add(steam);
        result.Statuses.Add(epic);
        result.Statuses.Add(gog);

        // 读不到东西的原因统一记一条日志，用户把日志发过来就能直接定位
        foreach (var s in result.Statuses.Where(s => s.HasProblem))
        {
            Logger.Warn($"平台导入：{s.Platform} — {s.Message}（{s.Hint}）");
        }

        return result;
    }

    // ── 按 ID 反查（拖入 steam:// 快捷方式时用）─────────────
    //
    // 解析快捷方式不像点「平台导入」那样需要全量列表，只查一个 AppID。
    // 但每次拖入都重扫一遍所有库目录也没必要，所以加一层短期缓存。

    private static List<PlatformGame>? _steamCache;
    private static List<PlatformGame>? _epicCache;
    private static List<PlatformGame>? _gogCache;
    private static DateTime _cacheAt;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);

    private static bool CacheExpired => DateTime.UtcNow - _cacheAt > CacheLifetime;

    /// <summary>按 Steam AppID 查本地清单；查不到返回 null。</summary>
    public static PlatformGame? FindSteamApp(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        if (_steamCache is null || CacheExpired)
        {
            _steamCache = ReadSteam();
            _cacheAt = DateTime.UtcNow;
        }

        return _steamCache.FirstOrDefault(g =>
            string.Equals(g.AppId, appId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按 Epic AppName 查安装清单。</summary>
    public static PlatformGame? FindEpicApp(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return null;
        }

        if (_epicCache is null || CacheExpired)
        {
            _epicCache = ReadEpic();
            _cacheAt = DateTime.UtcNow;
        }

        return _epicCache.FirstOrDefault(g =>
            string.Equals(g.AppId, appName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按 GOG 游戏 ID 查注册表。</summary>
    public static PlatformGame? FindGogGame(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return null;
        }

        if (_gogCache is null || CacheExpired)
        {
            _gogCache = ReadGog();
            _cacheAt = DateTime.UtcNow;
        }

        return _gogCache.FirstOrDefault(g =>
            string.Equals(g.AppId, gameId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>安装/卸载游戏后让缓存失效。</summary>
    public static void InvalidateCache()
    {
        _steamCache = null;
        _epicCache = null;
        _gogCache = null;
        _cacheAt = DateTime.MinValue;
    }

    /// <summary>在安装目录里猜主程序：优先体积最大的 exe，其次名字最接近游戏名的。</summary>
    private static string FindMainExe(string? dir, string gameName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                return string.Empty;
            }

            var exes = Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .Where(f => f.Length > 1024 * 1024)
                .ToList();

            if (exes.Count == 0)
            {
                return string.Empty;
            }

            var normalized = Regex.Replace(gameName.ToLowerInvariant(), "[^a-z0-9]", string.Empty);
            return exes
                .OrderByDescending(f =>
                {
                    var stem = Regex.Replace(Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant(), "[^a-z0-9]", string.Empty);
                    var similar = normalized.Length > 0 && stem.Length > 0
                        && (stem.Contains(normalized) || normalized.Contains(stem));
                    return (similar ? 1_000_000_000L : 0) + f.Length;
                })
                .First()
                .FullName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? Match(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
