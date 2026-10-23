using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GameWall.Services;

/// <summary>封面匹配的结果：命中了哪条线索，方便写日志和给界面提示。</summary>
public sealed class CoverMatch
{
    /// <summary>可以直接给 GameEntry.CoverPath 用的本地图片路径（已经在 covers 目录里）。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>命中方式，例如「游戏目录自带图片」「steam_appid.txt」「Steam 库名匹配」。</summary>
    public string Source { get; init; } = string.Empty;

    public override string ToString() => $"{Source} → {Path}";
}

/// <summary>
/// 给「不是从 Steam 启动」的游戏找封面。
///
/// 纯本地、不联网，按「线索强弱」依次尝试：
///  1. 游戏目录里自带的图片 —— 有些游戏（尤其是独立游戏、汉化版）会把封面放在
///     安装目录里，文件名通常就叫 cover / folder / poster / icon / logo 之类。
///  2. <c>steam_appid.txt</c> —— 几乎所有 Steam 游戏（含破解版）的安装目录里都有
///     一个只写了一行 appid 的文本文件。有了它就能去 Steam 自己的
///     appcache\librarycache 里拿官方竖版封面。
///  3. 双击启动过一次的 exe，注册表里能查到 Steam AppID。
///  4. Steam 库里按游戏名模糊匹配（"JAPANSTIGMATIZED" ↔ "Japan Stigmatized"），
///     用来覆盖「游戏本体在自己建的文件夹里、但 Steam 库里也装了同一款」的情况。
///
/// 全部命中失败时返回 null，调用方继续用占位封面。
/// </summary>
public static class CoverMatcher
{
    /// <summary>游戏目录里常见的封面文件名（不含扩展名），越靠前越像封面。</summary>
    private static readonly string[] LocalCoverStems =
    {
        "cover", "folder", "poster", "boxart", "box_art", "capsule",
        "grid", "library", "logo", "icon", "title", "thumbnail", "thumb",
        "封面", "图标",
    };

    private static readonly string[] LocalCoverExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

    /// <summary>目录里最多往下找几层（有些游戏把图放在 Data\Textures 里）。</summary>
    private const int LocalSearchDepth = 3;

    /// <summary>
    /// 目录图的最小边。低于 200px 的多半是启动图标 / 成就徽章 / 贴图碎片，
    /// 铺到卡片上会糊成一片；上了 200 的即便字节数很小（纯色底图）也是张真图。
    /// </summary>
    private const int MinLocalImageSide = 200;

    /// <summary>
    /// 尝试给一款游戏找封面。
    /// </summary>
    /// <param name="gameName">当前显示名（已经被扫描器纠正过，不是自建文件夹名）</param>
    /// <param name="executablePath">主程序路径，可为空（平台游戏只有协议地址）</param>
    /// <param name="gameId">用来命名落盘文件</param>
    public static CoverMatch? TryResolve(string gameName, string? executablePath, string gameId)
    {
        try
        {
            AppPaths.EnsureCreated();

            var installDir = ResolveInstallDir(executablePath);

            // ── 线索 1：游戏目录自带的图片 ──
            if (!string.IsNullOrWhiteSpace(installDir))
            {
                var local = FindLocalCover(installDir!);
                if (!string.IsNullOrWhiteSpace(local))
                {
                    var saved = Save(gameId, local!, "cover");
                    if (!string.IsNullOrWhiteSpace(saved))
                    {
                        Logger.Info($"封面匹配（游戏目录自带图片）：{gameName} → {System.IO.Path.GetFileName(local!)}");
                        return new CoverMatch { Path = saved!, Source = "游戏目录自带图片" };
                    }
                }
            }

            // ── 线索 2 / 3：拿到 Steam AppID ──
            var appId = ResolveSteamAppId(installDir, executablePath);
            if (!string.IsNullOrWhiteSpace(appId))
            {
                var steamCover = CoverArtService.TryFetchSteamCover($"steam://rungameid/{appId}", gameId);
                if (!string.IsNullOrWhiteSpace(steamCover))
                {
                    Logger.Info($"封面匹配（Steam AppID {appId}）：{gameName}");
                    return new CoverMatch { Path = steamCover!, Source = $"Steam AppID {appId}" };
                }
            }

            // ── 线索 4：按名字去 Steam 库里模糊匹配 ──
            var byName = MatchBySteamLibraryName(gameName);
            if (byName is not null)
            {
                var steamCover = CoverArtService.TryFetchSteamCover($"steam://rungameid/{byName.AppId}", gameId);
                if (!string.IsNullOrWhiteSpace(steamCover))
                {
                    Logger.Info($"封面匹配（Steam 库名匹配「{byName.Name}」）：{gameName}");
                    return new CoverMatch { Path = steamCover!, Source = $"Steam 库名匹配（{byName.Name}）" };
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.Error($"封面匹配失败: {gameName}", ex);
            return null;
        }
    }

    /// <summary>
    /// 从 exe 路径反推安装目录。
    ///
    /// 不能直接取 exe 的父目录：多数游戏的 exe 埋在 Binaries\Win64 里，
    /// 目录里带封面的那一层要往上走好几级。这里爬到第一个「看起来像游戏根目录」的层：
    /// 该层下面出现了 Content / Engine / Data / Binaries 这类标志性目录。
    /// </summary>
    private static string? ResolveInstallDir(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var dir = System.IO.Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return null;
        }

        // 先把 exe 目录往上 4 层收集起来（含自身），再从外往里挑最合适的那一层
        var chain = new List<string>();
        var current = dir!;
        for (var i = 0; i < 4; i++)
        {
            chain.Add(current);

            var parent = System.IO.Path.GetDirectoryName(current);

            // 走到盘符根就停 —— 盘符根上随便一个同名目录（E:\Game、E:\Data 之类）
            // 都会让 LooksLikeGameRoot 误判成"这就是游戏目录"，
            // 结果拿整块盘去找封面，既慢又错。
            if (string.IsNullOrWhiteSpace(parent) || IsDriveRoot(current))
            {
                break;
            }

            current = parent!;
        }

        // 最可靠的信号：steam_appid.txt 所在的那层就是安装根。
        // 它一般放在游戏根目录，也可能跟 exe 放在一起。
        foreach (var level in chain)
        {
            if (HasSteamAppId(level))
            {
                return level;
            }
        }

        // 退一步：取「最外层」那个像游戏目录的层。
        // 取最外层而不是最内层，是因为封面图通常放在游戏根目录，
        // 而 Binaries\Win64 这种内层目录里什么图都没有。
        // 盘符根一律不参与判断。
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (!IsDriveRoot(chain[i]) && LooksLikeGameRoot(chain[i]))
            {
                return chain[i];
            }
        }

        return dir;
    }

    private static bool HasSteamAppId(string dir)
    {
        try
        {
            return File.Exists(System.IO.Path.Combine(dir, "steam_appid.txt"))
                || File.Exists(System.IO.Path.Combine(dir, "SteamAppId.txt"));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDriveRoot(string dir)
    {
        var trimmed = dir.TrimEnd('\\', '/');
        return trimmed.Length <= 2;
    }

    private static bool LooksLikeGameRoot(string dir)
    {
        try
        {
            foreach (var probe in new[] { "Content", "Engine", "Data", "Binaries", "Bin", "Game", "Assets" })
            {
                if (Directory.Exists(System.IO.Path.Combine(dir, probe)))
                {
                    return true;
                }
            }

            // 目录里直接放着 exe 和一堆资源文件，也算游戏根
            var hasExe = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Any();
            var hasData = Directory.EnumerateDirectories(dir, "*_Data", SearchOption.TopDirectoryOnly).Any();
            return hasExe && hasData;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在游戏目录里找一张能当封面的图。
    /// </summary>
    private static string? FindLocalCover(string installDir)    {
        var candidates = new List<(string Path, int Rank, long Size)>();

        try
        {
            Walk(installDir, 0);

            void Walk(string dir, int depth)
            {
                if (depth > LocalSearchDepth)
                {
                    return;
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    return;
                }

                foreach (var file in files)
                {
                    var ext = System.IO.Path.GetExtension(file);
                    if (!LocalCoverExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var stem = System.IO.Path.GetFileNameWithoutExtension(file).ToLowerInvariant();

                    // 文件名必须命中白名单词根，避免把贴图碎片当封面
                    var rank = Array.FindIndex(LocalCoverStems, s => stem == s);
                    if (rank < 0)
                    {
                        rank = Array.FindIndex(LocalCoverStems, s => stem.StartsWith(s, StringComparison.Ordinal));
                        if (rank < 0)
                        {
                            continue;
                        }

                        // 前缀式命中权重低一档
                        rank += LocalCoverStems.Length;
                    }

                    long size;
                    try
                    {
                        size = new FileInfo(file).Length;
                    }
                    catch
                    {
                        continue;
                    }

                    if (size <= 0)
                    {
                        continue;
                    }

                    // 只看字节数不够可靠：一张 600×900 的纯色封面完全可能只有 5 KB，
                    // 而 256×256 的图标也可能有 20 KB。真正该判的是像素尺寸和长宽比。
                    var shape = ReadImageShape(file);
                    if (shape is null || !IsUsableCoverSize(shape.Value.Width, shape.Value.Height))
                    {
                        continue;
                    }

                    // 竖版图优先（封面通常是 2:3 的立式图），横版图降两档
                    var portraitBonus = shape.Value.Height >= shape.Value.Width ? 0 : 200;

                    candidates.Add((file, rank + depth * 100 + portraitBonus, size));
                }

                IEnumerable<string> subs;
                try
                {
                    subs = Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    return;
                }

                foreach (var sub in subs)
                {
                    var name = System.IO.Path.GetFileName(sub).ToLowerInvariant();
                    // 这些目录里的图是贴图资源，不是封面
                    if (name is "engine" or "content" or "textures" or "texture" or "materials"
                        or "shaders" or "audio" or "sound" or "movies" or "video" or "localization"
                        or "pak" or "paks" or "_commonredist" or "redist" or "directx" or "dotnet")
                    {
                        continue;
                    }

                    Walk(sub, depth + 1);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"查找本地封面图片失败: {installDir}", ex);
        }

        return candidates
            .OrderBy(c => c.Rank)
            .ThenByDescending(c => c.Size)
            .Select(c => c.Path)
            .FirstOrDefault();
    }

    /// <summary>
    /// 只读图片文件头拿到像素尺寸，不解码整张图。
    ///
    /// 在游戏目录里搜图时这很关键：一个 3A 大作的目录里可能有上千张 png，
    /// 挨个完整解码会卡好几秒，而且没有任何必要 —— 判断「够不够大」只要
    /// 头几个字节里的宽高。
    /// </summary>
    private static (int Width, int Height)? ReadImageShape(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[32];
            if (stream.Read(head, 0, head.Length) < 24)
            {
                return null;
            }

            // PNG: 8 字节签名 + 4 字节长度 + "IHDR" + 4 字节宽 + 4 字节高（大端）
            if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
                && head[12] == 0x49 && head[13] == 0x48 && head[14] == 0x44 && head[15] == 0x52)
            {
                var w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
                var h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
                return (w, h);
            }

            // BMP: 文件头 14 字节 + DIB 头，宽高在偏移 18 / 22（小端，高度可能为负表示自上而下）
            if (head[0] == 0x42 && head[1] == 0x4D)
            {
                stream.Position = 18;
                var buf = new byte[8];
                if (stream.Read(buf, 0, buf.Length) < 8)
                {
                    return null;
                }

                var w = BitConverter.ToInt32(buf, 0);
                var h = BitConverter.ToInt32(buf, 4);
                return (w, Math.Abs(h));
            }

            // JPEG: 扫 SOFn 段，段里第 3~4 字节是高、第 5~6 字节是宽
            if (head[0] == 0xFF && head[1] == 0xD8)
            {
                stream.Position = 2;
                while (true)
                {
                    int marker = stream.ReadByte();
                    if (marker < 0)
                    {
                        return null;
                    }

                    if (marker != 0xFF)
                    {
                        continue;
                    }

                    int code = stream.ReadByte();
                    if (code < 0)
                    {
                        return null;
                    }

                    // 填充字节
                    if (code == 0xFF)
                    {
                        stream.Position--;
                        continue;
                    }

                    // 无长度的标记
                    if (code is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7)
                    {
                        continue;
                    }

                    var hi = stream.ReadByte();
                    var lo = stream.ReadByte();
                    if (hi < 0 || lo < 0)
                    {
                        return null;
                    }

                    var length = (hi << 8) | lo;
                    if (length < 2)
                    {
                        return null;
                    }

                    // SOF0..SOF15，排除 DHT(C4) / JPG(C8) / DAC(CC)
                    if (code is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    {
                        var sof = new byte[5];
                        if (stream.Read(sof, 0, sof.Length) < 5)
                        {
                            return null;
                        }

                        var h = (sof[1] << 8) | sof[2];
                        var w = (sof[3] << 8) | sof[4];
                        return (w, h);
                    }

                    stream.Position += length - 2;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 这张图适不适合当封面。
    ///
    /// 两条硬性要求：最短边 ≥ 200px（再小铺到 150px 宽的卡片上就是糊的），
    /// 长宽比不能极端（横 3:1 的横幅、竖 5:1 的侧边条都不是封面）。
    /// </summary>
    private static bool IsUsableCoverSize(int width, int height)
    {
        if (width < MinLocalImageSide || height < MinLocalImageSide)
        {
            return false;
        }

        var ratio = (double)Math.Max(width, height) / Math.Min(width, height);
        return ratio <= 2.6;
    }

    /// <summary>
    /// 拿 Steam AppID：先看 <c>steam_appid.txt</c>，再看注册表里的 AppIDMark。
    /// </summary>
    private static string? ResolveSteamAppId(string? installDir, string? executablePath)
    {
        if (!string.IsNullOrWhiteSpace(installDir))
        {
            var probes = new[]
            {
                System.IO.Path.Combine(installDir!, "steam_appid.txt"),
                System.IO.Path.Combine(installDir!, "SteamAppId.txt"),
            };

            foreach (var probe in probes)
            {
                try
                {
                    if (!File.Exists(probe))
                    {
                        continue;
                    }

                    var text = File.ReadAllText(probe).Trim();
                    var match = Regex.Match(text, @"\d{3,}");
                    if (match.Success)
                    {
                        return match.Value;
                    }
                }
                catch
                {
                    // 读不到就换下一个线索
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 按游戏名去本机 Steam 已安装库里模糊匹配。
    ///
    /// 典型场景：用户的游戏是绿色版，装在 E:\Games\XIAORIZI\JAPANSTIGMATIZED 下，
    /// 而 Steam 库里也装着同一款 —— 目录名去掉空格/符号后和 Steam 名字一致就能对上。
    /// </summary>
    private static PlatformGame? MatchBySteamLibraryName(string gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName))
        {
            return null;
        }

        var target = NameSimilarity.Normalize(gameName);
        if (target.Length < 3)
        {
            return null;
        }

        List<PlatformGame> steamGames;
        try
        {
            steamGames = PlatformImportService.ReadAll()
                .Where(g => g.Platform.Equals("Steam", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return null;
        }

        PlatformGame? best = null;
        var bestScore = 0.0;

        foreach (var game in steamGames)
        {
            foreach (var probe in new[] { game.Name, game.SuggestedName, System.IO.Path.GetFileName(game.InstallDir) })
            {
                if (string.IsNullOrWhiteSpace(probe))
                {
                    continue;
                }

                var normalized = NameSimilarity.Normalize(probe);
                if (normalized.Length < 3)
                {
                    continue;
                }

                var score = NameSimilarity.Score(gameName, probe);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = game;
                }
            }
        }

        // 0.72 是实测阈值：能对上 "JAPANSTIGMATIZED" ↔ "Japan Stigmatized"，
        // 又不会把 "Sniper Elite 4" 和 "Sniper Elite 3" 认成一款。
        return bestScore >= 0.72 ? best : null;
    }

    /// <summary>把找到的图片复制进 covers 目录，返回落盘路径。</summary>
    private static string? Save(string gameId, string sourcePath, string suffix)
    {
        try
        {
            var ext = System.IO.Path.GetExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = ".jpg";
            }

            var dest = System.IO.Path.Combine(AppPaths.Covers, $"{IconService.Sanitize(gameId)}_{suffix}{ext}");
            File.Copy(sourcePath, dest, overwrite: true);
            return dest;
        }
        catch (Exception ex)
        {
            Logger.Error($"保存封面失败: {sourcePath}", ex);
            return null;
        }
    }
}
