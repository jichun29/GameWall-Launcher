using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GameWall.Services;

/// <summary>
/// 把用户拖进来 / 选中的东西统一解析成「一个可启动的 exe 路径」。
///
/// 支持：.exe 直接返回、.lnk 快捷方式解析目标、URL 快捷方式（.url）解析出 exe。
/// </summary>
public static class ShortcutResolver
{
    /// <summary>返回可执行文件路径；无法解析时返回 null。</summary>
    public static string? ResolveToExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();

        return ext switch
        {
            ".exe" => path,
            ".lnk" => ResolveLink(path),
            ".url" => ResolveInternetShortcut(path),
            ".bat" or ".cmd" => path,
            _ => null,
        };
    }

    /// <summary>解析 .lnk，返回它指向的 exe（若指向文件夹或文档则返回 null）。</summary>
    private static string? ResolveLink(string lnkPath)
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

            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            // steam:// 之类的 Internet 快捷方式，TargetPath 会是空
            if (!File.Exists(target))
            {
                return null;
            }

            var targetExt = Path.GetExtension(target).ToLowerInvariant();
            return targetExt is ".exe" or ".bat" or ".cmd" ? target : null;
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

    private static string? ResolveInternetShortcut(string urlPath)
    {
        try
        {
            foreach (var line in File.ReadAllLines(urlPath, Encoding.Default))
            {
                if (!line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var url = line[4..].Trim();

                // 指向本地 exe 的写法
                if (url.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                {
                    var local = Uri.UnescapeDataString(url[8..]).Replace('/', '\\');
                    return File.Exists(local) ? local : null;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"解析 .url 失败: {urlPath}", ex);
        }

        return null;
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
