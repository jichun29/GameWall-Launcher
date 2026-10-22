using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using GameWall.Models;

namespace GameWall.Services;

/// <summary>
/// 一次「拖进来 / 选进来」解析出的可启动项。
///
/// 之所以不直接返回一个 exe 路径：Steam 游戏的桌面快捷方式其实是一个
/// <c>.url</c> 文件，里面写的是 <c>steam://rungameid/5194080</c> 这种协议地址，
/// 硬盘上可能根本没有「开箱即用」的主程序路径，只能带着协议地址去启动。
/// </summary>
public sealed class ResolvedLaunch
{
    /// <summary>启动方式：本地 exe / Steam / Epic / GOG / 自定义协议</summary>
    public LaunchKind Kind { get; init; } = LaunchKind.Executable;

    /// <summary>本地可执行文件；协议启动时可能为空，仅用于取图标和「打开安装目录」</summary>
    public string ExecutablePath { get; init; } = string.Empty;

    /// <summary>协议地址，如 steam://rungameid/5194080</summary>
    public string LaunchUri { get; init; } = string.Empty;

    /// <summary>建议名（Steam 清单里的官方名优先）</summary>
    public string SuggestedName { get; init; } = string.Empty;

    /// <summary>安装目录，便于「打开安装目录 / 存档目录」</summary>
    public string InstallDir { get; init; } = string.Empty;

    /// <summary>来源说明，写进 GameEntry.Source</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>这个解析结果是否真的能启动</summary>
    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(LaunchUri)
        || (!string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath));

    public override string ToString() => string.IsNullOrWhiteSpace(SuggestedName) ? LaunchUri : SuggestedName;
}

/// <summary>
/// 把用户拖进来 / 选中的东西统一解析成「一个可启动项」。
///
/// 支持：
///  - .exe / .bat / .cmd        直接当主程序
///  - .lnk                      WScript.Shell 解析目标；目标为空时退回解析 Arguments
///                              （Steam 生成的快捷方式就是 steam.exe -applaunch &lt;id&gt;）
///  - .url                      Internet 快捷方式：file:/// 指向本地 exe，
///                              steam:// rungameid/&lt;id&gt; 之类按协议还原成平台条目
/// </summary>
public static class ShortcutResolver
{
    /// <summary>steam://rungameid/5194080</summary>
    private static readonly Regex SteamUriPattern =
        new(@"steam://(?:rungameid|run)/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>steam.exe -applaunch 5194080</summary>
    private static readonly Regex SteamAppLaunchPattern =
        new(@"-applaunch\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>com.epicgames.launcher://apps/&lt;AppName&gt;?action=launch</summary>
    private static readonly Regex EpicUriPattern =
        new(@"com\.epicgames\.launcher://apps/([^?/&]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>galaxy2://runGame/&lt;id&gt;</summary>
    private static readonly Regex GogUriPattern =
        new(@"galaxy2://runGame/(\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>把一个路径解析成可启动项；无法识别时返回 null。</summary>
    public static ResolvedLaunch? Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();

            return ext switch
            {
                ".exe" or ".bat" or ".cmd" => FromExecutable(path),
                ".lnk" => FromLink(path),
                ".url" => FromInternetShortcut(path),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"解析启动项失败: {path}", ex);
            return null;
        }
    }

    /// <summary>兼容旧调用：只想要一个 exe 路径时用它。</summary>
    public static string? ResolveToExecutable(string path)
    {
        var resolved = Resolve(path);
        return resolved is { Kind: LaunchKind.Executable, ExecutablePath.Length: > 0 } ? resolved.ExecutablePath : null;
    }

    // ── 各类文件 ──────────────────────────────────────────────

    private static ResolvedLaunch FromExecutable(string exePath) => new()
    {
        Kind = LaunchKind.Executable,
        ExecutablePath = exePath,
        SuggestedName = Path.GetFileNameWithoutExtension(exePath),
        InstallDir = Path.GetDirectoryName(exePath) ?? string.Empty,
        Source = "手动添加",
    };

    private static ResolvedLaunch? FromLink(string lnkPath)
    {
        object? shell = null;
        object? shortcut = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { lnkPath });

            if (shortcut is null)
            {
                return null;
            }

            var target = shortcut.GetType()
                .InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
            var arguments = shortcut.GetType()
                .InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;

            // 1) 指向本地可执行文件：最常见
            if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
            {
                var targetExt = Path.GetExtension(target).ToLowerInvariant();
                if (targetExt is ".exe" or ".bat" or ".cmd")
                {
                    return FromExecutable(target);
                }

                return null;
            }

            // 2) Steam 生成的快捷方式：目标 steam.exe + -applaunch 5194080
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                var appLaunch = SteamAppLaunchPattern.Match(arguments);
                if (appLaunch.Success)
                {
                    return FromSteamAppId(appLaunch.Groups[1].Value, "Steam 快捷方式");
                }

                var steamUri = SteamUriPattern.Match(arguments);
                if (steamUri.Success)
                {
                    return FromSteamAppId(steamUri.Groups[1].Value, "Steam 快捷方式");
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.Error($"解析快捷方式失败: {lnkPath}", ex);
            return null;
        }
        finally
        {
            ReleaseCom(shortcut);
            ReleaseCom(shell);
        }
    }

    private static ResolvedLaunch? FromInternetShortcut(string urlPath)
    {
        try
        {
            string? url = null;
            foreach (var line in File.ReadAllLines(urlPath, Encoding.Default))
            {
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    url = line[4..].Trim();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            // 指向本地 exe 的写法
            if (url.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            {
                var local = Uri.UnescapeDataString(url[8..]).Replace('/', '\\');
                return File.Exists(local) ? FromExecutable(local) : null;
            }

            return FromUri(url, Path.GetFileNameWithoutExtension(urlPath));
        }
        catch (Exception ex)
        {
            Logger.Error($"解析 .url 失败: {urlPath}", ex);
            return null;
        }
    }

    /// <summary>
    /// 把平台协议地址还原成可启动项。
    /// 这一步是 Steam / Epic / GOG 桌面快捷方式能加进来的关键。
    /// </summary>
    public static ResolvedLaunch? FromUri(string uri, string? fallbackName = null)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        var steam = SteamUriPattern.Match(uri);
        if (steam.Success)
        {
            return FromSteamAppId(steam.Groups[1].Value, "Steam 快捷方式", fallbackName);
        }

        var epic = EpicUriPattern.Match(uri);
        if (epic.Success)
        {
            var appName = Uri.UnescapeDataString(epic.Groups[1].Value);
            var game = PlatformImportService.FindEpicApp(appName);
            return new ResolvedLaunch
            {
                Kind = LaunchKind.Epic,
                LaunchUri = $"com.epicgames.launcher://apps/{appName}?action=launch&silent=true",
                ExecutablePath = game?.ExecutablePath ?? string.Empty,
                InstallDir = game?.InstallDir ?? string.Empty,
                SuggestedName = game?.Name ?? fallbackName ?? appName,
                Source = "Epic 快捷方式",
            };
        }

        var gog = GogUriPattern.Match(uri);
        if (gog.Success)
        {
            var id = gog.Groups[1].Value;
            var game = PlatformImportService.FindGogGame(id);
            return new ResolvedLaunch
            {
                Kind = LaunchKind.Gog,
                LaunchUri = $"galaxy2://runGame/{id}",
                ExecutablePath = game?.ExecutablePath ?? string.Empty,
                InstallDir = game?.InstallDir ?? string.Empty,
                SuggestedName = game?.Name ?? fallbackName ?? id,
                Source = "GOG 快捷方式",
            };
        }

        return null;
    }

    /// <summary>用 Steam AppID 还原成游戏条目：名字和安装目录都从本地清单里读。</summary>
    private static ResolvedLaunch FromSteamAppId(string appId, string source, string? fallbackName = null)
    {
        var game = PlatformImportService.FindSteamApp(appId);

        return new ResolvedLaunch
        {
            Kind = LaunchKind.Steam,
            LaunchUri = $"steam://rungameid/{appId}",
            ExecutablePath = game?.ExecutablePath ?? string.Empty,
            InstallDir = game?.InstallDir ?? string.Empty,
            // 清单里没有（比如还没下载完）时，退回到 .url 文件名
            SuggestedName = game?.Name ?? fallbackName ?? $"Steam 游戏 {appId}",
            Source = source,
        };
    }

    private static void ReleaseCom(object? obj)
    {
        if (obj is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(obj))
            {
                Marshal.ReleaseComObject(obj);
            }
        }
        catch
        {
            // 忽略释放异常
        }
    }
}
