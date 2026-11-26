using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameWall.Models;

namespace GameWall.Services;

/// <summary>导出结果。</summary>
public sealed class LibraryExportResult
{
    public bool Success { get; init; }

    public string Path { get; init; } = string.Empty;

    public int GameCount { get; init; }

    public int AssetCount { get; init; }

    public long Bytes { get; init; }

    public string Message { get; init; } = string.Empty;

    public static LibraryExportResult Fail(string message) => new() { Success = false, Message = message };
}

/// <summary>导入结果。</summary>
public sealed class LibraryImportResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    /// <summary>真正加进库里的条目（已换新 Id、封面已解出来）。</summary>
    public List<GameEntry> Added { get; } = new();

    /// <summary>因为已存在而跳过的数量。</summary>
    public int Skipped { get; init; }

    public static LibraryImportResult Fail(string message) => new() { Success = false, Message = message };
}

/// <summary>
/// 游戏库的打包导出 / 导入。
///
/// 为什么不是「复制 %AppData%\GameWall 文件夹」：那个目录里还有日志、桌面备份、
/// 注册表改动备份，加起来能到几十 MB，而且换机器时那些备份对新机器毫无意义。
/// 这里只打三样东西：
///   library.json   — 游戏列表（路径、标签、备注、启动统计）
///   covers/        — 自定义封面副本
///   icons/         — 从 exe 提取的图标缓存
///
/// 这样一份包通常只有几 MB，换机器时解开就有完整封面墙，不用重新扫一遍。
/// 包内路径统一用正斜杠，跨平台解包工具也不会出错。
/// </summary>
public static class LibraryPackService
{
    /// <summary>包内的清单文件名，导入时用来判断这是不是一个合法的 GameWall 备份。</summary>
    public const string ManifestName = "gamewall-library.json";

    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions PackJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ── 导出 ────────────────────────────────────────────────

    /// <summary>
    /// 把整个库打成一个 zip。
    /// </summary>
    /// <param name="games">要导出的游戏</param>
    /// <param name="destZip">目标 zip 路径</param>
    public static LibraryExportResult Export(IReadOnlyList<GameEntry> games, string destZip)
    {
        if (games is null || games.Count == 0)
        {
            return LibraryExportResult.Fail("游戏库是空的，没有可导出的内容。");
        }

        if (string.IsNullOrWhiteSpace(destZip))
        {
            return LibraryExportResult.Fail("没有指定导出文件。");
        }

        try
        {
            var dir = Path.GetDirectoryName(destZip);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // 先写临时文件再改名：中途失败不会留下一个半截的 zip 让用户以为备份成功了
            var tmp = destZip + ".tmp";
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }

            var assetCount = 0;

            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                var manifest = new PackManifest
                {
                    FormatVersion = FormatVersion,
                    App = "GameWall",
                    AppVersion = AppInfo.Version,
                    ExportedAt = DateTime.Now,
                    GameCount = games.Count,
                    Machine = Environment.MachineName,
                };

                // 封面 / 图标按「原文件名」存，清单里记住映射关系，
                // 导入时按新 Id 重命名，不会互相覆盖。
                //
                // 注意：ZipArchiveMode.Create 下 Entries / GetEntry **不可用**（会抛
                // NotSupportedException），所以重名检测只能自己用一个集合记。
                var files = new List<PackFile>();
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var game in games)
                {
                    var cover = TryAddAsset(zip, game.CoverPath, "covers", usedNames);
                    if (cover is not null)
                    {
                        files.Add(cover);
                        assetCount++;
                    }

                    var icon = TryAddAsset(zip, game.IconPath, "icons", usedNames);
                    if (icon is not null)
                    {
                        files.Add(icon);
                        assetCount++;
                    }
                }

                manifest.Files = files;

                var entry = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
                using var stream = entry.Open();
                JsonSerializer.Serialize(stream, new PackContent
                {
                    Manifest = manifest,
                    Games = games.ToList(),
                }, PackJson);
            }

            if (File.Exists(destZip))
            {
                File.Delete(destZip);
            }

            File.Move(tmp, destZip);

            var size = new FileInfo(destZip).Length;
            Logger.Info($"已导出游戏库：{destZip}（{games.Count} 个游戏，{assetCount} 个资源，{size / 1024} KB）");

            return new LibraryExportResult
            {
                Success = true,
                Path = destZip,
                GameCount = games.Count,
                AssetCount = assetCount,
                Bytes = size,
                Message = "已导出",
            };
        }
        catch (Exception ex)
        {
            Logger.Error("导出游戏库失败", ex);
            return LibraryExportResult.Fail("导出失败：" + ex.Message);
        }
    }

    /// <summary>把一个资源文件塞进 zip，返回映射记录；文件不存在就跳过。</summary>
    private static PackFile? TryAddAsset(ZipArchive zip, string sourcePath, string folder, HashSet<string> usedNames)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return null;
            }

            // 只收自己数据目录里的文件。用户直接指向原图（没复制进 covers）的情况不打包，
            // 否则一个导出包可能把整块硬盘的图片都拖进来
            var normalized = Path.GetFullPath(sourcePath);
            var root = Path.GetFullPath(AppPaths.Root);
            if (!normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var name = Path.GetFileName(normalized);
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var entryPath = folder + "/" + name;

            // 同名已在包里就换个名字（不同游戏可能恰好有同名封面）
            if (!usedNames.Add(entryPath))
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                var ext = Path.GetExtension(name);
                var index = 1;
                do
                {
                    entryPath = $"{folder}/{stem}_{index++}{ext}";
                }
                while (!usedNames.Add(entryPath));
            }

            zip.CreateEntryFromFile(normalized, entryPath, CompressionLevel.Optimal);

            return new PackFile
            {
                Entry = entryPath,
                Kind = folder,
                OriginalPath = sourcePath,
            };
        }
        catch (Exception ex)
        {
            Logger.Warn($"打包资源失败：{sourcePath}，{ex.Message}");
            return null;
        }
    }

    // ── 导入 ────────────────────────────────────────────────

    /// <summary>
    /// 从一个 zip 里读出游戏，解出自带的封面 / 图标，并换上新 Id。
    /// 不做去重 —— 交给调用方（主视图模型知道库里已有哪些）。
    /// </summary>
    public static LibraryImportResult Read(string zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            return LibraryImportResult.Fail("找不到这个文件。");
        }

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);

            var manifestEntry = zip.GetEntry(ManifestName);
            if (manifestEntry is null)
            {
                return LibraryImportResult.Fail(
                    "这不是 GameWall 的库备份文件（缺少 " + ManifestName + "）。\n" +
                    "请选择用「导出游戏库」生成的 .zip。");
            }

            PackContent? content;
            using (var stream = manifestEntry.Open())
            {
                content = JsonSerializer.Deserialize<PackContent>(stream, PackJson);
            }

            if (content?.Games is null || content.Games.Count == 0)
            {
                return LibraryImportResult.Fail("这个备份里没有任何游戏。");
            }

            if (content.Manifest?.FormatVersion > FormatVersion)
            {
                return LibraryImportResult.Fail(
                    $"这个备份是更新版本的 GameWall 导出的（格式 v{content.Manifest.FormatVersion}），当前程序读不了，请先升级。");
            }

            AppPaths.EnsureCreated();

            // 资源解包：包内路径 → 落盘后的新路径
            var extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in content.Manifest?.Files ?? new List<PackFile>())
            {
                var saved = TryExtract(zip, file);
                if (saved is not null)
                {
                    extracted[file.OriginalPath] = saved;
                }
            }

            var result = new LibraryImportResult { Success = true, Message = "读取成功" };

            foreach (var game in content.Games)
            {
                if (game is null || string.IsNullOrWhiteSpace(game.Name))
                {
                    continue;
                }

                // 换新 Id：否则会和库里已有的同 Id 记录在封面文件名上撞车
                game.Id = Guid.NewGuid().ToString("N");

                if (!string.IsNullOrWhiteSpace(game.CoverPath)
                    && extracted.TryGetValue(game.CoverPath, out var newCover))
                {
                    game.CoverPath = newCover;
                }
                else
                {
                    game.CoverPath = string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(game.IconPath)
                    && extracted.TryGetValue(game.IconPath, out var newIcon))
                {
                    game.IconPath = newIcon;
                }
                else
                {
                    game.IconPath = string.Empty;
                }

                result.Added.Add(game);
            }

            Logger.Info($"已从 {zipPath} 读出 {result.Added.Count} 个游戏（{extracted.Count} 个资源）");
            return result;
        }
        catch (Exception ex)
        {
            Logger.Error("读取游戏库备份失败", ex);
            return LibraryImportResult.Fail("读取失败：" + ex.Message);
        }
    }

    /// <summary>把一个资源从包里解到数据目录，按新名字落盘；返回新路径。</summary>
    private static string? TryExtract(ZipArchive zip, PackFile file)
    {
        try
        {
            var entry = zip.GetEntry(file.Entry);
            if (entry is null)
            {
                return null;
            }

            var targetDir = file.Kind == "icons" ? AppPaths.Icons : AppPaths.Covers;
            Directory.CreateDirectory(targetDir);

            // 用「随机前缀 + 原扩展名」：不依赖 entry 里的原文件名，避免路径穿越
            var ext = Path.GetExtension(file.Entry);
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = ".png";
            }

            var target = Path.Combine(targetDir, $"import_{Guid.NewGuid():N}{ext}");
            entry.ExtractToFile(target, overwrite: true);
            return target;
        }
        catch (Exception ex)
        {
            Logger.Warn($"解包资源失败：{file.Entry}，{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 判断一条导入记录是否已经在库里（按启动标识 + 名字比）。
    /// 名字比得严一点：同一款游戏换机器后路径肯定不同，只能靠名字认。
    /// </summary>
    public static bool IsDuplicate(GameEntry candidate, IEnumerable<GameEntry> existing)
    {
        foreach (var item in existing)
        {
            if (item is null)
            {
                continue;
            }

            // 平台游戏看 appid / 启动 URI，最准
            if (!string.IsNullOrWhiteSpace(candidate.LaunchUri)
                && string.Equals(candidate.LaunchUri, item.LaunchUri, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(candidate.Name, item.Name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // ── 包内结构 ────────────────────────────────────────────

    private sealed class PackContent
    {
        public PackManifest? Manifest { get; set; }

        public List<GameEntry> Games { get; set; } = new();
    }

    private sealed class PackManifest
    {
        public int FormatVersion { get; set; }

        public string App { get; set; } = "GameWall";

        public string AppVersion { get; set; } = string.Empty;

        public DateTime ExportedAt { get; set; }

        public int GameCount { get; set; }

        public string Machine { get; set; } = string.Empty;

        public List<PackFile> Files { get; set; } = new();
    }

    private sealed class PackFile
    {
        /// <summary>包内相对路径，例如 covers/abc.png</summary>
        public string Entry { get; set; } = string.Empty;

        /// <summary>covers 或 icons</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>导出时它在原机器上的绝对路径，导入时用它和清单里的路径对上</summary>
        public string OriginalPath { get; set; } = string.Empty;
    }
}
