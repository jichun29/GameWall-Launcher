using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
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

        // 构造时只做「不碰 Shell」的轻量检查：清掉失效的封面路径。
        // 从 exe 提取图标要过 COM（IShellItemImageFactory），几十个游戏就会把
        // 首屏拖到 2 秒以上，所以留给 WarmUpVisualsAsync 在后台补。
        EnsureVisuals(allowExtractIcon: false);
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

    private Brush? _placeholderBrush;

    /// <summary>
    /// 卡片封面图：只有用户自己指定的封面才满铺。
    ///
    /// 这里曾经把 exe 图标也塞进来满铺，结果是灾难——exe 里的图标大多是 32×32 的
    /// 小图，拉伸到 208×208 就是一团黑影（A Pond Hunt Demo 的图标就是纯黑的）。
    /// 现在图标改成「居中摆在渐变底上的徽标」，见 <see cref="ShowIcon"/>。
    /// </summary>
    public string PrimaryImage =>
        !string.IsNullOrWhiteSpace(Entry.CoverPath) && File.Exists(Entry.CoverPath)
            ? Entry.CoverPath
            : string.Empty;

    /// <summary>有用户指定的封面图。</summary>
    public bool HasCover => !string.IsNullOrWhiteSpace(PrimaryImage);

    /// <summary>
    /// 没有封面也没有图标时铺在卡片底下的渐变。直接在内存里算，不落盘、不编码 PNG，
    /// 绑定期间的开销可以忽略（早期版本这里会现渲染一张 480×640 的图，几十张卡片就是半秒）。
    /// </summary>
    public Brush PlaceholderBrush =>
        _placeholderBrush ??= CoverFactory.CreatePlaceholderBrush(Entry.Name);

    /// <summary>exe 图标文件路径（没有则为空）。</summary>
    public string IconImage =>
        !string.IsNullOrWhiteSpace(Entry.IconPath) && File.Exists(Entry.IconPath)
            ? Entry.IconPath
            : string.Empty;

    /// <summary>
    /// 卡片上要不要把 exe 图标当「徽标」画出来。
    ///
    /// 规则：没有自定义封面、但有图标 → 铺渐变底 + 居中画图标。
    /// 有自定义封面时不再叠图标，避免盖住用户挑的图。
    /// </summary>
    public bool ShowIcon => !HasCover && !string.IsNullOrEmpty(IconImage);

    /// <summary>既没有封面也没有图标，只能显示名字首字。</summary>
    public bool UsesPlaceholder => !HasCover && string.IsNullOrEmpty(IconImage);

    /// <summary>
    /// 占位封面上显示的字。
    ///
    /// 中文名取前两个字（「英雄联盟」→「英雄」）：只留一个字在一整墙卡片里
    /// 根本认不出是哪款游戏。英文名取前两三个单词的首字母（Sniper Elite → SE、
    /// It Takes Two → ITT），连写的一整串（JAPANSTIGMATIZED）取前两个字母。
    /// 字数变多以后由 XAML 里的 Viewbox 自动缩放，不会溢出标牌。
    /// </summary>
    public string InitialGlyph => BuildGlyph(Name);

    private static string BuildGlyph(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var trimmed = name.Trim();

        // 中文（或任何非 ASCII 字母）开头：取前两个字
        if (trimmed[0] > 0x7F)
        {
            return trimmed.Length >= 2 ? trimmed[..2] : trimmed[..1];
        }

        var words = trimmed
            .Split(new[] { ' ', '-', ':', '_', '.', '\'', '!' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 0 && char.IsLetter(w[0]))
            .Take(3)
            .ToList();

        if (words.Count >= 2)
        {
            return string.Concat(words.Select(w => char.ToUpperInvariant(w[0])));
        }

        // 单个单词：取前两个字母，撑满标牌不显得空
        var word = words.Count > 0 ? words[0] : trimmed;
        return word.Length >= 2
            ? (char.ToUpperInvariant(word[0]).ToString() + char.ToUpperInvariant(word[1]))
            : char.ToUpperInvariant(word[0]).ToString();
    }

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
    /// <param name="forceRefresh">true 时无视已有缓存，重新提取图标</param>
    /// <param name="allowExtractIcon">
    /// 是否允许走 Shell 提取图标。构造卡片时传 false，把这一步留给后台预热，
    /// 避免几十张卡片排队过 COM 把首屏拖慢。
    /// </param>
    public bool EnsureVisuals(bool forceRefresh = false, bool allowExtractIcon = true)
    {
        var changed = false;

        // 图标：只要能定位到本地 exe 就提取。
        // 平台游戏（Steam/Epic/GOG）虽然没有「必须双击的 exe」，
        // 但从安装目录里找到的主程序图标比占位渐变好看得多。
        if (allowExtractIcon
            && (forceRefresh || string.IsNullOrWhiteSpace(Entry.IconPath) || !File.Exists(Entry.IconPath))
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

        // 还没封面时去本地找一张：游戏目录自带图片 → steam_appid.txt →
        // Steam appid → Steam 库名模糊匹配。
        // 跟提取图标一样属于「重 IO」，所以放在同一个 allowExtractIcon 开关后面，
        // 首屏构造卡片时不会碰它，等后台预热再补。
        if (allowExtractIcon && string.IsNullOrWhiteSpace(Entry.CoverPath))
        {
            var matched = CoverMatcher.TryResolve(Entry.Name, Entry.ExecutablePath, Entry.Id);
            var cover = matched?.Path;

            // 兜底：协议启动的 Steam 游戏直接按 appid 取
            if (string.IsNullOrWhiteSpace(cover) && Entry.LaunchKind == LaunchKind.Steam)
            {
                cover = CoverArtService.TryFetchSteamCover(Entry.LaunchUri, Entry.Id);
            }

            if (!string.IsNullOrWhiteSpace(cover))
            {
                Entry.CoverPath = cover!;
                changed = true;
            }
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
        OnPropertyChanged(nameof(HasCover));
        OnPropertyChanged(nameof(PlaceholderBrush));
        OnPropertyChanged(nameof(IconImage));
        OnPropertyChanged(nameof(ShowIcon));
        OnPropertyChanged(nameof(UsesPlaceholder));
        OnPropertyChanged(nameof(InitialGlyph));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Tooltip));
    }
}
