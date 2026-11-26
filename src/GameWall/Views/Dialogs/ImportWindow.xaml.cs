using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameWall.Services;
using GameWall.ViewModels;

namespace GameWall.Views.Dialogs;

/// <summary>
/// 平台导入窗口。列出 Steam / Epic / GOG 已安装游戏，勾选后交给主窗口入库。
/// </summary>
public partial class ImportWindow : Window
{
    private readonly ObservableCollection<SelectablePlatformGame> _items = new();

    public ImportWindow()
    {
        InitializeComponent();

        GameList.ItemsSource = _items;
        Loaded += (_, _) => LoadPlatforms();
    }

    /// <summary>用户勾选的游戏。</summary>
    public List<PlatformGame> SelectedGames { get; private set; } = new();

    private void LoadPlatforms()
    {
        _items.Clear();
        StatusList.Children.Clear();
        StatusPanel.Visibility = Visibility.Collapsed;

        try
        {
            var result = PlatformImportService.ReadAllWithStatus();

            foreach (var game in result.Games)
            {
                var vm = new SelectablePlatformGame(game);
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(SelectablePlatformGame.IsSelected))
                    {
                        UpdateCount();
                    }
                };

                _items.Add(vm);
            }

            RenderStatuses(result.Statuses);
        }
        catch (Exception ex)
        {
            Logger.Error("读取平台游戏列表失败", ex);
            AddStatusLine("读取平台列表失败：" + ex.Message,
                "把运行日志导出发给开发者，能直接看到是哪一步出的问题。", isError: true);
        }

        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
    }

    /// <summary>
    /// 把每个平台的诊断画成一行。全部正常时不显示这块面板，
    /// 免得导个游戏还要先读三段「一切正常」。
    /// </summary>
    private void RenderStatuses(IReadOnlyList<PlatformStatus> statuses)
    {
        // 有问题的排前面，用户一眼先看到「为什么没读到」
        var problems = statuses.Where(s => s.HasProblem).ToList();
        if (problems.Count == 0)
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        StatusPanel.Visibility = Visibility.Visible;

        foreach (var status in problems)
        {
            AddStatusLine(
                $"{status.Platform}：{status.Message}",
                status.Hint,
                isError: status.State == PlatformState.Error);
        }
    }

    private void AddStatusLine(string message, string hint, bool isError)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

        line.Children.Add(new TextBlock
        {
            Text = isError ? "⚠" : "·",
            FontSize = 11.5,
            Margin = new Thickness(0, 1, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = (Brush)FindResource(isError ? "Brush.Warning" : "Brush.TextFaint"),
        });

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource(isError ? "Brush.Warning" : "Brush.Text"),
        });

        if (!string.IsNullOrWhiteSpace(hint))
        {
            text.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("Brush.TextDim"),
            });
        }

        line.Children.Add(text);
        StatusList.Children.Add(line);
    }

    private void UpdateCount()
    {
        var selected = _items.Count(i => i.IsSelected);
        CountText.Text = $"已选中 {selected} / {_items.Count} 个";
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            item.IsSelected = true;
        }
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            item.IsSelected = false;
        }
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => LoadPlatforms();

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        SelectedGames = _items.Where(i => i.IsSelected).Select(i => i.Game).ToList();

        if (SelectedGames.Count == 0)
        {
            MessageBox.Show("至少勾选一个游戏。", "GameWall",
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

/// <summary>把平台游戏包一层，好让列表里能勾选。</summary>
public sealed class SelectablePlatformGame : ObservableObject
{
    private bool _isSelected = true;

    public SelectablePlatformGame(PlatformGame game)
    {
        Game = game ?? throw new ArgumentNullException(nameof(game));
    }

    public PlatformGame Game { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string SuggestedName => Game.SuggestedName;

    public string InstallDir => string.IsNullOrWhiteSpace(Game.InstallDir) ? Game.LaunchUri : Game.InstallDir;

    public string Platform => Game.Platform;
}
