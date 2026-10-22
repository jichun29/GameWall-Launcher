using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameWall.Services;

/// <summary>
/// 负责所有本地路径的解析与 JSON 读写。
/// 所有数据都存放在 %AppData%\GameWall\ 下，绝不联网上传。
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "GameWall";

    /// <summary>%AppData%\GameWall</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppFolderName);

    /// <summary>封面图片缓存目录</summary>
    public static string Covers => Path.Combine(Root, "covers");

    /// <summary>exe 图标缓存目录</summary>
    public static string Icons => Path.Combine(Root, "icons");

    public static string LibraryFile => Path.Combine(Root, "library.json");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string LogFile => Path.Combine(Root, "gamewall.log");

    /// <summary>确保所有目录存在（首次运行时自动创建）</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Covers);
        Directory.CreateDirectory(Icons);
    }
}

/// <summary>
/// 通用 JSON 持久化助手：写入时先写临时文件再替换，避免断电/崩溃造成存档损坏。
/// </summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>读取 JSON，文件不存在或解析失败时返回 null 并保留坏文件备份。</summary>
    public static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (Exception ex)
        {
            Logger.Error($"读取 {path} 失败", ex);
            TryBackupCorrupt(path);
            return null;
        }
    }

    /// <summary>原子化保存：写 .tmp → 备份旧文件 → 替换。</summary>
    public static bool Save<T>(string path, T value) where T : class
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(value, Options);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);

            if (File.Exists(path))
            {
                var bak = path + ".bak";
                File.Copy(path, bak, overwrite: true);
            }

            // File.Move(overwrite) 在同一卷上是原子替换
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"保存 {path} 失败", ex);
            return false;
        }
    }

    private static void TryBackupCorrupt(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Copy(path, path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
            }
        }
        catch
        {
            // 备份失败不影响主流程
        }
    }
}
