param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$DataDir
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class GGR {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$script:pass = 0
$script:fail = 0

function Check([string]$label, [bool]$ok, [string]$detail) {
    if ($ok) { $script:pass++; Write-Output ("PASS  " + $label + "   " + $detail) }
    else { $script:fail++; Write-Output ("FAIL  " + $label + "   " + $detail) }
}
function App-Root($thePid) { return $AE::FromHandle((Get-Process -Id $thePid).MainWindowHandle) }
function Focus-App($thePid) {
    [void][GGR]::SetForegroundWindow((Get-Process -Id $thePid).MainWindowHandle)
    Start-Sleep -Milliseconds 800
}
function Find-ByName($r, [string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
    return $r.FindFirst($TS::Descendants, $c)
}
function Get-ByType($r, $t) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t)
    return $r.FindAll($TS::Descendants, $c)
}
function Get-TabNames($r) {
    $out = @()
    foreach ($e in (Get-ByType $r $CT::ListItem)) { if ($e.Current.Name -match '^(全部|原神|崩坏|绝区零|收藏|其他)') { $out += $e.Current.Name } }
    return $out
}
function Mem-MB($thePid) {
    $p = Get-Process -Id $thePid -ErrorAction SilentlyContinue
    if (-not $p) { return -1 }
    return [math]::Round($p.WorkingSet64 / 1MB, 1)
}

# 干净的一次启动：不预置 settings.json，完全依赖自动发现
Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
$env:GAMEGALLERY_DATA_DIR = $DataDir

$proc = Start-Process -FilePath $Exe -PassThru
$appPid = $proc.Id
Start-Sleep -Seconds 22
if (-not (Get-Process -Id $appPid -ErrorAction SilentlyContinue)) {
    Write-Output "app exited early: $($proc.ExitCode)"
    Get-Content (Join-Path $DataDir 'startup.log') -Raw -Encoding UTF8
    exit 1
}

$root = App-Root $appPid
Write-Output "===== 真实游戏截图库（干净首次启动）====="
$tabLine = (Get-TabNames $root) -join ' / '
Write-Output ("  tabs: " + $tabLine)
Check "原神 6" ($tabLine -like '*原神 6*') ""
Check "崩坏：星穹铁道 54" ($tabLine -like '*星穹铁道 54*') ""
Check "绝区零 140" ($tabLine -like '*绝区零 140*') ""
Check "崩坏3 7" ($tabLine -like '*崩坏3 7*') ""
Check "收藏 0" ($tabLine -like '*收藏 0*') ""
Check "全部 207" ($tabLine -like '*全部 207*') ""

$thumbCount = (Get-ChildItem (Join-Path $DataDir 'thumbnails') -Recurse -File -ErrorAction SilentlyContinue).Count
Write-Output ("  已生成缩略图: " + $thumbCount)
Check "缩略图缓存已生成" ($thumbCount -gt 50) ("count=" + $thumbCount)
$memAfterLoad = Mem-MB $appPid
Write-Output ("  内存(加载后): " + $memAfterLoad + " MB")

Write-Output ""
Write-Output "===== 浏览绝区零（含 jpg/png 混合、4K 大图）====="
$zzzTab = Find-ByName $root '绝区零 140'
$zzzTab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Seconds 4
$root = App-Root $appPid
Check "切换到绝区零后显示 140 张" ($null -ne (Find-ByName $root '140 张')) ""

$grid = $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'PhotoGrid')))
$items = $grid.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)
Check "网格已实现图块" ($items.Count -gt 5) ("visible=" + $items.Count)
$items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
$items[0].SetFocus()
Start-Sleep -Milliseconds 800

Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Seconds 7
$root = App-Root $appPid
Check "查看器打开" ($null -ne (Find-ByName $root '返回图库')) ""
Check "显示 1 / 140" ($null -ne (Find-ByName $root '1 / 140')) ""

Write-Output "  连续右翻 14 张…"
$failed = 0
for ($i = 0; $i -lt 14; $i++) {
    Focus-App $appPid
    [System.Windows.Forms.SendKeys]::SendWait('{RIGHT}')
    Start-Sleep -Milliseconds 1400
    $proc2 = Get-Process -Id $appPid -ErrorAction SilentlyContinue
    if (-not $proc2) { $failed = $i + 1; break }
    if (($i % 5) -eq 4) { Write-Output ("    step " + ($i + 1) + " mem=" + (Mem-MB $appPid) + " MB") }
}
Check "翻页 14 张后进程仍存活" ($failed -eq 0) ("died at step " + $failed)
if ($failed -eq 0) {
    $root = App-Root $appPid
    Check "翻到 15 / 140" ($null -ne (Find-ByName $root '15 / 140')) ""
    $memAfterBrowse = Mem-MB $appPid
    Write-Output ("  内存(浏览后): " + $memAfterBrowse + " MB")
    Check "内存未失控 (< 1200MB)" ($memAfterBrowse -lt 1200) ($memAfterBrowse.ToString() + " MB")
}

Write-Output ""
Write-Output "===== 缩放到 400% 再翻页 ====="
Focus-App $appPid
for ($i = 0; $i -lt 6; $i++) { [System.Windows.Forms.SendKeys]::SendWait('{ADD}'); Start-Sleep -Milliseconds 250 }
Start-Sleep -Seconds 2
$root = App-Root $appPid
$zoom = $null
foreach ($t in (Get-ByType $root $CT::Text)) { if ($t.Current.Name -match '^\d+%$') { $zoom = $t.Current.Name } }
Write-Output ("  zoom=" + $zoom)
Check "放大生效" ($null -ne $zoom -and $zoom -ne '100%') ("zoom=" + $zoom)
Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('{RIGHT}')
Start-Sleep -Seconds 2
[System.Windows.Forms.SendKeys]::SendWait('{RIGHT}')
Start-Sleep -Seconds 2
Check "放大后翻页仍存活" ($null -ne (Get-Process -Id $appPid -ErrorAction SilentlyContinue)) ""

Write-Output ""
Write-Output "===== ESC 返回 + 全选 + 内存 ====="
Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Seconds 3
$root = App-Root $appPid
Check "查看器已关闭" ($null -eq (Find-ByName $root '返回图库')) ""
Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('^a')
Start-Sleep -Seconds 3
$root = App-Root $appPid
$selText = $null
foreach ($t in (Get-ByType $root $CT::Text)) { if ($t.Current.Name -like '已选*') { $selText = $t.Current.Name } }
Write-Output ("  selection: " + $selText)
Check "Ctrl+A 全选 140 张" ($selText -like '已选 140 张*') ($selText)
$memFinal = Mem-MB $appPid
Write-Output ("  内存(最终): " + $memFinal + " MB")
Check "全程无崩溃" ($memFinal -gt 0) ""

Stop-Process -Id $appPid -Force
Start-Sleep -Seconds 1
Write-Output ""
Write-Output ("RESULT: pass=" + $script:pass + " fail=" + $script:fail)
Write-Output "--- startup.log ---"
Get-Content (Join-Path $DataDir 'startup.log') -Encoding UTF8 -ErrorAction SilentlyContinue
Write-Output "--- startup.log (tail) ---"
Get-Content (Join-Path $DataDir 'startup.log') -Encoding UTF8 -ErrorAction SilentlyContinue | Select-Object -Last 4`r`n