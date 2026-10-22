# 排版审计：把窗口摆成指定尺寸，用 UIA 拉出所有可见元素的屏幕矩形，
# 检查「越界」（超出窗口）与「重叠」（控件互相压住），并顺手抓一张图。
# 用法: & .\tools\audit-layout.ps1 -Width 900 -Height 700
param(
    [string]$ProcessName = "GameWall",
    [string]$Title = "GameWall",
    [int]$Width = 900,
    [int]$Height = 700
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
if (-not ("GWLayout" -as [type])) {
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class GWLayout {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
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
$hwnd = [GWLayout]::Find([uint32]$proc.Id, $Title)
if ($hwnd -eq [IntPtr]::Zero) { throw "找不到窗口 '$Title'" }

[GWLayout]::SetWindowPos($hwnd, [IntPtr]::Zero, 40, 40, $Width, $Height, 0x0040) | Out-Null
[GWLayout]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 900

$rect = New-Object GWLayout+RECT
[GWLayout]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$winW = $rect.Right - $rect.Left
$winH = $rect.Bottom - $rect.Top
Write-Host ("== window {0} x {1} at ({2},{3})" -f $winW, $winH, $rect.Left, $rect.Top)

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)

$items = @()
foreach ($e in $all) {
    try {
        $ct = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        $r = $e.Current.BoundingRectangle
        if ($r.Width -le 0 -or $r.Height -le 0) { continue }
        $name = $e.Current.Name
        if ([string]::IsNullOrWhiteSpace($name)) { $name = "<$ct>" }
        $items += [pscustomobject]@{
            Type = $ct
            Name = ($name -replace "`r?`n", " ")
            X = [int]$r.X; Y = [int]$r.Y; W = [int]$r.Width; H = [int]$r.Height
        }
    } catch { }
}

$over = $items | Where-Object {
    $_.X -lt $rect.Left -or ($_.X + $_.W) -gt $rect.Right -or
    $_.Y -lt $rect.Top -or ($_.Y + $_.H) -gt $rect.Bottom
}
Write-Host ("-- overflow (out of window bounds): {0}" -f @($over).Count)
$over | Sort-Object Y, X | ForEach-Object {
    Write-Host ("   [{0}] '{1}'  x {2}->{3}  y {4}->{5}" -f $_.Type, $_.Name, $_.X, ($_.X + $_.W), $_.Y, ($_.Y + $_.H))
}

Write-Host "-- overlaps:"
$pairs = 0
for ($i = 0; $i -lt $items.Count; $i++) {
    for ($j = $i + 1; $j -lt $items.Count; $j++) {
        $a = $items[$i]; $b = $items[$j]
        $ox = [Math]::Max(0, [Math]::Min($a.X + $a.W, $b.X + $b.W) - [Math]::Max($a.X, $b.X))
        $oy = [Math]::Max(0, [Math]::Min($a.Y + $a.H, $b.Y + $b.H) - [Math]::Max($a.Y, $b.Y))
        if ($ox -le 0 -or $oy -le 0) { continue }
        $areaA = $a.W * $a.H; $areaB = $b.W * $b.H
        $inter = $ox * $oy
        if ($inter -ge [Math]::Min($areaA, $areaB) * 0.85) { continue }
        $pairs++
        Write-Host ("   '{0}'[{1}] {2}..{3}/{4}..{5}  VS  '{6}'[{7}] {8}..{9}/{10}..{11}  overlap {12}x{13}" -f `
            $a.Name, $a.Type, $a.X, ($a.X + $a.W), $a.Y, ($a.Y + $a.H), `
            $b.Name, $b.Type, $b.X, ($b.X + $b.W), $b.Y, ($b.Y + $b.H), $ox, $oy)
    }
}
Write-Host "-- overlap pairs: $pairs"

Write-Host "-- top bar (window-relative y < 92):"
$items | Where-Object { $_.Y -lt ($rect.Top + 92) } | Sort-Object Y, X | ForEach-Object {
    Write-Host ("   {0,-12} '{1}'  x {2}->{3}  y {4}->{5}" -f $_.Type, $_.Name, $_.X, ($_.X + $_.W), $_.Y, ($_.Y + $_.H))
}

# 截图（PrintWindow 才能在 WPF 无边框窗口上拿到内容）
$bmp = New-Object System.Drawing.Bitmap $winW, $winH
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [GWLayout]::PrintWindow($hwnd, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
$shot = Join-Path $PWD ("audit_{0}x{1}.png" -f $winW, $winH)
$bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved $shot ok=$ok"
