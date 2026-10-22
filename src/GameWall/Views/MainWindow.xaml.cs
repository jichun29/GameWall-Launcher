using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GameWall.Models;
using GameWall.Services;
using GameWall.ViewModels;
using GameWall.Views.Dialogs;

namespace GameWall.Views;

/// <summary>
/// 主窗口：自定义无边框标题栏 + 卡片拖拽排序 + 窗口级 exe 拖放导入。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    // 拖拽排序状态
    private GameCardViewModel? _dragCard;
    private Point _dragStart;
    private bool _dragArmed;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel();
        DataContext = _vm;

        HookRequestEvents();
        RestoreWindowPlacement();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    /// <summary>其它模块（例如托盘菜单）需要访问视图模型时用。</summary>
    public MainViewModel ViewModel => _vm;

    private void HookRequestEvents()
    {
        _vm.RequestAddGame += () => AddGameFromDialog();
        _vm.RequestSearchAdd += () => SearchAndAdd();
        _vm.RequestEdit += card => EditGame(card);
        _vm.RequestSettings += () => OpenSettings();
        _vm.RequestImport += () => ImportFromPlatforms();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.ApplyAll(_vm.Settings);

        // 监听系统深浅色变化：只有主题设为「跟随系统」时才真的换肤
        SystemThemeWatcher.SystemThemeChanged += OnSystemThemeChanged;
        SystemThemeWatcher.Start();

        ApplyTopmostVisual(_vm.Settings.Topmost);

        if (_vm.Settings.Topmost)
        {
            Topmost = true;
        }

        if (_vm.Settings.StartFullscreen)
        {
            WindowState = WindowState.Maximized;
        }

        // 首次运行引导
        if (!_vm.Settings.FirstRunCompleted)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var guide = new FirstRunWindow { Owner = this };
                guide.ShowDialog();
                _vm.Settings.FirstRunCompleted = true;
                _vm.SaveSettings();
            }));
        }

        // 后台预热：补齐缺失的封面 / 图标，不阻塞首屏
        _ = WarmUpVisualsAsync();
    }

    private async Task WarmUpVisualsAsync()
    {
        await Task.Delay(600);
        try
        {
            var changed = false;
            foreach (var card in _vm.Cards)
            {
                if (card.EnsureVisuals())
                {
                    changed = true;
                    card.Refresh();
                }
            }

            if (changed)
            {
                _vm.SaveSettings();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("预热封面失败", ex);
        }
    }

    // ══════════════ 主题跟随 ══════════════

    /// <summary>
    /// 系统深浅色变了。只有主题模式是「跟随系统」时才重新上色，
    /// 用户手动选了深色/浅色就不该被系统带着跑。
    /// </summary>
    private void OnSystemThemeChanged(string systemTheme)
    {
        try
        {
            if (!string.Equals(ThemeModes.Normalize(_vm.Settings.Theme), ThemeModes.System, StringComparison.Ordinal))
            {
                return;
            }

            // 背景色和强调色如果也是「跟随主题」，这里必须整组重刷
            ThemeManager.Apply(ThemeModes.System);
            ThemeManager.ApplyAccent(_vm.Settings.AccentColor);

            // 主窗口背景色绑定的是 Settings.BackgroundColor，空值时靠转换器回退，
            // 所以这里通知一次让它重算
            _vm.NotifyThemeChanged();
        }
        catch (Exception ex)
        {
            Logger.Error("跟随系统主题失败", ex);
        }
    }

    // ══════════════ 窗口按钮 ══════════════

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        // 只有点在空白处才允许拖动窗口，否则会抢掉输入框焦点
        if (e.OriginalSource is DependencyObject src && IsInteractive(src))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // 拖拽被系统取消时忽略
        }
    }

    private static bool IsInteractive(DependencyObject source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is TextBox or Button or CheckBox or ComboBox or ScrollBar or Slider)
            {
                return true;
            }

            if (node is Window)
            {
                break;
            }
        }

        return false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ══════════════ 细节交互：快捷键 & 粘贴导入 ══════════════

    /// <summary>
    /// 搜索框按键：Esc 清空关键词，Enter 直接启动当前筛选结果里的第一个。
    /// 这样「输名字 → 回车」就是最快的一次启动。
    /// </summary>
    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _vm.SearchText = string.Empty;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_vm.Cards.Count > 0)
            {
                _vm.Launch(_vm.Cards[0]);
            }

            e.Handled = true;
        }
    }

    /// <summary>
    /// 窗口级快捷键：
    ///   Ctrl+V        把剪贴板里的路径直接加进游戏墙
    ///   F5            刷新封面与图标
    ///   Delete        移除选中的游戏（会二次确认）
    ///   Ctrl+F        聚焦到搜索框
    ///   Esc           清空筛选
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (ctrl && e.Key == Key.V)
        {
            // 输入框里的粘贴交给输入框自己处理
            if (Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            if (TryPasteFromClipboard())
            {
                e.Handled = true;
            }

            return;
        }

        if (ctrl && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.F5:
                _vm.RefreshAllVisuals();
                e.Handled = true;
                break;

            case Key.Delete when _vm.SelectedCard is { } selected:
                _vm.RemoveWithConfirm(selected);
                e.Handled = true;
                break;

            case Key.Escape:
                _vm.ClearFiltersCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 从剪贴板读路径并导入。支持纯文本路径、多个换行分隔的路径，
    /// 以及资源管理器里复制文件后产生的 FileDrop。
    /// </summary>
    private bool TryPasteFromClipboard()
    {
        string[] candidates;

        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var list = Clipboard.GetFileDropList();
                candidates = new string[list.Count];
                list.CopyTo(candidates, 0);
            }
            else if (Clipboard.ContainsText())
            {
                // 资源管理器「复制为路径」会给每行加上引号
                candidates = Clipboard.GetText()
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim().Trim('"'))
                    .Where(line => line.Length > 0)
                    .ToArray();
            }
            else
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("读取剪贴板失败", ex);
            return false;
        }

        if (candidates.Length == 0)
        {
            return false;
        }

        HandleDroppedFiles(candidates);
        return true;
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        _vm.Settings.Topmost = Topmost;
        _vm.SaveSettings();
        ApplyTopmostVisual(Topmost);
    }

    private void ApplyTopmostVisual(bool pinned)
    {
        PinButton.Foreground = pinned
            ? (Brush)FindResource("Brush.Accent")
            : (Brush)FindResource("Brush.Text");
        PinButton.ToolTip = pinned ? "取消置顶" : "窗口置顶";
    }

    // ══════════════ 卡片交互 ══════════════

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not GameCardViewModel card)
        {
            return;
        }

        _vm.SelectedCard = card;

        if (e.ClickCount == 2)
        {
            _dragArmed = false;
            _vm.Launch(card);
            return;
        }

        // 记录起点，等鼠标移动超过阈值才进入拖拽，避免误触
        _dragCard = card;
        _dragStart = e.GetPosition(this);
        _dragArmed = true;
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || _dragCard is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragArmed = false;

        try
        {
            DragDrop.DoDragDrop(
                (DependencyObject)sender,
                new DataObject(DragFormats.CardId, _dragCard.Id),
                DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            Logger.Error("拖拽排序失败", ex);
        }
        finally
        {
            _dragCard = null;
        }
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        // 鼠标离开时取消待命状态（不取消已开始的拖拽）
    }

    private void Card_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormats.CardId)
                     || e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Move
            : DragDropEffects.None;

        e.Handled = true;
    }

    private void Card_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not GameCardViewModel target)
        {
            return;
        }

        if (e.Data.GetDataPresent(DragFormats.CardId))
        {
            var sourceId = e.Data.GetData(DragFormats.CardId) as string;
            var source = FindCard(sourceId);
            if (source is not null && !ReferenceEquals(source, target))
            {
                var pos = e.GetPosition(fe);
                var after = pos.X > fe.ActualWidth / 2;
                _vm.MoveCard(source, target, after);
            }

            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files is not null)
            {
                HandleDroppedFiles(files);
            }

            e.Handled = true;
        }
    }

    private GameCardViewModel? FindCard(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        foreach (var card in _vm.Cards)
        {
            if (string.Equals(card.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return card;
            }
        }

        return null;
    }

    // ══════════════ 窗口级拖放（exe / 快捷方式） ══════════════

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            HandleDroppedFiles(files);
        }

        e.Handled = true;
    }

    private void HandleDroppedFiles(IEnumerable<string> files)
    {
        var added = 0;
        var dup = 0;
        var failed = 0;

        foreach (var raw in files)
        {
            try
            {
                var exe = ShortcutResolver.ResolveToExecutable(raw);
                if (exe is null)
                {
                    failed++;
                    continue;
                }

                if (_vm.AddFromExecutable(exe, null, "拖放添加"))
                {
                    added++;
                }
                else
                {
                    dup++;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"处理拖入文件失败: {raw}", ex);
                failed++;
            }
        }

        if (added == 0 && dup == 0 && failed == 0)
        {
            return;
        }

        var parts = new List<string>();
        if (added > 0) parts.Add($"新增 {added} 个");
        if (dup > 0) parts.Add($"已存在 {dup} 个");
        if (failed > 0) parts.Add($"无法识别 {failed} 个");

        MessageBox.Show(string.Join("，", parts) + "。", "GameWall",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ══════════════ 弹窗入口 ══════════════

    private void AddGameFromDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择游戏可执行文件",
            Filter = "可执行文件 (*.exe)|*.exe|快捷方式 (*.lnk)|*.lnk|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var added = 0;
        foreach (var file in dialog.FileNames)
        {
            var exe = ShortcutResolver.ResolveToExecutable(file);
            if (exe is not null && _vm.AddFromExecutable(exe))
            {
                added++;
            }
        }

        if (added == 0)
        {
            MessageBox.Show("这些文件已经在游戏墙里了。", "GameWall",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void SearchAndAdd()
    {
        var win = new SearchAddWindow { Owner = this };
        if (win.ShowDialog() == true && win.SelectedExecutable is { } exe)
        {
            if (!_vm.AddFromExecutable(exe, win.SelectedName, "搜索添加"))
            {
                MessageBox.Show("这个游戏已经在游戏墙里了。", "GameWall",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private void EditGame(GameCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        var win = new GameEditWindow(card) { Owner = this };
        if (win.ShowDialog() == true)
        {
            _vm.CommitEdit(card, win.CoverChanged);
        }
    }

    private void OpenSettings()
    {
        var win = new SettingsWindow(_vm.Settings) { Owner = this };
        if (win.ShowDialog() == true)
        {
            _vm.SaveSettings();
            ThemeManager.ApplyAll(_vm.Settings);

            // 外观可能改了卡片宽度 / 标签显隐，重建卡片以刷新布局
            _vm.ReloadCards();

            AutoStartService.SetEnabled(_vm.Settings.AutoStart);
            ApplyTopmostVisual(_vm.Settings.Topmost);
            Topmost = _vm.Settings.Topmost;
        }
    }

    private void ImportFromPlatforms()
    {
        var win = new ImportWindow { Owner = this };
        if (win.ShowDialog() != true)
        {
            return;
        }

        var added = 0;
        foreach (var item in win.SelectedGames)
        {
            var entry = new GameEntry
            {
                Name = item.SuggestedName,
                LaunchKind = item.Kind,
                LaunchUri = item.LaunchUri,
                ExecutablePath = item.ExecutablePath,
                Source = item.Platform + " 导入",
            };

            if (_vm.AddGame(entry))
            {
                added++;
            }
        }

        MessageBox.Show(added > 0 ? $"已导入 {added} 个游戏。" : "选中的游戏都已经在列表里了。",
            "GameWall", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ══════════════ 窗口位置 / 关闭行为 ══════════════

    private void RestoreWindowPlacement()
    {
        var s = _vm.Settings;
        if (s.WindowWidth > 400 && s.WindowHeight > 300)
        {
            Width = s.WindowWidth;
            Height = s.WindowHeight;
        }

        if (s.WindowLeft is { } savedLeft && s.WindowTop is { } savedTop)
        {
            // 防止上次在副屏、这次副屏没接，窗口跑到看不见的地方
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

            if (savedLeft >= virtualLeft - 50 && savedLeft < virtualRight - 50
                && savedTop >= virtualTop - 50 && savedTop < virtualBottom - 50)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = savedLeft;
                Top = savedTop;
            }
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var s = _vm.Settings;

        // 记住窗口尺寸与位置
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = ActualWidth;
            s.WindowHeight = ActualHeight;
            s.WindowLeft = Left;
            s.WindowTop = Top;
        }

        _vm.SaveSettings();

        // 最小化到托盘：真正退出由托盘菜单触发
        if (s.MinimizeToTray && !App.IsExiting)
        {
            e.Cancel = true;
            Hide();
            App.ShowTrayHintOnce();
        }
        else
        {
            // 真退出时摘掉系统主题监听，否则静态事件会把窗口吊住
            SystemThemeWatcher.SystemThemeChanged -= OnSystemThemeChanged;
            SystemThemeWatcher.Stop();
        }
    }
}

/// <summary>拖拽时自定义的数据格式。</summary>
internal static class DragFormats
{
    public const string CardId = "GameWall.CardId";
}
