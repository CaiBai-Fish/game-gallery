param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [string]$ShotDir
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class GGA {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[void][GGA]::SetProcessDPIAware()

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$script:pass = 0; $script:fail = 0

function Check([string]$label, [bool]$ok, [string]$detail) {
    if ($ok) { $script:pass++; Write-Output ("PASS  " + $label + "   " + $detail) }
    else { $script:fail++; Write-Output ("FAIL  " + $label + "   " + $detail) }
}

function Sample-Tabs([bool]$useIcons, [string]$tag) {
    if ($useIcons) { Remove-Item Env:\GAMEGALLERY_NO_ICONS -ErrorAction SilentlyContinue }
    else { $env:GAMEGALLERY_NO_ICONS = '1' }

    Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
    $proc = Start-Process -FilePath $Exe -PassThru
    $appPid = $proc.Id
    Start-Sleep -Seconds 19
    $p = Get-Process -Id $appPid -ErrorAction SilentlyContinue
    if (-not $p) { Write-Output "app exited ($tag)"; return $null }

    $hwnd = $p.MainWindowHandle
    [void][GGA]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 900

    $rect = New-Object GGA+RECT
    [void][GGA]::GetWindowRect($hwnd, [ref]$rect)
    $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bmp.Size)
    $g.Dispose()
    if ($ShotDir) { $bmp.Save((Join-Path $ShotDir ("tabs-" + $tag + ".png")), [System.Drawing.Imaging.ImageFormat]::Png) }

    $root = $AE::FromHandle($hwnd)
    $tabCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem)
    $tabs = $root.FindAll($TS::Descendants, $tabCond)

    $result = @{}
    foreach ($tab in $tabs) {
        $name = $tab.Current.Name
        if ($name -notmatch '^(全部|原神|崩坏|绝区零|收藏)') { continue }
        $r = $tab.Current.BoundingRectangle
        if ($r.Width -lt 5) { continue }

        $x0 = [int]($r.X - $rect.Left); $y0 = [int]($r.Y - $rect.Top)
        $y1 = [math]::Min($bmp.Height - 1, [int]($r.Y + $r.Height - $rect.Top))
        $iconX1 = [math]::Min([int]($r.X + $r.Width - $rect.Left), $x0 + 26)

        $sig = New-Object System.Collections.ArrayList
        for ($y = $y0 + 8; $y -le $y1 - 8; $y += 2) {
            for ($x = $x0 + 6; $x -le $iconX1; $x += 2) {
                $c = $bmp.GetPixel($x, $y)
                [void]$sig.Add("$($c.R),$($c.G),$($c.B)")
            }
        }
        $result[$name] = $sig
    }
    $bmp.Dispose()
    Stop-Process -Id $appPid -Force
    Start-Sleep -Seconds 2
    Remove-Item Env:\GAMEGALLERY_NO_ICONS -ErrorAction SilentlyContinue
    return $result
}

Write-Output "===== Game icon A/B (real icons vs forced font glyphs) ====="
$a = Sample-Tabs $true 'icons'
$b = Sample-Tabs $false 'glyphs'

if ($null -eq $a -or $null -eq $b) { Write-Output "sampling failed"; exit 1 }

foreach ($name in @($a.Keys)) {
    if (-not $b.ContainsKey($name)) { continue }
    $sa = $a[$name]; $sb = $b[$name]
    $n = [math]::Min($sa.Count, $sb.Count)
    $diff = 0
    for ($i = 0; $i -lt $n; $i++) {
        $pa = $sa[$i].Split(','); $pb = $sb[$i].Split(',')
        $d = [math]::Abs([int]$pa[0] - [int]$pb[0]) + [math]::Abs([int]$pa[1] - [int]$pb[1]) + [math]::Abs([int]$pa[2] - [int]$pb[2])
        if ($d -gt 60) { $diff++ }
    }
    $isGameTab = ($name -match '^(原神|崩坏|绝区零)')
    Write-Output ("  " + $name.PadRight(14) + " samples=" + $n + " differingPixels=" + $diff + $(if ($isGameTab) { "   <- game tab" } else { "   <- baseline" }))
    if ($isGameTab) {
        Check ("tab '" + $name + "' renders a distinct icon") ($diff -gt 8) ("differing=" + $diff)
    }
}

Write-Output ""
Write-Output ("RESULT: pass=" + $script:pass + " fail=" + $script:fail)
