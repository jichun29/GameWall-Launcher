# 生成一份演示用的 library.json / settings.json，仅用于本地截图与回归测试。
# 用法：powershell -ExecutionPolicy Bypass -File tools\seed-demo.ps1
$ErrorActionPreference = 'Stop'

$root = Join-Path $env:AppData 'GameWall'
New-Item -ItemType Directory -Force -Path $root | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $root 'icons') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $root 'covers') | Out-Null

$utf8 = New-Object System.Text.UTF8Encoding($false)

$games = @(
    @{ id = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1'; name = '记事本之战';   exe = 'C:\Windows\System32\notepad.exe'; tags = @('测试', '工具');   notes = '自动化验证用'; fav = $true;  count = 8  },
    @{ id = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2'; name = '计算器大冒险'; exe = 'C:\Windows\System32\calc.exe';    tags = @('休闲', '益智');   notes = '';             fav = $false; count = 3  },
    @{ id = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa3'; name = '字符映射远征'; exe = 'C:\Windows\System32\charmap.exe'; tags = @('RPG', '工具');    notes = '老牌工具';     fav = $true;  count = 15 },
    @{ id = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa4'; name = '放大镜传说';   exe = 'C:\Windows\System32\magnify.exe'; tags = @('休闲');           notes = '';             fav = $false; count = 0  },
    @{ id = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa5'; name = '屏幕键盘纪元'; exe = 'C:\Windows\System32\osk.exe';     tags = @('工具', '辅助');   notes = '';             fav = $true;  count = 5  },
    @{ id = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa6'; name = '音量合成器奇谭'; exe = 'C:\Windows\System32\SndVol.exe'; tags = @('RPG');            notes = '';             fav = $false; count = 2  }
)

$list = @()
$i = 1
foreach ($g in $games) {
    $last = $null
    if ($g.count -gt 0) { $last = (Get-Date).AddHours(-$i * 5).ToString('o') }

    $list += [pscustomobject]@{
        Id               = $g.id
        Name             = $g.name
        LaunchKind       = 'Executable'
        ExecutablePath   = $g.exe
        LaunchUri        = ''
        Arguments        = ''
        WorkingDirectory = [System.IO.Path]::GetDirectoryName($g.exe)
        CoverPath        = $null
        IconPath         = (Join-Path $root "icons\$($g.id).png")
        SaveDirectory    = ''
        Tags             = @($g.tags)
        Notes            = $g.notes
        IsFavorite       = $g.fav
        AddedAt          = (Get-Date).AddDays(-30 + $i * 3).ToString('o')
        LastLaunchAt     = $last
        LaunchCount      = $g.count
        SortIndex        = $i
        Source           = '手动添加'
    }
    $i++
}

$json = ConvertTo-Json -InputObject $list -Depth 6
[System.IO.File]::WriteAllText((Join-Path $root 'library.json'), $json, $utf8)

$settings = Get-Content (Join-Path $root 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$settings.Theme             = 'Dark'
$settings.BackgroundColor   = ''
$settings.AccentColor       = ''
$settings.MinimizeToTray    = $false
$settings.FirstRunCompleted = $true
$settings.WindowWidth       = 1280
$settings.WindowHeight      = 820

$settings | Add-Member -NotePropertyName WindowLeft -NotePropertyValue $null -Force
$settings | Add-Member -NotePropertyName WindowTop  -NotePropertyValue $null -Force

[System.IO.File]::WriteAllText((Join-Path $root 'settings.json'),
    (ConvertTo-Json -InputObject $settings -Depth 5), $utf8)

Write-Host "seeded: $root"
Write-Host ("first name = " + $list[0].Name)
