using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
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

        try
        {
            foreach (var game in PlatformImportService.ReadAll())
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
        }
        catch (Exception ex)
        {
            Logger.Error("读取平台游戏列表失败", ex);
        }

        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
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
