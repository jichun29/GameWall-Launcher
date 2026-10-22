using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameWall.Models;

namespace GameWall.Services;

/// <summary>
/// 设置的读写与默认值补全。
/// </summary>
public sealed class SettingsService
{
    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        AppPaths.EnsureCreated();
        var loaded = JsonStore.Load<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();

        // 老版本配置可能缺字段，这里补齐合理默认值
        if (loaded.ScanPaths is null || loaded.ScanPaths.Count == 0)
        {
            loaded.ScanPaths = GameScanner.DefaultScanRoots();
        }

        if (loaded.CardWidth < 120 || loaded.CardWidth > 420)
        {
            loaded.CardWidth = 208;
        }

        // 主题三态归一：System / Dark / Light，非法值回退深色（默认）
        loaded.Theme = ThemeModes.Normalize(loaded.Theme);

        // 强调色留空 = 跟随主题默认色（深色 #4F8CFF / 浅色 #2563EB），
        // 只有用户明确填了非法值才清空回「跟随主题」
        if (!string.IsNullOrWhiteSpace(loaded.AccentColor) && !IsValidHex(loaded.AccentColor))
        {
            loaded.AccentColor = string.Empty;
        }

        // 背景色留空 = 跟随主题窗口底色；填了非法值同样清空
        if (!string.IsNullOrWhiteSpace(loaded.BackgroundColor) && !IsValidHex(loaded.BackgroundColor))
        {
            loaded.BackgroundColor = string.Empty;
        }

        // 旧版本把这两个值写死当默认，升级后一律视为「跟随主题」，
        // 否则用户切到浅色模式时背景还是深色。
        if (string.Equals(loaded.AccentColor, AppSettings.LegacyAccentColor, StringComparison.OrdinalIgnoreCase))
        {
            loaded.AccentColor = string.Empty;
        }

        if (string.Equals(loaded.BackgroundColor, AppSettings.LegacyBackgroundColor, StringComparison.OrdinalIgnoreCase))
        {
            loaded.BackgroundColor = string.Empty;
        }

        Current = loaded;

        // 首次运行（或配置文件被杀掉）时立刻落盘一份，
        // 这样用户一启动就能在 %AppData%\GameWall\ 里看到 settings.json，
        // 也避免「改了设置但进程被杀、配置还没写过」的情况。
        if (!File.Exists(AppPaths.SettingsFile))
        {
            Save();
        }
    }

    public void Save()
    {
        JsonStore.Save(AppPaths.SettingsFile, Current);
    }

    /// <summary>把设置恢复成出厂默认（保留首次运行标记，避免再次弹引导）。</summary>
    public void ResetToDefaults()
    {
        var firstRunDone = Current.FirstRunCompleted;
        Current = new AppSettings
        {
            ScanPaths = GameScanner.DefaultScanRoots(),
            FirstRunCompleted = firstRunDone,
        };
        Save();
    }

    public static bool IsValidHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            return color.A > 0 || true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>过滤出当前真实存在的扫描根目录。</summary>
    public List<string> ExistingScanRoots() =>
        Current.ScanPaths.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).ToList();
}
