using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using GameWall.Services;

namespace GameWall.Services;

/// <summary>
/// 开机自启：写入 HKCU\Software\Microsoft\Windows\CurrentVersion\Run，
/// 不写系统目录、不需要管理员权限，卸载时删掉注册表项即可。
/// </summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GameWall";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch (Exception ex)
        {
            Logger.Error("读取开机自启状态失败", ex);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                {
                    return;
                }

                // 单文件发布时 exe 会自解压，直接写 exe 路径即可
                key.SetValue(ValueName, $"\"{exe}\" --silent", RegistryValueKind.String);
                Logger.Info($"已启用开机自启: {exe}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Logger.Info("已关闭开机自启");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("设置开机自启失败", ex);
        }
    }
}
