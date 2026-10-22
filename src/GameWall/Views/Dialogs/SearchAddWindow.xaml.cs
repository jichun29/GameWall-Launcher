using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using GameWall.Services;

namespace GameWall.Views.Dialogs;

/// <summary>
/// 按名字在本地磁盘里搜索 exe，选中后由主窗口负责真正入库。
/// </summary>
public partial class SearchAddWindow : Window
{
    private readonly GameScanner _scanner = new();
    private CancellationTokenSource? _cts;

    public SearchAddWindow()
    {
        InitializeComponent();

        Loaded += (_, _) => KeywordBox.Focus();
        Closed += (_, _) => _cts?.Cancel();
    }

    /// <summary>用户最终选中的可执行文件路径。</summary>
    public string? SelectedExecutable { get; private set; }

    /// <summary>搜索结果推荐的显示名（可能来自目录名，比 exe 名更友好）。</summary>
    public string? SelectedName { get; private set; }

    private async void Search_Click(object sender, RoutedEventArgs e) => await RunSearchAsync();

    private async void KeywordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await RunSearchAsync();
        }
    }

    private async Task RunSearchAsync()
    {
        var keyword = KeywordBox.Text.Trim();
        if (keyword.Length == 0)
        {
            StatusText.Text = "先输入关键字再搜索。";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        ResultList.ItemsSource = null;
        StatusText.Text = "正在扫描…（大硬盘可能需要几十秒）";
        HintText.Text = "扫描中，请不要关闭窗口。";

        var settings = new SettingsService();
        settings.Load();
        var roots = settings.ExistingScanRoots();

        if (roots.Count == 0)
        {
            StatusText.Text = "没有可用的扫描目录，请到「设置」里添加游戏所在的分区或文件夹。";
            HintText.Text = string.Empty;
            return;
        }

        var progress = new Progress<string>(msg =>
        {
            if (_cts is { IsCancellationRequested: false })
            {
                StatusText.Text = "正在扫描… " + msg;
            }
        });

        try
        {
            var results = await _scanner.ScanAsync(
                roots,
                settings.Current.ScanDepth,
                settings.Current.MinExecutableSize,
                keyword,
                progress,
                _cts.Token);

            // 关键字命中的排前面，其次按体积
            var ordered = results
                .OrderByDescending(c => c.SuggestedName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(c => c.SizeBytes)
                .Take(400)
                .ToList();

            ResultList.ItemsSource = ordered;

            if (ordered.Count == 0)
            {
                StatusText.Text = $"没有找到包含「{keyword}」的可执行文件。可以到「设置」里增加扫描目录，或直接手动选择 exe。";
                HintText.Text = string.Empty;
            }
            else
            {
                StatusText.Text = $"找到 {ordered.Count} 个结果，按体积从大到小排列。";
                HintText.Text = "双击结果直接添加，或选中后点「添加选中」。";
                ResultList.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "搜索已取消。";
        }
        catch (Exception ex)
        {
            Logger.Error("搜索添加失败", ex);
            StatusText.Text = "搜索出错：" + ex.Message;
        }
    }

    private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ConfirmSelection();

    private void Add_Click(object sender, RoutedEventArgs e) => ConfirmSelection();

    private void ConfirmSelection()
    {
        if (ResultList.SelectedItem is not GameCandidate candidate)
        {
            MessageBox.Show("请先在列表里选中一个游戏。", "GameWall",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedExecutable = candidate.FilePath;
        SelectedName = candidate.SuggestedName;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // 忽略
            }
        }
    }
}
