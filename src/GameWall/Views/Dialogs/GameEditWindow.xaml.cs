using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using GameWall.Models;
using GameWall.Services;
using GameWall.ViewModels;
using Microsoft.Win32;

namespace GameWall.Views.Dialogs;

/// <summary>
/// 编辑单个游戏的信息。所有改动先落在 Entry 上，点「保存」才由主窗口落库。
/// </summary>
public partial class GameEditWindow : Window
{
    private readonly GameCardViewModel _card;
    private string _coverPath;
    private string _iconPath;

    public GameEditWindow(GameCardViewModel card)
    {
        InitializeComponent();

        _card = card ?? throw new ArgumentNullException(nameof(card));
        _coverPath = card.Entry.CoverPath;
        _iconPath = card.Entry.IconPath;

        NameBox.Text = card.Name;
        PathBox.Text = card.ExecutablePath;
        SaveBox.Text = card.Entry.SaveDirectory;
        TagsBox.Text = string.Join(", ", card.Tags);
        NotesBox.Text = card.Notes;
        FavoriteBox.IsChecked = card.IsFavorite;

        UpdateCoverPreview();
    }

    /// <summary>用户是否真的换了封面（主窗口据此刷新卡片）。</summary>
    public bool CoverChanged { get; private set; }

    private void UpdateCoverPreview()
    {
        var image = FirstExisting(_coverPath, _iconPath);

        if (image is null)
        {
            CoverPreview.Source = null;
            CoverPreview.Visibility = Visibility.Collapsed;
            PlaceholderGlyph.Visibility = Visibility.Visible;
            PlaceholderGlyph.Text = string.IsNullOrWhiteSpace(NameBox.Text)
                ? "?"
                : NameBox.Text.Trim()[..1].ToUpperInvariant();
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(image, UriKind.Absolute);
            bitmap.DecodePixelWidth = 300;
            bitmap.EndInit();
            bitmap.Freeze();

            CoverPreview.Source = bitmap;
            CoverPreview.Visibility = Visibility.Visible;
            PlaceholderGlyph.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Logger.Error("预览封面失败", ex);
            CoverPreview.Source = null;
            PlaceholderGlyph.Visibility = Visibility.Visible;
        }
    }

    private static string? FirstExisting(params string?[] candidates)
        => candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));

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
                // 忽略拖拽异常
            }
        }
    }

    private void PickCover_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择封面图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            // 复制到 %AppData%\GameWall\covers，避免用户挪动原图后封面丢失
            var imported = IconService.ImportCover(dialog.FileName, _card.Id);
            if (imported is null)
            {
                MessageBox.Show("这张图片无法读取，请换一张试试。", "GameWall",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _coverPath = imported;
            CoverChanged = true;
            UpdateCoverPreview();
        }
        catch (Exception ex)
        {
            Logger.Error("导入封面失败", ex);
            MessageBox.Show("导入封面时出错：" + ex.Message, "GameWall",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UseIcon_Click(object sender, RoutedEventArgs e)
    {
        // 清空自定义封面 → 卡片自动回退到 exe 图标
        _coverPath = string.Empty;
        CoverChanged = true;

        if (string.IsNullOrWhiteSpace(_iconPath) || !File.Exists(_iconPath))
        {
            RefreshIcon();
        }

        UpdateCoverPreview();
    }

    private void ClearCover_Click(object sender, RoutedEventArgs e)
    {
        _coverPath = string.Empty;
        CoverChanged = true;
        UpdateCoverPreview();
    }

    private void RefreshIcon_Click(object sender, RoutedEventArgs e) => RefreshIcon();

    private void RefreshIcon()
    {
        var exe = PathBox.Text;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            MessageBox.Show("找不到可执行文件，无法提取图标。", "GameWall",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var cached = IconService.CacheIcon(exe, _card.Id);
        if (string.IsNullOrWhiteSpace(cached))
        {
            MessageBox.Show("这个 exe 里没有可用的图标。", "GameWall",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _iconPath = cached!;
        CoverChanged = true;
        UpdateCoverPreview();
    }

    private void PickExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择游戏可执行文件",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            PathBox.Text = dialog.FileName;
        }
    }

    private void PickSaveDir_Click(object sender, RoutedEventArgs e)
    {
        // 用 OpenFileDialog 借一个文件夹选择体验太差，这里用 WPF 自带的 OpenFolderDialog（.NET 8 新增）
        var dialog = new OpenFolderDialog
        {
            Title = "选择存档目录",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true)
        {
            SaveBox.Text = dialog.FolderName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("游戏名称不能为空。", "GameWall",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }

        var entry = _card.Entry;
        entry.Name = name;
        entry.ExecutablePath = PathBox.Text.Trim();
        entry.SaveDirectory = SaveBox.Text.Trim();
        entry.Notes = NotesBox.Text.Trim();
        entry.IsFavorite = FavoriteBox.IsChecked == true;
        entry.CoverPath = _coverPath;
        entry.IconPath = _iconPath;
        entry.Tags = TagsBox.Text
            .Split(new[] { ',', '，', ';', '；', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 工作目录跟随 exe 变化
        if (!string.IsNullOrWhiteSpace(entry.ExecutablePath))
        {
            var dir = Path.GetDirectoryName(entry.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                entry.WorkingDirectory = dir;
            }
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
