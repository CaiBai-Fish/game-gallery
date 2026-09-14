param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$TestDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class GGK2 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    public static void DoubleClick(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(300);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(110);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
}
"@

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class RBTool {
    [StructLayout(LayoutKind.Sequential)]
    public struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
    public static extern int SHQueryRecycleBinW(string pszRootPath, ref SHQUERYRBINFO p);
}
"@
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$AnyCond = [System.Windows.Automation.Condition]::TrueCondition
$CT = [System.Windows.Automation.ControlType]
$script:pass = 0
$script:fail = 0

function Check([string]$label, [bool]$ok, [string]$detail) {
    if ($ok) { $script:pass++; Write-Output ("PASS  " + $label + "   " + $detail) }
    else { $script:fail++; Write-Output ("FAIL  " + $label + "   " + $detail) }
}
function App-Root($thePid) { return $AE::FromHandle((Get-Process -Id $thePid).MainWindowHandle) }
function Focus-App($thePid) {
    [void][GGK2]::SetForegroundWindow((Get-Process -Id $thePid).MainWindowHandle)
    Start-Sleep -Milliseconds 900
}
function Find-ByName($r, [string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
    return $r.FindFirst($TS::Descendants, $c)
}
function Get-ByType($r, $ctrlType) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ctrlType)
    return $r.FindAll($TS::Descendants, $c)
}
function Get-TabNames($r) {
    $out = @()
    foreach ($e in (Get-ByType $r $CT::ListItem)) { if ($e.Current.Name -match '^(全部|原神|崩坏|绝区零|收藏|其他)') { $out += $e.Current.Name } }
    return $out
}
function Get-Grid($r) {
    return $r.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'PhotoGrid')))
}
function Viewer-Open($r) { return $null -ne (Find-ByName $r '返回图库') }
function Invoke-Elem($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Reset-Clipboard {
    for ($i = 0; $i -lt 15; $i++) {
        try { Set-Clipboard -Value 'clipboard-sentinel'; return $true } catch { Start-Sleep -Milliseconds 500 }
    }
    return $false
}
function Get-RecycleCount([string]$root) {
    try {
        $i = New-Object RBTool+SHQUERYRBINFO
        $i.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf([type][RBTool+SHQUERYRBINFO])
        $hr = [RBTool]::SHQueryRecycleBinW($root, [ref]$i)
        if ($hr -ne 0) { return -1 }
        return [int]$i.i64NumItems
    } catch { return -1 }
}
function New-TestImage([string]$path, [int]$w, [int]$h, [int]$r, [int]$g_, [int]$b) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $gr = [System.Drawing.Graphics]::FromImage($bmp)
    $gr.Clear([System.Drawing.Color]::FromArgb($r, $g_, $b))
    $br = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $gr.FillEllipse($br, [int]($w / 4), [int]($h / 4), [int]($w / 2), [int]($h / 2))
    $gr.Dispose(); $br.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
function FileCount { return (Get-ChildItem $TestDir -File).Count }

# ------------------------------------------------------------------ setup
Remove-Item $TestDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $TestDir -Force | Out-Null
New-TestImage (Join-Path $TestDir 'test-alpha.png') 2400 1350 40 90 160
New-TestImage (Join-Path $TestDir 'test-beta.png') 1024 768 150 60 60
New-TestImage (Join-Path $TestDir 'test-gamma.png') 800 600 60 140 70

Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
(@{ CustomFolders = @(@{ Path = $TestDir; GameKey = 'other' }) } | ConvertTo-Json -Depth 5) |
    Set-Content -Path (Join-Path $DataDir 'settings.json') -Encoding UTF8

[void](Reset-Clipboard)

$env:GAMEGALLERY_DATA_DIR = $DataDir
$proc = Start-Process -FilePath $Exe -PassThru
$appPid = $proc.Id
Start-Sleep -Seconds 18
if (-not (Get-Process -Id $appPid -ErrorAction SilentlyContinue)) {
    Write-Output "app exited early: $($proc.ExitCode)"
    Get-Content (Join-Path $DataDir 'startup.log') -Raw -Encoding UTF8
    exit 1
}

$root = App-Root $appPid
Write-Output "===== 1. discovery + tabs ====="
$tabLine = (Get-TabNames $root) -join ' / '
Write-Output ("  " + $tabLine)
Check "4 HoYoPlay games discovered" (($tabLine -like '*原神 6*') -and ($tabLine -like '*星穹铁道 54*') -and ($tabLine -like '*绝区零 140*') -and ($tabLine -like '*崩坏3 7*')) ""
Check "manual folder merged" ($tabLine -like '*其他截图 3*') ""
Check "favorites tab present" ($tabLine -like '*收藏 0*') ""

Write-Output ""
Write-Output "===== 2. tab filtering ====="
$tab = Find-ByName $root '其他截图 3'
$tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Seconds 3
$root = App-Root $appPid
Check "count label = 3" ($null -ne (Find-ByName $root '3 张')) ""
$tileCount = (Get-Grid $root).FindAll($TS::Children, $AnyCond).Count
Check "grid shows 3 tiles" ($tileCount -eq 3) ("count=" + $tileCount)

Write-Output ""
Write-Output "===== 3. Enter opens viewer ====="
$items = (Get-Grid $root).FindAll($TS::Children, $AnyCond)
$items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
$items[0].SetFocus()
Start-Sleep -Milliseconds 800
Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Seconds 6
$root = App-Root $appPid
Check "viewer opened" (Viewer-Open $root) ""
Check "viewer index badge 1 / 3" ($null -ne (Find-ByName $root '1 / 3')) ""

Write-Output ""
Write-Output "===== 4. keyboard paging ====="
Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('{RIGHT}')
Start-Sleep -Seconds 3
$root = App-Root $appPid
Check "Right arrow -> 2 / 3" ($null -ne (Find-ByName $root '2 / 3')) ""
Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('{LEFT}')
Start-Sleep -Seconds 2
$root = App-Root $appPid
Check "Left arrow -> 1 / 3" ($null -ne (Find-ByName $root '1 / 3')) ""

Write-Output ""
Write-Output "===== 5. zoom ====="
$zoomBefore = $null
foreach ($t in (Get-ByType $root $CT::Text)) { if ($t.Current.Name -match '^\d+%$') { $zoomBefore = $t.Current.Name } }
Write-Output ("  zoom badge before: '" + $zoomBefore + "'")
Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('{ADD}')
Start-Sleep -Seconds 2
$root = App-Root $appPid
$zoomAfter = $null
foreach ($t in (Get-ByType $root $CT::Text)) { if ($t.Current.Name -match '^\d+%$') { $zoomAfter = $t.Current.Name } }
Check "zoom badge present and changed" (($null -ne $zoomAfter) -and ($zoomAfter -ne $zoomBefore)) ("before='" + $zoomBefore + "' after='" + $zoomAfter + "'")

Write-Output ""
Write-Output "===== 6. favorite toggle in viewer ====="
Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('f')
Start-Sleep -Seconds 2
$root = App-Root $appPid
$favOn = (Get-TabNames $root) | Where-Object { $_ -like '收藏*' }
Check "favorite on -> 1" (($favOn -join ',') -like '*收藏 1*') ($favOn -join ',')
Check "viewer still open" (Viewer-Open $root) ""
Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('f')
Start-Sleep -Seconds 2
$root = App-Root $appPid
$favOff = (Get-TabNames $root) | Where-Object { $_ -like '收藏*' }
Check "favorite off -> 0" (($favOff -join ',') -like '*收藏 0*') ($favOff -join ',')

Write-Output ""
Write-Output "===== 7. copy to clipboard ====="
[void](Reset-Clipboard)
Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('^c')
Start-Sleep -Seconds 3
$drop = Get-Clipboard -Format FileDropList
Check "clipboard holds the image file" ($null -ne $drop -and $drop.Count -ge 1) (($drop | ForEach-Object { $_.Name }) -join ',')

Write-Output ""
Write-Output "===== 8. ESC closes viewer ====="
Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Seconds 3
$root = App-Root $appPid
Check "viewer closed" ((-not (Viewer-Open $root))) ""

Write-Output ""
Write-Output "===== 9. double-click opens viewer ====="
$root = App-Root $appPid
$items = (Get-Grid $root).FindAll($TS::Children, $AnyCond)
$clicked = $false
foreach ($idx in @(1, 0, 2)) {
    if ($idx -ge $items.Count) { continue }
    $rect = $items[$idx].Current.BoundingRectangle
    if ($rect.Width -lt 4 -or $rect.Height -lt 4) { continue }
    $cx = [int]($rect.X + $rect.Width / 2)
    $cy = [int]($rect.Y + $rect.Height / 2)
    Focus-App $appPid
    [GGK2]::DoubleClick($cx, $cy)
    $clicked = $true
    Write-Output ("  double-clicked tile #" + $idx + " at " + $cx + "," + $cy + " (" + [int]$rect.Width + "x" + [int]$rect.Height + ")")
    break
}
Start-Sleep -Seconds 6
$root = App-Root $appPid
Check "viewer opened via double-click" ((Viewer-Open $root) -and $clicked) ("clicked=" + $clicked)

Write-Output ""
Write-Output "===== 10. delete: cancel is safe ====="
$before = FileCount
Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('{DELETE}')
Start-Sleep -Seconds 4
$root = App-Root $appPid
$cancelBtn = Find-ByName $root '取消'
Check "confirm dialog appeared" ($null -ne $cancelBtn) ""
if ($cancelBtn) { Invoke-Elem $cancelBtn; Start-Sleep -Seconds 3 }
Check "cancel left the file on disk" ((FileCount) -eq $before) ("before=$before after=" + (FileCount))

Write-Output ""
Write-Output "===== 11. delete: confirm removes one file ====="
$before = FileCount
$rbBefore = Get-RecycleCount 'E:\'
Write-Output ("  recycle bin items before: " + $rbBefore)
Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('{DELETE}')
Start-Sleep -Seconds 4
$root = App-Root $appPid
$primaryBtn = Find-ByName $root '移到回收站'
Check "confirm dialog appeared" ($null -ne $primaryBtn) ""
if ($primaryBtn) { Invoke-Elem $primaryBtn; Start-Sleep -Seconds 5 }
$after = FileCount
Check "one file removed" ($after -eq ($before - 1)) ("before=$before after=$after")
$rbAfter = Get-RecycleCount 'E:\'
Write-Output ("  recycle bin items after: " + $rbAfter)
Check "deleted file went to the recycle bin (recoverable)" (($rbBefore -ge 0) -and ($rbAfter -eq ($rbBefore + 1))) ("before=" + $rbBefore + " after=" + $rbAfter)
$root = App-Root $appPid
$tabs2 = Get-TabNames $root
Check "tab counts updated" (($tabs2 -join ',') -like '*其他截图 2*') (($tabs2 | Where-Object { $_ -like '其他截图*' }) -join ',')

Write-Output ""
Write-Output "===== 12. search filter ====="
$searchBox = Find-ByName $root '搜索文件名'
if ($searchBox) {
    $vp = $searchBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue('test')
    Start-Sleep -Seconds 3
    $root = App-Root $appPid
    Check "search narrows the list" ($null -ne (Find-ByName $root '2 张')) ""
    $vp.SetValue('')
    Start-Sleep -Seconds 2
} else { Check "search box found" $false "" }

Write-Output ""
Write-Output "===== 13. health ====="
Check "process still alive" ($null -ne (Get-Process -Id $appPid -ErrorAction SilentlyContinue)) ""


Write-Output ""
Write-Output "===== 14. delete is immune to a mid-dialog library refresh (HIGH-1) ====="
# 重新生成一批图，不依赖前面步骤删掉了哪一张
Get-ChildItem $TestDir -File | Remove-Item -Force -ErrorAction SilentlyContinue
New-TestImage (Join-Path $TestDir 'test-delta.png') 640 480 200 40 40
New-TestImage (Join-Path $TestDir 'test-epsilon.png') 640 480 40 200 40
Start-Sleep -Seconds 5
$root = App-Root $appPid
$tabLine3 = (Get-TabNames $root) -join ' / '
Write-Output ("  tabs: " + $tabLine3)

# 全选后打开删除确认框
$gridNow = $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'PhotoGrid')))
$tilesNow = $gridNow.FindAll($TS::Children, $AnyCond)
$first = $true
foreach ($tl in $tilesNow) {
    try {
        $pat = $tl.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if ($first) { $pat.Select(); $first = $false } else { $pat.AddToSelection() }
    } catch { }
}
Start-Sleep -Seconds 1
$selectedCount = $tilesNow.Count
Write-Output ("  selected tiles: " + $selectedCount)

Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('{DELETE}')
Start-Sleep -Seconds 4
$root = App-Root $appPid
$dlg = Find-ByName $root '移到回收站'
Check "confirm dialog is open" ($null -ne $dlg) ""

# 对话框还开着的时候，往截图目录里再丢一张，触发 FileSystemWatcher 自动刷新
$before14 = FileCount
$lateFile = Join-Path $TestDir 'test-zeta-late.png'
New-TestImage $lateFile 640 480 40 40 200
Write-Output ("  watcher event injected (dir now has " + (FileCount) + " files, confirmed=" + $selectedCount + ")")
Start-Sleep -Seconds 6

Check "dialog survived the background refresh" ($null -ne (Find-ByName (App-Root $appPid) '移到回收站')) ""
if ($dlg) { Invoke-Elem $dlg; Start-Sleep -Seconds 7 }

$after14 = FileCount
# 确认时选了 selectedCount 张，对话框开着期间又新增 1 张；
# 只有当初确认的那 selectedCount 张应该被删掉，那张新图必须还在。
Check "only the confirmed files were deleted" ($after14 -eq (($before14 + 1) - $selectedCount)) ("selected=" + $selectedCount + " before=" + $before14 + " after=" + $after14)
Check "the late-added screenshot was NOT deleted" (Test-Path $lateFile) ("late file exists=" + (Test-Path $lateFile))
Check "process survived" ($null -ne (Get-Process -Id $appPid -ErrorAction SilentlyContinue)) ""

Write-Output ""
Write-Output "===== 15. double DELETE must not kill the app (HIGH-2) ====="
Focus-App $appPid
[System.Windows.Forms.SendKeys]::SendWait('{DELETE}{DELETE}{DELETE}')
Start-Sleep -Seconds 3
Check "rapid triple DELETE: app alive with only one dialog" ($null -ne (Get-Process -Id $appPid -ErrorAction SilentlyContinue)) ""
$dlg2 = Find-ByName (App-Root $appPid) '取消'
if ($dlg2) { Invoke-Elem $dlg2; Start-Sleep -Seconds 3 }
Check "still alive after cancelling" ($null -ne (Get-Process -Id $appPid -ErrorAction SilentlyContinue)) ""

Write-Output ""
Write-Output "===== 16. viewer stays aligned with the list across a refresh (HIGH-3) ====="
$root = App-Root $appPid
$gridV = $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'PhotoGrid')))
$tilesV = $gridV.FindAll($TS::Children, $AnyCond)
if ($tilesV.Count -ge 1) {
    $tilesV[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $tilesV[0].SetFocus()
    Start-Sleep -Milliseconds 700
    Focus-App $appPid
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Seconds 6
    $root = App-Root $appPid
    $metaBefore = $null
    foreach ($tx in (Get-ByType $root $CT::Text)) { if ($tx.Current.Name -match ' · \d+/\d+$') { $metaBefore = $tx.Current.Name } }
    Write-Output ("  viewer before refresh: " + $metaBefore)

    # 查看器开着的时候再塞一张新图，触发自动刷新重排列表
    New-TestImage (Join-Path $TestDir 'test-eta-late.png') 640 480 90 90 90
    Start-Sleep -Seconds 6

    $root = App-Root $appPid
    $metaAfter = $null
    foreach ($tx in (Get-ByType $root $CT::Text)) { if ($tx.Current.Name -match ' · \d+/\d+$') { $metaAfter = $tx.Current.Name } }
    Write-Output ("  viewer after refresh:  " + $metaAfter)
    $fpBefore = if ($metaBefore) { (($metaBefore -split ' · ')[0..2] -join ' · ') } else { '' }
    $fpAfter = if ($metaAfter) { (($metaAfter -split ' · ')[0..2] -join ' · ') } else { '' }
    Check "viewer still shows the same photo after the list reordered" (($null -ne $metaAfter) -and ($fpBefore -eq $fpAfter)) ("before='" + $metaBefore + "' after='" + $metaAfter + "'")
    Check "viewer list total grew to 2 after the refresh" (($metaAfter -split ' · ')[3] -like '*/2') (($metaAfter -split ' · ')[3])
    Focus-App $appPid
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Seconds 2
}

Write-Output ""
Write-Output "===== 17. final health ====="
Check "process alive" ($null -ne (Get-Process -Id $appPid -ErrorAction SilentlyContinue)) ""
Stop-Process -Id $appPid -Force
Start-Sleep -Seconds 1
Write-Output ""
Write-Output ("RESULT: pass=" + $script:pass + " fail=" + $script:fail)
