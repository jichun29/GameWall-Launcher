using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using GameWall.Services;

namespace GameWall.Converters;

/// <summary>
/// 封面 / 图标的解码结果缓存。
///
/// 为什么需要它：卡片模板里的 Image 绑的是图片路径，走转换器取值。
/// 这个控件不虚拟化（WrapPanel 要换行），窗口里有多少游戏就会问多少次。
/// 解码一张 256px 的 PNG 大约 6~12ms，四十多个游戏光解码就要半秒，
/// 而这半秒全压在第一帧上 —— 启动慢的主因之一。
///
/// 缓存键 = 完整路径 + 文件最后写入时间，所以用户替换了封面文件、
/// 或者游戏更新后 exe 图标变了，缓存会自动失效，不会显示旧图。
/// 用 WeakReference 保存位图：内存吃紧时让 GC 能回收，
/// 界面还活着就继续复用，不会额外占用常驻内存。
/// </summary>
internal static class ImageCache
{
    /// <summary>缓存条目上限，超过就整体清一次（这个量级清空比做 LRU 划算）。</summary>
    private const int MaxEntries = 512;

    private sealed class Entry
    {
        public long Stamp;
        public WeakReference<BitmapImage>? Image;
    }

    private static readonly Dictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>
    /// 取（必要时解码）图片。decodeWidth 决定解码时缩到的宽度，
    /// 传 0 表示按原始尺寸解码 —— 卡片永远不需要原图，所以都会给一个宽度。
    /// </summary>
    public static BitmapImage? Get(string path, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        long stamp;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            stamp = info.LastWriteTimeUtc.Ticks;
        }
        catch
        {
            return null;
        }

        var key = decodeWidth + "|" + path;

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var hit) && hit.Stamp == stamp)
            {
                if (hit.Image is not null && hit.Image.TryGetTarget(out var cached))
                {
                    return cached;
                }
            }
        }

        BitmapImage? bmp = null;
        try
        {
            bmp = new BitmapImage();
            bmp.BeginInit();
            // OnLoad：读完立刻关掉文件句柄，用户随时能替换封面
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            if (decodeWidth > 0)
            {
                bmp.DecodePixelWidth = decodeWidth;
            }

            bmp.EndInit();
            bmp.Freeze(); // 冻结后才能跨线程共享，也省掉 WPF 的变更跟踪开销
        }
        catch (Exception ex)
        {
            Logger.Error($"解码图片失败: {path}", ex);
            return null;
        }

        lock (Gate)
        {
            if (Cache.Count >= MaxEntries)
            {
                Cache.Clear();
            }

            Cache[key] = new Entry { Stamp = stamp, Image = new WeakReference<BitmapImage>(bmp) };
        }

        return bmp;
    }

    /// <summary>图片被替换 / 移除后主动失效（编辑窗口换封面时用）。</summary>
    public static void Forget(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (Gate)
        {
            var stale = new List<string>();
            foreach (var key in Cache.Keys)
            {
                if (key.EndsWith("|" + path, StringComparison.OrdinalIgnoreCase))
                {
                    stale.Add(key);
                }
            }

            foreach (var key in stale)
            {
                Cache.Remove(key);
            }
        }
    }
}
