using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using GameWall.Services;

namespace GameWall.Services;

/// <summary>
/// 图标 / 封面提取。
///
/// 关键点：exe 里通常带 16/32/48 的小图标，直接 ExtractAssociatedIcon 只能拿到 32×32，
/// 铺到「大图墙」上会糊成一片。这里优先走 Windows Shell 的 IShellItemImageFactory，
/// 它会按请求尺寸返回最高质量的缩略图（最高 256×256），拿不到时才降级用 System.Drawing。
/// </summary>
public static class IconService
{
    private const int SIIGBF_RESIZETOFIT = 0x00;
    private const int SIIGBF_BIGGERSIZEOK = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object shellItem);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// 从任意 shell 对象（exe / lnk / 自定义图标资源）取出位图。
    /// 该方法内部已做异常兜底，失败返回 null。
    /// </summary>
    public static BitmapSource? GetShellImage(string path, int size = 256)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, iid, out var item);
            if (item is not IShellItemImageFactory factory)
            {
                return null;
            }

            factory.GetImage(new NativeSize { cx = size, cy = size }, SIIGBF_BIGGERSIZEOK | SIIGBF_RESIZETOFIT, out hBitmap);
            if (hBitmap == IntPtr.Zero)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            Logger.Error($"Shell 取图失败: {path}", ex);
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                DeleteObject(hBitmap);
            }
        }
    }

    /// <summary>
    /// 用 System.Drawing 抓取 exe 内嵌图标，作为 Shell 取图的降级方案。
    /// </summary>
    public static BitmapSource? GetEmbeddedIcon(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            Logger.Error($"ExtractAssociatedIcon 失败: {path}", ex);
            return null;
        }
    }

    /// <summary>
    /// 取图标并把结果缓存成 PNG，返回缓存文件路径。
    /// 缓存名基于路径 + 修改时间哈希，exe 更新后会自动重新提取。
    /// </summary>
    public static string? CacheIcon(string targetPath, string cacheKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
            {
                return null;
            }

            AppPaths.EnsureCreated();
            var stamp = File.GetLastWriteTimeUtc(targetPath).Ticks;
            var name = $"{Sanitize(cacheKey)}_{stamp}.png";
            var cachePath = Path.Combine(AppPaths.Icons, name);

            if (File.Exists(cachePath))
            {
                return cachePath;
            }

            var bmp = GetShellImage(targetPath) ?? GetEmbeddedIcon(targetPath);
            if (bmp is null)
            {
                return null;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(cachePath);
            encoder.Save(fs);

            CleanupOldIcons(cacheKey, cachePath);
            return cachePath;
        }
        catch (Exception ex)
        {
            Logger.Error($"缓存图标失败: {targetPath}", ex);
            return null;
        }
    }

    /// <summary>同一游戏的多份旧图标缓存直接删掉，避免 icons 目录膨胀。</summary>
    private static void CleanupOldIcons(string cacheKey, string keepPath)
    {
        try
        {
            var prefix = Sanitize(cacheKey) + "_";
            foreach (var file in Directory.EnumerateFiles(AppPaths.Icons, prefix + "*.png"))
            {
                if (!string.Equals(file, keepPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 清理失败不影响功能
        }
    }

    /// <summary>路径里的非法字符替换成下划线，避免生成非法文件名。</summary>
    public static string Sanitize(string text)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            text = text.Replace(c, '_');
        }

        return text.Length > 64 ? text[^64..] : text;
    }

    /// <summary>把用户选择的封面图复制进数据目录，返回副本路径。</summary>
    public static string? ImportCover(string sourcePath, string gameId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return null;
            }

            AppPaths.EnsureCreated();
            var ext = Path.GetExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = ".png";
            }

            var dest = Path.Combine(AppPaths.Covers, $"{gameId}{ext}");
            File.Copy(sourcePath, dest, overwrite: true);
            return dest;
        }
        catch (Exception ex)
        {
            Logger.Error($"导入封面失败: {sourcePath}", ex);
            return null;
        }
    }
}
