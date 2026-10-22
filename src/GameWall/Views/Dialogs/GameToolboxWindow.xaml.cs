using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameWall.Models;
using GameWall.Services;
using GameWall.ViewModels;

namespace GameWall.Views.Dialogs
{
    /// <summary>
    /// 游戏工具箱窗口：左栏是分类，右栏整块显示对应内容。
    /// 「硬件信息」「优化方案」「电源方案」三个页面共用同一个窗口，
    /// 靠左栏的选中项切换；优化方案那页再按分类过滤具体条目。
    /// 所有修改都是用户点按钮才发生，且每条都记了旧值，可以一键还原。
    /// </summary>
    public partial class GameToolboxWindow : Window
    {
        private readonly GameToolboxViewModel _vm = new();

        /// <summary>多选列表切页时要临时关掉选中联动，否则会误触发切换。</summary>
        private bool _suppressNavSync;

        public GameToolboxWindow()
        {
            InitializeComponent();
            DataContext = _vm;

            _vm.Notify += OnNotify;
            _vm.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(GameToolboxViewModel.SelectedNav):
                    case nameof(GameToolboxViewModel.CurrentTitle):
                        SyncNavSelection();
                        break;
                    case nameof(GameToolboxViewModel.SelectedPlan):
                    case nameof(GameToolboxViewModel.HasPlanSelection):
                        SyncPlanSelection();
                        break;
                }
            };

            Loaded += (_, _) =>
            {
                _vm.Initialize();
                SyncNavSelection();
                SyncPlanSelection();
                SyncSummary();
            };
        }

        // ── 标题栏拖动 ───────────────────────────────────────

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && IsInteractive(source)) return;
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标已经抬起，忽略
            }
        }

        private static bool IsInteractive(DependencyObject source)
        {
            var node = source;
            while (node != null)
            {
                if (node is System.Windows.Controls.Button or System.Windows.Controls.Primitives.ButtonBase
                    or System.Windows.Controls.TextBox or System.Windows.Controls.ComboBox
                    or System.Windows.Controls.Primitives.ScrollBar)
                {
                    return true;
                }
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        // ── 界面同步 ─────────────────────────────────────────

        /// <summary>左栏选中项与视图模型对齐（视图模型侧被改时反向同步）。</summary>
        private void SyncNavSelection()
        {
            if (_suppressNavSync) return;
            _suppressNavSync = true;
            try
            {
                NavList.SelectedItem = _vm.SelectedNav;
            }
            finally
            {
                _suppressNavSync = false;
            }
        }

        /// <summary>电源方案列表的选中项与视图模型对齐。</summary>
        private void SyncPlanSelection()
        {
            if (PowerList.SelectedItem == _vm.SelectedPlan) return;
            PowerList.SelectedItem = _vm.SelectedPlan;
        }

        private void SyncSummary()
        {
            StatusText.Text = _vm.StatusText;
            AppliedSummary.Text = _vm.AppliedSummary;
            ApplyButton.Content = _vm.SelectedCountText;
            ApplyButton.IsEnabled = _vm.SelectedTweaks.Count > 0;
        }

        private void OnNotify(string title, string message)
        {
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ── 事件处理 ─────────────────────────────────────────

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _vm.RefreshAll();
            SyncPlanSelection();
            SyncSummary();
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();

            var txt = new MenuItem { Header = "导出为文本（.txt）" };
            txt.Click += (_, _) =>
            {
                _vm.ExportTextCommand.Execute(null);
                SyncSummary();
            };
            var json = new MenuItem { Header = "导出为 JSON（.json）" };
            json.Click += (_, _) =>
            {
                _vm.ExportJsonCommand.Execute(null);
                SyncSummary();
            };

            menu.Items.Add(txt);
            menu.Items.Add(json);
            menu.PlacementTarget = sender as UIElement;
            menu.IsOpen = true;
        }

        private void Search_Changed(object sender, TextChangedEventArgs e)
        {
            _vm.SearchText = SearchBox.Text;
            SyncSummary();
        }

        private void AppliedOnly_Changed(object sender, RoutedEventArgs e)
        {
            _vm.ShowAppliedOnly = AppliedOnlyBox.IsChecked == true;
            SyncSummary();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            _vm.SelectAllCommand.Execute(null);
            SyncSummary();
        }

        private void ClearSelection_Click(object sender, RoutedEventArgs e)
        {
            _vm.ClearSelectionCommand.Execute(null);
            SyncSummary();
        }

        private void ResetFilter_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = string.Empty;
            AppliedOnlyBox.IsChecked = false;
            _vm.ResetFilter();
            SyncSummary();
        }

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string preset })
            {
                _vm.ApplyPresetCommand.Execute(preset);
                SyncSummary();
            }
        }

        private async void ApplySelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = _vm.SelectedTweaks;
            if (selected.Count == 0)
            {
                StatusText.Text = "先勾选要应用的优化项。";
                return;
            }

            var highRisk = selected.Where(t => t.Definition.Risk == TweakRisk.High).ToList();

            var affected = highRisk.Count > 0
                ? $"\n\n其中 {highRisk.Count} 项是高风险（{string.Join("、", highRisk.Select(t => t.Name))}），改完可能需要重启甚至重装驱动。"
                : string.Empty;

            var confirm = MessageBox.Show(
                this,
                $"即将修改 {selected.Count} 项系统设置。\n所有改动都会记下旧值，随时可以在本窗口点「全部还原」写回去。{affected}",
                "确认应用",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;

            ApplyButton.IsEnabled = false;
            try
            {
                _vm.ApplySelectedCommand.Execute(null);
                // AsyncRelayCommand 是 async void，这里等状态回落再刷新界面
                while (_vm.IsBusy)
                {
                    await System.Threading.Tasks.Task.Delay(120);
                }
            }
            finally
            {
                ApplyButton.IsEnabled = true;
                SyncSummary();
            }
        }

        private async void RevertAll_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                this,
                "把本工具改过的注册表值全部写回原样？\n\n只会还原经过这个窗口改动的项，不会动其它设置。",
                "确认还原",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

            _vm.RevertAllCommand.Execute(null);
            while (_vm.IsBusy)
            {
                await System.Threading.Tasks.Task.Delay(120);
            }
            SyncSummary();
        }

        private async void RevertOne_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: TweakItemViewModel item })
            {
                await _vm.RevertOneAsync(item);
                SyncSummary();
            }
        }

        private void PowerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PowerList.SelectedItem is PowerPlanInfo plan)
            {
                _vm.SelectedPlan = plan;
            }
        }

        private async void ActivatePlan_Click(object sender, RoutedEventArgs e)
        {
            if (PowerList.SelectedItem is PowerPlanInfo plan)
            {
                await _vm.ActivatePlanAsync(plan);
                SyncPlanSelection();
                SyncSummary();
            }
        }

        private async void Ultimate_Click(object sender, RoutedEventArgs e)
        {
            _vm.UseUltimateCommand.Execute(null);
            while (_vm.IsBusy)
            {
                await System.Threading.Tasks.Task.Delay(120);
            }
            SyncPlanSelection();
            SyncSummary();
        }

        private async void Balanced_Click(object sender, RoutedEventArgs e)
        {
            _vm.UseBalancedCommand.Execute(null);
            while (_vm.IsBusy)
            {
                await System.Threading.Tasks.Task.Delay(120);
            }
            SyncPlanSelection();
            SyncSummary();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>
        /// 快捷键：Ctrl+1/2/3 直接在「硬件 / 优化 / 电源」三页之间跳，
        /// Ctrl+F 定位到搜索框，Esc 关窗。列表里按 Esc 时先清搜索，符合直觉。
        /// </summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Text = string.Empty;
                    e.Handled = true;
                    return;
                }
                Close();
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers != ModifierKeys.Control) return;

            switch (e.Key)
            {
                case Key.F:
                    if (_vm.IsTweakPage)
                    {
                        SearchBox.Focus();
                        SearchBox.SelectAll();
                    }
                    e.Handled = true;
                    break;
                case Key.D1:
                case Key.NumPad1:
                    SelectNavByKey(GameToolboxViewModel.NavHardware);
                    e.Handled = true;
                    break;
                case Key.D2:
                case Key.NumPad2:
                    // 第二个页签是「优化方案」，落在第一个优化分类上
                    SelectNavByKey(TweakCatalog.Categories.Length > 0
                        ? TweakCatalog.Categories[0]
                        : GameToolboxViewModel.NavHardware);
                    e.Handled = true;
                    break;
                case Key.D3:
                case Key.NumPad3:
                    SelectNavByKey(GameToolboxViewModel.NavPower);
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>按分类键选中左栏的某一项。</summary>
        private void SelectNavByKey(string key)
        {
            var target = _vm.NavItems.FirstOrDefault(n => n.Key == key);
            if (target != null) _vm.SelectedNav = target;
        }
    }
}
