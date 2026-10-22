using System;
using System.Collections.Generic;
using System.IO;
using GameWall.Models;
using GameWall.Services;

namespace GameWall.ViewModels;

/// <summary>
/// 一张游戏卡片的视图模型。
///
/// 展示用的派生属性（封面、副标题、悬停提示）全部集中在这里，
/// 卡片的 GameEntry 由 LibraryService 持有，两者通过 Refresh 同步。
/// </summary>
public sealed class GameCardViewModel : ObservableObject
{
    private bool _isSelected;

    public GameCardViewModel(GameEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));

        // 卡片自带一套命令，右键菜单（独立可视化树）可以直接绑定，无需向上找 DataContext
        LaunchCommand = new RelayCommand(() => Host?.Launch(this));
        EditCommand = new RelayCommand(() => Host?.Edit(this));
        RevealCommand = new RelayCommand(() => Host?.Reveal(this));
        OpenSaveDirCommand = new RelayCommand(() => Host?.OpenSaveDirectory(this));
        ToggleFavoriteCommand = new RelayCommand(() => Host?.ToggleFavorite(this));
        RemoveCommand = new RelayCommand(() => Host?.RemoveWithConfirm(this));

        EnsureVisuals();
    }

    public GameEntry Entry { get; }

    /// <summary>所属主视图模型，构造后由 MainViewModel 注入。</summary>
    public MainViewModel? Host { get; set; }

    public RelayCommand LaunchCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand OpenSaveDirCommand { get; }
    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand RemoveCommand { get; }

    // ── 基础信息 ───────────────────────────────────────────
    public string Id => Entry.Id;

    public string Name
    {
        get => Entry.Name;
        set
        {
            if (Entry.Name == value)
            {
                return;
            }

            Entry.Name = value;
            OnPropertyChanged();
        }
    }

    public string ExecutablePath => Entry.ExecutablePath;

    public string LaunchUri => Entry.LaunchUri;

    public LaunchKind LaunchKind => Entry.LaunchKind;

    public bool IsFavorite => Entry.IsFavorite;

    public int LaunchCount => Entry.LaunchCount;

    public DateTime? LastLaunchAt => Entry.LastLaunchAt;

    public string Source => Entry.Source;

    public string Notes => Entry.Notes;

    public IReadOnlyList<string> Tags => Entry.Tags;

    /// <summary>文件缺失（被卸载或移动过）时卡片会显示「丢失」角标。</summary>
    public bool IsMissing => !Entry.Exists;

    private string? _placeholderImage;

    /// <summary>卡片封面：优先自定义封面 → exe 图标 → 首字渐变占位图。</summary>
    public string PrimaryImage
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Entry.CoverPath) && File.Exists(Entry.CoverPath))
            {
                return Entry.CoverPath;
            }

            if (!string.IsNullOrWhiteSpace(Entry.IconPath) && File.Exists(Entry.IconPath))
            {
                return Entry.IconPath;
            }

            _placeholderImage ??= CoverFactory.CreatePlaceholder(Entry.Name, Entry.Id) ?? string.Empty;
            return _placeholderImage;
        }
    }

    /// <summary>只有从 exe 取到的小图标时才把它当「徽标」叠在封面上。</summary>
    public bool HasIconBadge =>
        string.IsNullOrWhiteSpace(Entry.CoverPath) == false
        && !string.IsNullOrWhiteSpace(Entry.IconPath)
        && File.Exists(Entry.IconPath);

    public string IconImage =>
        !string.IsNullOrWhiteSpace(Entry.IconPath) && File.Exists(Entry.IconPath)
            ? Entry.IconPath
            : string.Empty;

    /// <summary>是否只在用占位图（既没有自定义封面也没有 exe 图标）。</summary>
    public bool UsesPlaceholder =>
        (string.IsNullOrWhiteSpace(Entry.CoverPath) || !File.Exists(Entry.CoverPath))
        && (string.IsNullOrWhiteSpace(Entry.IconPath) || !File.Exists(Entry.IconPath));

    public string InitialGlyph => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();

    /// <summary>卡片右下角的补充信息。</summary>
    public string Subtitle
    {
        get
        {
            if (IsMissing)
            {
                return "文件缺失";
            }

            return LaunchCount > 0 ? $"启动 {LaunchCount} 次" : Source;
        }
    }

    public string Tooltip
    {
        get
        {
            var lines = new List<string> { Name };

            if (!string.IsNullOrWhiteSpace(Entry.ExecutablePath))
            {
                lines.Add(Entry.ExecutablePath);
            }
            else if (!string.IsNullOrWhiteSpace(Entry.LaunchUri))
            {
                lines.Add(Entry.LaunchUri);
            }

            if (Tags.Count > 0)
            {
                lines.Add("标签：" + string.Join(" / ", Tags));
            }

            if (LaunchCount > 0)
            {
                lines.Add($"启动 {LaunchCount} 次" + (LastLaunchAt is { } t ? $"，最近 {t:yyyy-MM-dd HH:mm}" : string.Empty));
            }
            else
            {
                lines.Add("还没有启动记录");
            }

            if (!string.IsNullOrWhiteSpace(Notes))
            {
                lines.Add("备注：" + Notes);
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>是否被勾选（预留多选批量操作）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// 补齐缺失的视觉资源：没有封面就生成占位图，没有图标就从 exe 提取。
    /// 返回 true 表示有字段被更新（调用方需要保存库）。
    /// </summary>
    public bool EnsureVisuals(bool forceRefresh = false)
    {
        var changed = false;

        // 图标：只有指向 exe 的游戏才提取
        if ((forceRefresh || string.IsNullOrWhiteSpace(Entry.IconPath) || !File.Exists(Entry.IconPath))
            && Entry.LaunchKind == LaunchKind.Executable
            && !string.IsNullOrWhiteSpace(Entry.ExecutablePath)
            && File.Exists(Entry.ExecutablePath))
        {
            var cached = IconService.CacheIcon(Entry.ExecutablePath, Entry.Id);
            if (!string.IsNullOrWhiteSpace(cached) && cached != Entry.IconPath)
            {
                Entry.IconPath = cached!;
                changed = true;
            }
        }

        // 封面文件被用户手动删掉时，回退到图标 / 占位图
        if (!string.IsNullOrWhiteSpace(Entry.CoverPath) && !File.Exists(Entry.CoverPath))
        {
            Entry.CoverPath = string.Empty;
            changed = true;
        }

        return changed;
    }

    /// <summary>Entry 被外部（编辑窗口 / 启动统计）修改后刷新所有绑定。</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(LaunchCount));
        OnPropertyChanged(nameof(LastLaunchAt));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(PrimaryImage));
        OnPropertyChanged(nameof(HasIconBadge));
        OnPropertyChanged(nameof(IconImage));
        OnPropertyChanged(nameof(UsesPlaceholder));
        OnPropertyChanged(nameof(InitialGlyph));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Tooltip));
    }
}
