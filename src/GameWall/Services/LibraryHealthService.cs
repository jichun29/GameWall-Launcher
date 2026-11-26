using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameWall.Models;

namespace GameWall.Services;

/// <summary>一条路径失效的游戏 + 猜出来的新位置。</summary>
public sealed class BrokenGame
{
    public GameEntry Entry { get; init; } = new();

    /// <summary>库里记录的旧路径（用来给用户看、也用来猜新路径）。</summary>
    public string OldPath { get; init; } = string.Empty;

    /// <summary>自动猜出来的新 exe 路径，猜不到就是空串。</summary>
    public string GuessPath { get; set; } = string.Empty;

    /// <summary>猜的把握有多大，只用于排序和界面提示。</summary>
    public GuessConfidence Confidence { get; set; } = GuessConfidence.None;

    public string Name => Entry.Name;

    public string OldDirectory => SafeDirName(OldPath);

    public string StatusText => Confidence switch
    {
        GuessConfidence.Exact => "找到同名文件，目录层级也对得上",
        GuessConfidence.Likely => "找到同名文件，位置不同",
        GuessConfidence.Weak => "找到一个同名文件，不确定是不是同一个",
        _ => "没找到",
    };

    private static string SafeDirName(string path)
    {
        try
        {
            return Path.GetDirectoryName(path) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

/// <summary>猜路径的把握程度。</summary>
public enum GuessConfidence
{
    None = 0,
    Weak = 1,
    Likely = 2,
    Exact = 3,
}

/// <summary>
/// 游戏库的路径体检。
///
/// 为什么需要它：`library.json` 里存的是**绝对路径**，换机器、改盘符、把游戏从
/// `D:\Games` 挪到 `E:\Games` 之后，所有卡片都会变成「文件缺失」，用户只能一张一张
/// 右键重新指定。这里做的就是把这件事自动化：
///   1. 找出所有 exe 已经不存在的条目；
///   2. 用「文件名 + 路径尾部层级」在现有扫描目录里找同名文件，猜一个新位置；
///   3. 猜得够准的直接批量修，猜不准的交给用户确认。
///
/// 只读，不改任何数据——写回由界面上用户点「应用」之后才发生。
/// </summary>
public static class LibraryHealthService
{
    /// <summary>搜索候选时的递归深度上限，太深会把整个盘扫一遍。</summary>
    private const int MaxSearchDepth = 6;

    /// <summary>
    /// 体检。只关心「直接启动 exe」的游戏——平台游戏（Steam/Epic/GOG）走协议启动，
    /// exe 没了也照样能玩，硬给它改路径反而可能改坏。
    /// </summary>
    /// <param name="games">整个游戏库</param>
    /// <param name="searchRoots">用来找候选的根目录（一般是扫描目录 + 旧路径所在盘符）</param>
    /// <param name="guess">是否要猜新路径（关掉就只列缺失，快很多）</param>
    public static List<BrokenGame> Inspect(
        IEnumerable<GameEntry> games,
        IEnumerable<string> searchRoots,
        bool guess = true)
    {
        var broken = new List<BrokenGame>();

        foreach (var entry in games)
        {
            if (entry is null || entry.LaunchKind != LaunchKind.Executable)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.ExecutablePath))
            {
                continue;
            }

            bool exists;
            try
            {
                exists = File.Exists(entry.ExecutablePath);
            }
            catch
            {
                exists = false;
            }

            if (exists)
            {
                continue;
            }

            broken.Add(new BrokenGame
            {
                Entry = entry,
                OldPath = entry.ExecutablePath,
            });
        }

        if (!guess || broken.Count == 0)
        {
            return broken;
        }

        var roots = NormalizeRoots(searchRoots, broken);
        if (roots.Count == 0)
        {
            return broken;
        }

        foreach (var group in broken.GroupBy(b => Path.GetFileName(b.OldPath), StringComparer.OrdinalIgnoreCase))
        {
            var fileName = group.Key;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var candidates = FindByName(roots, fileName);
            if (candidates.Count == 0)
            {
                continue;
            }

            foreach (var item in group)
            {
                var best = PickBest(item.OldPath, candidates);
                if (best is null)
                {
                    continue;
                }

                item.GuessPath = best.Value.Path;
                item.Confidence = best.Value.Confidence;
            }
        }

        return broken
            .OrderByDescending(b => b.Confidence)
            .ThenBy(b => b.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// 在一个目录下批量找这些文件名对应的新位置。
    /// 用于「我把整个 Games 目录从 D 盘挪到 E 盘」这种整批搬家。
    /// </summary>
    /// <param name="root">新根目录</param>
    /// <param name="broken">体检结果</param>
    /// <returns>旧路径 → 新路径</returns>
    public static Dictionary<string, string> RemapUnderRoot(string root, IEnumerable<BrokenGame> broken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return map;
        }

        foreach (var group in broken.GroupBy(b => Path.GetFileName(b.OldPath), StringComparer.OrdinalIgnoreCase))
        {
            var fileName = group.Key;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var candidates = FindByName(new List<string> { root }, fileName);
            if (candidates.Count == 0)
            {
                continue;
            }

            foreach (var item in group)
            {
                var best = PickBest(item.OldPath, candidates);
                if (best is not null)
                {
                    map[item.OldPath] = best.Value.Path;
                }
            }
        }

        return map;
    }

    /// <summary>
    /// 把一条记录指向新的 exe，并让工作目录跟着搬家。
    ///
    /// 工作目录不能简单「等于旧 exe 目录就换掉」——真实情况里它经常是 exe 目录的
    /// 兄弟目录（exe 在 `bin\`，工作目录在 `data\`），也可能是 exe 目录的子目录。
    /// 所以这里按**路径段**算旧 exe 目录与工作目录的最长公共前缀，
    /// 再把这个前缀换成「新 exe 目录里对应的那一段」：
    ///
    ///   旧 exe: D:\Games\A\bin\a.exe        新 exe: E:\Moved\A\bin\a.exe
    ///   工作目录: D:\Games\A\data      →  公共前缀 D:\Games\A → E:\Moved\A  → E:\Moved\A\data
    ///
    /// 完全没有公共前缀时一律不动，避免把用户手填的路径改坏。
    /// </summary>
    public static void ApplyNewPath(GameEntry entry, string newPath)
    {
        if (entry is null || string.IsNullOrWhiteSpace(newPath))
        {
            return;
        }

        var oldExeDir = SafeGetDirectory(entry.ExecutablePath);
        var newExeDir = SafeGetDirectory(newPath);
        var oldWorkDir = entry.WorkingDirectory;

        entry.ExecutablePath = newPath;

        if (!string.IsNullOrWhiteSpace(oldWorkDir) && !string.IsNullOrWhiteSpace(oldExeDir)
            && !string.IsNullOrWhiteSpace(newExeDir))
        {
            var remapped = RemapUnderCommonPrefix(oldExeDir, newExeDir, oldWorkDir);
            if (remapped is not null)
            {
                entry.WorkingDirectory = remapped;
            }
        }

        // 图标是从旧 exe 提取的，路径变了要重新提取，否则卡片上还是旧图标
        entry.IconPath = string.Empty;
    }

    /// <summary>
    /// 按最长公共前缀把 <paramref name="path"/> 从旧根映射到新根，映射不出来返回 null。
    /// 比较时把 / 和 \ 都归一，用户手改配置写成正斜杠也能认。
    /// </summary>
    private static string? RemapUnderCommonPrefix(string oldRoot, string newRoot, string path)
    {
        var oldSegments = SplitSegments(oldRoot);
        var newSegments = SplitSegments(newRoot);
        var pathSegments = SplitSegments(path);

        if (oldSegments.Count == 0 || pathSegments.Count == 0)
        {
            return null;
        }

        // 盘符不同就说明根本不是一棵树，别硬改
        if (!IsSameDrive(oldRoot, path))
        {
            return null;
        }

        var shared = 0;
        while (shared < oldSegments.Count
               && shared < pathSegments.Count
               && string.Equals(oldSegments[shared], pathSegments[shared], StringComparison.OrdinalIgnoreCase))
        {
            shared++;
        }

        if (shared == 0)
        {
            return null;
        }

        // 新根里对应位置的那一段：旧根的第 shared 段前缀，在新根里取同样多的段。
        // 新旧根的公共部分不一定等长（D:\Games\A → E:\Moved\A），所以按「新根段数 - 旧根剩余段数」对齐。
        var newPrefixLength = Math.Max(0, newSegments.Count - (oldSegments.Count - shared));
        if (newPrefixLength > newSegments.Count)
        {
            return null;
        }

        var result = newSegments.Take(newPrefixLength).Concat(pathSegments.Skip(shared)).ToList();
        if (result.Count == 0)
        {
            return null;
        }

        // 盘符要保留成 "E:" 的形式
        var rebuilt = string.Join(Path.DirectorySeparatorChar, result);
        return rebuilt;
    }

    // ── 内部实现 ────────────────────────────────────────────

    private static string SafeGetDirectory(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetDirectoryName(path) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 搜索根目录 = 用户配的扫描目录 + 旧路径所在盘的根。
    /// 换电脑时盘符常常不变（D:\Games → D:\Games2），加盘符根能兜住大部分情况。
    /// </summary>
    private static List<string> NormalizeRoots(IEnumerable<string> searchRoots, List<BrokenGame> broken)
    {
        var roots = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }
            }
            catch
            {
                return;
            }

            if (!roots.Any(r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase)))
            {
                roots.Add(path);
            }
        }

        foreach (var root in searchRoots)
        {
            Add(root);
        }

        foreach (var drive in broken
                     .Select(b => SafeGetDirectory(b.OldPath))
                     .Where(d => d.Length >= 2 && d[1] == ':')
                     .Select(d => d[..2] + Path.DirectorySeparatorChar)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Add(drive);
        }

        return roots;
    }

    /// <summary>
    /// 在所有根目录下找同名 exe。
    /// 用 EnumerateFiles(fileName, AllDirectories) 一次找到底，比自己递归快，
    /// 但要自己控深度——盘符根扫全盘太狠，这里用一个轻量的深度判断兜住。
    /// </summary>
    private static List<string> FindByName(List<string> roots, string fileName)
    {
        var found = new List<string>();

        foreach (var root in roots)
        {
            try
            {
                // 盘符根（"D:\"）只搜前几层，避免把整块盘翻一遍
                var isDriveRoot = root.Length <= 3 && root.EndsWith(Path.DirectorySeparatorChar);
                var depth = isDriveRoot ? 4 : MaxSearchDepth;

                foreach (var file in EnumerateWithDepth(root, fileName, depth))
                {
                    found.Add(file);
                    if (found.Count >= 40)
                    {
                        return found;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Info($"体检时搜索 {root} 失败：{ex.Message}");
            }
        }

        return found;
    }

    private static IEnumerable<string> EnumerateWithDepth(string root, string fileName, int maxDepth)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, fileName, SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            IEnumerable<string> subs;
            try
            {
                subs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var sub in subs)
            {
                // 这些目录里不会有游戏主程序，跳过省时间
                var leaf = Path.GetFileName(sub);
                if (leaf.Equals("Windows", StringComparison.OrdinalIgnoreCase)
                    || leaf.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
                    || leaf.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
                    || leaf.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                    || leaf.Equals("WinSxS", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                queue.Enqueue((sub, depth + 1));
            }
        }
    }

    /// <summary>
    /// 从候选里挑最像的一个：从路径末尾往前比目录层级，重合越多越可信。
    /// 旧路径 D:\Games\EldenRing\Game\eldenring.exe 与新路径 E:\Games\EldenRing\Game\eldenring.exe
    /// 尾部两层（EldenRing\Game）完全一致 → 判定 Exact。
    /// </summary>
    private static (string Path, GuessConfidence Confidence)? PickBest(string oldPath, List<string> candidates)
    {
        var oldSegments = SplitSegments(SafeGetDirectory(oldPath));
        if (oldSegments.Count == 0)
        {
            return candidates.Count == 1
                ? (candidates[0], GuessConfidence.Weak)
                : null;
        }

        string? bestPath = null;
        var bestShared = -1;
        var bestSameDrive = false;

        foreach (var candidate in candidates)
        {
            var segments = SplitSegments(SafeGetDirectory(candidate));

            var shared = 0;
            for (var i = 1; i <= Math.Min(segments.Count, oldSegments.Count); i++)
            {
                if (string.Equals(segments[^i], oldSegments[^i], StringComparison.OrdinalIgnoreCase))
                {
                    shared = i;
                }
                else
                {
                    break;
                }
            }

            var sameDrive = IsSameDrive(oldPath, candidate);

            // 选重合层级最多的；同分时同盘优先
            var better = shared > bestShared
                         || (shared == bestShared && sameDrive && !bestSameDrive);
            if (!better)
            {
                continue;
            }

            bestPath = candidate;
            bestShared = shared;
            bestSameDrive = sameDrive;
        }

        if (bestPath is null || bestShared <= 0)
        {
            // 一个都没重合上：只有唯一候选时才敢猜
            return candidates.Count == 1
                ? (candidates[0], GuessConfidence.Weak)
                : null;
        }

        var confidence = bestShared >= 2
            ? GuessConfidence.Exact
            : GuessConfidence.Likely;

        return (bestPath, confidence);
    }

    private static bool IsSameDrive(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2)
        {
            return false;
        }

        return char.ToUpperInvariant(a[0]) == char.ToUpperInvariant(b[0]) && a[1] == ':' && b[1] == ':';
    }

    private static List<string> SplitSegments(string dir)
        => string.IsNullOrWhiteSpace(dir)
            ? new List<string>()
            : dir.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries)
                .ToList();
}
