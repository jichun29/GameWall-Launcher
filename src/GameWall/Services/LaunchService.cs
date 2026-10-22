using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using GameWall.Models;

namespace GameWall.Services;

/// <summary>
/// 启动游戏。四种启动方式统一在这里处理，失败时抛出可读异常由界面提示。
/// </summary>
public static class LaunchService
{
    /// <summary>启动游戏并更新统计信息。</summary>
    public static void Launch(GameEntry game)
    {
        switch (game.LaunchKind)
        {
            case LaunchKind.Steam:
            case LaunchKind.Epic:
            case LaunchKind.Gog:
            case LaunchKind.Uri:
                OpenUri(game);
                break;

            default:
                StartExecutable(game);
                break;
        }

        game.LaunchCount++;
        game.LastLaunchAt = DateTime.Now;
    }

    private static void StartExecutable(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.ExecutablePath))
        {
            throw new InvalidOperationException("还没有设置可执行文件路径。");
        }

        if (!File.Exists(game.ExecutablePath))
        {
            throw new FileNotFoundException($"找不到文件：{game.ExecutablePath}");
        }

        var workDir = !string.IsNullOrWhiteSpace(game.WorkingDirectory) && Directory.Exists(game.WorkingDirectory)
            ? game.WorkingDirectory
            : Path.GetDirectoryName(game.ExecutablePath) ?? string.Empty;

        var psi = new ProcessStartInfo
        {
            FileName = game.ExecutablePath,
            Arguments = game.Arguments ?? string.Empty,
            WorkingDirectory = workDir,
            // shell 执行：兼容需要 UAC 或依赖 launcher 的游戏
            UseShellExecute = true,
        };

        using var process = Process.Start(psi);
        Logger.Info($"启动游戏: {game.Name} -> {game.ExecutablePath}");
    }

    private static void OpenUri(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.LaunchUri))
        {
            throw new InvalidOperationException("该游戏没有配置平台启动协议。");
        }

        var psi = new ProcessStartInfo
        {
            FileName = game.LaunchUri,
            UseShellExecute = true,
        };

        using var process = Process.Start(psi);
        Logger.Info($"启动平台游戏: {game.Name} -> {game.LaunchUri}");
    }

    /// <summary>在资源管理器中打开所在目录并选中文件。</summary>
    public static void RevealInExplorer(string? path, bool selectFile = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                return;
            }

            if (File.Exists(path))
            {
                var args = selectFile ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
                Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"打开目录失败: {path}", ex);
            throw;
        }
    }

    /// <summary>用系统默认程序打开任意文件/目录（README、存档目录等）。</summary>
    public static void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"打开路径失败: {path}", ex);
            throw;
        }
    }
}
