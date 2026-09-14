# Verifies the RUNTIME window icon (title bar + taskbar button), which is independent of the
# icon embedded in the EXE file. Uses pixel matching against the accent colour of AppIcon.png.
param(
    [switch]$KeepRunning,
    [int]$Tolerance = 26,
    [int]$MinPixels = 12
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\GameGallery\bin\Release\net8.0-windows10.0.19041.0\win-x64\GameGallery.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "not built: $exe" }

# Accent colour of the source artwork, measured from AppIcon.png.
$src = [System.Drawing.Image]::FromFile((Join-Path $root 'installer\AppIcon.png'))
$sb = New-Object System.Drawing.Bitmap($src, 64, 64)
$src.Dispose()
$buckets = @{}
for ($y = 0; $y -lt 64; $y++) {
    for ($x = 0; $x -lt 64; $x++) {
        $c = $sb.GetPixel($x, $y)
        if ($c.A -lt 200) { continue }
        # Only saturated colours identify the artwork; white/greys are just background.
        $max = [math]::Max($c.R, [math]::Max($c.G, $c.B))
        $min = [math]::Min($c.R, [math]::Min($c.G, $c.B))
        if (($max - $min) -lt 60) { continue }
        $k = "{0},{1},{2}" -f $c.R, $c.G, $c.B
        $buckets[$k] = 1 + ($buckets[$k] -as [int])
    }
}
$sb.Dispose()
if ($buckets.Count -eq 0) { throw 'AppIcon.png has no saturated colour to key on' }
$accentKey = ($buckets.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1).Key
$ar, $ag, $ab = ($accentKey -split ',') | ForEach-Object { [int]$_ }
Write-Host "accent colour from AppIcon.png: $ar,$ag,$ab"

function Count-Accent([System.Drawing.Bitmap]$bmp) {
    $n = 0
    for ($y = 0; $y -lt $bmp.Height; $y++) {
        for ($x = 0; $x -lt $bmp.Width; $x++) {
            $c = $bmp.GetPixel($x, $y)
            if ([math]::Abs($c.R - $ar) -le $Tolerance -and [math]::Abs($c.G - $ag) -le $Tolerance -and [math]::Abs($c.B - $ab) -le $Tolerance) { $n++ }
        }
    }
    return $n
}

function Shot([int]$x, [int]$y, [int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    return $bmp
}

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
Start-Sleep -Seconds 6

$proc.Refresh()
$title = $proc.MainWindowTitle
$rect = $win.Current.BoundingRectangle
Write-Host ("window '{0}' at {1},{2} {3}x{4}" -f $title, [int]$rect.X, [int]$rect.Y, [int]$rect.Width, [int]$rect.Height)

$fails = New-Object System.Collections.Generic.List[string]

Write-Host "`n=== 1. title bar icon (in-app) ==="
$bmp = Shot ([int]$rect.X + 4) ([int]$rect.Y + 3) 44 36
$titleBarPixels = Count-Accent $bmp
$bmp.Save((Join-Path $root 'build\shot-titlebar.png'))
$bmp.Dispose()
Write-Host "  accent pixels in title bar icon area: $titleBarPixels"
if ($titleBarPixels -lt $MinPixels) { $fails.Add("title bar icon does not contain the app accent colour ($titleBarPixels px)") }

Write-Host "`n=== 2. taskbar button ==="
$tbCond = New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'Shell_TrayWnd')
$taskbar = $AE::RootElement.FindFirst($TS::Children, $tbCond)
$taskbarPixels = -1
if (-not $taskbar) {
    $fails.Add('taskbar (Shell_TrayWnd) not found')
} else {
    $btnCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)
    $buttons = @($taskbar.FindAll($TS::Descendants, $btnCond))
    $target = $buttons | Where-Object { $_.Current.Name -like "*$title*" } | Select-Object -First 1
    if (-not $target) {
        Write-Host ("  taskbar buttons: " + (($buttons | ForEach-Object { "'" + $_.Current.Name + "'" }) -join ', '))
        $fails.Add("no taskbar button named '$title'")
    } else {
        $br = $target.Current.BoundingRectangle
        Write-Host ("  taskbar button at {0},{1} {2}x{3}" -f [int]$br.X, [int]$br.Y, [int]$br.Width, [int]$br.Height)
        $bmp = Shot ([int]$br.X) ([int]$br.Y) ([int]$br.Width) ([int]$br.Height)
        $taskbarPixels = Count-Accent $bmp
        $bmp.Save((Join-Path $root 'build\shot-taskbar.png'))
        $bmp.Dispose()
        Write-Host "  accent pixels in taskbar button: $taskbarPixels"
        if ($taskbarPixels -lt $MinPixels) { $fails.Add("taskbar button icon does not contain the app accent colour ($taskbarPixels px)") }
    }
}

Write-Host "`n=== 3. startup log ==="
$log = Join-Path $env:LOCALAPPDATA 'GameGallery\startup.log'
if (Test-Path -LiteralPath $log) {
    Get-Content -LiteralPath $log -Tail 3 -Encoding UTF8 | ForEach-Object { Write-Host "  $_" }
}

Write-Host ''
if ($fails.Count -eq 0) { Write-Host 'APPICON OK' } else { foreach ($f in $fails) { Write-Host "FAIL: $f" } }

if (-not $KeepRunning) { Get-Process -Name GameGallery -ErrorAction SilentlyContinue | Stop-Process -Force }
if ($fails.Count -gt 0) { exit 1 }
