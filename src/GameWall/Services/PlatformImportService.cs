using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using GameWall.Models;

namespace GameWall.Services;

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
    public static List<PlatformGame> ReadSteam()
    {
        var list = new List<PlatformGame>();
        try
        {
            var steamRoot = ReadSteamRoot();
            if (string.IsNullOrEmpty(steamRoot))
            {
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

                        var fullInstall = string.IsNullOrWhiteSpace(installDir)
                            ? string.Empty
                            : Path.Combine(steamApps, "common", installDir);

                        list.Add(new PlatformGame
                        {
                            Platform = "Steam",
                            Name = name,
                            AppId = appId,
                            Kind = LaunchKind.Steam,
                            LaunchUri = $"steam://rungameid/{appId}",
                            InstallDir = fullInstall,
                            ExecutablePath = FindMainExe(fullInstall, name),
                            SuggestedName = name,
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
        }

        return list.OrderBy(g => g.Name, StringComparer.CurrentCulture).ToList();
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
    public static List<PlatformGame> ReadEpic()
    {
        var list = new List<PlatformGame>();
        try
        {
            var dat = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");

            if (!File.Exists(dat))
            {
                return list;
            }

            // 该文件是标准 JSON，直接借用 System.Text.Json 解析
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(dat));
            if (!doc.RootElement.TryGetProperty("InstallationList", out var arr))
            {
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
        }

        return list.OrderBy(g => g.Name, StringComparer.CurrentCulture).ToList();
    }

    // ── GOG ──────────────────────────────────────────────────
    public static List<PlatformGame> ReadGog()
    {
        var list = new List<PlatformGame>();
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
        }

        return list.OrderBy(g => g.Name, StringComparer.CurrentCulture).ToList();
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
