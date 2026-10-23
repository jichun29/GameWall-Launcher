using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameWall.Services;

namespace GameWall.Views.Dialogs;

/// <summary>
/// 运行日志窗口。
///
/// 之所以要做这个：玩家遇到「扫出来一堆不是游戏的东西」「某个游戏没封面」这类问题，
/// 光靠截图很难复现，得把扫描过程本身记下来。这里把 Logger 的内存副本列出来，
/// 支持只看错误、复制、导出成 txt，直接发给开发者就能定位。
/// </summary>
public partial class LogWindow : Window
{
    private readonly ObservableCollection<LogEntry> _items = new();
    private bool _subscribed;

    public LogWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _items;

        Reload();

        // 窗口开着的时候有新日志就实时追加，边操作边看效果
        Logger.Written += OnLogWritten;
        _subscribed = true;

        Closed += (_, _) => Detach();
    }

    private void OnLogWritten(LogEntry entry)
    {
        // Logger 可能在后台线程触发（扫描是 Task.Run），必须切回 UI 线程
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnLogWritten(entry)));
            return;
        }

        if (ErrorOnlyBox.IsChecked == true && !entry.IsError)
        {
            return;
        }

        _items.Add(entry);
        UpdateCount();

        if (LogList.Items.Count > 0 && AutoScroll())
        {
            LogList.ScrollIntoView(_items[^1]);
        }
    }

    /// <summary>用户往上翻着看历史时别硬拽到底部。</summary>
    private bool AutoScroll()
    {
        var scroll = FindScrollViewer(LogList);
        if (scroll is null) { return true; }
        return scroll.VerticalOffset >= scroll.ScrollableHeight - 40;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) { return viewer; }
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) { return found; }
        }

        return null;
    }

    private void Reload()
    {
        _items.Clear();

        var entries = Logger.Recent;
        var errorOnly = ErrorOnlyBox.IsChecked == true;
        foreach (var entry in entries)
        {
            if (errorOnly && !entry.IsError)
            {
                continue;
            }

            _items.Add(entry);
        }

        UpdateCount();
    }

    private void UpdateCount()
    {
        var total = Logger.Recent.Count;
        var errors = Logger.Recent.Count(e => e.IsError);
        CountText.Text = errors > 0
            ? $"显示 {_items.Count} 条（共 {total} 条，其中 {errors} 条错误）"
            : $"显示 {_items.Count} 条（共 {total} 条）";
        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Detach()
    {
        if (!_subscribed) { return; }
        Logger.Written -= OnLogWritten;
        _subscribed = false;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        // 构造阶段控件还没建好，CheckBox 初始赋值也会触发
        if (!IsLoaded) { return; }
        Reload();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0)
        {
            MessageBox.Show(this, "现在没有可以复制的日志。", "运行日志",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var text = Logger.ExportText(_items);

            // 剪贴板是全局独占资源：别的程序（输入法、聊天工具、剪贴板同步）
            // 正好持有它时会抛 CLIPBRD_E_CANT_OPEN (0x800401D0)。
            // 这种情况重试几次基本都能成，直接报错让用户「改用导出」太不友好。
            if (!TrySetClipboardText(text))
            {
                Logger.Warn("剪贴板被其它程序占用，复制失败");
                MessageBox.Show(this, "剪贴板正被其它程序占用，稍后再试一次，或者改用「导出为文本…」。", "运行日志",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(this, $"已复制 {_items.Count} 条日志到剪贴板。", "运行日志",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Error("复制日志失败", ex);
            MessageBox.Show(this, "复制失败，可以改用「导出为文本…」。", "运行日志",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>重试几次写剪贴板，全部失败返回 false（不抛异常）。</summary>
    private static bool TrySetClipboardText(string text)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(60);
            }
        }

        return false;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0)
        {
            MessageBox.Show(this, "现在没有可以导出的日志。", "运行日志",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出运行日志",
            Filter = "文本文件 (*.txt)|*.txt",
            DefaultExt = ".txt",
            FileName = $"GameWall-日志-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };

        if (dialog.ShowDialog(this) != true) { return; }

        try
        {
            // 报告开头带一段环境信息，别人拿到不用再问「你什么版本什么系统」
            var header = new StringBuilder();
            header.AppendLine($"GameWall {Services.AppInfo.VersionLabel}  ·  {Services.AppInfo.RuntimeSummary}");
            header.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            header.AppendLine($"系统：{Environment.OSVersion}  ·  {(Environment.Is64BitProcess ? "x64" : "x86")}");
            header.AppendLine(new string('-', 60));
            header.AppendLine();

            File.WriteAllText(dialog.FileName, header + Logger.ExportText(_items),
                new UTF8Encoding(true));

            Logger.Info($"已导出运行日志：{dialog.FileName}");

            var open = MessageBox.Show(this, "导出成功，现在打开这个文件吗？", "运行日志",
                MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (open == MessageBoxResult.Yes)
            {
                LaunchService.OpenPath(dialog.FileName);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("导出日志失败", ex);
            MessageBox.Show(this, "导出失败：" + ex.Message, "运行日志",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(AppPaths.LogFile))
            {
                MessageBox.Show(this, "日志文件还没生成（说明目前没有记录）。", "运行日志",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            LaunchService.RevealInExplorer(AppPaths.LogFile);
        }
        catch (Exception ex)
        {
            Logger.Error("打开日志文件失败", ex);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) { return; }

        var confirm = MessageBox.Show(this,
            "只清空这个窗口里的记录，磁盘上的日志文件会保留。确定清空吗？",
            "运行日志", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) { return; }

        Logger.ClearMemory();
        Reload();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInteractive(source)) { return; }
        if (e.ButtonState != MouseButtonState.Pressed) { return; }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已经松开，忽略
        }
    }

    private static bool IsInteractive(DependencyObject source)
    {
        var node = source;
        while (node is not null)
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.TextBox
                or System.Windows.Controls.ComboBox
                or System.Windows.Controls.Primitives.ScrollBar
                or CheckBox)
            {
                return true;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }
}
