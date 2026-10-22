using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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

        public void Refresh()
        {
            OnPropertyChanged(nameof(IsApplied));
            OnPropertyChanged(nameof(StateText));
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
        }

        public string Title { get; }
        public string Glyph { get; }
        public ObservableCollection<HardwareRow> Rows { get; } = new();
    }

    /// <summary>
    /// 游戏工具箱的视图模型：硬件快照 + 优化清单 + 电源方案。
    /// 所有操作都是用户显式点击才执行，不会在后台偷偷改系统。
    /// </summary>
    public sealed class GameToolboxViewModel : ObservableObject
    {
        private readonly TweakService _tweaks = new();
        private HardwareSnapshot _snapshot = new();
        private bool _isElevated;
        private string _statusText = "点「重新检测」可以刷新硬件信息。";
        private bool _isBusy;
        private string _selectedCategory = "全部";
        private bool _showAppliedOnly;
        private bool _includeHighRisk;

        public GameToolboxViewModel()
        {
            _isElevated = TweakService.IsElevated();
            Categories.Add("全部");
            foreach (var category in TweakCatalog.Categories) Categories.Add(category);

            RefreshCommand = new RelayCommand(_ => RefreshAll(), _ => !IsBusy);
            ApplySelectedCommand = new AsyncRelayCommand(() => ApplySelectedAsync(), () => !IsBusy && SelectedTweaks.Count > 0);
            RevertAllCommand = new AsyncRelayCommand(() => RevertAllAsync(), () => !IsBusy);
            ExportTextCommand = new RelayCommand(_ => Export("txt"), _ => !IsBusy);
            ExportJsonCommand = new RelayCommand(_ => Export("json"), _ => !IsBusy);
            ApplyPresetCommand = new RelayCommand(p => ApplyPreset(p as string));
            UseHighPerformanceCommand = new AsyncRelayCommand(() => UsePowerPlanAsync(PowerMode.HighPerformance), () => !IsBusy);
            UseUltimateCommand = new AsyncRelayCommand(() => UsePowerPlanAsync(PowerMode.Ultimate), () => !IsBusy);
            UseBalancedCommand = new AsyncRelayCommand(() => UsePowerPlanAsync(PowerMode.Balanced), () => !IsBusy);
            ShowElevateHintCommand = new RelayCommand(_ => ShowElevateHint());

            LoadTweaks();
        }

        // ── 事件 ─────────────────────────────────────────────

        /// <summary>需要主窗口帮忙弹一次性提示时用。</summary>
        public event Action<string, string>? Notify;

        // ── 集合与属性 ───────────────────────────────────────

        public ObservableCollection<HardwareSection> Sections { get; } = new();
        public ObservableCollection<HardwareAdvice> Advices { get; } = new();
        public ObservableCollection<TweakItemViewModel> Tweaks { get; } = new();
        public ObservableCollection<string> Categories { get; } = new();
        public ObservableCollection<PowerPlanInfo> PowerPlans { get; } = new();

        /// <summary>按当前分类 / 「只看已应用」筛选后的可见列表（界面直接绑这个）。</summary>
        public ObservableCollection<TweakItemViewModel> FilteredTweaks { get; } = new();

        public HardwareSnapshot Snapshot => _snapshot;

        public bool IsElevated => _isElevated;

        public string ElevationText => _isElevated
            ? "已以管理员身份运行，系统级优化可以直接应用。"
            : "当前不是管理员身份，带锁标记的优化项需要以管理员身份重新启动才能应用。";

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
                }
            }
        }

        public string SelectedCategory
        {
            get => _selectedCategory;
            set
            {
                if (SetProperty(ref _selectedCategory, value)) ApplyFilter();
            }
        }

        public bool ShowAppliedOnly
        {
            get => _showAppliedOnly;
            set
            {
                if (SetProperty(ref _showAppliedOnly, value)) ApplyFilter();
            }
        }

        public bool IncludeHighRisk
        {
            get => _includeHighRisk;
            set => SetProperty(ref _includeHighRisk, value);
        }

        public RelayCommand RefreshCommand { get; }
        public AsyncRelayCommand ApplySelectedCommand { get; }
        public AsyncRelayCommand RevertAllCommand { get; }
        public RelayCommand ExportTextCommand { get; }
        public RelayCommand ExportJsonCommand { get; }
        public RelayCommand ApplyPresetCommand { get; }
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
                return applied == 0 ? "还没有应用任何优化项" : $"已应用 {applied} 项优化";
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
                        ApplySelectedCommand.NotifyCanExecuteChanged();
                    },
                });
            }
        }

        /// <summary>
        /// 重建筛选后的可见列表。
        /// 用 ObservableCollection 原地增删而不是换一个新 List —— 换引用会让 ListBox
        /// 整个重建容器，勾选状态与滚动位置都会跳掉。
        /// </summary>
        private void ApplyFilter()
        {
            IEnumerable<TweakItemViewModel> query = Tweaks;
            if (!string.Equals(SelectedCategory, "全部", StringComparison.Ordinal))
            {
                query = query.Where(t => string.Equals(t.Category, SelectedCategory, StringComparison.Ordinal));
            }
            if (ShowAppliedOnly) query = query.Where(t => t.IsApplied);

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

        private void BuildSections()
        {
            Sections.Clear();

            var cpu = new HardwareSection("处理器", "🧠");
            cpu.Rows.Add(new HardwareRow("型号", _snapshot.Cpu.Name));
            cpu.Rows.Add(new HardwareRow("核心", _snapshot.Cpu.TopologyText));
            cpu.Rows.Add(new HardwareRow("架构", _snapshot.Cpu.Architecture));
            if (_snapshot.Cpu.BaseClockMhz > 0)
            {
                cpu.Rows.Add(new HardwareRow("基准频率", $"{_snapshot.Cpu.BaseClockMhz} MHz"));
            }
            Sections.Add(cpu);

            var gpu = new HardwareSection("显卡", "🎮");
            if (_snapshot.Gpus.Count == 0)
            {
                gpu.Rows.Add(new HardwareRow("状态", "未能读取到显卡信息"));
            }
            else
            {
                foreach (var item in _snapshot.Gpus)
                {
                    gpu.Rows.Add(new HardwareRow("型号", item.Name));
                    gpu.Rows.Add(new HardwareRow("显存", item.MemoryText));
                    if (!string.IsNullOrEmpty(item.DriverVersion))
                    {
                        var date = string.IsNullOrEmpty(item.DriverDate) ? string.Empty : $" / {item.DriverDate}";
                        gpu.Rows.Add(new HardwareRow("驱动", item.DriverVersion + date));
                    }
                }
            }
            Sections.Add(gpu);

            var memory = new HardwareSection("内存", "💾");
            memory.Rows.Add(new HardwareRow("容量", _snapshot.Memory.TotalText));
            memory.Rows.Add(new HardwareRow("已用", $"{_snapshot.Memory.UsedText}（{_snapshot.Memory.LoadPercent}%）"));
            memory.Rows.Add(new HardwareRow("可用", _snapshot.Memory.AvailableText));
            Sections.Add(memory);

            var disks = new HardwareSection("磁盘", "🗄");
            if (_snapshot.Disks.Count == 0)
            {
                disks.Rows.Add(new HardwareRow("状态", "未能读取到磁盘信息"));
            }
            else
            {
                foreach (var disk in _snapshot.Disks)
                {
                    var label = string.IsNullOrWhiteSpace(disk.Label) ? disk.DriveType : $"{disk.DriveType} · {disk.Label}";
                    disks.Rows.Add(new HardwareRow($"{disk.Drive}（{label}）", disk.UsageText, $"已用 {disk.UsedPercent:0}%"));
                }
            }
            Sections.Add(disks);

            var displays = new HardwareSection("显示器", "🖥");
            if (_snapshot.Displays.Count == 0)
            {
                displays.Rows.Add(new HardwareRow("状态", "未能读取到显示器信息"));
            }
            else
            {
                foreach (var display in _snapshot.Displays)
                {
                    displays.Rows.Add(new HardwareRow(
                        display.Name + (display.IsPrimary ? "（主屏）" : string.Empty),
                        display.ResolutionText,
                        $"{display.BitsPerPixel} bit"));
                }
            }
            Sections.Add(displays);

            var machine = new HardwareSection("主板与系统", "🧩");
            machine.Rows.Add(new HardwareRow("机型", _snapshot.Machine.MachineText));
            if (!string.IsNullOrEmpty(_snapshot.Machine.Board))
            {
                machine.Rows.Add(new HardwareRow("主板", _snapshot.Machine.Board));
            }
            if (!string.IsNullOrEmpty(_snapshot.Machine.BiosVersion))
            {
                machine.Rows.Add(new HardwareRow("BIOS", _snapshot.Machine.BiosVersion,
                    string.IsNullOrEmpty(_snapshot.Machine.BiosDate) ? null : _snapshot.Machine.BiosDate));
            }
            machine.Rows.Add(new HardwareRow("系统",
                $"{_snapshot.Machine.OsName} {_snapshot.Machine.OsVersion}".Trim()));
            machine.Rows.Add(new HardwareRow("内部版本", _snapshot.Machine.OsBuild));
            Sections.Add(machine);

            var network = new HardwareSection("网络", "🌐");
            if (_snapshot.Networks.Count == 0)
            {
                network.Rows.Add(new HardwareRow("状态", "未能读取到网卡信息"));
            }
            else
            {
                foreach (var nic in _snapshot.Networks)
                {
                    network.Rows.Add(new HardwareRow(
                        $"{nic.Name}（{nic.Type}）",
                        nic.IsUp ? $"已连接 · {nic.SpeedText}" : "未连接",
                        string.IsNullOrEmpty(nic.MacAddress) ? null : "MAC " + nic.MacAddress));
                }
            }
            Sections.Add(network);
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
                StatusText = "勾选项里包含高风险项目，请先在上方打开「包含高风险项」。";
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

        /// <summary>还原单条（界面上的「还原」按钮）。</summary>
        public async Task RevertOneAsync(TweakItemViewModel? item)
        {
            if (item == null) return;
            IsBusy = true;
            try
            {
                var result = await Task.Run(() => _tweaks.Revert(item.Id));
                if (result.Success) item.IsApplied = false;
                ApplyFilter();
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

                PowerPlans.Clear();
                foreach (var plan in await Task.Run(PowerPlanService.ListPlans)) PowerPlans.Add(plan);
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

        /// <summary>切换到列表里任意一套方案（界面双击时调）。</summary>
        public async Task ActivatePlanAsync(PowerPlanInfo? plan)
        {
            if (plan == null) return;
            IsBusy = true;
            try
            {
                var result = await Task.Run(() => PowerPlanService.SetActive(plan.Guid));
                StatusText = result.Message;
                PowerPlans.Clear();
                foreach (var item in await Task.Run(PowerPlanService.ListPlans)) PowerPlans.Add(item);
            }
            finally
            {
                IsBusy = false;
            }
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
