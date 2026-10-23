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
        _vm.RequestAbout += () => ShowAbout();
        _vm.RequestTidyDesktop += () => ShowTidyDesktop();
        _vm.RequestToolbox += () => ShowToolbox();
        _vm.RequestLog += () => ShowLog();

        // 检查更新的结果统一交给主窗口处理：用户主动点的要弹窗，
        // 后台自动检查的只有在真发现新版本时才弹（顶栏提示条另外由绑定驱动）
        _vm.UpdateFoundHandler = OnUpdateCheckedAsync;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.ApplyAll(_vm.Settings);

        // 顶栏那个太阳/月亮按钮的图标要与实际生效的主题一致
        ApplyThemeVisual();

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

        // 自动检查更新（每天最多一次），等界面彻底起来之后再发请求
        _ = AutoCheckUpdateAsync();

        // 数一遍桌面上还有多少快捷方式可以收纳（只读目录，很便宜，但要等首屏画完）
        _ = RefreshTidyHintAsync();

        // 把库里遗留的英文名换成官方中文名（读 Steam 的 appinfo.vdf，放首屏之后）
        _ = LocalizeNamesAsync();
    }

    // ══════════════ 更新 ══════════════

    /// <summary>
    /// 启动时的自动检查。一天只查一次，避免每次开软件都去打 GitHub。
    /// </summary>
    private async Task AutoCheckUpdateAsync()
    {
        try
        {
            if (!_vm.Settings.AutoCheckUpdate)
            {
                return;
            }

            var last = _vm.Settings.LastCheckAt;
            if (last is { } stamp && (DateTime.Now - stamp).TotalHours < 24)
            {
                return;
            }

            // 让首屏先画完再联网
            await Task.Delay(1500);

            await _vm.CheckUpdateAsync(interactive: false);
        }
        catch (Exception ex)
        {
            Logger.Error("自动检查更新失败", ex);
        }
    }

    /// <summary>检查更新回来的统一出口。</summary>
    private Task OnUpdateCheckedAsync(UpdateCheckResult result)
    {
        // 后台静默检查、又没新版本 —— 什么都不做，别打扰用户
        if (!result.Success)
        {
            if (!this.IsLoaded)
            {
                return Task.CompletedTask;
            }

            Logger.Info("检查更新失败：" + result.Message);
            return Task.CompletedTask;
        }

        if (result.HasUpdate)
        {
            Logger.Info($"发现新版本 v{result.Info!.Version}（当前 {AppInfo.VersionLabel}）");
        }

        return Task.CompletedTask;
    }

    private void ShowUpdate_Click(object sender, RoutedEventArgs e) => ShowAbout();

    /// <summary>
    /// 点左上角的版本实胶囊：进「关于」窗口看版本与更新。
    ///
    /// 移植说明：版本号和 NEW 标记是常驻显示的，用户看到 NEW 想点的时候
    /// 直觉是点那个胶囊本身，所以这里也要能进更新页。已经查出有新版本时
    /// 直接把结果带过去（省一次网络请求），否则进窗口后静默查一次。
    /// </summary>
    private void VersionPill_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        try
        {
            var about = new AboutWindow { Owner = this };

            if (_vm.AvailableUpdate is { } pending)
            {
                about.ApplyKnownResult(new UpdateCheckResult
                {
                    Success = true,
                    HasUpdate = true,
                    Info = pending,
                    CurrentVersion = AppInfo.Version,
                    Message = $"发现新版本 v{pending.Version}（当前 {AppInfo.VersionLabel}）。"
                });
            }
            else
            {
                about.AutoCheckOnLoad();
            }

            about.Show();
        }
        catch (Exception ex)
        {
            Logger.Error("打开关于窗口失败", ex);
        }
    }

    private void DismissUpdate_Click(object sender, RoutedEventArgs e) => _vm.DismissUpdate();

    // ══════════════ 桌面整理 ══════════════

    /// <summary>
    /// 启动时统计桌面快捷方式。解析 .lnk 要过 WScript.Shell，几十个快捷方式
    /// 加起来也有几十毫秒，所以等首屏画完再干，别卡住窗口出现。
    /// </summary>
    private async Task RefreshTidyHintAsync()
    {
        try
        {
            await Task.Delay(900);
            _vm.RefreshTidyHint();
        }
        catch (Exception ex)
        {
            Logger.Error("统计桌面快捷方式失败", ex);
        }
    }

    /// <summary>
    /// 启动后把库里已有的英文名换成官方中文名。
    /// 读 Steam 的 appinfo.vdf（1.6MB）要几十毫秒，放在首屏之后跑。
    /// </summary>
    private async Task LocalizeNamesAsync()
    {
        try
        {
            await Task.Delay(600);
            _vm.LocalizeLibraryNames();
        }
        catch (Exception ex)
        {
            Logger.Error("回填中文名失败", ex);
        }
    }

    private void ShowTidy_Click(object sender, RoutedEventArgs e) => ShowTidyDesktop();

    private void DismissTidy_Click(object sender, RoutedEventArgs e) => _vm.DismissTidyHint();

    /// <summary>
    /// 打开整理桌面窗口。整理完（真的挪走了东西）之后重新数一遍桌面，
    /// 免得顶栏的「还有 N 个可以收纳」停留在一个过时的数字上。
    /// </summary>
    private void ShowTidyDesktop()
    {
        try
        {
            var win = new TidyDesktopWindow(_vm.LibraryGames) { Owner = this };
            win.ShowDialog();

            _vm.RefreshTidyHint();
        }
        catch (Exception ex)
        {
            Logger.Error("打开整理桌面窗口失败", ex);
        }
    }

    private void ShowAbout()
    {
        try
        {
            var about = new AboutWindow { Owner = this };
            about.Show();
        }
        catch (Exception ex)
        {
            Logger.Error("打开关于窗口失败", ex);
        }
    }

    /// <summary>打开游戏工具箱（硬件信息 + 优化方案 + 电源方案）。</summary>
    private void ShowToolbox()
    {
        try
        {
            var win = new GameToolboxWindow { Owner = this };
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            Logger.Error("打开游戏工具箱失败", ex);
        }
    }

    /// <summary>
    /// 打开运行日志。用非模态 Show()：
    /// 用户经常是一边看日志一边回去改设置 / 重扫，模态窗口会挡住主界面。
    /// </summary>
    private void ShowLog()
    {
        try
        {
            // 已经开着就把它拿到前面，别堆出一摞同样的窗口
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

    private async Task WarmUpVisualsAsync()
    {
        await Task.Delay(600);
        try
        {
            var changed = false;
            var index = 0;

            foreach (var card in _vm.Cards)
            {
                if (card.EnsureVisuals())
                {
                    changed = true;
                    card.Refresh();
                }

                // 提取图标要过 shell COM，几十个游戏连做会把界面顶住。
                // 每 4 张让出一次，滚动和点击都不受影响。
                if (++index % 4 == 0)
                {
                    await Task.Delay(1);
                }
            }

            if (changed)
            {
                // 补出来的是 IconPath，属于游戏库数据，要写 library.json
                _vm.SaveLibrary();
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

            // 跟随系统时主题是被动变的，顶栏那个切换按钮的图标也要跟着翻
            ApplyThemeVisual();
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

    // ══════════════ 主题快速切换 ══════════════

    /// <summary>
    /// 顶栏的深浅色快速切换。
    ///
    /// 点一下就把主题改成明确的一种（深色 / 浅色），而不是在「跟随系统」和三态之间循环 ——
    /// 用户点这个按钮的意思就是「我现在就要看另一种」，如果还留着 System，
    /// 系统颜色一变又被翻回去，会让人以为按钮失灵。
    /// 想恢复跟随系统，去设置面板里选。
    /// </summary>
    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var next = string.Equals(ThemeManager.CurrentTheme, ThemeModes.Light, StringComparison.Ordinal)
                ? ThemeModes.Dark
                : ThemeModes.Light;

            _vm.Settings.Theme = next;
            _vm.SaveSettings();

            ThemeManager.Apply(next);
            ThemeManager.ApplyAccent(_vm.Settings.AccentColor);

            // 主窗口背景色绑的是 Settings.BackgroundColor（空值 = 跟随主题），
            // AppSettings 不是可观察对象，必须显式通知一次才会按新主题重算
            _vm.NotifyThemeChanged();
            ApplyThemeVisual();
        }
        catch (Exception ex)
        {
            Logger.Error("快速切换主题失败", ex);
        }
    }

    /// <summary>
    /// 让按钮上的图标和提示语跟当前实际生效的主题对上：
    /// 深色下显示太阳（点一下变浅），浅色下显示月亮（点一下变深）。
    /// </summary>
    private void ApplyThemeVisual()
    {
        var isLight = string.Equals(ThemeManager.CurrentTheme, ThemeModes.Light, StringComparison.Ordinal);
        ThemeButton.Content = isLight ? "🌙" : "☀";
        ThemeButton.ToolTip = isLight ? "切换到深色主题" : "切换到浅色主题";
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
        var rejected = 0;

        foreach (var raw in files)
        {
            try
            {
                var resolved = ShortcutResolver.Resolve(raw);
                if (resolved is null)
                {
                    failed++;
                    continue;
                }

                // 拖进来一个 Steam 客户端、平台启动器时提示用户，而不是默默加进墙里
                if (resolved.Kind == LaunchKind.Executable
                    && GameScanner.IsLikelyPlatformOrTool(resolved.ExecutablePath))
                {
                    rejected++;
                    continue;
                }

                if (_vm.AddResolved(resolved))
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

        if (added == 0 && dup == 0 && failed == 0 && rejected == 0)
        {
            return;
        }

        // 拖进来的东西里真的有新游戏，就顺手更新「桌面还能再干净一点」的计数
        if (added > 0)
        {
            _vm.RefreshTidyHint();
        }

        var parts = new List<string>();
        if (added > 0) parts.Add($"新增 {added} 个");
        if (dup > 0) parts.Add($"已存在 {dup} 个");
        if (rejected > 0) parts.Add($"看起来不是游戏 {rejected} 个");
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
            Filter = "游戏程序 (*.exe;*.url;*.lnk)|*.exe;*.url;*.lnk|可执行文件 (*.exe)|*.exe|快捷方式 (*.lnk;*.url)|*.lnk;*.url|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var added = 0;
        var failed = 0;
        foreach (var file in dialog.FileNames)
        {
            var resolved = ShortcutResolver.Resolve(file);
            if (resolved is null)
            {
                failed++;
                continue;
            }

            if (_vm.AddResolved(resolved))
            {
                added++;
            }
        }

        if (added == 0)
        {
            var message = failed > 0
                ? "没能从这些文件里认出可启动的游戏。\n\nSteam/Epic/GOG 的快捷方式可以直接拖进来，会自动识别成平台启动。"
                : "这些文件已经在游戏墙里了。";
            MessageBox.Show(message, "GameWall", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 手动添加完不弹框打扰，只在顶栏亮一条「桌面还能再干净一点」的提示
        _vm.RefreshTidyHint();
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

            // 卡片宽度是绑在 Settings.CardHeight 上的派生值，AppSettings 不是
            // 可观察对象，改完必须显式通知一次，否则封面还停在旧高度上
            _vm.NotifySettingsChanged();

            AutoStartService.SetEnabled(_vm.Settings.AutoStart);
            ApplyTopmostVisual(_vm.Settings.Topmost);
            Topmost = _vm.Settings.Topmost;

            // 设置里可能改了主题（包括改成「跟随系统」），按钮图标要跟上
            ApplyThemeVisual();
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

        if (added > 0)
        {
            // 导入之后同样问一句要不要顺手整理桌面
            _vm.OfferTidy($"已从平台导入 {added} 个游戏。");
        }
        else
        {
            MessageBox.Show("选中的游戏都已经在列表里了。", "GameWall",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
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
