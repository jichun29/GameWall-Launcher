using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GameWall.Models;
using GameWall.Services;

namespace GameWall.Views.Dialogs;

/// <summary>
/// 设置窗口。改动先写在内存副本上，点「保存」才回写到外面那份 AppSettings。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _target;
    private AppSettings _working;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();

        _target = settings ?? throw new ArgumentNullException(nameof(settings));
        _working = CloneOf(settings);

        BindToUi();
        LivePreview();
    }

    // ══════════════ 初始化 ══════════════

    private static AppSettings CloneOf(AppSettings source)
    {
        // 直接走一次 JSON 往返，字段增减时不用改克隆代码
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    private void BindToUi()
    {
        // 主题三态：明确勾一个，不要靠「不是浅色就是深色」这种二选一推断
        var mode = ThemeModes.Normalize(_working.Theme);
        DarkRadio.IsChecked = mode == ThemeModes.Dark;
        LightRadio.IsChecked = mode == ThemeModes.Light;
        SystemRadio.IsChecked = mode == ThemeModes.System;

        UpdateAccentSwatch(_working.AccentColor);

        CardWidthSlider.Value = _working.CardWidth;
        CardWidthText.Text = $"{_working.CardWidth:0} px";
        ShowLabelsBox.IsChecked = _working.ShowLabels;

        BackgroundPathBox.Text = _working.BackgroundImagePath;
        BgOpacitySlider.Value = _working.BackgroundImageOpacity;
        BgOpacityText.Text = $"{_working.BackgroundImageOpacity:P0}";
        UpdateBgColorSwatch(_working.BackgroundColor);

        TopmostBox.IsChecked = _working.Topmost;
        FullscreenBox.IsChecked = _working.StartFullscreen;
        TrayBox.IsChecked = _working.MinimizeToTray;
        AutoStartBox.IsChecked = _working.AutoStart;
        ConfirmRemoveBox.IsChecked = _working.ConfirmOnRemove;
        MinimizeOnLaunchBox.IsChecked = _working.MinimizeOnGameLaunch;

        PathList.ItemsSource = _working.ScanPaths;
        UpdatePathHint();

        DepthSlider.Value = _working.ScanDepth;
        DepthText.Text = $"{_working.ScanDepth} 层";

        DataPathText.Text = "数据目录：" + AppPaths.Root;

        UpdateSystemThemeHint();
    }

    /// <summary>「跟随系统」旁边的小字：告诉用户系统当前到底是深色还是浅色。</summary>
    private void UpdateSystemThemeHint()
    {
        var isLight = SystemThemeWatcher.IsSystemLight();
        SystemThemeHint.Text = isLight
            ? "系统当前是浅色，跟随系统时界面会用浅色主题。"
            : "系统当前是深色，跟随系统时界面会用深色主题。";
    }

    /// <summary>主题 / 强调色 / 背景这类改动实时预览，取消时再由主窗口恢复。</summary>
    private void LivePreview()
    {
        ThemeManager.Apply(_working.Theme);
        ThemeManager.ApplyAccent(_working.AccentColor);
    }

    // ══════════════ 外观事件 ══════════════

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        // 构造期间控件还没建好，容错处理
        if (_working is null)
        {
            return;
        }

        if (SystemRadio.IsChecked == true)
        {
            _working.Theme = ThemeModes.System;
        }
        else if (LightRadio.IsChecked == true)
        {
            _working.Theme = ThemeModes.Light;
        }
        else
        {
            _working.Theme = ThemeModes.Dark;
        }

        ThemeManager.Apply(_working.Theme);
        UpdateSystemThemeHint();
    }

    private void PickAccent_Click(object sender, RoutedEventArgs e)
    {
        var picked = PickColor(_working.AccentColor);
        if (picked is null)
        {
            return;
        }

        _working.AccentColor = picked;
        UpdateAccentSwatch(picked);
        ThemeManager.ApplyAccent(picked);
    }

    /// <summary>「跟随主题」：清空自定义色，让主题字典自带的强调色生效。</summary>
    private void ResetAccent_Click(object sender, RoutedEventArgs e)
    {
        _working.AccentColor = string.Empty;
        UpdateAccentSwatch(string.Empty);
        ThemeManager.ApplyAccent(string.Empty);
    }

    /// <summary>显示强调色色块。传空字符串表示「跟随主题」，此时展示当前主题的实际颜色。</summary>
    private void UpdateAccentSwatch(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            var actual = ThemeManager.CurrentTheme == ThemeModes.Light ? "#2563EB" : "#4F8CFF";
            AccentText.Text = $"跟随主题（{actual}）";
            AccentSwatch.Background = MakeBrush(actual);
            return;
        }

        AccentText.Text = hex;
        AccentSwatch.Background = MakeBrush(hex);
    }

    /// <summary>显示背景色色块。传空字符串表示「跟随主题」。</summary>
    private void UpdateBgColorSwatch(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            var actual = ThemeManager.CurrentTheme == ThemeModes.Light ? "#F4F6FA" : "#101318";
            BgColorText.Text = $"跟随主题（{actual}）";
            BgColorSwatch.Background = MakeBrush(actual);
            return;
        }

        BgColorText.Text = hex;
        BgColorSwatch.Background = MakeBrush(hex);
    }

    private static Brush MakeBrush(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Gray;
        }
    }

    private void CardWidth_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_working is null || CardWidthText is null)
        {
            return;
        }

        _working.CardWidth = Math.Round(e.NewValue);
        CardWidthText.Text = $"{_working.CardWidth:0} px";
    }

    private void BgOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_working is null || BgOpacityText is null)
        {
            return;
        }

        _working.BackgroundImageOpacity = Math.Round(e.NewValue, 2);
        BgOpacityText.Text = $"{_working.BackgroundImageOpacity:P0}";
    }

    private void Depth_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_working is null || DepthText is null)
        {
            return;
        }

        _working.ScanDepth = (int)Math.Round(e.NewValue);
        DepthText.Text = $"{_working.ScanDepth} 层";
    }

    private void PickBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        // 同样复制一份到数据目录，避免用户删掉原图后背景消失
        var copied = CopyBackground(dialog.FileName);
        _working.BackgroundImagePath = copied ?? dialog.FileName;
        BackgroundPathBox.Text = _working.BackgroundImagePath;
    }

    private static string? CopyBackground(string source)
    {
        try
        {
            var dir = Path.Combine(AppPaths.Root, "backgrounds");
            Directory.CreateDirectory(dir);

            var ext = Path.GetExtension(source);
            var name = $"bg{DateTime.Now:yyyyMMddHHmmss}{ext}";

            // 只保留最新一张，避免数据目录膨胀
            foreach (var old in Directory.GetFiles(dir, "bg*"))
            {
                try
                {
                    File.Delete(old);
                }
                catch
                {
                    // 被占用就跳过
                }
            }

            var dest = Path.Combine(dir, name);
            File.Copy(source, dest, true);
            return dest;
        }
        catch (Exception ex)
        {
            Logger.Error("复制背景图片失败", ex);
            return null;
        }
    }

    private void ClearBackground_Click(object sender, RoutedEventArgs e)
    {
        _working.BackgroundImagePath = string.Empty;
        BackgroundPathBox.Text = string.Empty;
    }

    private void PickBackgroundColor_Click(object sender, RoutedEventArgs e)
    {
        var picked = PickColor(_working.BackgroundColor);
        if (picked is null)
        {
            return;
        }

        _working.BackgroundColor = picked;
        UpdateBgColorSwatch(picked);
    }

    /// <summary>让背景色回到「跟随主题」，切换深浅色时背景会自动跟着变。</summary>
    private void ResetBackgroundColor_Click(object sender, RoutedEventArgs e)
    {
        _working.BackgroundColor = string.Empty;
        UpdateBgColorSwatch(string.Empty);
    }

    /// <summary>用系统取色器。取消时返回 null。</summary>
    private string? PickColor(string current)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
        };

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(current);
            dialog.Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B);
        }
        catch
        {
            // 当前颜色不合法就保持系统默认
        }

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return null;
        }

        return $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }

    // ══════════════ 扫描目录 ══════════════

    private void AddPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要扫描的游戏目录",
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (var folder in dialog.FolderNames)
        {
            if (!_working.ScanPaths.Any(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase)))
            {
                _working.ScanPaths.Add(folder);
            }
        }

        RefreshPathList();
    }

    private void RemovePath_Click(object sender, RoutedEventArgs e)
    {
        if (PathList.SelectedItem is not string selected)
        {
            return;
        }

        _working.ScanPaths.Remove(selected);
        RefreshPathList();
    }

    private void ResetPaths_Click(object sender, RoutedEventArgs e)
    {
        _working.ScanPaths = GameScanner.DefaultScanRoots();
        RefreshPathList();
    }

    private void RefreshPathList()
    {
        PathList.ItemsSource = null;
        PathList.ItemsSource = _working.ScanPaths;
        UpdatePathHint();
    }

    private void UpdatePathHint()
        => NoPathHint.Visibility = _working.ScanPaths.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ══════════════ 数据 ══════════════

    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureCreated();
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Error("打开数据目录失败", ex);
        }
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("把所有设置恢复成出厂默认？游戏列表不受影响。", "GameWall",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _working = new AppSettings { ScanPaths = GameScanner.DefaultScanRoots() };
        BindToUi();
        LivePreview();
    }

    // ══════════════ 保存 / 取消 ══════════════

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _working.ShowLabels = ShowLabelsBox.IsChecked == true;
        _working.Topmost = TopmostBox.IsChecked == true;
        _working.StartFullscreen = FullscreenBox.IsChecked == true;
        _working.MinimizeToTray = TrayBox.IsChecked == true;
        _working.AutoStart = AutoStartBox.IsChecked == true;
        _working.ConfirmOnRemove = ConfirmRemoveBox.IsChecked == true;
        _working.MinimizeOnGameLaunch = MinimizeOnLaunchBox.IsChecked == true;

        // 回写到调用方持有的对象上（引用不变，主窗口无需重新绑定）
        CopyInto(_working, _target);

        DialogResult = true;
        Close();
    }

    private static void CopyInto(AppSettings from, AppSettings to)
    {
        to.Theme = from.Theme;
        to.ViewMode = from.ViewMode;
        to.CardWidth = from.CardWidth;
        to.ShowLabels = from.ShowLabels;
        to.BackgroundImagePath = from.BackgroundImagePath;
        to.BackgroundColor = from.BackgroundColor;
        to.BackgroundImageOpacity = from.BackgroundImageOpacity;
        to.AccentColor = from.AccentColor;
        to.Topmost = from.Topmost;
        to.StartFullscreen = from.StartFullscreen;
        to.MinimizeToTray = from.MinimizeToTray;
        to.AutoStart = from.AutoStart;
        to.ConfirmOnRemove = from.ConfirmOnRemove;
        to.MinimizeOnGameLaunch = from.MinimizeOnGameLaunch;
        to.ScanPaths = from.ScanPaths.ToList();
        to.ScanDepth = from.ScanDepth;
        to.MinExecutableSize = from.MinExecutableSize;
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
