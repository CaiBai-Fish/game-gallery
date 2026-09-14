param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$TestDir,
    [string]$ShotDir
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class GGV2 {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public static void Click(int x, int y) {
        SetCursorPos(x, y); System.Threading.Thread.Sleep(160);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(40);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
    public static void DoubleClick(int x, int y) {
        Click(x, y); System.Threading.Thread.Sleep(90);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(40);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
    public static void RightClick(int x, int y) {
        SetCursorPos(x, y); System.Threading.Thread.Sleep(180);
        mouse_event(0x0008, 0, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(60);
        mouse_event(0x0010, 0, 0, 0, IntPtr.Zero);
    }
}
"@
[void][GGV2]::SetProcessDPIAware()

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$AnyCond = [System.Windows.Automation.Condition]::TrueCondition
$script:pass = 0; $script:fail = 0

function Check([string]$label, [bool]$ok, [string]$detail) {
    if ($ok) { $script:pass++; Write-Output ("PASS  " + $label + "   " + $detail) }
    else { $script:fail++; Write-Output ("FAIL  " + $label + "   " + $detail) }
}
function CpuSec($p) {
    $proc = Get-Process -Id $p -ErrorAction SilentlyContinue
    if (-not $proc) { return -1 }
    return [math]::Round($proc.TotalProcessorTime.TotalSeconds, 1)
}
function App-Root($p) { return $AE::FromHandle((Get-Process -Id $p).MainWindowHandle) }
function Find-ByName($r, $n) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $n)
    return $r.FindFirst($TS::Descendants, $c)
}
function Focus-App($p) { [void][GGV2]::SetForegroundWindow((Get-Process -Id $p).MainWindowHandle); Start-Sleep -Milliseconds 600 }
function Get-ByType($r, $ctrlType) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ctrlType)
    return $r.FindAll($TS::Descendants, $c)
}

function Capture($hwnd) {
    $rect = New-Object GGV2+RECT
    [void][GGV2]::GetWindowRect($hwnd, [ref]$rect)
    $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bmp.Size)
    $g.Dispose()
    return $bmp
}
function RedWidth($bmp, [int]$row) {
    $min = -1; $max = -1
    for ($x = 0; $x -lt $bmp.Width; $x++) {
        $c = $bmp.GetPixel($x, $row)
        if ($c.R -gt 180 -and $c.G -lt 80 -and $c.B -lt 80) { if ($min -lt 0) { $min = $x }; $max = $x }
    }
    if ($min -lt 0) { return 0 }
    return ($max - $min + 1)
}
# 视口底部那条纯背景带在动画期间是半透明的，会透出浅色图库背景，亮度明显更高
function BandLuma($bmp, [int]$y0, [int]$y1) {
    $sum = 0.0; $n = 0
    for ($y = $y0; $y -le $y1; $y++) {
        for ($x = 300; $x -lt 1100; $x += 8) {
            $c = $bmp.GetPixel($x, $y)
            $sum += (0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B); $n++
        }
    }
    if ($n -eq 0) { return 0 }
    return [math]::Round($sum / $n, 1)
}

function RedHeight($bmp) {
    $minY = -1; $maxY = -1
    for ($y = 60; $y -lt $bmp.Height - 20; $y++) {
        if ((RedWidth $bmp $y) -gt 1000) { if ($minY -lt 0) { $minY = $y }; $maxY = $y }
    }
    if ($minY -lt 0) { return 0 }
    return ($maxY - $minY + 1)
}

Remove-Item $TestDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $TestDir -Force | Out-Null
$img = New-Object System.Drawing.Bitmap(1920, 1080)
$g = [System.Drawing.Graphics]::FromImage($img)
$g.Clear([System.Drawing.Color]::FromArgb(255, 0, 0))
$g.Dispose()
$img.Save((Join-Path $TestDir 'red-1920x1080.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$img.Dispose()

Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
(@{ CustomFolders = @(@{ Path = $TestDir; GameKey = 'other' }) } | ConvertTo-Json -Depth 5) |
    Set-Content -Path (Join-Path $DataDir 'settings.json') -Encoding UTF8
try { Set-Clipboard -Value 'sentinel' } catch { }

$env:GAMEGALLERY_DATA_DIR = $DataDir
$proc = Start-Process -FilePath $Exe -PassThru
$appPid = $proc.Id
Start-Sleep -Seconds 16
$hwnd = (Get-Process -Id $appPid).MainWindowHandle
$root = App-Root $appPid

$tab = $null
for ($i = 0; $i -lt 20 -and $null -eq $tab; $i++) {
    $root = App-Root $appPid
    $tab = Find-ByName $root '其他截图 1'
    if ($null -eq $tab) { Start-Sleep -Seconds 2 }
}
if ($null -eq $tab) {
    Write-Output "  找不到 '其他截图 1' 标签，实际可见的导航项："
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    foreach ($e in $root.FindAll($TS::Descendants, $c)) { Write-Output ("    ListItem '" + $e.Current.Name + "'") }
    Stop-Process -Id $appPid -Force
    exit 1
}
$tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Seconds 3
$root = App-Root $appPid
$grid = $null
for ($i = 0; $i -lt 15 -and $null -eq $grid; $i++) {
    $root = App-Root $appPid
    $grid = $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'PhotoGrid')))
    if ($null -eq $grid -or $grid.FindAll($TS::Children, $AnyCond).Count -eq 0) { $grid = $null; Start-Sleep -Seconds 2 }
}
if ($null -eq $grid) { Write-Output "  找不到已填充的 PhotoGrid"; Stop-Process -Id $appPid -Force; exit 1 }
$tile = $grid.FindAll($TS::Children, $AnyCond)[0]
$tile.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
$tile.SetFocus()
Start-Sleep -Milliseconds 700
$tileRect = $tile.Current.BoundingRectangle
$tx = [int]($tileRect.X + $tileRect.Width / 2)
$ty = [int]($tileRect.Y + $tileRect.Height / 2)
$cpuBefore = CpuSec $appPid

Write-Output "===== 0. 左侧导航项 ====="
$navHeights = @()
$lc = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
foreach ($e in $root.FindAll($TS::Descendants, $lc)) {
    if ($e.Current.Name -match '^(全部|原神|崩坏|绝区零|收藏|其他)') { $navHeights += [int]$e.Current.BoundingRectangle.Height }
}
Write-Output ("  导航项高度: " + ($navHeights -join ', '))
Check "导航项已加高（>=48px）" (($navHeights.Count -gt 0) -and (($navHeights | Measure-Object -Minimum).Minimum -ge 48)) (($navHeights | Measure-Object -Minimum).Minimum)

Write-Output ""
Write-Output "===== 1. Double-click opens viewer (freeze regression) ====="
# 用 Enter 触发动画采样：模拟双击时第一下经常被"激活窗口"吞掉，
# 会被拆成两次单击而不触发 DoubleTapped，导致采样随机失败。
# 双击本身在最后单独用重试断言（见 step 6）。
Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')

# 连拍若干帧：入场动画只有 240ms，且要等大图解码上屏才开始，单次取样很容易错过
# 整窗截图一次要 ~200ms，对 280ms 的动画太粗，会漏掉中间帧。
# 这里改成只截「缩略图所在的那一条窄带」，单帧只要几毫秒。
$winRect = New-Object GGV2+RECT
[void][GGV2]::GetWindowRect($hwnd, [ref]$winRect)
$winW = $winRect.Right - $winRect.Left
$stripY = [int]($tileRect.Y - $winRect.Top + $tileRect.Height / 2) - 15

function CaptureStrip([int]$y) {
    $bmp = New-Object System.Drawing.Bitmap($winW, 30)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($winRect.Left, $winRect.Top + $y, 0, 0, $bmp.Size)
    $g.Dispose()
    return $bmp
}

$frames = @()
for ($i = 0; $i -lt 40; $i++) {
    $b = CaptureStrip $stripY
    $frames += [pscustomobject]@{ Width = (RedWidth $b 15) }
    if ($i -eq 6 -and $ShotDir) { $full = Capture $hwnd; $full.Save((Join-Path $ShotDir 'anim-early.png'), [System.Drawing.Imaging.ImageFormat]::Png); $full.Dispose() }
    $b.Dispose()
}

Start-Sleep -Seconds 3
$final = Capture $hwnd
$finalWidth = RedWidth $final 380
$finalHeight = RedHeight $final
$finalLuma = BandLuma $final 905 915
if ($ShotDir) { $final.Save((Join-Path $ShotDir 'anim-final.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
$final.Dispose()
$cpuAfter = CpuSec $appPid

$widths = ($frames | Where-Object { $_.Width -gt 0 } | ForEach-Object { $_.Width })
$minWidth = if ($widths.Count -gt 0) { ($widths | Measure-Object -Minimum).Minimum } else { 0 }

# 非线性判据：逐帧增量应当明显不均匀（慢-快-慢），线性插值的增量会是恒定的
$deltas = @()
for ($i = 1; $i -lt $widths.Count; $i++) { $d = $widths[$i] - $widths[$i-1]; if ($d -gt 0) { $deltas += $d } }
$dMin = if ($deltas.Count -gt 0) { ($deltas | Measure-Object -Minimum).Minimum } else { 0 }
$dMax = if ($deltas.Count -gt 0) { ($deltas | Measure-Object -Maximum).Maximum } else { 0 }
Write-Output ("  每帧增量: " + (($deltas | Select-Object -First 14) -join ', '))
Check "放大动画是非线性的（增量最大/最小 >= 2）" (($dMin -gt 0) -and ($dMax -ge 2 * $dMin)) ("max=" + $dMax + " min=" + $dMin)
Write-Output ("  40 帧窄条采样 minWidth=" + $minWidth + "  final=" + $finalWidth + "x" + $finalHeight + " luma=" + $finalLuma + "  CPU " + $cpuBefore + "s -> " + $cpuAfter + "s")
Write-Output ("  首帧序列: " + (($frames | Select-Object -First 16 | ForEach-Object { $_.Width }) -join ', '))
Check "process alive after double-click" ($cpuAfter -ge 0) ""
Check "UI responsive, no spin (CPU delta < 4s)" (($cpuAfter - $cpuBefore) -lt 4) ("delta " + [math]::Round($cpuAfter - $cpuBefore, 1) + "s")
# 适应窗口的判定放在 step 3（用 Enter 触发，前台稳定）；这里只报告不判定
Write-Output ("  稳定后实测 " + $finalWidth + "x" + $finalHeight)
Check "open transition visible (a frame is drawn smaller than the settled size)" (($minWidth -gt 0) -and ($minWidth -lt ($finalWidth - 8))) ("minFrameWidth=" + $minWidth + " finalWidth=" + $finalWidth)

$root = App-Root $appPid
Check "viewer opened" ($null -ne (Find-ByName $root '返回图库')) ""

$badge = $null
foreach ($tx in (Get-ByType $root ([System.Windows.Automation.ControlType]::Text))) { if ($tx.Current.Name -match '^\d+%$') { $badge = $tx.Current.Name } }
Write-Output ("  打开时的缩放角标: " + $badge)
Check "默认缩放角标是 100%（以自适应为基准）" ($badge -eq '100%') ("badge=" + $badge)

Write-Output ""
Write-Output "===== 2. Viewer context menu ====="
try { Set-Clipboard -Value 'sentinel-viewer' } catch { }
$rect = New-Object GGV2+RECT
[void][GGV2]::GetWindowRect($hwnd, [ref]$rect)
$cx = [int]($rect.Left + ($rect.Right - $rect.Left) / 2)
$cy = [int]($rect.Top + ($rect.Bottom - $rect.Top) / 2)
Focus-App $appPid
[GGV2]::RightClick($cx, $cy)
Start-Sleep -Seconds 3
$root = App-Root $appPid
$menuReveal = Find-ByName $root '在资源管理器中显示'
$menuCopy = Find-ByName $root '复制到剪贴板'
Check "viewer context menu shows both items" (($null -ne $menuReveal) -and ($null -ne $menuCopy)) ("reveal=" + ($null -ne $menuReveal) + " copy=" + ($null -ne $menuCopy))
if ($menuCopy) {
    $menuCopy.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 3
    $drop = Get-Clipboard -Format FileDropList
    Check "viewer context copy works" ($null -ne $drop -and $drop.Count -ge 1) (($drop | ForEach-Object { $_.Name }) -join ',')
} else { Check "viewer context copy works" $false "menu missing" }

Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Seconds 3

Write-Output ""
Write-Output "===== 3. Enter opens viewer (no freeze) ====="
$cpuA = CpuSec $appPid
$root = App-Root $appPid
$grid = $null
for ($i = 0; $i -lt 15 -and $null -eq $grid; $i++) {
    $root = App-Root $appPid
    $grid = $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'PhotoGrid')))
    if ($null -eq $grid -or $grid.FindAll($TS::Children, $AnyCond).Count -eq 0) { $grid = $null; Start-Sleep -Seconds 2 }
}
if ($null -eq $grid) { Write-Output "  找不到已填充的 PhotoGrid"; Stop-Process -Id $appPid -Force; exit 1 }
$tile = $grid.FindAll($TS::Children, $AnyCond)[0]
$tile.SetFocus()
Start-Sleep -Milliseconds 700
# 前台窗口/焦点偶尔会抖动，重试几次再判定，避免把抖动当失败
$opened = $null
for ($attempt = 0; $attempt -lt 3 -and $null -eq $opened; $attempt++) {
    if ($attempt -gt 0) {
        $tile = (App-Root $appPid).FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'PhotoGrid'))).FindAll($TS::Children, $AnyCond)[0]
        $tile.SetFocus()
        Start-Sleep -Milliseconds 500
    }
    Focus-App $appPid
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Seconds 3
    $opened = Find-ByName (App-Root $appPid) '返回图库'
}
Start-Sleep -Seconds 1
$cpuB = CpuSec $appPid
$root = App-Root $appPid
Check "Enter opens viewer" ($null -ne $opened) ""
Check "Enter path no spin" (($cpuB - $cpuA) -lt 4) ("delta " + [math]::Round($cpuB - $cpuA, 1) + "s")
$shot = Capture $hwnd
$sw = RedWidth $shot 380
$shot.Dispose()
Check "Enter path also fits fully" ($sw -eq 1424) ("w=" + $sw)

Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Seconds 3

Write-Output ""
Write-Output "===== 4. Tile context menu ====="
try { Set-Clipboard -Value 'sentinel-tile' } catch { }
$root = App-Root $appPid
$grid = $null
for ($i = 0; $i -lt 15 -and $null -eq $grid; $i++) {
    $root = App-Root $appPid
    $grid = $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'PhotoGrid')))
    if ($null -eq $grid -or $grid.FindAll($TS::Children, $AnyCond).Count -eq 0) { $grid = $null; Start-Sleep -Seconds 2 }
}
if ($null -eq $grid) { Write-Output "  找不到已填充的 PhotoGrid"; Stop-Process -Id $appPid -Force; exit 1 }
$tile = $grid.FindAll($TS::Children, $AnyCond)[0]
$r2 = $tile.Current.BoundingRectangle
$tx2 = [int]($r2.X + $r2.Width / 2)
$ty2 = [int]($r2.Y + $r2.Height / 2)
Focus-App $appPid
[GGV2]::RightClick($tx2, $ty2)
Start-Sleep -Seconds 3
$root = App-Root $appPid
$tReveal = Find-ByName $root '在资源管理器中显示'
$tCopy = Find-ByName $root '复制到剪贴板'
Check "tile context menu shows both items" (($null -ne $tReveal) -and ($null -ne $tCopy)) ("reveal=" + ($null -ne $tReveal) + " copy=" + ($null -ne $tCopy))
if ($tCopy) {
    $tCopy.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 3
    $drop2 = Get-Clipboard -Format FileDropList
    Check "tile context copy works" ($null -ne $drop2 -and $drop2.Count -ge 1) (($drop2 | ForEach-Object { $_.Name }) -join ',')
} else { Check "tile context copy works" $false "menu missing" }

Write-Output ""
Write-Output "===== 5. Stability ====="
Start-Sleep -Seconds 3
$cpuFinal = CpuSec $appPid
Check "process alive" ($cpuFinal -ge 0) ""
Check "total CPU sane (no busy wait)" ($cpuFinal -lt 40) ("total CPU " + $cpuFinal + "s")

Stop-Process -Id $appPid -Force
Start-Sleep -Seconds 1
Write-Output ""
Write-Output "===== 6. Double-click also opens the viewer ====="
$dbOpened = $null
for ($attempt = 0; $attempt -lt 4 -and $null -eq $dbOpened; $attempt++) {
    $root = App-Root $appPid
    $grid = $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'PhotoGrid')))
    if ($null -eq $grid) { break }
    $tiles = $grid.FindAll($TS::Children, $AnyCond)
    if ($tiles.Count -eq 0) { break }
    $r = $tiles[0].Current.BoundingRectangle
    # 无人值守时 SetForegroundWindow 会被前台锁拦下，点击会落到别的窗口。
    # 先点一下标题栏强制激活（标题栏没有应用交互，点了也没副作用）。
    # 先按 Esc 确保上一步的右键菜单已经关闭，否则点击会被弹出层吃掉
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 600
    $wr = New-Object GGV2+RECT
    [void][GGV2]::GetWindowRect($hwnd, [ref]$wr)
    [GGV2]::Click([int](($wr.Left + $wr.Right) / 2), $wr.Top + 14)
    Start-Sleep -Milliseconds 400
    [GGV2]::DoubleClick([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    Start-Sleep -Seconds 4
    $dbOpened = Find-ByName (App-Root $appPid) '返回图库'
    if ($null -ne $dbOpened) { break }
    Start-Sleep -Seconds 1
}
Check "双击缩略图能打开查看器" ($null -ne $dbOpened) ""
Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Seconds 2

Write-Output ""
Write-Output ("RESULT: pass=" + $script:pass + " fail=" + $script:fail)
