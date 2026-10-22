using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using GameWall.Models;
using GameWall.Services;

namespace GameWall.ViewModels
{
    /// <summary>优化项在界面上的可勾选包装。</summary>
    public sealed class TweakItemViewModel : ObservableObject
    {
        private bool _isSelected;
        private bool _isApplied;

        public TweakItemViewModel(TweakDefinition definition, bool applied, bool selected)
        {
            Definition = definition;
            _isApplied = applied;
            _isSelected = selected;
        }

        public TweakDefinition Definition { get; }

        public string Id => Definition.Id;
        public string Name => Definition.Name;
        public string Summary => Definition.Summary;
        public string Effect => Definition.Effect;
        public string Category => Definition.Category;
        public string RiskText => Definition.RiskText + (Definition.NeedsRestart ? " · 需重启" : string.Empty);

        /// <summary>高风险项在界面上用红色，普通项用中性色。</summary>
        public bool IsHighRisk => Definition.Risk == TweakRisk.High;
        public bool IsMediumRisk => Definition.Risk == TweakRisk.Medium;

        /// <summary>需要管理员权限才能应用，界面上打个小锁标记。</summary>
        public string AdminBadge => Definition.NeedsAdmin ? "需要管理员" : string.Empty;
        public bool NeedsAdmin => Definition.NeedsAdmin;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value)) SelectionChanged?.Invoke();
            }
        }

        /// <summary>勾选状态变了就通知工具箱刷新「应用勾选的优化（N）」。</summary>
        public Action? SelectionChanged { get; set; }

        public bool IsApplied
        {
            get => _isApplied;
            set
            {
                if (SetProperty(ref _isApplied, value))
                {
                    OnPropertyChanged(nameof(StateText));
                    OnPropertyChanged(nameof(IsRevertVisible));
                }
            }
        }

        public string StateText => IsApplied ? "已应用" : "未应用";

        public bool IsRevertVisible => IsApplied;

        /// <summary>搜索用：名称、说明、效果三处任意命中即可。</summary>
        public bool Matches(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return true;
            return Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                   || Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                   || Effect.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                   || Id.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }

        public void Refresh()
        {
            OnPropertyChanged(nameof(IsApplied));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsRevertVisible));
        }
    }

    /// <summary>左侧分类栏的一项。</summary>
    public sealed class ToolboxNavItem : ObservableObject
    {
        private bool _isSelected;
        private int _count;
        private string _badgeText = string.Empty;

        public ToolboxNavItem(string key, string title, string glyph, bool showTopDivider = false)
        {
            Key = key;
            Title = title;
            Glyph = glyph;
            ShowTopDivider = showTopDivider;
        }

        /// <summary>分类键：hardware / power / 分类名。</summary>
        public string Key { get; }

        public string Title { get; }

        public string Glyph { get; }

        /// <summary>是不是某一组的第一项，界面上在它上面画一条分隔线。</summary>
        public bool ShowTopDivider { get; }

        /// <summary>右侧角标上的数字（优化项条数 / 已应用条数），0 就不显示。</summary>
        public int Count
        {
            get => _count;
            set
            {
                if (SetProperty(ref _count, value))
                {
                    BadgeText = value > 0 ? value.ToString() : string.Empty;
                    OnPropertyChanged(nameof(HasBadge));
                }
            }
        }

        public string BadgeText
        {
            get => _badgeText;
            private set
            {
                if (SetProperty(ref _badgeText, value)) OnPropertyChanged(nameof(HasBadge));
            }
        }

        public bool HasBadge => !string.IsNullOrEmpty(BadgeText);

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }

    /// <summary>硬件信息行的通用展示单元（标签 + 值）。</summary>
    public sealed class HardwareRow
    {
        public HardwareRow(string label, string value, string? hint = null)
        {
            Label = label;
            Value = value;
            Hint = hint ?? string.Empty;
        }

        public string Label { get; }
        public string Value { get; }
        public string Hint { get; }
        public bool HasHint => !string.IsNullOrEmpty(Hint);
    }

    /// <summary>一个硬件分区（标题 + 若干行）。</summary>
    public sealed class HardwareSection
    {
        public HardwareSection(string title, string glyph)
        {
            Title = title;
            Glyph = glyph;
            HasContent = true;
        }

        public string Title { get; }
        public string Glyph { get; }
        public ObservableCollection<HardwareRow> Rows { get; } = new();

        /// <summary>整块都没读到信息时为 false，界面上直接不画这个分区。</summary>
        public bool HasContent { get; set; }
    }

    /// <summary>
    /// 游戏工具箱的视图模型：左栏分类、右栏整块内容。
    /// 三个页面分别是「硬件信息」「优化方案」「电源方案」，中间六类优化项共用同一个
    /// 页面，靠左栏的选中项切换分类。
    /// 所有操作都是用户显式点击才执行，不会在后台偷偷改系统。
    /// </summary>
    public sealed class GameToolboxViewModel : ObservableObject
    {
        /// <summary>左栏里「硬件信息」那一项的键。</summary>
        public const string NavHardware = "hardware";

        /// <summary>左栏里「电源方案」那一项的键。</summary>
        public const string NavPower = "power";

        private readonly TweakService _tweaks = new();
        private HardwareSnapshot _snapshot = new();
        private bool _isElevated;
        private string _statusText = "点「重新检测」可以刷新硬件信息。";
        private bool _isBusy;
        private ToolboxNavItem? _selectedNav;
        private bool _showAppliedOnly;
        private bool _includeHighRisk;
        private string _searchText = string.Empty;
        private PowerPlanInfo? _selectedPlan;

        public GameToolboxViewModel()
        {
            _isElevated = TweakService.IsElevated();

            RefreshCommand = new RelayCommand(_ => RefreshAll(), _ => !IsBusy);
            ApplySelectedCommand = new AsyncRelayCommand(() => ApplySelectedAsync(), () => !IsBusy && SelectedTweaks.Count > 0);
            RevertAllCommand = new AsyncRelayCommand(() => RevertAllAsync(), () => !IsBusy);
            ExportTextCommand = new RelayCommand(_ => Export("txt"), _ => !IsBusy);
            ExportJsonCommand = new RelayCommand(_ => Export("json"), _ => !IsBusy);
            ApplyPresetCommand = new RelayCommand(p => ApplyPreset(p as string));
            SelectAllCommand = new RelayCommand(_ => SelectAllInCurrentCategory(), _ => !IsBusy);
            ClearSelectionCommand = new RelayCommand(_ => ClearSelection(), _ => !IsBusy);
            UseHighPerformanceCommand = new AsyncRelayCommand(() => UsePowerPlanAsync(PowerMode.HighPerformance), () => !IsBusy);
            UseUltimateCommand = new AsyncRelayCommand(() => UsePowerPlanAsync(PowerMode.Ultimate), () => !IsBusy);
            UseBalancedCommand = new AsyncRelayCommand(() => UsePowerPlanAsync(PowerMode.Balanced), () => !IsBusy);
            ShowElevateHintCommand = new RelayCommand(_ => ShowElevateHint());

            BuildNavItems();
            LoadTweaks();
        }

        // ── 事件 ─────────────────────────────────────────────

        /// <summary>需要主窗口帮忙弹一次性提示时用。</summary>
        public event Action<string, string>? Notify;

        // ── 集合与属性 ───────────────────────────────────────

        public ObservableCollection<HardwareSection> Sections { get; } = new();
        public ObservableCollection<HardwareAdvice> Advices { get; } = new();
        public ObservableCollection<TweakItemViewModel> Tweaks { get; } = new();
        public ObservableCollection<PowerPlanInfo> PowerPlans { get; } = new();

        /// <summary>左侧分类栏。</summary>
        public ObservableCollection<ToolboxNavItem> NavItems { get; } = new();

        /// <summary>按当前分类 / 搜索 / 「只看已应用」筛选后的可见列表（界面直接绑这个）。</summary>
        public ObservableCollection<TweakItemViewModel> FilteredTweaks { get; } = new();

        public HardwareSnapshot Snapshot => _snapshot;

        public string VersionText => AppInfo.VersionLabel;

        public bool IsElevated => _isElevated;

        public string ElevationText => _isElevated
            ? "已以管理员身份运行，系统级优化可以直接应用。"
            : "当前不是管理员身份，带锁标记的优化项需要以管理员身份重新启动才能应用。";

        public string ElevationShortText => _isElevated ? "管理员权限" : "普通权限";

        public string ElevationHintText => _isElevated
            ? "系统级项目也能直接改"
            : "带锁的项目需要管理员身份";

        /// <summary>分类栏底部那个小圆点：有权限是绿的，没有是琥珀的。</summary>
        public Brush ElevationBrush => _isElevated
            ? (Brush)Application.Current.Resources["Brush.Success"]
            : (Brush)Application.Current.Resources["Brush.Warning"];

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    RefreshCommand.NotifyCanExecuteChanged();
                    ApplySelectedCommand.NotifyCanExecuteChanged();
                    RevertAllCommand.NotifyCanExecuteChanged();
                    SelectAllCommand.NotifyCanExecuteChanged();
                    ClearSelectionCommand.NotifyCanExecuteChanged();
                }
            }
        }

        // ── 左栏导航 ─────────────────────────────────────────

        public ToolboxNavItem? SelectedNav
        {
            get => _selectedNav;
            set
            {
                if (value == null) return;
                if (!SetProperty(ref _selectedNav, value)) return;

                foreach (var item in NavItems) item.IsSelected = ReferenceEquals(item, value);
                RaisePageState();
                ApplyFilter();
            }
        }

        /// <summary>当前是不是「硬件信息」页。</summary>
        public bool IsHardwarePage => SelectedNav?.Key == NavHardware;

        /// <summary>当前是不是「电源方案」页。</summary>
        public bool IsPowerPage => SelectedNav?.Key == NavPower;

        /// <summary>其余分类都归到「优化方案」页，靠 FilteredTweaks 换内容。</summary>
        public bool IsTweakPage => !IsHardwarePage && !IsPowerPage;

        public string CurrentTitle => SelectedNav?.Title ?? "硬件信息";

        public string CurrentSubtitle
        {
            get
            {
                if (IsHardwarePage) return "本机 CPU / 显卡 / 内存 / 磁盘 / 显示器 / 主板与网络，全部只读采集。";
                if (IsPowerPage) return "切换系统电源方案，改的是 Windows 自己的设置。";
                var applied = FilteredTweaks.Count(t => t.IsApplied);
                return $"{FilteredTweaks.Count} 项，其中 {applied} 项已应用。勾选后点右下角「应用勾选的优化」。";
            }
        }

        private void RaisePageState()
        {
            OnPropertyChanged(nameof(IsHardwarePage));
            OnPropertyChanged(nameof(IsPowerPage));
            OnPropertyChanged(nameof(IsTweakPage));
            OnPropertyChanged(nameof(CurrentTitle));
            OnPropertyChanged(nameof(CurrentSubtitle));
        }

        private void BuildNavItems()
        {
            NavItems.Add(new ToolboxNavItem(NavHardware, "硬件信息", "🧠"));

            var first = true;
            foreach (var category in TweakCatalog.Categories)
            {
                // 六个优化分类属于同一组，组头上画一条分隔线，和上下两组区分开
                NavItems.Add(new ToolboxNavItem(category, category, GlyphFor(category), showTopDivider: first));
                first = false;
            }

            NavItems.Add(new ToolboxNavItem(NavPower, "电源方案", "⚡", showTopDivider: true));

            _selectedNav = NavItems[0];
            _selectedNav.IsSelected = true;
            OnPropertyChanged(nameof(SelectedNav));
            RaisePageState();
        }

        private static string GlyphFor(string category) => category switch
        {
            "游戏性能" => "🚀",
            "图形与显示" => "🎮",
            "输入延迟" => "🖱",
            "网络" => "🌐",
            "后台与系统" => "🧩",
            "隐私与广告" => "🛡",
            _ => "•",
        };

        // ── 优化项筛选 ───────────────────────────────────────

        public bool ShowAppliedOnly
        {
            get => _showAppliedOnly;
            set
            {
                if (SetProperty(ref _showAppliedOnly, value)) ApplyFilter();
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value ?? string.Empty)) ApplyFilter();
            }
        }

        public bool IncludeHighRisk
        {
            get => _includeHighRisk;
            set => SetProperty(ref _includeHighRisk, value);
        }

        public bool IsTweakEmpty => IsTweakPage && FilteredTweaks.Count == 0;

        public string TweakEmptyHint => string.IsNullOrWhiteSpace(SearchText)
            ? "这一类里的项目可能都已经应用了。取消「只看已应用」看看全部。"
            : $"没有名称或说明里带「{SearchText}」的优化项。";

        public RelayCommand RefreshCommand { get; }
        public AsyncRelayCommand ApplySelectedCommand { get; }
        public AsyncRelayCommand RevertAllCommand { get; }
        public RelayCommand ExportTextCommand { get; }
        public RelayCommand ExportJsonCommand { get; }
        public RelayCommand ApplyPresetCommand { get; }
        public RelayCommand SelectAllCommand { get; }
        public RelayCommand ClearSelectionCommand { get; }
        public AsyncRelayCommand UseHighPerformanceCommand { get; }
        public AsyncRelayCommand UseUltimateCommand { get; }
        public AsyncRelayCommand UseBalancedCommand { get; }
        public RelayCommand ShowElevateHintCommand { get; }

        public List<TweakItemViewModel> SelectedTweaks => Tweaks.Where(t => t.IsSelected && !t.IsApplied).ToList();

        public string AppliedSummary
        {
            get
            {
                var applied = Tweaks.Count(t => t.IsApplied);
                var total = Tweaks.Count;
                return applied == 0
                    ? $"共 {total} 项优化方案，还没有应用任何一项"
                    : $"共 {total} 项优化方案，已应用 {applied} 项";
            }
        }

        // ── 电源方案 ─────────────────────────────────────────

        public PowerPlanInfo? SelectedPlan
        {
            get => _selectedPlan;
            set
            {
                if (SetProperty(ref _selectedPlan, value))
                {
                    OnPropertyChanged(nameof(HasPlanSelection));
                    OnPropertyChanged(nameof(PlanHintText));
                }
            }
        }

        public bool HasPlanSelection => SelectedPlan != null && !SelectedPlan.IsActive;

        public string PlanHintText
        {
            get
            {
                if (SelectedPlan == null) return "选中一套方案后点左边按钮切换。";
                return SelectedPlan.IsActive
                    ? $"「{SelectedPlan.Name}」正在使用中。"
                    : $"将把系统电源方案切换到「{SelectedPlan.Name}」。";
            }
        }

        // ── 加载 ─────────────────────────────────────────────

        private void LoadTweaks()
        {
            var applied = _tweaks.AppliedIds;
            Tweaks.Clear();
            foreach (var definition in TweakCatalog.All)
            {
                var isApplied = applied.Contains(definition.Id, StringComparer.OrdinalIgnoreCase)
                                || TweakService.IsCurrentlyOn(definition);
                // 高风险项默认不勾，安全项也默认不勾 —— 免得用户稀里糊涂点了「应用」
                Tweaks.Add(new TweakItemViewModel(definition, isApplied, selected: false)
                {
                    SelectionChanged = () =>
                    {
                        OnPropertyChanged(nameof(SelectedCountText));
                        OnPropertyChanged(nameof(AppliedSummary));
                        ApplySelectedCommand.NotifyCanExecuteChanged();
                        UpdateNavBadges();
                    },
                });
            }
            UpdateNavBadges();
        }

        /// <summary>分类角标：没应用的显示总数，全应用完了显示已应用数。</summary>
        private void UpdateNavBadges()
        {
            foreach (var nav in NavItems)
            {
                if (nav.Key == NavHardware)
                {
                    nav.Count = Sections.Count;
                    continue;
                }
                if (nav.Key == NavPower)
                {
                    nav.Count = 0;
                    continue;
                }
                nav.Count = Tweaks.Count(t => t.Category == nav.Key);
            }
        }

        /// <summary>
        /// 重建筛选后的可见列表。
        /// 用 ObservableCollection 原地增删而不是换一个新 List —— 换引用会让 ListBox
        /// 整个重建容器，勾选状态与滚动位置都会跳掉。
        /// </summary>
        private void ApplyFilter()
        {
            // 左栏的高亮跟着 SelectedNav 走；这里再兜一次，防止外部改了集合后高亮丢了
            if (_selectedNav != null) _selectedNav.IsSelected = true;

            IEnumerable<TweakItemViewModel> query = Tweaks;
            if (!IsHardwarePage && !IsPowerPage && _selectedNav != null)
            {
                var category = _selectedNav.Key;
                query = query.Where(t => string.Equals(t.Category, category, StringComparison.Ordinal));
            }
            if (ShowAppliedOnly) query = query.Where(t => t.IsApplied);
            if (!string.IsNullOrWhiteSpace(SearchText)) query = query.Where(t => t.Matches(SearchText.Trim()));

            var wanted = query.ToList();
            for (var i = FilteredTweaks.Count - 1; i >= 0; i--)
            {
                if (!wanted.Contains(FilteredTweaks[i])) FilteredTweaks.RemoveAt(i);
            }
            for (var i = 0; i < wanted.Count; i++)
            {
                if (i >= FilteredTweaks.Count || !ReferenceEquals(FilteredTweaks[i], wanted[i]))
                {
                    FilteredTweaks.Insert(i, wanted[i]);
                }
            }

            OnPropertyChanged(nameof(AppliedSummary));
            OnPropertyChanged(nameof(SelectedCountText));
            OnPropertyChanged(nameof(CurrentSubtitle));
            OnPropertyChanged(nameof(IsTweakEmpty));
            OnPropertyChanged(nameof(TweakEmptyHint));
            ApplySelectedCommand.NotifyCanExecuteChanged();
        }

        /// <summary>当前勾选了但还没应用的条数，显示在按钮上。</summary>
        public string SelectedCountText
        {
            get
            {
                var count = SelectedTweaks.Count;
                return count == 0 ? "应用勾选的优化" : $"应用勾选的优化（{count}）";
            }
        }

        public void RefreshAll()
        {
            IsBusy = true;
            try
            {
                _snapshot = HardwareService.Capture();
                BuildSections();
                BuildAdvices();
                LoadTweaks();
                ApplyFilter();

                PowerPlans.Clear();
                foreach (var plan in PowerPlanService.ListPlans()) PowerPlans.Add(plan);
                SelectedPlan = PowerPlans.FirstOrDefault(p => p.IsActive);

                StatusText = $"硬件信息已更新（{DateTime.Now:HH:mm:ss}）。" +
                             (IsElevated ? string.Empty : " 部分系统级项目需要管理员权限。");
            }
            catch (Exception ex)
            {
                Logger.Error("刷新硬件信息失败", ex);
                StatusText = "刷新硬件信息时出现问题，详情见日志。";
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ── 当前分类的批量勾选 ───────────────────────────────

        /// <summary>勾选当前分类里所有还没应用的项。</summary>
        private void SelectAllInCurrentCategory()
        {
            var targets = FilteredTweaks.Where(t => !t.IsApplied).ToList();
            foreach (var item in targets)
            {
                if (item.Definition.Risk == TweakRisk.High && !IncludeHighRisk) continue;
                item.IsSelected = true;
            }
            StatusText = targets.Count == 0
                ? "当前分类里没有可勾选的项。"
                : $"已勾选当前分类的 {targets.Count(t => t.IsSelected)} 项，点「应用勾选的优化」生效。";
            ApplyFilter();
        }

        private void ClearSelection()
        {
            foreach (var item in Tweaks) item.IsSelected = false;
            StatusText = "已清空勾选。";
            ApplyFilter();
        }

        /// <summary>清空搜索与「只看已应用」，界面上的空状态按钮用。</summary>
        public void ResetFilter()
        {
            _searchText = string.Empty;
            _showAppliedOnly = false;
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(ShowAppliedOnly));
            ApplyFilter();
        }

        /// <summary>
        /// 把硬件快照铺成分区。约定：值为空串的行直接不加 —— 截图里那些「未知」既没信息量，
        /// 又会让人误以为检测失败。整块都读不到的，只在分区里留一条状态说明。
        /// </summary>
        private void BuildSections()
        {
            Sections.Clear();

            var cpu = new HardwareSection("处理器", "🧠");
            AddRow(cpu, "型号", _snapshot.Cpu.Name);
            AddRow(cpu, "核心", _snapshot.Cpu.TopologyText);
            AddRow(cpu, "架构", _snapshot.Cpu.Architecture);
            if (_snapshot.Cpu.BaseClockMhz > 0)
            {
                AddRow(cpu, "基准频率", $"{_snapshot.Cpu.BaseClockMhz} MHz");
            }

            AddSection(cpu);

            var gpu = new HardwareSection("显卡", "🎮");
            var realGpus = _snapshot.Gpus.Where(g => !g.IsVirtual).ToList();
            var virtualGpus = _snapshot.Gpus.Where(g => g.IsVirtual).ToList();
            foreach (var item in realGpus.Concat(virtualGpus))
            {
                AddRow(gpu, "型号", item.Name);
                AddRow(gpu, "显存", item.MemoryText);
                if (!string.IsNullOrEmpty(item.DriverVersion))
                {
                    var date = string.IsNullOrEmpty(item.DriverDate) ? string.Empty : $" / {item.DriverDate}";
                    AddRow(gpu, "驱动", item.DriverVersion + date);
                }
            }

            AddSection(gpu, "未能读取到显卡信息");

            var memory = new HardwareSection("内存", "💾");
            AddRow(memory, "容量", _snapshot.Memory.TotalText);
            if (_snapshot.Memory.TotalBytes > 0)
            {
                AddRow(memory, "已用", $"{_snapshot.Memory.UsedText}（{_snapshot.Memory.LoadPercent}%）");
                AddRow(memory, "可用", _snapshot.Memory.AvailableText);
            }

            AddSection(memory);

            var disks = new HardwareSection("磁盘", "🗄");
            foreach (var disk in _snapshot.Disks)
            {
                var label = string.IsNullOrWhiteSpace(disk.Label) ? disk.DriveType : $"{disk.DriveType} · {disk.Label}";
                AddRow(disks, $"{disk.Drive}（{label}）", disk.UsageText, $"已用 {disk.UsedPercent:0}%");
            }

            AddSection(disks, "未能读取到磁盘信息");

            var displays = new HardwareSection("显示器", "🖥");
            foreach (var display in _snapshot.Displays)
            {
                AddRow(displays,
                    display.Name + (display.IsPrimary ? "（主屏）" : string.Empty),
                    display.ResolutionText,
                    display.BitsPerPixel > 0 ? $"{display.BitsPerPixel} bit" : null);
            }

            AddSection(displays, "未能读取到显示器信息");

            var machine = new HardwareSection("主板与系统", "🧩");
            AddRow(machine, "机型", _snapshot.Machine.MachineText);
            AddRow(machine, "主板", _snapshot.Machine.Board);
            AddRow(machine, "BIOS", _snapshot.Machine.BiosVersion,
                string.IsNullOrEmpty(_snapshot.Machine.BiosDate) ? null : _snapshot.Machine.BiosDate);
            AddRow(machine, "系统", $"{_snapshot.Machine.OsName} {_snapshot.Machine.OsVersion}".Trim());
            AddRow(machine, "内部版本", _snapshot.Machine.OsBuild);
            AddSection(machine);

            var network = new HardwareSection("网络", "🌐");
            foreach (var nic in _snapshot.Networks)
            {
                var speed = string.IsNullOrEmpty(nic.SpeedText) ? string.Empty : $" · {nic.SpeedText}";
                AddRow(network,
                    $"{nic.Name}（{nic.Type}）",
                    nic.IsUp ? $"已连接{speed}" : "未连接",
                    string.IsNullOrEmpty(nic.MacAddress) ? null : "MAC " + nic.MacAddress);
            }

            AddSection(network, "未能读取到网卡信息");
        }

        /// <summary>空值不加行。所有硬件行都从这里进，避免漏判。</summary>
        private static void AddRow(HardwareSection section, string label, string? value, string? hint = null)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            section.Rows.Add(new HardwareRow(label, value!, hint));
        }

        /// <summary>分区里一行都没有时，放一条状态说明，别留一个空壳标题。连说明都没有就整块不收。</summary>
        private void AddSection(HardwareSection section, string emptyHint = "")
        {
            if (section.Rows.Count == 0)
            {
                if (string.IsNullOrEmpty(emptyHint))
                {
                    return;
                }

                section.Rows.Add(new HardwareRow("状态", emptyHint));
            }

            section.HasContent = true;
            Sections.Add(section);
        }

        private void BuildAdvices()
        {
            Advices.Clear();
            foreach (var advice in HardwareService.BuildAdvice(_snapshot, new AppSettings()))
            {
                Advices.Add(advice);
            }
        }

        // ── 应用 / 还原 ──────────────────────────────────────

        /// <summary>按预设一次性勾选一组优化项。</summary>
        private void ApplyPreset(string? preset)
        {
            foreach (var item in Tweaks) item.IsSelected = false;
            if (string.IsNullOrEmpty(preset)) return;

            var ids = preset switch
            {
                "推荐" => new[]
                {
                    "game.mode", "game.mmcss", "game.responsiveness", "sys.gamedvr", "sys.gamebar",
                    "input.mouse.accel", "input.keyboard.repeat", "net.throttling", "gpu.dedicated",
                },
                "竞技（低延迟）" => new[]
                {
                    "input.mouse.accel", "input.keyboard.repeat", "input.menu.delay", "input.snap.default",
                    "game.mmcss", "game.priority.quantum", "net.throttling", "net.timedwait", "net.dns.cache",
                    "sys.audio.duck", "gpu.flipmodel",
                },
                "画质（图形优化）" => new[]
                {
                    "gpu.hags", "gpu.dedicated", "gpu.flipmodel", "gpu.autohdr", "gpu.vrr",
                    "gpu.shadercache", "game.mode",
                },
                _ => Array.Empty<string>(),
            };

            var selected = 0;
            foreach (var id in ids.Distinct())
            {
                var item = Tweaks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
                if (item == null || item.IsApplied) continue;
                item.IsSelected = true;
                selected++;
            }

            StatusText = selected == 0
                ? $"预设「{preset}」里的项目都已经应用过了。"
                : $"已勾选预设「{preset}」中的 {selected} 项，点「应用勾选的优化」生效。";
            ApplyFilter();
        }

        private async Task ApplySelectedAsync()
        {
            var items = SelectedTweaks;
            if (items.Count == 0)
            {
                StatusText = "没有勾选任何优化项。";
                return;
            }

            if (!IncludeHighRisk && items.Any(t => t.Definition.Risk == TweakRisk.High))
            {
                StatusText = "勾选项里包含高风险项目，请先打开右上角的「包含高风险项」。";
                return;
            }

            IsBusy = true;
            StatusText = "正在应用…";
            try
            {
                var result = await Task.Run(() => _tweaks.ApplyMany(items.Select(t => t.Definition), IncludeHighRisk));
                foreach (var item in items)
                {
                    if (_tweaks.IsApplied(item.Id)) item.IsApplied = true;
                }
                ApplyFilter();
                UpdateNavBadges();
                StatusText = result.Message;
                if (result.Failures.Count > 0)
                {
                    Notify?.Invoke("部分优化未成功", result.Message + Environment.NewLine + Environment.NewLine +
                                                      string.Join(Environment.NewLine, result.Failures));
                }
            }
            catch (Exception ex)
            {
                Logger.Error("应用优化项失败", ex);
                StatusText = "应用优化时出现问题：" + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RevertAllAsync()
        {
            IsBusy = true;
            StatusText = "正在还原…";
            try
            {
                var result = await Task.Run(() => _tweaks.RevertAll());
                LoadTweaks();
                ApplyFilter();
                StatusText = result.Message;
            }
            catch (Exception ex)
            {
                Logger.Error("还原优化项失败", ex);
                StatusText = "还原时出现问题：" + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>还原单条（界面上的「撤销」按钮）。</summary>
        public async Task RevertOneAsync(TweakItemViewModel? item)
        {
            if (item == null) return;
            IsBusy = true;
            try
            {
                var result = await Task.Run(() => _tweaks.Revert(item.Id));
                if (result.Success) item.IsApplied = false;
                ApplyFilter();
                UpdateNavBadges();
                StatusText = result.Message;
            }
            catch (Exception ex)
            {
                Logger.Error("还原单条优化失败", ex);
                StatusText = "还原时出现问题：" + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ── 电源方案 ─────────────────────────────────────────

        private enum PowerMode
        {
            Balanced,
            HighPerformance,
            Ultimate,
        }

        private async Task UsePowerPlanAsync(PowerMode mode)
        {
            IsBusy = true;
            try
            {
                var result = await Task.Run(() => mode switch
                {
                    PowerMode.HighPerformance => PowerPlanService.UseHighPerformance(),
                    PowerMode.Ultimate => PowerPlanService.UseUltimatePerformance(),
                    _ => SetBalanced(),
                });

                StatusText = result.Message;
                await ReloadPowerPlansAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("切换电源方案失败", ex);
                StatusText = "切换电源方案时出现问题：" + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static TweakResult SetBalanced()
        {
            var balanced = PowerPlanService.ListPlans()
                .FirstOrDefault(p => p.Name.Contains("平衡", StringComparison.Ordinal));
            return balanced == null
                ? TweakResult.Fail("没找到「平衡」电源方案。")
                : PowerPlanService.SetActive(balanced.Guid);
        }

        /// <summary>切换到列表里选中的方案。</summary>
        public async Task ActivatePlanAsync(PowerPlanInfo? plan)
        {
            if (plan == null) return;
            IsBusy = true;
            try
            {
                var result = await Task.Run(() => PowerPlanService.SetActive(plan.Guid));
                StatusText = result.Message;
                await ReloadPowerPlansAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("切换电源方案失败", ex);
                StatusText = "切换电源方案时出现问题：" + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>重新读一遍方案列表，并把选中项落在当前生效的那套上。</summary>
        public async Task ReloadPowerPlansAsync()
        {
            var plans = await Task.Run(PowerPlanService.ListPlans);
            PowerPlans.Clear();
            foreach (var item in plans) PowerPlans.Add(item);
            SelectedPlan = PowerPlans.FirstOrDefault(p => p.IsActive);
        }

        // ── 导出 ─────────────────────────────────────────────

        private void Export(string format)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = System.IO.Path.GetFileName(HardwareReportService.DefaultFileName(format)),
                Filter = format == "json" ? "JSON 文件 (*.json)|*.json" : "文本文件 (*.txt)|*.txt",
                DefaultExt = "." + format,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var content = format == "json"
                ? HardwareReportService.BuildJson(_snapshot, PowerPlans.ToList())
                : HardwareReportService.BuildText(_snapshot, PowerPlans.ToList());

            var result = HardwareReportService.Save(dialog.FileName, content);
            StatusText = result.Message;
            if (!result.Success)
            {
                Notify?.Invoke("导出失败", result.Message);
            }
        }

        // ── 其它 ─────────────────────────────────────────────

        private void ShowElevateHint()
        {
            if (IsElevated)
            {
                Notify?.Invoke("当前已经是管理员", "现在可以直接应用所有优化项。");
                return;
            }

            Notify?.Invoke(
                "需要管理员权限",
                "带「需要管理员」标记的优化项要改 HKLM 下的键，普通权限写不进去。" + Environment.NewLine +
                Environment.NewLine +
                "做法：关掉 GameWall，右键 GameWall.exe →「以管理员身份运行」，再回到这个页面。" + Environment.NewLine +
                "不需要管理员的那部分（鼠标加速、键盘重复、Game Bar、Game DVR 等）现在就能用。");
        }

        /// <summary>工具箱被打开时调用：采集硬件 + 加载方案。</summary>
        public void Initialize()
        {
            RefreshAll();
        }
    }
}
