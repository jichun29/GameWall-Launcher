using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using GameWall.Models;
using GameWall.Services;
using GameWall.ViewModels;

namespace GameWall.Views.Dialogs;

/// <summary>
/// 桌面整理窗口：列出桌面上的快捷方式，把「已经收进游戏墙」的移到备份目录。
/// </summary>
public partial class TidyDesktopWindow : Window
{
    private readonly List<GameEntry> _games;
    private readonly ObservableCollection<TidyShortcutItem> _items = new();
    private bool _suppressToggle;

    /// <param name="games">当前游戏库，用来判断哪些快捷方式已经被收录</param>
    public TidyDesktopWindow(IReadOnlyList<GameEntry> games)
    {
        InitializeComponent();

        _games = games?.ToList() ?? new List<GameEntry>();
        ShortcutList.ItemsSource = _items;

        Loaded += (_, _) => Reload();
    }

    /// <summary>实际被移动走的条数（供调用方提示）。</summary>
    public TidyResult? Result { get; private set; }

    // ── 加载 ────────────────────────────────────────────────

    private void Reload()
    {
        _items.Clear();

        List<DesktopShortcutInfo> found;
        try
        {
            found = DesktopShortcutService.Inspect(_games);
        }
        catch (Exception ex)
        {
            Logger.Error("检查桌面快捷方式失败", ex);
            found = new List<DesktopShortcutInfo>();
        }

        _suppressToggle = true;
        foreach (var info in found)
        {
            var item = new TidyShortcutItem(info)
            {
                // 默认只勾「已收录且可移动」的，别的都要用户自己动手
                IsSelected = info.Selectable,
            };

            item.PropertyChanged += (_, args) =>
            {
                if (!_suppressToggle && args.PropertyName == nameof(TidyShortcutItem.IsSelected))
                {
                    UpdateSummary();
                }
            };

            _items.Add(item);
        }

        _suppressToggle = false;

        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var total = _items.Count;
        var picked = _items.Count(i => i.IsSelected && i.Info.Selectable);
        var alreadyTidy = total - _items.Count(i => i.Info.Selectable || i.Info.LooksLikeTool || i.Info.Unresolved);

        if (total == 0)
        {
            StatusText.Text = "桌面上没有快捷方式。";
        }
        else if (picked == 0)
        {
            StatusText.Text = $"桌面共 {total} 个快捷方式，暂时没有可以收纳的。";
        }
        else
        {
            StatusText.Text = $"桌面共 {total} 个快捷方式，已勾选 {picked} 个准备收纳" +
                              (alreadyTidy > 0 ? $"（另有 {alreadyTidy} 个仍未收录）" : string.Empty) + "。";
        }

        TidyButton.IsEnabled = picked > 0;
        TidyButton.Content = picked > 0 ? $"移到备份文件夹（{picked}）" : "移到备份文件夹";
    }

    // ── 按钮 ────────────────────────────────────────────────

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAll(true);

    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool value)
    {
        _suppressToggle = true;
        foreach (var item in _items)
        {
            // 不允许勾的（公共桌面、没收录、工具）保持原样
            if (item.Info.Selectable)
            {
                item.IsSelected = value;
            }
        }

        _suppressToggle = false;
        UpdateSummary();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void Tidy_Click(object sender, RoutedEventArgs e)
    {
        var targets = _items.Where(i => i.IsSelected && i.Info.Selectable)
            .Select(i => i.Info)
            .ToList();

        if (targets.Count == 0)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"把桌面上的 {targets.Count} 个快捷方式移到备份文件夹？\n\n" +
            $"备份位置：\n{DesktopShortcutService.BackupRoot}\n\n" +
            "文件不会删除，设置里可以随时还原。",
            "整理桌面",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        TidyResult result;
        try
        {
            result = DesktopShortcutService.Tidy(targets);
        }
        catch (Exception ex)
        {
            Logger.Error("整理桌面失败", ex);
            MessageBox.Show("整理失败：" + ex.Message, "GameWall",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = result;

        var lines = new List<string> { $"已收纳 {result.Moved} 个快捷方式。" };
        if (result.Skipped > 0)
        {
            lines.Add($"跳过 {result.Skipped} 个。");
        }

        if (result.Failed > 0)
        {
            lines.Add($"失败 {result.Failed} 个：");
            lines.AddRange(result.Failures.Take(5));
        }

        if (!string.IsNullOrEmpty(result.BackupDirectory))
        {
            lines.Add(string.Empty);
            lines.Add("备份在：" + result.BackupDirectory);
            lines.Add("想找回的话，到「设置 → 桌面整理 → 还原到桌面」。");
        }

        MessageBox.Show(string.Join(Environment.NewLine, lines), "整理桌面",
            MessageBoxButton.OK, MessageBoxImage.Information);

        DialogResult = result.Moved > 0;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // 拖拽被系统取消时忽略
            }
        }
    }
}

/// <summary>把一条桌面快捷方式包一层，好让列表里能勾选。</summary>
public sealed class TidyShortcutItem : ObservableObject
{
    private bool _isSelected;

    public TidyShortcutItem(DesktopShortcutInfo info)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
    }

    public DesktopShortcutInfo Info { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string DisplayName => Info.DisplayName;

    public string TargetText => Info.TargetText;

    public string StatusText => Info.StatusText;

    public bool InLibrary => Info.InLibrary;

    /// <summary>只对「已收录、且不在公共桌面」的条目开放勾选。</summary>
    public bool Selectable => Info.Selectable;
}
