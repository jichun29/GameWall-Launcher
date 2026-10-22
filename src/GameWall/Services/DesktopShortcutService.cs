using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using GameWall.Models;

namespace GameWall.Services;

/// <summary>
/// 桌面上一条快捷方式（.lnk / .url）的体检结果。
/// </summary>
public sealed class DesktopShortcutInfo
{
    /// <summary>快捷方式文件完整路径</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>文件名（不含扩展名），也就是桌面上显示的名字</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>所在桌面：用户桌面 / 公共桌面（公共桌面移动需要管理员权限）</summary>
    public bool IsPublicDesktop { get; init; }

    /// <summary>解析出来的启动方式</summary>
    public LaunchKind Kind { get; init; } = LaunchKind.Executable;

    /// <summary>解析出来的可执行文件（可能为空）</summary>
    public string ExecutablePath { get; init; } = string.Empty;

    /// <summary>解析出来的协议地址（Steam/Epic 等）</summary>
    public string LaunchUri { get; init; } = string.Empty;

    /// <summary>命中的游戏库条目 Id（没命中为 null）</summary>
    public string? MatchedGameId { get; set; }

    /// <summary>命中的游戏名（没命中为空）</summary>
    public string MatchedGameName { get; set; } = string.Empty;

    /// <summary>被判定为「不是游戏」（平台客户端、运行库、工具软件）</summary>
    public bool LooksLikeTool { get; init; }

    /// <summary>完全无法解析（指向文件夹、已失效的快捷方式等）</summary>
    public bool Unresolved { get; init; }

    /// <summary>是否已经在游戏墙里（唯一值得收纳的判据）</summary>
    public bool InLibrary => !string.IsNullOrEmpty(MatchedGameId);

    /// <summary>列表里显示的副标题：路径或协议</summary>
    public string TargetText =>
        !string.IsNullOrWhiteSpace(ExecutablePath) ? ExecutablePath
        : !string.IsNullOrWhiteSpace(LaunchUri) ? LaunchUri
        : "（无法解析）";

    /// <summary>列表里的状态标签</summary>
    public string StatusText =>
        InLibrary ? "已收录"
        : LooksLikeTool ? "不是游戏"
        : Unresolved ? "无法识别"
        : "未收录";

    /// <summary>这一条能不能被勾选收纳</summary>
    public bool Selectable => InLibrary && !IsPublicDesktop;
}

/// <summary>一次整理的结果。</summary>
public sealed class TidyResult
{
    public int Moved { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }

    /// <summary>备份目录（移动成功时才有）</summary>
    public string BackupDirectory { get; set; } = string.Empty;

    /// <summary>失败明细，用于在界面上逐条提示</summary>
    public List<string> Failures { get; } = new();

    public bool AnythingHappened => Moved > 0 || Failed > 0;
}

/// <summary>备份目录里记录的一批快捷方式。</summary>
public sealed class TidyBackup
{
    public string Directory { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int Count { get; set; }
    public List<string> Names { get; set; } = new();

    public string DisplayText =>
        $"{CreatedAt:yyyy-MM-dd HH:mm} · {Count} 个" +
        (Names.Count > 0 ? "（" + string.Join("、", Names.Take(3)) + (Names.Count > 3 ? "…" : string.Empty) + "）" : string.Empty);
}

/// <summary>
/// 桌面快捷方式收纳。
///
/// 设计取舍：
///  · 只「移动」不「删除」——全部挪到 %AppData%\GameWall\desktop-backup\{时间戳}\ 下，
///    目录里附一份 restore.json 记录原始位置。整理错了随时能一键还原，
///    比直接删进回收站更可控（回收站还会被用户清空，这里不会）。
///  · 只收「已经在游戏墙里」的快捷方式。没收录的一律不动，避免把用户桌面上
///    没导入的游戏一起藏起来——那样用户会觉得「我的东西没了」。
///  · 公共桌面（C:\Users\Public\Desktop）只读不写：那里的快捷方式所有用户共享，
///    移动需要管理员权限，也更不该由一个小工具擅自处理。列表里会标出来但不让勾。
/// </summary>
public static class DesktopShortcutService
{
    private const string RestoreFileName = "restore.json";

    /// <summary>备份根目录</summary>
    public static string BackupRoot => Path.Combine(AppPaths.Root, "desktop-backup");

    /// <summary>枚举两个桌面上的 .lnk / .url，并逐条和游戏库比对。</summary>
    public static List<DesktopShortcutInfo> Inspect(IReadOnlyList<GameEntry> games)
    {
        var result = new List<DesktopShortcutInfo>();

        foreach (var (dir, isPublic) in EnumerateDesktopFolders())
        {
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir)
                    .Where(f => IsShortcut(f))
                    .ToList();
            }
            catch (Exception ex)
            {
                Logger.Error($"枚举桌面失败: {dir}", ex);
                continue;
            }

            foreach (var file in files)
            {
                var info = InspectOne(file, isPublic, games);
                if (info is not null)
                {
                    result.Add(info);
                }
            }
        }

        // 已收录的排前面，其次未收录，最后是工具/无法识别的
        return result
            .OrderByDescending(i => i.InLibrary)
            .ThenBy(i => i.LooksLikeTool || i.Unresolved)
            .ThenBy(i => i.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    private static DesktopShortcutInfo? InspectOne(string file, bool isPublic, IReadOnlyList<GameEntry> games)
    {
        try
        {
            var display = Path.GetFileNameWithoutExtension(file);
            var resolved = ShortcutResolver.Resolve(file);

            if (resolved is null)
            {
                return new DesktopShortcutInfo
                {
                    FilePath = file,
                    DisplayName = display,
                    IsPublicDesktop = isPublic,
                    Unresolved = true,
                };
            }

            var looksLikeTool = resolved.Kind == LaunchKind.Executable
                                && !string.IsNullOrWhiteSpace(resolved.ExecutablePath)
                                && GameScanner.IsLikelyPlatformOrTool(resolved.ExecutablePath);

            var info = new DesktopShortcutInfo
            {
                FilePath = file,
                DisplayName = display,
                IsPublicDesktop = isPublic,
                Kind = resolved.Kind,
                ExecutablePath = resolved.ExecutablePath,
                LaunchUri = resolved.LaunchUri,
                LooksLikeTool = looksLikeTool,
            };

            var match = FindGame(resolved, games);
            if (match is not null)
            {
                info.MatchedGameId = match.Id;
                info.MatchedGameName = match.Name;
            }

            return info;
        }
        catch (Exception ex)
        {
            Logger.Error($"检查桌面快捷方式失败: {file}", ex);
            return null;
        }
    }

    /// <summary>
    /// 判断一条桌面快捷方式对应哪款已收录的游戏。
    ///
    /// 三条判据，从强到弱：
    ///  1. 协议地址相同（Steam/Epic/GOG 最准确，同一个 appid 一定是同一款）
    ///  2. 可执行文件路径相同
    ///  3. 可执行文件在同一个安装目录下（快捷方式有时指向游戏目录里的启动器而不是主程序）
    /// </summary>
    private static GameEntry? FindGame(ResolvedLaunch resolved, IReadOnlyList<GameEntry> games)
    {
        if (!string.IsNullOrWhiteSpace(resolved.LaunchUri))
        {
            var byUri = games.FirstOrDefault(g =>
                !string.IsNullOrWhiteSpace(g.LaunchUri)
                && string.Equals(g.LaunchUri, resolved.LaunchUri, StringComparison.OrdinalIgnoreCase));
            if (byUri is not null)
            {
                return byUri;
            }
        }

        if (!string.IsNullOrWhiteSpace(resolved.ExecutablePath))
        {
            var byExe = games.FirstOrDefault(g =>
                !string.IsNullOrWhiteSpace(g.ExecutablePath)
                && string.Equals(g.ExecutablePath, resolved.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            if (byExe is not null)
            {
                return byExe;
            }

            // 同目录兜底：只在游戏自己的安装目录里比较，别把两个都装在
            // E:\Games 下的不同游戏认成一个
            var dir = Path.GetDirectoryName(resolved.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                var byDir = games.FirstOrDefault(g =>
                {
                    if (string.IsNullOrWhiteSpace(g.ExecutablePath))
                    {
                        return false;
                    }

                    var gameDir = Path.GetDirectoryName(g.ExecutablePath);
                    return !string.IsNullOrWhiteSpace(gameDir)
                           && string.Equals(gameDir, dir, StringComparison.OrdinalIgnoreCase);
                });

                if (byDir is not null)
                {
                    return byDir;
                }
            }
        }

        return null;
    }

    /// <summary>统计桌面上「已经收录、但快捷方式还在」的条数（状态栏提示用）。</summary>
    public static int CountPending(IReadOnlyList<GameEntry> games)
    {
        try
        {
            return Inspect(games).Count(i => i.InLibrary && !i.IsPublicDesktop);
        }
        catch (Exception ex)
        {
            Logger.Error("统计桌面快捷方式失败", ex);
            return 0;
        }
    }

    /// <summary>
    /// 把选中的快捷方式移进备份目录。返回移动/失败数量和备份路径。
    /// </summary>
    public static TidyResult Tidy(IEnumerable<DesktopShortcutInfo> targets)
    {
        var result = new TidyResult();
        var list = targets.Where(t => t is not null).ToList();
        if (list.Count == 0)
        {
            return result;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupDir = Path.Combine(BackupRoot, stamp);
        Directory.CreateDirectory(backupDir);
        result.BackupDirectory = backupDir;

        var manifest = new List<RestoreEntry>();

        foreach (var item in list)
        {
            if (item.IsPublicDesktop)
            {
                result.Skipped++;
                continue;
            }

            try
            {
                if (!File.Exists(item.FilePath))
                {
                    result.Skipped++;
                    continue;
                }

                // 重名时加序号，避免覆盖上一批备份
                var dest = Path.Combine(backupDir, Path.GetFileName(item.FilePath));
                var counter = 1;
                while (File.Exists(dest))
                {
                    var name = Path.GetFileNameWithoutExtension(item.FilePath);
                    var ext = Path.GetExtension(item.FilePath);
                    dest = Path.Combine(backupDir, $"{name} ({counter++}){ext}");
                }

                File.Move(item.FilePath, dest);
                manifest.Add(new RestoreEntry
                {
                    OriginalPath = item.FilePath,
                    BackupName = Path.GetFileName(dest),
                    DisplayName = item.DisplayName,
                });

                result.Moved++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Failures.Add($"{item.DisplayName}：{ex.Message}");
                Logger.Error($"整理桌面快捷方式失败: {item.FilePath}", ex);
            }
        }

        if (manifest.Count == 0)
        {
            // 一条都没移成功就别留空目录
            TryDeleteDirectory(backupDir);
            result.BackupDirectory = string.Empty;
            return result;
        }

        WriteManifest(backupDir, manifest);
        return result;
    }

    /// <summary>列出所有备份批次，最新的排前面。</summary>
    public static List<TidyBackup> ListBackups()
    {
        var result = new List<TidyBackup>();

        try
        {
            if (!Directory.Exists(BackupRoot))
            {
                return result;
            }

            foreach (var dir in Directory.EnumerateDirectories(BackupRoot))
            {
                var manifest = ReadManifest(dir);
                var files = Directory.EnumerateFiles(dir)
                    .Where(f => IsShortcut(f))
                    .ToList();

                // 已经还原干净的空批次不再列出来
                if (files.Count == 0)
                {
                    continue;
                }

                result.Add(new TidyBackup
                {
                    Directory = dir,
                    CreatedAt = Directory.GetCreationTime(dir),
                    Count = files.Count,
                    Names = files.Select(f => Path.GetFileNameWithoutExtension(f) ?? string.Empty).ToList(),
                });

                _ = manifest;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("读取备份列表失败", ex);
        }

        return result.OrderByDescending(b => b.CreatedAt).ToList();
    }

    /// <summary>
    /// 把某一批备份还原回桌面。已存在同名文件时跳过，不覆盖用户后来新建的。
    /// </summary>
    public static (int restored, int skipped, int failed) Restore(string backupDir)
    {
        var restored = 0;
        var skipped = 0;
        var failed = 0;

        try
        {
            if (!Directory.Exists(backupDir))
            {
                return (0, 0, 0);
            }

            var manifest = ReadManifest(backupDir);

            foreach (var entry in manifest)
            {
                try
                {
                    var source = Path.Combine(backupDir, entry.BackupName);
                    if (!File.Exists(source))
                    {
                        failed++;
                        continue;
                    }

                    var target = entry.OriginalPath;
                    if (string.IsNullOrWhiteSpace(target))
                    {
                        target = Path.Combine(GetUserDesktop(), entry.BackupName);
                    }

                    if (File.Exists(target))
                    {
                        skipped++;
                        continue;
                    }

                    var dir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.Move(source, target);
                    restored++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Logger.Error($"还原快捷方式失败: {entry.BackupName}", ex);
                }
            }

            // 一个文件都不剩了就把这批备份目录删掉，免得设置里列出一堆空批次。
            // 注意清单文件 restore.json 还在目录里，得先删掉它才谈得上删目录。
            if (restored > 0 && !Directory.EnumerateFiles(backupDir).Any(f => IsShortcut(f)))
            {
                try
                {
                    var manifestFile = Path.Combine(backupDir, RestoreFileName);
                    if (File.Exists(manifestFile))
                    {
                        File.Delete(manifestFile);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("删除空批次清单失败", ex);
                }

                TryDeleteDirectory(backupDir);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("还原备份失败", ex);
        }

        return (restored, skipped, failed);
    }

    // ── 内部工具 ────────────────────────────────────────────

    private sealed class RestoreEntry
    {
        public string OriginalPath { get; set; } = string.Empty;
        public string BackupName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }

    private static void WriteManifest(string backupDir, List<RestoreEntry> entries)
    {
        try
        {
            JsonStore.Save(Path.Combine(backupDir, RestoreFileName), entries);
        }
        catch (Exception ex)
        {
            Logger.Error("写入备份清单失败", ex);
        }
    }

    private static List<RestoreEntry> ReadManifest(string backupDir)
    {
        try
        {
            var path = Path.Combine(backupDir, RestoreFileName);
            if (!File.Exists(path))
            {
                return new List<RestoreEntry>();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<RestoreEntry>>(json, JsonStore.Options)
                   ?? new List<RestoreEntry>();
        }
        catch (Exception ex)
        {
            Logger.Error("读取备份清单失败", ex);
            return new List<RestoreEntry>();
        }
    }

    private static IEnumerable<(string dir, bool isPublic)> EnumerateDesktopFolders()
    {
        var user = GetUserDesktop();
        if (!string.IsNullOrWhiteSpace(user) && Directory.Exists(user))
        {
            yield return (user, false);
        }

        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrWhiteSpace(common) && Directory.Exists(common)
            && !string.Equals(common, user, StringComparison.OrdinalIgnoreCase))
        {
            yield return (common, true);
        }
    }

    private static string GetUserDesktop()
        => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    private static bool IsShortcut(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch
        {
            // 删不掉就算了，留着也不影响
        }
    }
}
