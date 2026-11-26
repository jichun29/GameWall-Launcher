using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameWall.Models;
using GameWall.Services;
using GameWall.ViewModels;
using Microsoft.Win32;

namespace GameWall.Views.Dialogs;

/// <summary>
/// 路径修复窗口。
///
/// 数据来源是 <see cref="LibraryHealthService.Inspect"/> 的体检结果：
/// 左边列出所有「exe 不在了」的游戏，右边给一个猜出来的新路径。
/// 用户只确认，不用一个一个点「浏览」。
/// </summary>
public partial class RelinkWindow : Window
{
    private readonly ObservableCollection<BrokenGameRow> _rows = new();
    private readonly List<string> _searchRoots;
    private readonly List<GameEntry> _games;

    public RelinkWindow(IEnumerable<GameEntry> games, IEnumerable<string> searchRoots)
    {
        InitializeComponent();

        _games = games?.Where(g => g is not null).ToList() ?? new List<GameEntry>();
        _searchRoots = searchRoots?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList() ?? new List<string>();

        BrokenList.ItemsSource = _rows;
        Loaded += (_, _) => Reload(guess: true);
    }

    /// <summary>应用后要写回的新路径集合（Entry → 新 exe 路径）。</summary>
    public Dictionary<GameEntry, string> Repairs { get; } = new();

    private void Reload(bool guess)
    {
        _rows.Clear();

        var broken = LibraryHealthService.Inspect(_games, _searchRoots, guess);
        foreach (var item in broken)
        {
            var row = new BrokenGameRow(item);
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BrokenGameRow.IsSelected))
                {
                    UpdateCount();
                }
            };
            _rows.Add(row);
        }

        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var fixable = _rows.Count(r => r.HasGuess);
        IntroText.Text = _rows.Count == 0
            ? "库里没有失效的路径。"
            : fixable == 0
                ? $"有 {_rows.Count} 个游戏的 exe 找不到了，但没能自动猜到新位置。可以逐个点「指定…」，或用上面的「按目录批量修复」。"
                : $"有 {_rows.Count} 个游戏的 exe 找不到了，其中 {fixable} 个已经猜出新位置（默认勾选）。确认无误点「应用修复」即可。";

        UpdateCount();
    }

    private void UpdateCount()
    {
        var selected = _rows.Count(r => r.IsSelected && r.HasGuess);
        CountText.Text = $"已选中 {selected} 项可修复";
    }

    // ── 操作 ─────────────────────────────────────────────

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            if (row.HasGuess)
            {
                row.IsSelected = true;
            }
        }
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            row.IsSelected = false;
        }
    }

    private void PickForItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BrokenGameRow row })
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = $"选择「{row.Name}」的可执行文件",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        try
        {
            var dir = Path.GetDirectoryName(row.OldPath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                dialog.InitialDirectory = dir;
            }
        }
        catch
        {
            // 初始目录给不出来不影响选文件
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        row.SetManualPath(dialog.FileName);
        row.IsSelected = true;
        UpdateCount();
    }

    /// <summary>
    /// 整个目录搬家：指定新根目录，把「旧路径下相对旧根目录的那一段」拼到新根上，
    /// 存在就采用。比如旧 D:\Games\EldenRing\Game\x.exe + 新根 E:\Games
    /// → E:\Games\EldenRing\Game\x.exe。
    /// </summary>
    private void BatchRelink_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择游戏现在所在的新目录",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var root = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        var fixedCount = 0;

        foreach (var row in _rows)
        {
            var guess = GuessByRoot(root, row.OldPath);
            if (guess is null)
            {
                continue;
            }

            row.SetManualPath(guess);
            row.IsSelected = true;
            fixedCount++;
        }

        if (fixedCount == 0)
        {
            MessageBox.Show(
                $"在「{root}」下面没找到这些 exe。\n\n请确认选的是游戏目录的上一级（比如游戏在 E:\\Games\\EldenRing，就选 E:\\Games）。",
                "GameWall", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        UpdateCount();
        MessageBox.Show($"按目录对上了 {fixedCount} 个游戏，确认列表无误后点「应用修复」。",
            "GameWall", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// 用「旧路径里的游戏目录名」去新根下找。
    /// 逐级去掉盘符和上层目录试：旧 D:\Games\EldenRing\Game\x.exe，
    /// 依次试 新根\Games\EldenRing\Game\x.exe、新根\EldenRing\Game\x.exe、
    /// 新根\Game\x.exe、新根\x.exe。
    /// </summary>
    private static string? GuessByRoot(string newRoot, string oldPath)
    {
        try
        {
            var oldDir = Path.GetDirectoryName(oldPath);
            var fileName = Path.GetFileName(oldPath);
            if (string.IsNullOrWhiteSpace(oldDir) || string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            var segments = oldDir
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries)
                .Where(s => !(s.Length == 2 && s[1] == ':'))   // 去掉 "D:"
                .ToList();

            for (var skip = 0; skip < segments.Count; skip++)
            {
                var tail = segments.Skip(skip).ToList();
                var candidate = Path.Combine(new[] { newRoot }.Concat(tail).Append(fileName).ToArray());
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows.Where(r => r.IsSelected && r.HasGuess))
        {
            Repairs[row.Entry] = row.GuessPath;
        }

        if (Repairs.Count == 0)
        {
            MessageBox.Show("没有勾选任何可修复的项。", "GameWall",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
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
                // 忽略
            }
        }
    }
}

/// <summary>列表里的一行。包着体检结果，额外提供一个可勾选状态。</summary>
public sealed class BrokenGameRow : ObservableObject
{
    private readonly BrokenGame _item;
    private bool _isSelected;

    public BrokenGameRow(BrokenGame item)
    {
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _isSelected = item.Confidence >= GuessConfidence.Likely;
    }

    public GameEntry Entry => _item.Entry;

    public string Name => _item.Name;

    public string OldPath => _item.OldPath;

    public string GuessPath => _item.GuessPath;

    public bool HasGuess => !string.IsNullOrWhiteSpace(_item.GuessPath);

    public string ConfidenceText => _item.Confidence switch
    {
        GuessConfidence.Exact => "高可信",
        GuessConfidence.Likely => "较可信",
        GuessConfidence.Weak => "待确认",
        _ => "未找到",
    };

    public bool IsSelected
    {
        get => _isSelected && HasGuess;
        set
        {
            if (HasGuess && SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged();
            }
        }
    }

    /// <summary>用户手动指定的路径，直接盖掉猜的那份。</summary>
    public void SetManualPath(string path)
    {
        _item.GuessPath = path;
        _item.Confidence = GuessConfidence.Exact;
        OnPropertyChanged(nameof(GuessPath));
        OnPropertyChanged(nameof(HasGuess));
        OnPropertyChanged(nameof(ConfidenceText));
    }
}
