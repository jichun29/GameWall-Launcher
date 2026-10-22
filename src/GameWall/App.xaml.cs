using System;
using System.IO;
using System.Threading;
using System.Windows;
using GameWall.Models;
using GameWall.Services;
using GameWall.Views;

namespace GameWall;

/// <summary>
/// 应用入口。
///
/// 职责：
///  1. 单实例守卫（再次双击时把已有窗口唤到前台）
///  2. 崩溃兜底日志
///  3. 创建主窗口 + 托盘图标
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>「把已有窗口叫到前台」的信号名 —— 第二个实例 Set 一下就走。</summary>
    private const string ActivateSignalName = @"Global\GameWall.Activate";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateSignal;
    private RegisteredWaitHandle? _activateWait;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;

    /// <summary>为 true 时表示「真的要退出」，退出前由托盘菜单设置。</summary>
    public static bool IsExiting { get; private set; }

    private static string? _trayHintShownPath;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例：命名互斥量。已存在则唤醒旧实例并退出。
        _singleInstanceMutex = new Mutex(true, @"Global\GameWall.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            // 通知已经在跑的那个实例把窗口抬到前台，然后自己退出。
            // 用命名事件而不是 MessageBox：用户再点一次图标，期望的是「窗口出来」，不是「弹个框」。
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateSignalName, out var signal))
                {
                    signal.Set();
                    signal.Dispose();
                }
            }
            catch
            {
                // 打不开信号说明对方还没建好，直接退出即可
            }

            Shutdown();
            return;
        }

        // 监听「再来一个实例」的信号：跨线程回到 UI 线程恢复窗口
        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateSignalName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(
            _activateSignal,
            (_, _) => Dispatcher.BeginInvoke(new Action(RestoreMainWindow)),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        // 未捕获异常写日志再退出，避免静默消失
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("未处理的界面异常", args.Exception);
            MessageBox.Show($"GameWall 遇到意外错误，已记录到日志：\n{AppPaths.LogFile}\n\n{args.Exception.Message}",
                "GameWall", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Logger.Error("未处理的异常", ex);
            }
        };

        base.OnStartup(e);

        AppPaths.EnsureCreated();

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;

        // --silent：开机自启时不弹窗，直接进托盘
        var silent = Array.Exists(e.Args, a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));

        if (silent && _mainWindow.ViewModel.Settings.MinimizeToTray)
        {
            SetupTray();
            // 窗口第一次显示后立刻隐藏，保证句柄已创建
            _mainWindow.Show();
            _mainWindow.Hide();
        }
        else
        {
            SetupTray();
            _mainWindow.Show();
        }
    }

    /// <summary>托盘图标 + 菜单。</summary>
    private void SetupTray()
    {
        if (_trayIcon is not null)
        {
            return;
        }

        try
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "GameWall — 游戏墙",
                Visible = true,
            };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("打开游戏墙", null, (_, _) => RestoreMainWindow());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("添加游戏…", null, (_, _) =>
            {
                RestoreMainWindow();
                _mainWindow?.ViewModel.AddGameCommand.Execute(null);
            });
            menu.Items.Add("扫描游戏目录", null, (_, _) =>
            {
                RestoreMainWindow();
                _mainWindow?.ViewModel.ScanCommand.Execute(null);
            });
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => ExitApp());

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) => RestoreMainWindow();
        }
        catch (Exception ex)
        {
            Logger.Error("创建托盘图标失败", ex);
        }
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        // 优先从程序集自带的图标资源取（多尺寸，256px 也清晰）；
        // 单文件发布后再回退到 exe 关联图标。
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/GameWall.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                var icon = new System.Drawing.Icon(stream, new System.Drawing.Size(32, 32));
                return (System.Drawing.Icon)icon.Clone();
            }
        }
        catch
        {
            // 资源缺失就继续往下走
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch
        {
            // 回退到系统图标
        }

        return System.Drawing.SystemIcons.Application;
    }

    public void RestoreMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = _mainWindow.ViewModel.Settings.Topmost;

        _trayIcon?.ShowBalloonTip(0);
    }

    public void ExitApp()
    {
        IsExiting = true;

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _mainWindow?.Close();
        Shutdown();
    }

    /// <summary>第一次隐藏到托盘时提示一下，之后不再打扰。</summary>
    public static void ShowTrayHintOnce()
    {
        if (_trayHintShownPath is null)
        {
            _trayHintShownPath = Path.Combine(AppPaths.Root, "tray-hint.flag");
            if (File.Exists(_trayHintShownPath))
            {
                return;
            }
        }

        if (File.Exists(_trayHintShownPath))
        {
            return;
        }

        try
        {
            File.WriteAllText(_trayHintShownPath, DateTime.Now.ToString("O"));
        }
        catch
        {
            // 写不了标记文件也不算错误
        }

        if (Current is App app && app._trayIcon is not null)
        {
            app._trayIcon.BalloonTipTitle = "GameWall 还在运行";
            app._trayIcon.BalloonTipText = "窗口已最小化到托盘，双击图标可以重新打开。";
            app._trayIcon.ShowBalloonTip(3000);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        _activateWait?.Unregister(null);
        _activateWait = null;
        _activateSignal?.Dispose();
        _activateSignal = null;

        base.OnExit(e);
    }
}
