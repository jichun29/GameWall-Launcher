using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using GameWall.Models;
using GameWall.Services;

namespace GameWall.ViewModels;

/// <summary>
/// 主窗口的视图模型。
///
/// 结构说明：
///  - _library      : 全部游戏（持久化顺序）
///  - Games         : 经过筛选后的可绑定集合（CollectionView 之上再加一层，方便拖拽重排）
///  - Cards         : 对外暴露给 XAML 的集合，始终等于筛选结果
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly LibraryService _library = new();
    private readonly SettingsService _settings = new();
    private readonly GameScanner _scanner = new();

    private CancellationTokenSource? _scanCts;

    private string _searchText = string.Empty;
    private string _selectedTag = "全部";
    private bool _favoritesOnly;
    private bool _isBusy;
    private string _busyText = string.Empty;
    private GameCardViewModel? _selectedCard;
    private WallViewMode _viewMode = WallViewMode.Card;

    public MainViewModel()
    {
        _settings.Load();
        _library.Load();

        _viewMode = _settings.Current.ViewMode;

        Cards = new ObservableCollection<GameCardViewModel>();
        TagOptions = new ObservableCollection<string> { "全部" };

        // 命令
        AddGameCommand = new RelayCommand(_ => RequestAddGame?.Invoke());
        SearchAddCommand = new RelayCommand(_ => RequestSearchAdd?.Invoke());
        ScanCommand = new AsyncRelayCommand(ScanAsync);
        ImportCommand = new RelayCommand(_ => RequestImport?.Invoke());
        SettingsCommand = new RelayCommand(_ => RequestSettings?.Invoke());
        LaunchCommand = new RelayCommand(p => Launch(p as GameCardViewModel ?? SelectedCard));
        EditCommand = new RelayCommand(p => RequestEdit?.Invoke(p as GameCardViewModel ?? SelectedCard));
        RevealCommand = new RelayCommand(p => RevealExecutable(p as GameCardViewModel ?? SelectedCard));
        OpenSaveDirCommand = new RelayCommand(p => OpenSaveDirectory(p as GameCardViewModel ?? SelectedCard));
        RemoveCommand = new RelayCommand(p => Remove(p as GameCardViewModel ?? SelectedCard));
        ToggleFavoriteCommand = new RelayCommand(p => ToggleFavorite(p as GameCardViewModel ?? SelectedCard));
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        RefreshMissingCommand = new RelayCommand(_ => RefreshAllVisuals());
        AboutCommand = new RelayCommand(_ => RequestAbout?.Invoke());
        TidyDesktopCommand = new RelayCommand(_ => RequestTidyDesktop?.Invoke(), _ => TotalCount > 0);
        ToolboxCommand = new RelayCommand(_ => RequestToolbox?.Invoke());
        LogCommand = new RelayCommand(_ => RequestLog?.Invoke());
        CheckUpdateCommand = new AsyncRelayCommand(() => CheckUpdateAsync(interactive: true), () => !IsCheckingUpdate);
        SetViewModeCommand = new RelayCommand(p =>
        {
            if (p is WallViewMode m)
            {
                ViewMode = m;
            }
            else if (p is string s && Enum.TryParse<WallViewMode>(s, out var parsed))
            {
                ViewMode = parsed;
            }
        });

        ReloadCards();
    }

    // ── 事件（由 MainWindow 订阅，用来弹窗） ────────────────
    public event Action? RequestAddGame;
    public event Action? RequestSearchAdd;
    public event Action? RequestImport;
    public event Action? RequestSettings;
    public event Action<GameCardViewModel?>? RequestEdit;

    /// <summary>请求打开「关于」窗口</summary>
    public event Action? RequestAbout;

    /// <summary>请求打开「整理桌面」窗口</summary>
    public event Action? RequestTidyDesktop;

    /// <summary>请求打开「游戏工具箱」窗口</summary>
    public event Action? RequestToolbox;

    /// <summary>请求打开「运行日志」窗口</summary>
    public event Action? RequestLog;

    // ── 数据 ────────────────────────────────────────────────
    public ObservableCollection<GameCardViewModel> Cards { get; }

    public ObservableCollection<string> TagOptions { get; }

    public AppSettings Settings => _settings.Current;

    /// <summary>游戏库原始条目（整理桌面时要拿它判断哪些快捷方式已被收录）</summary>
    public IReadOnlyList<GameEntry> LibraryGames => _library.Games;

    public int TotalCount => _library.Games.Count;

    public int VisibleCount => Cards.Count;

    public bool IsEmpty => _library.Games.Count == 0;

    public bool IsFilteredEmpty => _library.Games.Count > 0 && Cards.Count == 0;

    // ── 命令 ────────────────────────────────────────────────
    public RelayCommand AddGameCommand { get; }
    public RelayCommand SearchAddCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand OpenSaveDirCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }
    public RelayCommand RefreshMissingCommand { get; }
    public RelayCommand SetViewModeCommand { get; }
    public RelayCommand AboutCommand { get; }
    public AsyncRelayCommand CheckUpdateCommand { get; }

    /// <summary>「整理桌面」：把已收录游戏的桌面快捷方式挪进备份目录</summary>
    public RelayCommand TidyDesktopCommand { get; }

    /// <summary>「工具箱」：硬件信息 + 游戏优化 + 电源方案</summary>
    public RelayCommand ToolboxCommand { get; }

    /// <summary>「运行日志」：扫描 / 启动 / 更新的记录，可复制或导出</summary>
    public RelayCommand LogCommand { get; }

    // ── 筛选状态 ────────────────────────────────────────────
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ReloadCards();
            }
        }
    }

    public string SelectedTag
    {
        get => _selectedTag;
        set
        {
            if (SetProperty(ref _selectedTag, value ?? "全部"))
            {
                ReloadCards();
            }
        }
    }

    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (SetProperty(ref _favoritesOnly, value))
            {
                ReloadCards();
            }
        }
    }

    public WallViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (SetProperty(ref _viewMode, value))
            {
                _settings.Current.ViewMode = value;
                _settings.Save();
                OnPropertyChanged(nameof(IsListMode));
                OnPropertyChanged(nameof(IsCardMode));
            }
        }
    }

    /// <summary>列表式</summary>
    public bool IsListMode => ViewMode == WallViewMode.List;

    /// <summary>卡片式（默认）</summary>
    public bool IsCardMode => ViewMode == WallViewMode.Card;

    /// <summary>卡片悬停动画开关（设置里可以关掉）</summary>
    public bool CardAnimationsEnabled => Settings.EnableCardAnimations;

    /// <summary>
    /// 当前选中的卡片。
    ///
    /// 注意：模板里的选中描边绑的是 <see cref="GameCardViewModel.IsSelected"/>，
    /// 不是这个属性。下面 setter 里必须把新旧两张卡的 IsSelected 一起翻转，
    /// 否则点了卡片只有「当前这张」在 VM 里变了、界面上一条描边都不会出现
    /// （之前的选中效果等于完全没生效）。
    /// </summary>
    public GameCardViewModel? SelectedCard
    {
        get => _selectedCard;
        set
        {
            if (ReferenceEquals(_selectedCard, value))
            {
                return;
            }

            if (_selectedCard is { } previous)
            {
                previous.IsSelected = false;
            }

            if (!SetProperty(ref _selectedCard, value))
            {
                return;
            }

            if (value is not null)
            {
                value.IsSelected = true;
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    // ── 更新检查 ────────────────────────────────────────────

    private bool _isCheckingUpdate;
    private UpdateInfo? _availableUpdate;
    private string _updateBanner = string.Empty;

    /// <summary>是否正在检查更新（命令的 CanExecute 会跟着变）</summary>
    public bool IsCheckingUpdate
    {
        get => _isCheckingUpdate;
        private set
        {
            if (SetProperty(ref _isCheckingUpdate, value))
            {
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>待安装的新版本（没有则为 null）</summary>
    public UpdateInfo? AvailableUpdate
    {
        get => _availableUpdate;
        private set
        {
            if (SetProperty(ref _availableUpdate, value))
            {
                OnPropertyChanged(nameof(HasUpdate));
            }
        }
    }

    /// <summary>顶栏那条「有新版本」提示的可见性</summary>
    public bool HasUpdate => _availableUpdate is not null;

    /// <summary>顶栏提示文字，例如「新版本 v1.6.1 可用」</summary>
    public string UpdateBanner
    {
        get => _updateBanner;
        private set => SetProperty(ref _updateBanner, value);
    }

    /// <summary>检查更新的成功回调，由 MainWindow 注入（用来弹对话框）。</summary>
    public Func<UpdateCheckResult, Task>? UpdateFoundHandler { get; set; }

    /// <summary>用户点了顶栏提示条上的「忽略」：收起提示，本次运行不再提。</summary>
    public void DismissUpdate()
    {
        UpdateBanner = string.Empty;
        AvailableUpdate = null;
    }

    // ── 桌面整理提示 ────────────────────────────────────────

    private int _pendingDesktopShortcuts;
    private string _tidyBanner = string.Empty;

    /// <summary>桌面上还有多少个「已收录但没挪走」的快捷方式</summary>
    public int PendingDesktopShortcuts
    {
        get => _pendingDesktopShortcuts;
        private set
        {
            if (SetProperty(ref _pendingDesktopShortcuts, value))
            {
                OnPropertyChanged(nameof(HasTidyHint));
            }
        }
    }

    /// <summary>顶栏「桌面还能再干净一点」提示的可见性</summary>
    public bool HasTidyHint => _pendingDesktopShortcuts > 0 && !string.IsNullOrEmpty(_tidyBanner);

    /// <summary>桌面整理提示文字</summary>
    public string TidyBanner
    {
        get => _tidyBanner;
        private set
        {
            if (SetProperty(ref _tidyBanner, value))
            {
                OnPropertyChanged(nameof(HasTidyHint));
            }
        }
    }

    /// <summary>
    /// 重新数一遍桌面上有多少快捷方式可以收纳。
    /// 扫描 / 导入 / 手动添加之后调用一次，顺带更新顶栏提示。
    /// </summary>
    /// <returns>可以收纳的条数</returns>
    public int RefreshTidyHint()
    {
        var count = 0;

        try
        {
            if (Settings.ShowTidyHint && _library.Games.Count > 0)
            {
                count = DesktopShortcutService.CountPending(_library.Games);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("统计桌面快捷方式失败", ex);
        }

        PendingDesktopShortcuts = count;
        TidyBanner = count > 0
            ? $"桌面上还有 {count} 个游戏的快捷方式可以收纳"
            : string.Empty;

        return count;
    }

    /// <summary>用户点了「以后别提示」：本次运行不再显示这条提示。</summary>
    public void DismissTidyHint()
    {
        TidyBanner = string.Empty;
        OnPropertyChanged(nameof(HasTidyHint));
    }

    /// <summary>
    /// 检查更新。
    /// </summary>
    /// <param name="interactive">
    /// true = 用户主动点的（无论有没有新版本都给反馈）；
    /// false = 启动时自动检查（只有真的发现新版本才打扰用户）。
    /// </param>
    public async Task CheckUpdateAsync(bool interactive)
    {
        if (IsCheckingUpdate)
        {
            return;
        }

        IsCheckingUpdate = true;
        BusyText = "正在检查更新…";

        try
        {
            var result = await UpdateService.CheckAsync(Settings.UpdateSource).ConfigureAwait(true);
            Settings.LastCheckAt = DateTime.Now;
            SaveSettings();

            if (result.Success && result.HasUpdate && result.Info is { } info)
            {
                AvailableUpdate = info;
                UpdateBanner = $"新版本 v{info.Version} 可用";
            }
            else
            {
                AvailableUpdate = null;
                UpdateBanner = string.Empty;
            }

            if (UpdateFoundHandler is not null && (interactive || result.HasUpdate))
            {
                await UpdateFoundHandler(result).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("检查更新时出错", ex);
            if (interactive)
            {
                MessageBox.Show("检查更新时出错：" + ex.Message, "GameWall",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            IsCheckingUpdate = false;
            BusyText = string.Empty;
        }
    }

    // ── 集合维护 ────────────────────────────────────────────

    /// <summary>把 _library 里的数据同步到 Cards，并应用筛选。</summary>
    public void ReloadCards()
    {
        var wanted = _library.Games
            .OrderBy(g => g.SortIndex)
            .Select(FindOrCreate)
            .Where(Matches)
            .ToList();

        // 就地同步，减少 UI 抖动（比 Clear + Add 更平滑）
        for (var i = Cards.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Cards[i]))
            {
                Cards.RemoveAt(i);
            }
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var card = wanted[i];
            var existing = Cards.IndexOf(card);
            if (existing < 0)
            {
                Cards.Insert(i, card);
            }
            else if (existing != i)
            {
                Cards.Move(existing, i);
            }
        }

        // 标签下拉
        var tags = _library.AllTags();
        if (!TagOptions.SequenceEqual(new[] { "全部" }.Concat(tags)))
        {
            TagOptions.Clear();
            TagOptions.Add("全部");
            foreach (var t in tags)
            {
                TagOptions.Add(t);
            }

            if (!TagOptions.Contains(_selectedTag))
            {
                _selectedTag = "全部";
                OnPropertyChanged(nameof(SelectedTag));
            }
        }

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsFilteredEmpty));
    }

    private readonly Dictionary<string, GameCardViewModel> _cardCache = new(StringComparer.OrdinalIgnoreCase);

    private GameCardViewModel FindOrCreate(GameEntry entry)
    {
        if (_cardCache.TryGetValue(entry.Id, out var card))
        {
            return card;
        }

        card = new GameCardViewModel(entry) { Host = this };
        _cardCache[entry.Id] = card;
        return card;
    }

    private bool Matches(GameCardViewModel card)
    {
        if (_favoritesOnly && !card.IsFavorite)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(_selectedTag) && _selectedTag != "全部")
        {
            if (!card.Tags.Any(t => string.Equals(t, _selectedTag, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        var q = _searchText?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            return true;
        }

        // 支持空格分词，「最终 幻想」能同时命中
        foreach (var token in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var hit = card.Name.Contains(token, StringComparison.OrdinalIgnoreCase)
                      || card.Tags.Any(t => t.Contains(token, StringComparison.OrdinalIgnoreCase))
                      || card.Notes.Contains(token, StringComparison.OrdinalIgnoreCase)
                      || card.Source.Contains(token, StringComparison.OrdinalIgnoreCase);
            if (!hit)
            {
                return false;
            }
        }

        return true;
    }

    private void ClearFilters()
    {
        _searchText = string.Empty;
        _favoritesOnly = false;
        _selectedTag = "全部";
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(FavoritesOnly));
        OnPropertyChanged(nameof(SelectedTag));
        ReloadCards();
    }

    // ── 增删改 ──────────────────────────────────────────────

    /// <summary>添加一个游戏（自动处理封面 / 图标）。返回 false 表示已存在。</summary>
    public bool AddGame(GameEntry entry)
    {
        // 去重：本地 exe 比路径，平台游戏比协议地址。
        // 否则同一个 Steam 游戏既可能通过 exe 路径加进来，又可能通过
        // steam:// 快捷方式加进来，墙上会出现两张一样的卡片。
        if (!string.IsNullOrWhiteSpace(entry.LaunchUri))
        {
            if (_library.Games.Any(g => string.Equals(g.LaunchUri, entry.LaunchUri, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }
        else if (entry.LaunchKind == LaunchKind.Executable
                 && _library.FindByExecutable(entry.ExecutablePath) is not null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            entry.Name = Path.GetFileNameWithoutExtension(entry.ExecutablePath);
        }

        // 名字能换成官方中文就换（Steam 库里带各语言名，纯离线）。
        LocalizeName(entry);

        // 顺手去本地找一张真封面（游戏目录自带图 / steam_appid.txt / Steam 库名匹配）。
        // 这一步只读本地文件，不联网；找不到就继续用渐变 + 图标/首字的占位方案。
        TryAttachCover(entry);

        _library.Add(entry);

        var card = FindOrCreate(entry);
        card.EnsureVisuals();
        _library.Save();

        ReloadCards();
        return true;
    }

    /// <summary>
    /// 把一次「拖入 / 选中 / 粘贴」的解析结果加入游戏库。
    /// 与 <see cref="AddFromExecutable"/> 的区别是它支持平台协议
    /// （Steam 的桌面快捷方式就是一个 steam:// 的 .url 文件）。
    /// </summary>
    public bool AddResolved(ResolvedLaunch resolved)
    {
        if (resolved is null || !resolved.IsUsable)
        {
            return false;
        }

        var entry = new GameEntry
        {
            Name = string.IsNullOrWhiteSpace(resolved.SuggestedName)
                ? Path.GetFileNameWithoutExtension(resolved.ExecutablePath)
                : resolved.SuggestedName,
            LaunchKind = resolved.Kind,
            ExecutablePath = resolved.ExecutablePath,
            LaunchUri = resolved.LaunchUri,
            WorkingDirectory = string.IsNullOrWhiteSpace(resolved.ExecutablePath)
                ? string.Empty
                : Path.GetDirectoryName(resolved.ExecutablePath) ?? string.Empty,
            Source = string.IsNullOrWhiteSpace(resolved.Source) ? "手动添加" : resolved.Source,
        };

        return AddGame(entry);
    }

    /// <summary>从 exe 路径直接添加（手动添加 / 扫描结果复用）。</summary>
    public bool AddFromExecutable(string exePath, string? displayName = null, string source = "手动添加")
    {
        var entry = new GameEntry
        {
            Name = string.IsNullOrWhiteSpace(displayName)
                ? Path.GetFileNameWithoutExtension(exePath)
                : displayName!,
            ExecutablePath = exePath,
            LaunchKind = LaunchKind.Executable,
            Source = source,
        };

        return AddGame(entry);
    }

    /// <summary>
    /// 把英文名换成官方中文名（只在能找到的时候换）。
    ///
    /// 来源是 Steam 客户端本地的 appinfo.vdf，纯离线。两条路：
    ///  1. 游戏是 Steam 协议启动的，直接按 appid 查；
    ///  2. 其余情况按名字反查（要求归一化后完全相等，不会把 4 认成 3）。
    ///
    /// 已经有中文的名字不动 —— 玩家自己改过的名字要尊重。
    /// </summary>
    private static void LocalizeName(GameEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            return;
        }

        // 先修「中文名 / English」这种中英混排的旧数据。
        // 老版本把 Steam 的整串官方名收了进来，墙上会出现
        // 「人类一败涂地 / Human Fall Flat」这种长标题。
        var trimmed = SteamNameService.TrimMixedName(entry.Name);
        if (!string.Equals(trimmed, entry.Name, StringComparison.Ordinal))
        {
            Logger.Info($"游戏名去掉英文尾巴：{entry.Name} → {trimmed}");
            entry.Name = trimmed;
        }

        // 已经有中文就不再去查英文名了 —— 玩家自己改过的名字要尊重。
        if (HasChinese(entry.Name))
        {
            return;
        }

        string? chinese = null;

        var appId = CoverArtService.GetSteamAppId(entry.LaunchUri);
        if (!string.IsNullOrWhiteSpace(appId))
        {
            chinese = SteamNameService.GetChineseName(appId);
        }

        chinese ??= SteamNameService.GetChineseNameByEnglish(entry.Name);

        if (!string.IsNullOrWhiteSpace(chinese) && !chinese.Equals(entry.Name, StringComparison.Ordinal))
        {
            Logger.Info($"游戏名换成官方中文名：{entry.Name} → {chinese}");
            entry.Name = chinese!;
        }
    }

    /// <summary>
    /// 把库里已有的条目也换成官方中文名。
    ///
    /// 加游戏时已经换过一次，但老版本加进来的条目名字早就落盘了，
    /// 不重扫的话它们会一直显示英文。这里在启动后补一次，
    /// 有改动才写盘，且只在首屏画完之后跑（读 appinfo.vdf 有几十毫秒的 IO）。
    /// 返回改了几条。
    /// </summary>
    public int LocalizeLibraryNames()
    {
        var changed = 0;

        foreach (var entry in _library.Games)
        {
            var before = entry.Name;
            LocalizeName(entry);
            if (!string.Equals(before, entry.Name, StringComparison.Ordinal))
            {
                changed++;
            }
        }

        if (changed > 0)
        {
            _library.Save();
            ReloadCards();
            Logger.Info($"已把 {changed} 个游戏名换成官方中文名");
        }

        return changed;
    }

    private static bool HasChinese(string text)
    {
        foreach (var ch in text)
        {
            if (ch >= '\u4e00' && ch <= '\u9fff')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 尽量给新加进来的游戏找一张真封面，全部在本地完成：
    /// 先看游戏目录里有没有自带图片、有没有 steam_appid.txt，
    /// 再拿名字去本机 Steam 库里模糊匹配；Steam 协议启动的直接按 appid 取。
    /// 都找不到就留着空，卡片会自动用占位封面。
    /// </summary>
    private static void TryAttachCover(GameEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.CoverPath))
        {
            return;
        }

        // 本地 exe 启动的游戏也有机会拿到封面（游戏目录自带图 / steam_appid.txt / 库名匹配）
        var matched = CoverMatcher.TryResolve(entry.Name, entry.ExecutablePath, entry.Id);
        if (matched is not null)
        {
            entry.CoverPath = matched.Path;
            return;
        }

        if (entry.LaunchKind != LaunchKind.Steam || string.IsNullOrWhiteSpace(entry.LaunchUri))
        {
            return;
        }

        var cover = CoverArtService.TryFetchSteamCover(entry.LaunchUri, entry.Id);
        if (!string.IsNullOrEmpty(cover))
        {
            entry.CoverPath = cover;
        }
    }

    /// <summary>打开编辑窗口（卡片右键菜单 / 工具栏调用）。</summary>
    public void Edit(GameCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        RequestEdit?.Invoke(card);
    }

    /// <summary>带确认的移除（卡片右键菜单调用）。</summary>
    public void RemoveWithConfirm(GameCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        if (_settings.Current.ConfirmOnRemove)
        {
            var result = MessageBox.Show(
                $"确定要从游戏墙移除「{card.Name}」吗？\n\n只删除 GameWall 里的记录，不会动硬盘上的游戏文件。",
                "移除游戏",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        Remove(card);
    }

    public void Remove(GameCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        _library.Remove(card.Entry);
        _cardCache.Remove(card.Id);
        Cards.Remove(card);
        ReloadCards();
    }

    public void ToggleFavorite(GameCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        card.Entry.IsFavorite = !card.Entry.IsFavorite;
        _library.Save();
        card.Refresh();
        if (_favoritesOnly)
        {
            ReloadCards();
        }
    }

    /// <summary>编辑窗口保存后调用。</summary>
    public void CommitEdit(GameCardViewModel card, bool coverChanged)
    {
        _library.Save();
        card.EnsureVisuals(forceRefresh: coverChanged);
        card.Refresh();
        ReloadCards();
    }

    // ── 启动 / 系统操作 ─────────────────────────────────────

    public void Launch(GameCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        try
        {
            LaunchService.Launch(card.Entry);
            _library.Save();
            card.Refresh();

            if (_settings.Current.MinimizeOnGameLaunch && Application.Current?.MainWindow is { } main)
            {
                main.WindowState = WindowState.Minimized;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"启动失败: {card.Name}", ex);
            MessageBox.Show(
                $"启动「{card.Name}」失败：\n{ex.Message}",
                "GameWall",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void RevealExecutable(GameCardViewModel? card) => Reveal(card);

    /// <summary>在资源管理器中定位可执行文件（卡片右键菜单调用）。</summary>
    public void Reveal(GameCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(card.ExecutablePath))
            {
                LaunchService.RevealInExplorer(card.ExecutablePath);
            }
            else if (!string.IsNullOrWhiteSpace(card.Entry.LaunchUri))
            {
                MessageBox.Show("这是平台导入的游戏，没有本地可执行文件路径。", "GameWall",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "GameWall", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>右键菜单「打开存档目录」。</summary>
    public void OpenSaveDirectory(GameCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        var dir = card.Entry.SaveDirectory;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            // 常见存档位置提示
            var suggested = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents", "My Games", card.Name);

            var msg = string.IsNullOrWhiteSpace(dir)
                ? $"还没有设置「{card.Name}」的存档目录。\n\n常见位置：\n{suggested}\n\n可以在「编辑信息」里指定。"
                : $"存档目录不存在：\n{dir}\n\n可以在「编辑信息」里重新指定。";

            MessageBox.Show(msg, "GameWall", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            LaunchService.OpenPath(dir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "GameWall", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>刷新所有卡片的封面 / 图标（换机器或文件移动后使用）。</summary>
    public void RefreshAllVisuals()
    {
        IsBusy = true;
        BusyText = "正在刷新封面与图标…";
        try
        {
            foreach (var card in _cardCache.Values)
            {
                card.EnsureVisuals(forceRefresh: true);
                card.Refresh();
            }

            _library.Save();
            ReloadCards();
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    // ── 自动扫描 ────────────────────────────────────────────

    /// <summary>
    /// 扫描配置的目录，把新发现的 exe 加入游戏库。
    /// 返回 (新增数量, 跳过的重复数量)。
    /// </summary>
    public async Task<(int added, int skipped)> ScanAndImportAsync(IProgress<string>? progress = null)
    {
        var roots = _settings.ExistingScanRoots();
        if (roots.Count == 0)
        {
            return (0, 0);
        }

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        IsBusy = true;
        BusyText = "正在扫描游戏目录…";

        try
        {
            var candidates = await _scanner.ScanAsync(
                roots,
                _settings.Current.ScanDepth,
                _settings.Current.MinExecutableSize,
                keyword: null,
                progress,
                token).ConfigureAwait(true);

            var added = 0;
            var skipped = 0;

            // 扫描明细全部写进运行日志。
            // 用户拿日志来反馈「扫出来不对」时，光有「扫描完成」是没法定位的，
            // 得能看到每个候选的 exe 路径、最终采用的显示名、以及封面命中了哪条线索。
            Logger.Info($"开始扫描 {roots.Count} 个目录，共发现 {candidates.Count} 个候选程序");
            foreach (var root in roots)
            {
                Logger.Info($"  扫描目录：{root}");
            }

            // 扫到的 exe 如果正好在某个平台（Steam / Epic / GOG）的安装目录里，
            // 就把它登记成「平台游戏」而不是裸 exe：
            //   · 启动时走 steam://rungameid/<id>，能正确触发 DRM 校验和云存档
            //   · 名字用商店里的正式名称，而不是 PumpFish.exe 这种内部代号
            var platformGames = BuildPlatformIndex();

            foreach (var candidate in candidates)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (_library.FindByExecutable(candidate.FilePath) is not null)
                {
                    skipped++;
                    Logger.Info($"  跳过（库里已有）：{candidate.FilePath}");
                    continue;
                }

                // 已通过 Steam 快捷方式 / 平台导入加过同一个游戏的，按协议地址判重
                var platform = MatchPlatformGame(candidate.FilePath, platformGames);
                if (platform is not null
                    && _library.Games.Any(g => string.Equals(g.LaunchUri, platform.LaunchUri, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    Logger.Info($"  跳过（平台游戏已存在 {platform.LaunchUri}）：{candidate.FilePath}");
                    continue;
                }

                var entry = new GameEntry
                {
                    Name = platform is not null && !string.IsNullOrWhiteSpace(platform.SuggestedName)
                        ? platform.SuggestedName
                        : candidate.SuggestedName,
                    ExecutablePath = candidate.FilePath,
                    LaunchKind = platform?.Kind ?? LaunchKind.Executable,
                    LaunchUri = platform?.LaunchUri ?? string.Empty,
                    Source = platform is not null ? $"{platform.Platform} 扫描" : "自动扫描",
                };

                // 扫描出来的游戏也顺手把本地能找到的官方封面带上，
                // 否则重扫一遍之后墙上会突然少几张封面
                LocalizeName(entry);
                TryAttachCover(entry);

                _library.Add(entry);
                added++;

                Logger.Info($"  新增：{entry.Name} | {entry.ExecutablePath}"
                            + $" | 来源 {entry.Source}"
                            + $" | 封面 {(string.IsNullOrWhiteSpace(entry.CoverPath) ? "未命中（用首字母封面）" : Path.GetFileName(entry.CoverPath))}");
            }

            Logger.Info($"扫描结束：新增 {added} 个，跳过 {skipped} 个");

            if (added > 0)
            {
                _library.Save();
                ReloadCards();
            }

            return (added, skipped);
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    private async Task ScanAsync()
    {
        try
        {
            var progress = new Progress<string>(text => BusyText = text);
            var (added, skipped) = await ScanAndImportAsync(progress).ConfigureAwait(true);

            if (added == 0 && skipped == 0)
            {
                MessageBox.Show(
                    "没有在配置的目录里找到新的游戏。\n\n可以到「设置」里添加扫描目录。",
                    "GameWall",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // 扫完之后顺手看一眼桌面：如果刚进来的这些游戏在桌面上还留着快捷方式，
            // 现在正是收拾掉它们的最好时机（用户刚把游戏收进来，桌面还是乱的）
            OfferTidy($"扫描完成：新增 {added} 个游戏，跳过 {skipped} 个已存在的。");
        }
        catch (OperationCanceledException)
        {
            // 用户取消，静默
        }
        catch (Exception ex)
        {
            Logger.Error("自动扫描失败", ex);
            MessageBox.Show(ex.Message, "扫描失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 扫描 / 导入结束后调用：先汇报结果，再（在必要时）问一句要不要顺手整理桌面。
    /// 只在「真的新增了游戏」且「桌面上确实还有可收纳的快捷方式」时才会问。
    /// </summary>
    /// <param name="summary">已经拼好的结果说明，例如「扫描完成：新增 3 个游戏」</param>
    public void OfferTidy(string summary)
    {
        var pending = 0;
        try
        {
            pending = RefreshTidyHint();
        }
        catch (Exception ex)
        {
            Logger.Error("统计桌面快捷方式失败", ex);
        }

        if (pending == 0 || !Settings.AskTidyAfterImport)
        {
            MessageBox.Show(summary, "GameWall", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            summary + "\n\n" +
            $"另外，桌面上还有 {pending} 个游戏的快捷方式已经收进游戏墙了。\n" +
            "要现在把它们挪走吗？\n\n" +
            "（不会删除文件，只是移到一个备份文件夹，随时可以还原）",
            "整理桌面",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        if (answer == MessageBoxResult.Yes)
        {
            RequestTidyDesktop?.Invoke();
        }
    }

    // ── 平台游戏与扫描结果的对应关系 ────────────────────────

    /// <summary>
    /// 平台清单里「安装目录 → 平台游戏」的索引。
    /// 扫描的时候拿 exe 的路径去比，命中就说明这个 exe 是某个平台装的游戏。
    /// </summary>
    private List<PlatformGame> BuildPlatformIndex()
    {
        try
        {
            return PlatformImportService.ReadAll()
                .Where(g => !string.IsNullOrWhiteSpace(g.InstallDir))
                .OrderByDescending(g => g.InstallDir.Length)   // 长的优先，避免父目录抢先匹配
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Error("读取平台游戏清单失败", ex);
            return new List<PlatformGame>();
        }
    }

    /// <summary>
    /// 判断 exe 是不是某个平台装的游戏。命中返回该平台游戏（带官方名与启动 URI）。
    /// </summary>
    private static PlatformGame? MatchPlatformGame(string exePath, List<PlatformGame> platformGames)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        foreach (var game in platformGames)
        {
            if (exePath.StartsWith(game.InstallDir, StringComparison.OrdinalIgnoreCase)
                && (exePath.Length == game.InstallDir.Length
                    || exePath[game.InstallDir.Length] == '\\'))
            {
                return game;
            }
        }

        return null;
    }

    // ── 拖拽排序 ────────────────────────────────────────────

    /// <summary>把 source 插到 target 之前/之后，并持久化顺序。</summary>
    public void MoveCard(GameCardViewModel source, GameCardViewModel? target, bool after)
    {
        if (ReferenceEquals(source, target))
        {
            return;
        }

        _library.Move(source.Entry, target?.Entry, after);
        _library.Save();
        ReloadCards();
    }

    /// <summary>把卡片移到列表末尾。</summary>
    public void MoveCardToEnd(GameCardViewModel source)
    {
        _library.Move(source.Entry, null, after: true);
        _library.Save();
        ReloadCards();
    }

    // ── 设置持久化 ──────────────────────────────────────────

    public void SaveSettings() => _settings.Save();

    /// <summary>
    /// 保存游戏库。后台预热补出来的 IconPath 走这条路径落盘，
    /// 不能错调 SaveSettings —— 那样图标缓存信息下次启动还得重新提取。
    /// </summary>
    public void SaveLibrary() => _library.Save();

    /// <summary>
    /// 通知界面「主题相关的设置可能整体变了」。
    /// 主窗口背景色绑定的是 Settings.BackgroundColor（空值表示跟随主题），
    /// 系统深浅色切换后必须重新求值，否则背景会停在旧颜色上。
    /// </summary>
    public void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(IsSystemTheme));
    }

    /// <summary>
    /// 通知界面「设置对象的内容变了，所有 Settings.* 绑定请重新求值」。
    ///
    /// AppSettings 是普通 POCO（不实现 INotifyPropertyChanged），所以
    /// 在设置窗口里改完 CardWidth / ShowLabels 之后，绑定
    /// DataContext.Settings.CardHeight 的卡片不会自己刷新 —— 必须由这里
    /// 把 Settings 整个属性通知一遍，WPF 才会重跑整条绑定路径。
    /// </summary>
    public void NotifySettingsChanged() => OnPropertyChanged(nameof(Settings));

    /// <summary>当前是否处于「跟随系统」模式（供界面显示/调试用）。</summary>
    public bool IsSystemTheme =>
        string.Equals(ThemeModes.Normalize(Settings.Theme), ThemeModes.System, StringComparison.Ordinal);

    public void ResetSettings()
    {
        _settings.ResetToDefaults();
        ThemeManager.ApplyAll(_settings.Current);
        OnPropertyChanged(nameof(Settings));
    }
}
