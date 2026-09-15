# 验证「更新日志」浮窗：能打开、固定大小（无 WS_THICKFRAME / WS_MAXIMIZEBOX）、内容由 Markdig 渲染。
param(
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Chk {
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
}
"@

$WS_THICKFRAME = 0x00040000
$WS_MAXIMIZEBOX = 0x00010000
$WS_MINIMIZEBOX = 0x00020000
$GWL_STYLE = -16

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\GameGallery\bin\Release\net8.0-windows10.0.19041.0\win-x64\GameGallery.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "未构建：$exe" }

Get-Process -Name GameGallery -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$proc = Start-Process -FilePath $exe -PassThru

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function Get-AppWindows {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)
    @($AE::RootElement.FindAll($TS::Children, $cond))
}

function Find-Button([string]$name) {
    $byName = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
    $byType = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)
    $cond = New-Object System.Windows.Automation.AndCondition($byName, $byType)

    foreach ($w in Get-AppWindows) {
        $found = $w.FindFirst($TS::Descendants, $cond)
        if ($found) { return $found }
    }

    return $AE::RootElement.FindFirst($TS::Descendants, $cond)
}

$main = $null
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $main = Get-AppWindows | Where-Object { $_.Current.Name -notlike '更新日志*' } | Select-Object -First 1
    if ($main) { break }
}
if (-not $main) { throw '主窗口没出现' }
Start-Sleep -Seconds 5
Write-Host ("主窗口：'{0}'" -f $main.Current.Name)

$fails = New-Object System.Collections.Generic.List[string]

Write-Host "`n=== 1. 打开设置面板并点「更新日志」 ==="
$settings = Find-Button '设置'
if (-not $settings) { throw '找不到「设置」按钮' }
$settings.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2

$button = Find-Button '更新日志'
if (-not $button) { throw '找不到「更新日志」按钮' }
$button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Host '  已点击'

$log = $null
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $log = Get-AppWindows | Where-Object { $_.Current.Name -like '更新日志*' } | Select-Object -First 1
    if ($log) { break }
}
if (-not $log) { throw '更新日志窗口没有出现' }
Write-Host ("  窗口标题：'{0}'" -f $log.Current.Name)

Write-Host "`n=== 2. 固定大小（不可拖拽调整 / 不可最大化）==="
$hwnd = [IntPtr]$log.Current.NativeWindowHandle
$style = [Chk]::GetWindowLong($hwnd, $GWL_STYLE)
$thick = ($style -band $WS_THICKFRAME) -ne 0
$maxBox = ($style -band $WS_MAXIMIZEBOX) -ne 0
$minBox = ($style -band $WS_MINIMIZEBOX) -ne 0
Write-Host ("  style=0x{0:X8}  可拖拽调整={1}  最大化框={2}  最小化框={3}" -f $style, $thick, $maxBox, $minBox)
if ($thick) { $fails.Add('窗口仍然带 WS_THICKFRAME（可拖拽调整大小）') }
if ($maxBox) { $fails.Add('窗口仍然带 WS_MAXIMIZEBOX（可最大化）') }

Write-Host "`n=== 3. Markdig 渲染结果 ==="
Start-Sleep -Seconds 4

$allCond = [System.Windows.Automation.Condition]::TrueCondition
$descendants = @($log.FindAll($TS::Descendants, $allCond))
Write-Host ("  浮窗里的自动化元素总数：{0}" -f $descendants.Count)

$statusCond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'ChangelogStatus')
$status = $log.FindFirst($TS::Descendants, $statusCond)
if ($status) { Write-Host ("  状态行：'{0}'" -f $status.Current.Name) } else { Write-Host '  状态行元素没找到' }

$texts = @($descendants | Where-Object { $_.Current.ControlType -eq $CT::Text } | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
Write-Host ("  文本片段：{0} 个" -f $texts.Count)
foreach ($t in ($texts | Select-Object -First 18)) {
    $short = if ($t.Length -gt 66) { $t.Substring(0, 66) + '...' } else { $t }
    Write-Host ("    - {0}" -f $short)
}
if ($texts.Count -lt 10) { $fails.Add("只渲染出 $($texts.Count) 个文本片段，看起来没内容") }
if (-not ($texts -match '^\d+\.\d+\.\d+')) { $fails.Add('没有渲染出任何版本号标题（形如 1.0.1）') }

$links = @($descendants | Where-Object { $_.Current.ControlType -eq $CT::Hyperlink })
Write-Host ("  链接（[文本](url) 解析结果）：{0} 个" -f $links.Count)
if ($links.Count -gt 0) { Write-Host ("    例如：'{0}'" -f $links[0].Current.Name) }

Write-Host "`n=== 4. 抓图留证 ==="
$bounds = $log.Current.BoundingRectangle
$shotW = [int]$bounds.Width
$shotH = [int]$bounds.Height
Write-Host ("  UIA 边界：{0},{1} {2}x{3}" -f [int]$bounds.X, [int]$bounds.Y, $shotW, $shotH)

if ($shotW -gt 100 -and $shotH -gt 100) {
    $bmp = New-Object System.Drawing.Bitmap($shotW, $shotH)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][Chk]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $shot = Join-Path $root 'build\shot-changelog.png'
    $bmp.Save($shot)
    $bmp.Dispose()
    Write-Host ("  已保存：{0}" -f $shot)
} else {
    $fails.Add("窗口尺寸异常：$shotW x $shotH")
}

Write-Host "`n=== 5. 重复点击不重复开窗 ==="
$button2 = Find-Button '更新日志'
if ($button2) {
    $button2.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 3
} else {
    Write-Host '  设置面板已关闭，直接统计窗口数'
}
$count = @(Get-AppWindows | Where-Object { $_.Current.Name -like '更新日志*' }).Count
Write-Host ("  更新日志窗口数量：{0}（期望 1）" -f $count)
if ($count -ne 1) { $fails.Add("重复点击后开了 $count 个更新日志窗口") }

Write-Host "`n=== 6. 启动日志 ==="
Get-Content (Join-Path $env:LOCALAPPDATA 'GameGallery\startup.log') -Tail 6 -Encoding UTF8 | ForEach-Object { Write-Host "  $_" }

Write-Host ''
if ($fails.Count -eq 0) { Write-Host 'CHANGELOG WINDOW OK' } else { foreach ($f in $fails) { Write-Host "FAIL: $f" } }

if (-not $KeepRunning) { Get-Process -Name GameGallery -ErrorAction SilentlyContinue | Stop-Process -Force }
if ($fails.Count -gt 0) { exit 1 }
