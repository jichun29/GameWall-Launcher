# 生成 README 用的界面截图：切换卡片式 / 列表式两种视图并各抓一张图。
# 依赖：GameWall 已在运行（卡片式为默认视图）。
param(
    [string]$ProcessName = "GameWall",
    [string]$OutDir = "docs\screenshots"
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

if (-not ("GameWallShooter" -as [type])) {
    Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class GameWallShooter {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr Find(uint targetPid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == targetPid && IsWindowVisible(h)) {
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, 256);
                if (sb.ToString() == title) { found = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@
}

$proc = Get-Process -Name $ProcessName -ErrorAction Stop | Select-Object -First 1

function Get-RootWindow {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $cond)
}

function Click-Button([string]$name) {
    $win = Get-RootWindow
    if (-not $win) { throw "主窗口未就绪" }
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)))
    $btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $btn) { throw "找不到按钮 '$name'" }
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Save-Window([string]$out, [string]$title = "GameWall") {
    Start-Sleep -Milliseconds 900
    $hwnd = [GameWallShooter]::Find([uint32]$proc.Id, $title)
    if ($hwnd -eq [IntPtr]::Zero) { throw "找不到窗口 '$title'" }
    $rect = New-Object GameWallShooter+RECT
    [GameWallShooter]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [GameWallShooter]::PrintWindow($hwnd, $hdc, 2)   # 2 = PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $full = [System.IO.Path]::GetFullPath((Join-Path $OutDir $out))
    $dir = [System.IO.Path]::GetDirectoryName($full)
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "saved $full (${w}x${h}) ok=$ok"
}

# 只要两种视图：▤ = 卡片式（默认，像 Steam 游戏库）、☰ = 列表式
Click-Button "▤"; Save-Window "card.png"
Click-Button "☰"; Save-Window "list.png"
Click-Button "▤"; Save-Window "card.png"

# 「关于」窗口单独抓一张（ShowInTaskbar=False，只能用 Win32 句柄找）
Click-Button "ⓘ 关于"
Save-Window "dialog-about.png" "关于 GameWall"
$aboutHwnd = [GameWallShooter]::Find([uint32]$proc.Id, "关于 GameWall")
if ($aboutHwnd -ne [IntPtr]::Zero) {
    # 关闭：发 WM_CLOSE，避免依赖焦点
    [GameWallShooter]::PostMessage($aboutHwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
}
Start-Sleep -Milliseconds 600

# 「游戏工具箱」也抓一张：硬件一览 + 优化方案 + 电源方案
Click-Button "🎛 工具箱"
Save-Window "dialog-toolbox.png" "游戏工具箱"
$boxHwnd = [GameWallShooter]::Find([uint32]$proc.Id, "游戏工具箱")
if ($boxHwnd -ne [IntPtr]::Zero) {
    [GameWallShooter]::PostMessage($boxHwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
}

Write-Host "done"
