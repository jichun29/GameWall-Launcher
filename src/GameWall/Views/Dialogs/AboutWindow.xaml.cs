using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using GameWall.Services;

namespace GameWall.Views.Dialogs;

/// <summary>
/// 关于窗口：作者信息、项目链接、版本与检查更新。
///
/// 这个窗口既可以从「设置 → 关于 GameWall」打开，也会在启动时发现新版本后自动弹出，
/// 所以它自己负责检查/下载/重启的完整流程，不依赖主窗口。
/// </summary>
public partial class AboutWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private UpdateCheckResult? _lastResult;
    private bool _busy;

    public AboutWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => FillStaticInfo();
    }

    /// <summary>打开时如果已经有检查结果（主窗口启动时查到的），直接拿来显示，省一次网络请求。</summary>
    public void ApplyKnownResult(UpdateCheckResult result)
    {
        _lastResult = result;
        RenderResult(result);
    }

    /// <summary>启动时自动检查过一次，进窗口就顺手再确认一遍最新状态。</summary>
    public void AutoCheckOnLoad() => _ = RunCheckAsync(silent: true);

    // ══════════════ 静态信息 ══════════════

    private void FillStaticInfo()
    {
        Title = $"关于 {AppInfo.ProductName}";
        TaglineText.Text = AppInfo.Tagline;
        VersionBadge.Text = AppInfo.VersionLabel;
        BuildBadge.Text = $"{AppInfo.BuildDate} · {AppInfo.PlatformLabel}";

        AuthorNameText.Text = AppInfo.Author;
        AuthorHandleText.Text = $"GitHub @{AppInfo.AuthorHandle}";
        AuthorGlyph.Text = string.IsNullOrWhiteSpace(AppInfo.AuthorGlyph) ? "G" : AppInfo.AuthorGlyph!;

        LicenseText.Text = AppInfo.LicenseSummary;
        CreditText.Text = AppInfo.CreditLine;
        FooterText.Text = AppInfo.Copyright;

        // 已经知道有新版本（主窗口传进来的）就别再自动查一遍
        UpdateStatusText.Text = _lastResult is null
            ? $"当前版本 {AppInfo.VersionLabel}。检查更新会读取 GitHub 上的公开版本号，只在你点击时联网。"
            : _lastResult.Message;
    }

    // ══════════════ 检查更新 ══════════════

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await RunCheckAsync(silent: false);

    private async Task RunCheckAsync(bool silent)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, silent ? "正在检查更新…" : "正在检查更新，国内网络可能需要一点时间…");

        try
        {
            var settings = new SettingsService();
            settings.Load();
            var result = await UpdateService.CheckAsync(settings.Current.UpdateSource, _cts.Token);
            _lastResult = result;
            RenderResult(result);
        }
        catch (OperationCanceledException)
        {
            // 窗口已经关掉，什么都不用做
        }
        catch (Exception ex)
        {
            Logger.Error("关于窗口检查更新失败", ex);
            UpdateStatusText.Text = "检查更新时出错了：" + ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void RenderResult(UpdateCheckResult result)
    {
        if (!result.Success)
        {
            UpdateStatusText.Text = "没查到版本信息：" + result.Message
                + "\n可以稍后再试，或者在设置里换一个下载源。";
            DownloadButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (!result.HasUpdate)
        {
            UpdateStatusText.Text = $"已经是最新版本 {AppInfo.VersionLabel}。";
            DownloadButton.Visibility = Visibility.Collapsed;
            return;
        }

        var info = result.Info!;
        var notes = info.NotesPlainText;
        var text = $"发现新版本 v{info.Version}（当前 v{AppInfo.Version}）";
        if (info.Size > 0)
        {
            text += $"\n安装包 {FormatSize(info.Size)}";
        }

        if (!string.IsNullOrWhiteSpace(notes))
        {
            text += "\n\n" + notes;
        }

        UpdateStatusText.Text = text;
        DownloadButton.Visibility = Visibility.Visible;
        DownloadButton.IsEnabled = true;
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult?.Info is not { } info || _busy)
        {
            return;
        }

        var settings = new SettingsService();
        settings.Load();

        SetBusy(true, "正在下载新版本…");
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;

        try
        {
            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                UpdateProgress.Value = p.Fraction;
                var percent = p.Fraction > 0 ? $"{p.Fraction:P0}" : FormatSize(p.Received);
                var speed = p.BytesPerSecond > 0 ? $" · {FormatSize((long)p.BytesPerSecond)}/s" : string.Empty;
                UpdateStatusText.Text = $"正在下载 v{info.Version}：{percent}{speed}";
            });

            var path = await UpdateService.DownloadAsync(info, settings.Current.UpdateSource, progress, _cts.Token);
            UpdateStatusText.Text = $"v{info.Version} 已下载完成，正在替换并重启…";

            if (UpdateService.TryApplyAndRestart(path, out var error))
            {
                // 替换脚本会把新 exe 拉起来，这里必须立刻退出，否则文件被占用换不掉
                Application.Current.Shutdown();
                return;
            }

            UpdateStatusText.Text = "下载完成，但没法自动替换：" + error
                + "\n文件已经下好了，可以手动覆盖：" + path;
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭，忽略
        }
        catch (Exception ex)
        {
            Logger.Error("下载更新失败", ex);
            UpdateStatusText.Text = "下载失败：" + ex.Message;
        }
        finally
        {
            UpdateProgress.Visibility = Visibility.Collapsed;
            SetBusy(false, null);
        }
    }

    private void SetBusy(bool busy, string? message)
    {
        _busy = busy;
        CheckButton.IsEnabled = !busy;
        DownloadButton.IsEnabled = !busy && _lastResult?.HasUpdate == true;
        CheckButton.Content = busy ? "检查中…" : "检查更新";
        if (message is not null)
        {
            UpdateStatusText.Text = message;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024 / 1024:0.0} GB";
        }

        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024:0.0} MB";
        }

        return bytes >= 1024 ? $"{bytes / 1024d:0} KB" : $"{bytes} B";
    }

    // ══════════════ 链接 ══════════════

    private void OpenAuthor_Click(object sender, RoutedEventArgs e) => OpenUrl(AppInfo.AuthorUrl);

    private void OpenHome_Click(object sender, RoutedEventArgs e) => OpenUrl(AppInfo.HomeUrl);

    private void OpenIssues_Click(object sender, RoutedEventArgs e) => OpenUrl(AppInfo.IssuesUrl);

    private void OpenReleases_Click(object sender, RoutedEventArgs e) => OpenUrl(AppInfo.ReleasesUrl);

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("打开链接失败：" + url, ex);
            MessageBox.Show(this, "打不开浏览器：" + ex.Message, "GameWall",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ══════════════ 窗口 ══════════════

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInteractive(source))
        {
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // 拖到一半松开鼠标会抛，忽略
            }
        }
    }

    /// <summary>标题栏里的按钮/文本框不该触发窗口拖动。</summary>
    private static bool IsInteractive(DependencyObject source)
    {
        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Button
                or System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.TextBox)
            {
                return true;
            }
        }

        return false;
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        catch
        {
            // 忽略
        }

        base.OnClosed(e);
    }
}
