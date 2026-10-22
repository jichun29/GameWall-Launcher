using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameWall.Models;
using GameWall.Services;

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

    public string SizeText => SizeBytes >= 1024L * 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024 / 1024:F1} GB"
        : $"{Math.Max(1, SizeBytes / 1024 / 1024)} MB";

    public override string ToString() => $"{SuggestedName}  ·  {FilePath}";
}

/// <summary>
/// 本地硬盘游戏扫描器。
///
/// 设计取舍：游戏目录里 exe 数量可能上万，逐个调用 Shell 取图标会卡死，
/// 所以这里只做「文件枚举 + 启发式打分」，图标留到真正加入游戏库时再提取。
/// 扫描全部走后台线程，通过 IProgress 回报进度。
/// </summary>
public sealed class GameScanner
{
    /// <summary>常见的「这不是游戏」命名，用来过滤掉启动器辅助程序。</summary>
    private static readonly string[] NoiseNames =
    {
        "unins", "uninstall", "setup", "install", "update", "updater", "crash",
        "report", "helper", "helper_", "vcredist", "dxsetup", "directx", "dotnet",
        "redist", "config", "settings", "launcher_helper", "diagnostics", "eac_",
        "easyanticheat", "battleye", "steamerrorreporter", "touchup", "register",
        "activation", "patcher", "cleanup", "repair", "bugreport", "dump",
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
                foreach (var name in new[] { "Games", "Game", "SteamLibrary", "Steam\\steamapps\\common", "游戏", "单机游戏" })
                {
                    var candidate = Path.Combine(root, name);
                    if (Directory.Exists(candidate))
                    {
                        roots.Add(candidate);
                    }
                }

                // steamapps/common 是 Steam 游戏的真正落点
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

            foreach (var name in new[] { "Epic Games", "GOG Galaxy\\Games", "Ubisoft", "EA Games", "Steam" })
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
    /// 扫描给定目录下的 exe。
    /// </summary>
    /// <param name="roots">要扫描的根目录</param>
    /// <param name="maxDepth">递归深度上限</param>
    /// <param name="minSize">exe 最小体积，过滤小工具</param>
    /// <param name="keyword">非空时只保留文件名/目录名包含关键字的候选</param>
    /// <param name="progress">进度回调：已完成目录数 / 当前目录</param>
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

                Walk(root, 0);
            }

            void Walk(string dir, int depth)
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

                    var candidate = TryBuild(file, minSize, keyword);
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
                    var name = Path.GetFileName(sub);
                    // 跳过明显无用的目录，避免遍历整个 Windows / 缓存
                    if (name.Equals("windows", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("$recycle.bin", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("node_modules", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("redist", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("_commonredist", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Walk(sub, depth + 1);
                }
            }

            // 体积大、名字像游戏的排前面
            return results
                .OrderByDescending(c => Score(c))
                .ThenBy(c => c.SuggestedName, StringComparer.CurrentCulture)
                .ToList();
        }, token);
    }

    private static GameCandidate? TryBuild(string file, long minSize, string? keyword)
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

            if (NoiseNames.Any(n => lower.Contains(n)))
            {
                return null;
            }

            var folder = info.Directory?.Name ?? string.Empty;
            var suggested = ChooseName(baseName, folder);

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
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 目录名往往比 exe 名更像游戏名（"Sekiro" 目录里是 sekiro.exe / Sekiro.exe）。
    /// 规则：目录名不是通用词、且比 exe 名信息量更大时用目录名。
    /// </summary>
    private static string ChooseName(string exeBaseName, string folderName)
    {
        var redundantFolders = new[]
        {
            "bin", "bin64", "binaries", "win64", "win32", "x64", "x86", "game",
            "release", "retail", "system", "engine", "common", "build", "launcher",
        };

        if (string.IsNullOrWhiteSpace(folderName)
            || redundantFolders.Contains(folderName.ToLowerInvariant())
            || folderName.Equals(exeBaseName, StringComparison.OrdinalIgnoreCase))
        {
            return exeBaseName;
        }

        // exe 名带版本号/后缀（如 Game-Win64-Shipping）时优先目录名
        var exeLower = exeBaseName.ToLowerInvariant();
        if (exeLower.Contains("-win64") || exeLower.Contains("-win32") || exeLower.Contains("shipping"))
        {
            return folderName;
        }

        return exeBaseName.Length >= folderName.Length ? exeBaseName : folderName;
    }

    private static long Score(GameCandidate c)
    {
        long score = 0;

        // 主程序通常较大
        score += Math.Min(c.SizeBytes / (1024 * 1024), 2000);

        var lower = c.FileName.ToLowerInvariant();
        if (lower.Contains("launcher"))
        {
            score -= 500;
        }

        if (c.FolderName.Equals(Path.GetFileNameWithoutExtension(c.FileName), StringComparison.OrdinalIgnoreCase))
        {
            score += 300;
        }

        return score;
    }

    private static string Shorten(string path) => path.Length <= 52 ? path : "…" + path[^51..];
}
