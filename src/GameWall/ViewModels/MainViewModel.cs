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
    private WallViewMode _viewMode = WallViewMode.Wall;

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

        foreach (var mode in new[] { WallViewMode.Wall, WallViewMode.List, WallViewMode.Compact })
        {
            // 视图切换用同一个 RelayCommand，参数区分模式
        }

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

    // ── 数据 ────────────────────────────────────────────────
    public ObservableCollection<GameCardViewModel> Cards { get; }

    public ObservableCollection<string> TagOptions { get; }

    public AppSettings Settings => _settings.Current;

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
                OnPropertyChanged(nameof(IsWallMode));
                OnPropertyChanged(nameof(IsListMode));
                OnPropertyChanged(nameof(IsCompactMode));
            }
        }
    }

    public bool IsWallMode => ViewMode == WallViewMode.Wall;
    public bool IsListMode => ViewMode == WallViewMode.List;
    public bool IsCompactMode => ViewMode == WallViewMode.Compact;

    public GameCardViewModel? SelectedCard
    {
        get => _selectedCard;
        set => SetProperty(ref _selectedCard, value);
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
        if (entry.LaunchKind == LaunchKind.Executable
            && _library.FindByExecutable(entry.ExecutablePath) is not null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            entry.Name = Path.GetFileNameWithoutExtension(entry.ExecutablePath);
        }

        _library.Add(entry);

        var card = FindOrCreate(entry);
        card.EnsureVisuals();
        _library.Save();

        ReloadCards();
        return true;
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

            foreach (var candidate in candidates)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (_library.FindByExecutable(candidate.FilePath) is not null)
                {
                    skipped++;
                    continue;
                }

                var entry = new GameEntry
                {
                    Name = candidate.SuggestedName,
                    ExecutablePath = candidate.FilePath,
                    LaunchKind = LaunchKind.Executable,
                    Source = "自动扫描",
                };

                _library.Add(entry);
                added++;
            }

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

            MessageBox.Show(
                added == 0 && skipped == 0
                    ? "没有在配置的目录里找到新的游戏。\n\n可以到「设置」里添加扫描目录。"
                    : $"扫描完成：新增 {added} 个游戏，跳过 {skipped} 个已存在的。",
                "GameWall",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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
    /// 通知界面「主题相关的设置可能整体变了」。
    /// 主窗口背景色绑定的是 Settings.BackgroundColor（空值表示跟随主题），
    /// 系统深浅色切换后必须重新求值，否则背景会停在旧颜色上。
    /// </summary>
    public void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(IsSystemTheme));
    }

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
