using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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

    // 打开窗口时的主题与强调色。主题是实时预览的，点「取消」必须能还原回去，
    // 否则用户试了一下浅色然后取消，界面会停在浅色但配置里还是深色。
    private readonly string _originalTheme;
    private readonly string _originalAccent;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();

        _target = settings ?? throw new ArgumentNullException(nameof(settings));
        _working = CloneOf(settings);

        _originalTheme = _target.Theme;
        _originalAccent = _target.AccentColor;

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
        CardAnimationsBox.IsChecked = _working.EnableCardAnimations;

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

        // 桌面整理区
        AskTidyBox.IsChecked = _working.AskTidyAfterImport;
        TidyHintBox.IsChecked = _working.ShowTidyHint;
        UpdateTidyBackupText();

        PathList.ItemsSource = _working.ScanPaths;
        UpdatePathHint();

        DepthSlider.Value = _working.ScanDepth;
        DepthText.Text = $"{_working.ScanDepth} 层";

        DataPathText.Text = "数据目录：" + AppPaths.Root;

        // 更新区：源下拉 + 自动检查开关 + 状态文案
        AutoCheckUpdateBox.IsChecked = _working.AutoCheckUpdate;
        DownloadCoverBox.IsChecked = _working.DownloadCoverFromWeb;

        UpdateSourceBox.Items.Clear();
        var selectedIndex = 0;
        var i = 0;
        foreach (var (key, display) in UpdateService.SourceOptions)
        {
            UpdateSourceBox.Items.Add(new ComboBoxItem { Content = display, Tag = key });
            if (string.Equals(key, _working.UpdateSource, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = i;
            }

            i++;
        }

        UpdateSourceBox.SelectedIndex = selectedIndex;

        UpdateHintText.Text = _working.LastCheckAt is { } last
            ? $"当前版本 {AppInfo.VersionLabel}，上次检查：{last:yyyy-MM-dd HH:mm}。"
            : $"当前版本 {AppInfo.VersionLabel}，还没有检查过更新。";

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
            var actual = ThemeManager.CurrentTheme == ThemeModes.Light ? "#1668E3" : "#3DDBFF";
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
            var actual = ThemeManager.CurrentTheme == ThemeModes.Light ? "#F4F6FA" : "#07080C";
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

    // ══════════════ 桌面整理 ══════════════

    /// <summary>把备份目录里有多少批、多少条显示出来。</summary>
    private void UpdateTidyBackupText()
    {
        try
        {
            var backups = DesktopShortcutService.ListBackups();
            var total = backups.Sum(b => b.Count);

            TidyBackupText.Text = backups.Count == 0
                ? "备份目录：" + DesktopShortcutService.BackupRoot + "（还没有收纳过）"
                : $"备份目录：{DesktopShortcutService.BackupRoot}\n" +
                  $"现有 {backups.Count} 批共 {total} 个快捷方式，最近一次：{backups[0].CreatedAt:yyyy-MM-dd HH:mm}";

            RestoreShortcutsButton.IsEnabled = backups.Any(b => b.Count > 0);
        }
        catch (Exception ex)
        {
            Logger.Error("读取桌面备份信息失败", ex);
            TidyBackupText.Text = "备份目录：" + DesktopShortcutService.BackupRoot;
            RestoreShortcutsButton.IsEnabled = false;
        }
    }

    private void OpenTidy_Click(object sender, RoutedEventArgs e)
    {
        // 设置窗口里还没保存的改动不影响整理功能，所以传当前库就行
        var win = new TidyDesktopWindow(new LibraryService().Games.ToList()) { Owner = this };
        win.ShowDialog();
        UpdateTidyBackupText();
    }

    private void RestoreShortcuts_Click(object sender, RoutedEventArgs e)
    {
        var backups = DesktopShortcutService.ListBackups();
        if (backups.Count == 0)
        {
            MessageBox.Show(this, "还没有收纳过桌面快捷方式。", "GameWall",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 只还原最近一批：多条批次一起还原会把已经删掉的游戏快捷方式也塞回桌面
        var latest = backups[0];
        if (MessageBox.Show(this,
                $"把最近一批（{latest.CreatedAt:yyyy-MM-dd HH:mm}，{latest.Count} 个）还原回桌面？\r\n\r\n" +
                "桌面上已有同名文件的会跳过，不覆盖。",
                "还原快捷方式", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var (restored, skipped, failed) = DesktopShortcutService.Restore(latest.Directory);
        MessageBox.Show(this,
            $"已还原 {restored} 个" +
            (skipped > 0 ? $"，跳过 {skipped} 个同名文件" : string.Empty) +
            (failed > 0 ? $"，失败 {failed} 个" : string.Empty) + "。",
            "GameWall", MessageBoxButton.OK, MessageBoxImage.Information);

        UpdateTidyBackupText();
    }

    private void OpenBackupDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DesktopShortcutService.BackupRoot);
            Process.Start(new ProcessStartInfo
            {
                FileName = DesktopShortcutService.BackupRoot,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Error("打开备份目录失败", ex);
        }
    }

    // ══════════════ 更新 ══════════════

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        var original = CheckUpdateButton.Content;
        CheckUpdateButton.Content = "检查中…";

        try
        {
            var source = (UpdateSourceBox.SelectedItem as ComboBoxItem)?.Tag as string;
            var result = await UpdateService.CheckAsync(source);

            if (!result.Success)
            {
                UpdateHintText.Text = "检查失败：" + result.Message;
                MessageBox.Show(this, "没能从 GitHub 拿到版本信息。\r\n\r\n" + result.Message
                        + "\r\n\r\n国内网络可以换一个下载源再试。",
                    "GameWall", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _working.LastCheckAt = DateTime.Now;
            UpdateHintText.Text = result.HasUpdate
                ? $"发现新版本 v{result.Info!.Version}（当前 {AppInfo.VersionLabel}），去「关于 GameWall」里下载。"
                : $"已经是最新版本 {AppInfo.VersionLabel}，上次检查：{DateTime.Now:yyyy-MM-dd HH:mm}。";

            if (result.HasUpdate)
            {
                var open = MessageBox.Show(this,
                    $"发现新版本 v{result.Info!.Version}（当前 {AppInfo.VersionLabel}）。\r\n\r\n要现在打开「关于」页面下载吗？",
                    "GameWall", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (open == MessageBoxResult.Yes)
                {
                    OpenAboutWindow(result);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("设置窗口检查更新失败", ex);
            UpdateHintText.Text = "检查更新出错：" + ex.Message;
        }
        finally
        {
            CheckUpdateButton.Content = original;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OpenReleases_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = AppInfo.ReleasesUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("打开 Releases 页面失败", ex);
        }
    }

    // ══════════════ 关于 ══════════════

    private void OpenAbout_Click(object sender, RoutedEventArgs e) => OpenAboutWindow(null);

    /// <summary>打开运行日志；已经开着就拉到前面，不重复开窗。</summary>
    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (Window win in Application.Current.Windows)
            {
                if (win is LogWindow existing)
                {
                    if (existing.WindowState == WindowState.Minimized)
                    {
                        existing.WindowState = WindowState.Normal;
                    }

                    existing.Activate();
                    return;
                }
            }

            var log = new LogWindow { Owner = this };
            log.Show();
        }
        catch (Exception ex)
        {
            Logger.Error("打开运行日志失败", ex);
        }
    }

    /// <summary>打开关于窗口；带了检查结果就直接显示，省一次请求。</summary>
    private void OpenAboutWindow(UpdateCheckResult? known)
    {
        var about = new AboutWindow();
        if (known is not null)
        {
            about.ApplyKnownResult(known);
        }

        // 设置窗口是模态的，这里不强设 Owner，避免模态链互相卡住
        about.Show();
    }

    // ══════════════ 保存 / 取消 ══════════════

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _working.ShowLabels = ShowLabelsBox.IsChecked == true;
        _working.EnableCardAnimations = CardAnimationsBox.IsChecked == true;
        _working.AutoCheckUpdate = AutoCheckUpdateBox.IsChecked == true;
        _working.DownloadCoverFromWeb = DownloadCoverBox.IsChecked == true;

        // 立刻生效，不用等重启：下一张没封面的卡片就能按新开关决定要不要联网
        CoverArtService.OnlineEnabled = _working.DownloadCoverFromWeb;

        // 下拉项的 Tag 才是真正的源 key，Content 只是给人看的显示名
        if (UpdateSourceBox.SelectedItem is ComboBoxItem { Tag: string sourceKey }
            && UpdateService.IsKnownSource(sourceKey))
        {
            _working.UpdateSource = sourceKey;
        }
        _working.Topmost = TopmostBox.IsChecked == true;
        _working.StartFullscreen = FullscreenBox.IsChecked == true;
        _working.MinimizeToTray = TrayBox.IsChecked == true;
        _working.AutoStart = AutoStartBox.IsChecked == true;
        _working.ConfirmOnRemove = ConfirmRemoveBox.IsChecked == true;
        _working.MinimizeOnGameLaunch = MinimizeOnLaunchBox.IsChecked == true;
        _working.AskTidyAfterImport = AskTidyBox.IsChecked == true;
        _working.ShowTidyHint = TidyHintBox.IsChecked == true;

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
        to.EnableCardAnimations = from.EnableCardAnimations;
        to.AutoCheckUpdate = from.AutoCheckUpdate;
        to.DownloadCoverFromWeb = from.DownloadCoverFromWeb;
        to.UpdateSource = from.UpdateSource;
        to.AskTidyAfterImport = from.AskTidyAfterImport;
        to.ShowTidyHint = from.ShowTidyHint;
        to.ScanPaths = from.ScanPaths.ToList();
        to.ScanDepth = from.ScanDepth;
        to.MinExecutableSize = from.MinExecutableSize;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // 主题和强调色是边选边预览的，取消时要把界面上的预览也撤掉
        ThemeManager.Apply(_originalTheme);
        ThemeManager.ApplyAccent(_originalAccent);

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
