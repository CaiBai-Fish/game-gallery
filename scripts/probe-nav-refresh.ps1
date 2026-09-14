# Timeline probe: after clicking the built-in pane toggle, sample (via UI Automation) how many
# nav rows show text, every ~120ms. Detects any "refresh" that lands later than the transition.
param(
    [int]$WindowMs = 2500,
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

function Get-Window {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)
    $AE::RootElement.FindFirst($TS::Children, $cond)
}

$win = $null
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $win = Get-Window
    if ($win) { break }
}
if (-not $win) { throw 'main window not found' }
Start-Sleep -Seconds 6

function Get-NavRows {
    $w = Get-Window
    if (-not $w) { return @() }
    $nc = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'Nav')
    $nav = $w.FindFirst($TS::Descendants, $nc)
    if (-not $nav) { return @() }
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem)
    $all = @($nav.FindAll($TS::Descendants, $c))
    $rows = @($all | Where-Object { $_.Current.ClassName -like '*Navigation*' })
    if ($rows.Count -eq 0) { $rows = @($all | Select-Object -First 6) }
    return $rows
}

function Get-TextCount {
    $tc = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text)
    $withText = 0
    foreach ($row in (Get-NavRows)) {
        $texts = @($row.FindAll($TS::Descendants, $tc) | Where-Object { $_.Current.Name })
        if ($texts.Count -gt 0) { $withText++ }
    }
    return $withText
}

function Get-Toggle {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'TogglePaneButton')
    (Get-Window).FindFirst($TS::Descendants, $c)
}

function Trace([string]$label, [int]$durationMs) {
    Write-Host "`n=== $label ==="
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $prev = -1
    while ($sw.ElapsedMilliseconds -lt $durationMs) {
        $t0 = $sw.ElapsedMilliseconds
        $n = Get-TextCount
        $mark = if ($prev -ge 0 -and $n -ne $prev) { '  <-- CHANGED' } else { '' }
        Write-Host ("  t={0,5}ms rowsWithText={1}{2}" -f $t0, $n, $mark)
        $prev = $n
        Start-Sleep -Milliseconds 120
    }
    return $prev
}

[void](Trace 'baseline (pane open, idle)' 500)

Write-Host "`n>>> clicking pane toggle (collapse) <<<"
$t = Get-Toggle
$t.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
[void](Trace 'after collapse' $WindowMs)

Write-Host "`n>>> clicking pane toggle (expand) <<<"
$t = Get-Toggle
$t.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
[void](Trace 'after expand' $WindowMs)

if (-not $KeepRunning) { Get-Process -Name GameGallery -ErrorAction SilentlyContinue | Stop-Process -Force }
