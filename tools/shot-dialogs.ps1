param(
    [string]$ProcessName = "GameWall",
    [string]$OutDir = "docs\screenshots"
)

# 抓取主窗口以及由它打开的对话框（设置 / 首次引导）。
# 用 UIA 找按钮并 Invoke，再用 PrintWindow(PW_RENDERFULLCONTENT) 取像素。

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ("GWShot2" -as [type])) {
    Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class GWShot2
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder sb, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static List<IntPtr> Windows(uint pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, l) =>
        {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) { return true; }

            RECT r;
            if (!GetWindowRect(h, out r)) { return true; }
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            // 太小的窗口是 WPF 的辅助窗口（光标、tooltip 宿主等），跳过
            if (w < 200 || ht < 150) { return true; }

            var cls = new StringBuilder(128);
            GetClassNameW(h, cls, 128);
            if (!cls.ToString().StartsWith("HwndWrapper")) { return true; }

            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string Title(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowTextW(h, sb, 256);
        return sb.ToString();
    }

    public static bool Save(IntPtr h, string path)
    {
        RECT r;
        if (!GetWindowRect(h, out r)) { return false; }
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        if (w <= 0 || ht <= 0) { return false; }
        using (var bmp = new System.Drawing.Bitmap(w, ht))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            PrintWindow(h, hdc, 2);
            g.ReleaseHdc(hdc);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        return true;
    }
}
"@ -ReferencedAssemblies System.Drawing
}

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Error "进程 $ProcessName 没有在运行"; exit 1 }
$pid32 = [uint32]$proc.Id

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

# 主窗口
$mainHwnd = $proc.MainWindowHandle
[void][GWShot2]::Save($mainHwnd, (Join-Path $OutDir "shot-main.png"))
Write-Host "main -> shot-main.png"

function Save-AllWindows([string]$prefix) {
    $wins = [GWShot2]::Windows($pid32)
    Write-Host ("windows: {0}" -f $wins.Count)
    foreach ($w in $wins) {
        $t = [GWShot2]::Title($w)
        if ([string]::IsNullOrWhiteSpace($t)) { continue }
        if ($t -eq "GameWall") { continue }
        $safe = $t -replace '[\\/:*?"<>|\s]', '_'
        $file = Join-Path $OutDir ("{0}-{1}.png" -f $prefix, $safe)
        $ok = [GWShot2]::Save($w, $file)
        Write-Host ("'{0}' -> {1} ok={2}" -f $t, (Split-Path $file -Leaf), $ok)
    }
}

# 点「⚙ 设置」
$root = [System.Windows.Automation.AutomationElement]::FromHandle($mainHwnd)
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, "⚙ 设置")
$btn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if ($btn) {
    $pat = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pat.Invoke()
    Start-Sleep -Milliseconds 1500
    Save-AllWindows "dialog"
} else {
    Write-Host "没有找到「⚙ 设置」按钮"
}
