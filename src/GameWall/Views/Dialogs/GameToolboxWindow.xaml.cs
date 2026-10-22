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
    /// 游戏工具箱窗口：左边硬件一览、右边优化方案与电源方案。
    /// 所有修改都是用户点按钮才发生，且每条都记了旧值，可以一键还原。
    /// </summary>
    public partial class GameToolboxWindow : Window
    {
        private readonly GameToolboxViewModel _vm = new();

        public GameToolboxWindow()
        {
            InitializeComponent();
            DataContext = _vm;

            _vm.Notify += OnNotify;

            CategoryBox.ItemsSource = _vm.Categories;
            CategoryBox.SelectedItem = "全部";

            Loaded += (_, _) =>
            {
                _vm.Initialize();
                SyncPowerPlans();
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

        private void SyncSummary()
        {
            StatusText.Text = _vm.StatusText;
            ElevationText.Text = _vm.ElevationText;
            AppliedSummary.Text = _vm.AppliedSummary;
            ApplyButton.Content = _vm.SelectedCountText;
        }

        private void SyncPowerPlans()
        {
            var selected = PowerPlanBox.SelectedItem as PowerPlanInfo;
            PowerPlanBox.ItemsSource = null;
            PowerPlanBox.ItemsSource = _vm.PowerPlans;
            PowerPlanBox.DisplayMemberPath = nameof(PowerPlanInfo.DisplayText);

            var active = _vm.PowerPlans.FirstOrDefault(p => p.IsActive);
            PowerPlanBox.SelectedItem = active ?? _vm.PowerPlans.FirstOrDefault();
            _ = selected;
        }

        private void OnNotify(string title, string message)
        {
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ── 事件处理 ─────────────────────────────────────────

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _vm.RefreshAll();
            SyncPowerPlans();
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

        private void Category_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CategoryBox.SelectedItem is string category) _vm.SelectedCategory = category;
            SyncSummary();
        }

        private void AppliedOnly_Changed(object sender, RoutedEventArgs e)
        {
            _vm.ShowAppliedOnly = AppliedOnlyBox.IsChecked == true;
            SyncSummary();
        }

        private void HighRisk_Changed(object sender, RoutedEventArgs e)
        {
            _vm.IncludeHighRisk = HighRiskBox.IsChecked == true;
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
            if (highRisk.Count > 0 && !_vm.IncludeHighRisk)
            {
                StatusText.Text = "勾选项里包含高风险条目，请先勾选右上角的「包含高风险项」。";
                return;
            }

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

        private async void ActivatePlan_Click(object sender, RoutedEventArgs e)
        {
            if (PowerPlanBox.SelectedItem is PowerPlanInfo plan)
            {
                await _vm.ActivatePlanAsync(plan);
                SyncPowerPlans();
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
            SyncPowerPlans();
            SyncSummary();
        }

        private async void Balanced_Click(object sender, RoutedEventArgs e)
        {
            _vm.UseBalancedCommand.Execute(null);
            while (_vm.IsBusy)
            {
                await System.Threading.Tasks.Task.Delay(120);
            }
            SyncPowerPlans();
            SyncSummary();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
