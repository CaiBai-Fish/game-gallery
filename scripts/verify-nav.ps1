# Regression check for the nav pane: collapse must hide only the text, expand must restore it,
# and the item set must stay stable (no rebuild => no selection-indicator replay).
# UI Automation only.
param(
    [int]$SettleSeconds = 3,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\GameGallery\bin\Release\net8.0-windows10.0.19041.0\win-x64\GameGallery.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "not built: $exe" }

Get-Process -Name GameGallery -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$proc = Start-Process -FilePath $exe -PassThru

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

$win = $null
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)
    $win = $AE::RootElement.FindFirst($TS::Children, $cond)
    if ($win) { break }
}
if (-not $win) { throw 'main window not found' }
Start-Sleep -Seconds 4

function Get-Element([System.Windows.Automation.AutomationElement]$w, [string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    $w.FindFirst($TS::Descendants, $c)
}

# Cached automation elements go stale after the pane relayouts, so always re-acquire the window.
function Get-Window {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)
    $AE::RootElement.FindFirst($TS::Children, $cond)
}

# Only the NavigationView items (the photo GridView tiles are ListItems too).
function Get-NavRows([System.Windows.Automation.AutomationElement]$w) {
    $nav = Get-Element $w 'Nav'
    if (-not $nav) { return @() }
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem)
    $all = @($nav.FindAll($TS::Descendants, $c))
    $rows = @($all | Where-Object { $_.Current.ClassName -like '*Navigation*' })
    if ($rows.Count -eq 0) { $rows = @($all | Select-Object -First 6) }
    return $rows
}

function Get-RowState([System.Windows.Automation.AutomationElement]$item) {
    $tc = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text)
    $texts = @($item.FindAll($TS::Descendants, $tc) | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
    $ic = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Image)
    $imgs = @($item.FindAll($TS::Descendants, $ic))
    $rect = if ($imgs.Count -gt 0) { $imgs[0].Current.BoundingRectangle } else { $null }
    [pscustomobject]@{
        Name = $item.Current.Name
        Texts = $texts
        ImageCount = $imgs.Count
        IconW = if ($rect) { [math]::Round($rect.Width) } else { 0 }
        IconH = if ($rect) { [math]::Round($rect.Height) } else { 0 }
    }
}

function Get-GameIconBox([System.Windows.Automation.AutomationElement]$w) {
    # First nav row that actually shows an image (the game tabs); avoids non-ASCII literals.
    foreach ($row in (Get-NavRows $w)) {
        $s = Get-RowState $row
        if ($s.ImageCount -gt 0) { return $s }
    }
    return $null
}

$fails = New-Object System.Collections.Generic.List[string]
function Snapshot([string]$label) {
    $rows = Get-NavRows (Get-Window)
    $states = @($rows | ForEach-Object { Get-RowState $_ })
    $withText = @($states | Where-Object { $_.Texts.Count -gt 0 })
    $withImage = @($states | Where-Object { $_.ImageCount -gt 0 })
    Write-Host ("[{0}] navRows={1} rowsWithText={2} rowsWithImage={3} firstNames={4}" -f `
        $label, $states.Count, $withText.Count, $withImage.Count,
        (($states | Select-Object -First 6 | ForEach-Object { $_.Name }) -join ' | '))
    foreach ($s in ($states | Select-Object -First 6)) {
        Write-Host ("      '{0}' texts='{1}' images={2} icon={3}x{4}" -f $s.Name, ($s.Texts -join ','), $s.ImageCount, $s.IconW, $s.IconH)
    }
    [pscustomobject]@{ Rows = $states; WithText = $withText.Count; WithImage = $withImage.Count }
}

Write-Host '=== 1. initial (pane open) ==='
$s1 = Snapshot 'open-initial'
if ($s1.Rows.Count -lt 5) { $fails.Add("expected >=5 nav rows, got $($s1.Rows.Count)") }
if ($s1.WithText -lt 5) { $fails.Add("expanded pane shows text on only $($s1.WithText) nav row(s)") }
$firstOpen = $s1.Rows[0].Name

$toggle = Get-Element (Get-Window) 'TogglePaneButton'
if (-not $toggle) { throw 'TogglePaneButton not found' }

Write-Host "`n=== 2. collapse (click toggle) ==="
$toggle.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 1
$s2a = Snapshot 'collapsed-t1'
Start-Sleep -Seconds $SettleSeconds
$s2 = Snapshot 'collapsed-settled'
if ($s2.WithText -ne 0) { $fails.Add("collapsed pane still shows text on $($s2.WithText) nav row(s)") }
if ($s2.WithImage -lt 4) { $fails.Add("collapsed pane lost icons: only $($s2.WithImage) row(s) have an image") }

Write-Host "`n=== 3. expand again ==="
$toggle = Get-Element (Get-Window) 'TogglePaneButton'
$toggle.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 1
$s3a = Snapshot 'expanded-t1'
Start-Sleep -Seconds $SettleSeconds
$s3 = Snapshot 'expanded-settled'
if ($s3.WithText -lt 5) { $fails.Add("expanded-after-collapse shows text on only $($s3.WithText) nav row(s)") }
if ($s3.WithText -ne $s1.WithText) { $fails.Add("text row count changed across collapse/expand: $($s1.WithText) -> $($s3.WithText)") }

Write-Host "`n=== 4. row identity stable (no rebuild) ==="
$names1 = ($s1.Rows | ForEach-Object { $_.Name }) -join '|'
$names3 = ($s3.Rows | ForEach-Object { $_.Name }) -join '|'
if ($names1 -ne $names3) { $fails.Add('nav item names changed across collapse/expand (items were rebuilt)') }

Write-Host "`n=== 5. two more collapse/expand cycles ==="
foreach ($n in 1, 2) {
    $t = Get-Element (Get-Window) 'TogglePaneButton'
    $t.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2
    $t = Get-Element (Get-Window) 'TogglePaneButton'
    $t.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2
    $sN = Snapshot "cycle$n-expanded"
    if ($sN.WithText -lt 5) { $fails.Add("cycle ${n}: text missing after re-expand ($($sN.WithText) row(s))") }
}

Write-Host "`n=== 6. icon rendered size (must match in both states) ==="
$t = Get-Element (Get-Window) 'TogglePaneButton'
$t.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 3
$colBox = Get-GameIconBox $win
$t = Get-Element (Get-Window) 'TogglePaneButton'
$t.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 3
$expBox = Get-GameIconBox $win
Write-Host ("  collapsed icon {0}x{1} | expanded icon {2}x{3}" -f $colBox.IconW, $colBox.IconH, $expBox.IconW, $expBox.IconH)
if ($expBox.IconW -ne 34 -or $expBox.IconH -ne 34) { $fails.Add("expanded icon is $($expBox.IconW)x$($expBox.IconH), expected 34x34") }
if ($colBox.IconW -ne $expBox.IconW -or $colBox.IconH -ne $expBox.IconH) {
    $fails.Add("icon size differs between states: $($colBox.IconW)x$($colBox.IconH) vs $($expBox.IconW)x$($expBox.IconH)")
}

Write-Host ''
if ($fails.Count -eq 0) { Write-Host 'NAV OK' } else { foreach ($f in $fails) { Write-Host "FAIL: $f" } }

if (-not $KeepRunning) { Get-Process -Name GameGallery -ErrorAction SilentlyContinue | Stop-Process -Force }
if ($fails.Count -gt 0) { exit 1 }
