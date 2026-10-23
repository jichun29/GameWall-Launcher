using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace GameWall.Services
{
    /// <summary>
    /// Steam 本地清单里的「官方游戏名」，纯离线读取，不联网。
    ///
    /// 数据来自 Steam 客户端自己的 appcache\appinfo.vdf —— 这个文件里带着每个 appid 的
    /// 各语言版本名字（schema 里 name_localized 那段，简体中文键是 schinese）。
    /// 我们的用途只有一个：玩家库里的英文名 → 换成官方中文名，
    /// 因为国内用户看到 "It Takes Two" 不如看到「双人成行」直观。
    ///
    /// 文件格式（Valve 的 KeyValues 二进制版，magic 0x07564429）：
    ///   16 字节文件头 → 一串 [appid u32][size u32][data size字节]，读到 magic 0x07564428 为止。
    /// data 内部是若干二进制 KV 段，字符串以 \0 结尾。
    /// 这里不做完整的 KV 解析 —— 那样要处理类型标记、嵌套、压缩，
    /// 而我们要的信息全在明文字符串里，直接在 appid 的字节块里扫 UTF-8 中文即可。
    /// </summary>
    public static class SteamNameService
    {
        private const uint AppInfoMagic = 0x07564429;
        private const uint AppInfoEndMagic = 0x07564428;

        /// <summary>appid → 官方中文名。首次访问时解析，之后常驻。</summary>
        private static Dictionary<string, string>? _cache;

        private static readonly object Gate = new();

        /// <summary>Steam 名字表是否可用（没装 Steam 或缓存没生成时不可用）。</summary>
        public static bool IsAvailable
        {
            get
            {
                EnsureLoaded();
                return _cache is { Count: > 0 };
            }
        }

        /// <summary>
        /// 给一个 appid 找官方中文名。找不到返回 null。
        /// </summary>
        public static string? GetChineseName(string? appId)
        {
            if (string.IsNullOrWhiteSpace(appId))
            {
                return null;
            }

            EnsureLoaded();
            if (_cache is null)
            {
                return null;
            }

            return _cache.TryGetValue(appId.Trim(), out var name) ? name : null;
        }

        /// <summary>
        /// 给一个英文名找官方中文名（用于 Steam 库里的游戏，按名字反查）。
        /// 命中要求归一化后完全相等，避免把 "Sniper Elite 4" 认成 "Sniper Elite 3"。
        /// </summary>
        public static string? GetChineseNameByEnglish(string? englishName)
        {
            if (string.IsNullOrWhiteSpace(englishName))
            {
                return null;
            }

            EnsureLoaded();
            if (_cache is null || _englishToId.Count == 0)
            {
                return null;
            }

            var key = NameSimilarity.Normalize(englishName);
            if (key.Length == 0)
            {
                return null;
            }

            return _englishToId.TryGetValue(key, out var appId) ? GetChineseName(appId) : null;
        }

        /// <summary>英文名（归一化）→ appid，用于名字反查。</summary>
        private static readonly Dictionary<string, string> _englishToId = new(StringComparer.Ordinal);

        private static void EnsureLoaded()
        {
            if (_cache is not null)
            {
                return;
            }

            lock (Gate)
            {
                if (_cache is not null)
                {
                    return;
                }

                _cache = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    var path = FindAppInfoFile();
                    if (path is null)
                    {
                        Logger.Info("没有找到 Steam 的 appinfo.vdf，游戏名保持扫描到的原文");
                        return;
                    }

                    Load(path);
                    Logger.Info($"Steam 官方中文名库已载入 {_cache.Count} 条（{path}）");
                }
                catch (Exception ex)
                {
                    Logger.Error("解析 Steam appinfo.vdf 失败，游戏名保持原文", ex);
                }
            }
        }

        private static string? FindAppInfoFile()
        {
            foreach (var root in EnumerateSteamRoots())
            {
                var candidate = Path.Combine(root, "appcache", "appinfo.vdf");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateSteamRoots()
        {
            // 不能用 yield 直接写在 try/catch 里（C# 不允许在带 catch 的 try 块中 yield），
            // 所以先收集到 List 再统一返回。
            var roots = new List<string>(4);

            // 注册表里的安装路径最准
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string steamPath && !string.IsNullOrWhiteSpace(steamPath))
                {
                    roots.Add(steamPath.Replace('/', '\\'));
                }
            }
            catch
            {
                // 读不到就算了，下面还有兜底路径
            }

            foreach (var guess in new[]
                     {
                         @"C:\Program Files (x86)\Steam",
                         @"C:\Program Files\Steam",
                         @"D:\Program Files (x86)\Steam",
                         @"D:\Steam",
                         @"E:\Steam",
                     })
            {
                if (Directory.Exists(guess))
                {
                    roots.Add(guess);
                }
            }

            return roots;
        }

        private static void Load(string path)
        {
            // 编译器看不到 EnsureLoaded 里已经赋过值，这里取个局部引用消掉 CS8602
            var cache = _cache;
            if (cache is null)
            {
                return;
            }

            var data = File.ReadAllBytes(path);
            if (data.Length < 16)
            {
                return;
            }

            var magic = BitConverter.ToUInt32(data, 0);
            if (magic != AppInfoMagic)
            {
                Logger.Info($"appinfo.vdf 的 magic 是 0x{magic:x8}，不是已知格式，跳过");
                return;
            }

            var pos = 16;
            while (pos + 8 <= data.Length)
            {
                var appId = BitConverter.ToUInt32(data, pos);
                if (appId == AppInfoEndMagic)
                {
                    break;
                }

                var size = BitConverter.ToUInt32(data, pos + 4);
                var start = pos + 8;
                if (appId == 0 || size == 0 || start + (long)size > data.Length)
                {
                    break;
                }

                pos = start + (int)size;

                // 体积异常的条目直接跳过，避免把整张表拖慢
                if (size > 8 * 1024 * 1024)
                {
                    continue;
                }

                var chinese = ExtractChineseName(data, start, (int)size);
                if (chinese is null)
                {
                    continue;
                }

                var id = appId.ToString(CultureInfo.InvariantCulture);
                cache[id] = chinese;

                var english = ExtractEnglishName(data, start, (int)size);
                if (english is not null)
                {
                    var key = NameSimilarity.Normalize(english);
                    if (key.Length > 0)
                    {
                        _englishToId[key] = id;
                    }
                }
            }
        }

        /// <summary>
        /// 在一个 appid 的字节块里找「简体中文游戏名」。
        ///
        /// 结构大致是： ... 01 C4 01 00 00 E9 9B 99 E4 BA BA E6 88 90 E8 A1 8C 00 ...
        ///                        └ 长度前缀 ┘ └──── "雙人成行" ────┘ 00
        /// 接着 01 C3 01 00 00 <双人成行> 00（01 = 有一个键，C3 01 00 00 = 键名长度）
        ///
        /// 这里不硬解这层结构（不同版本字段顺序会变），改用三条启发式：
        ///  1. 扫所有以 \0 结尾的 UTF-8 窜，筛出「含汉字」的；
        ///  2. 同一个 appid 里简体名和繁体名常常一起出现，优先取含简体特征字的；
        ///  3. 长度 2~40 个字符、不含换行 / 控制字符。
        /// 拿不到就返回 null，让上层退回英文名。
        /// </summary>
        private static string? ExtractChineseName(byte[] data, int start, int size)
        {
            string? best = null;

            foreach (var text in EnumerateStrings(data, start, size))
            {
                if (!LooksLikeGameTitle(text))
                {
                    continue;
                }

                if (!ContainsChinese(text))
                {
                    continue;
                }

                // 含简体专用字 / 常用简体字的优先
                if (LooksSimplified(text))
                {
                    return text;
                }

                best ??= text;
            }

            return best;
        }

        private static string? ExtractEnglishName(byte[] data, int start, int size)
        {
            foreach (var text in EnumerateStrings(data, start, size))
            {
                if (!LooksLikeGameTitle(text))
                {
                    continue;
                }

                if (ContainsChinese(text))
                {
                    continue;
                }

                // 纯 ASCII、首字母大写、像标题
                if (text.Length >= 2 && char.IsLetter(text[0]))
                {
                    return text;
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateStrings(byte[] data, int start, int size)
        {
            var end = start + size;
            var pos = start;
            var buffer = new List<byte>(128);

            while (pos < end)
            {
                var b = data[pos];
                if (b == 0)
                {
                    if (buffer.Count >= 2)
                    {
                        var text = TryDecode(buffer);
                        if (text is not null)
                        {
                            yield return text;
                        }
                    }

                    buffer.Clear();
                }
                else
                {
                    buffer.Add(b);
                    if (buffer.Count > 200)
                    {
                        buffer.Clear();
                    }
                }

                pos++;
            }
        }

        private static string? TryDecode(List<byte> buffer)
        {
            try
            {
                return new UTF8Encoding(false, true).GetString(buffer.ToArray());
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool LooksLikeGameTitle(string text)
        {
            if (text.Length < 2 || text.Length > 40)
            {
                return false;
            }

            foreach (var ch in text)
            {
                if (char.IsControl(ch))
                {
                    return false;
                }
            }

            // 明显的非游戏名（注册表路径、URL、GUID、纯数字版本号）
            if (text.Contains(':') || text.Contains("://") || text.Contains('\\'))
            {
                return false;
            }

            return true;
        }

        private static bool ContainsChinese(string text)
        {
            foreach (var ch in text)
            {
                if (ch >= '\u4e00' && ch <= '\u9fff')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>是否更像简体（含简体专用字，或没有繁体特征字）。</summary>
        private static bool LooksSimplified(string text)
        {
            const string simplifiedOnly = "双发动员战场线传说术战争龙门问题东车轮软体视频图画";
            const string traditionalOnly = "雙發動員戰場線傳說術戰爭龍門問題東車輪軟體視頻圖畫";

            var hasSimplified = false;
            var hasTraditional = false;

            foreach (var ch in text)
            {
                if (simplifiedOnly.Contains(ch))
                {
                    hasSimplified = true;
                }

                if (traditionalOnly.Contains(ch))
                {
                    hasTraditional = true;
                }
            }

            return hasSimplified && !hasTraditional;
        }
    }
}
