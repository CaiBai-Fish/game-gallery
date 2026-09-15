# 一键跑完整验证套件：按顺序执行各 verify-*.ps1，汇总 PASS/FAIL 数量。
# 每个套件的完整输出写入 build\verify\<name>.log。
#
#   .\scripts\verify-all.ps1                 # 全部
#   .\scripts\verify-all.ps1 -SkipReal       # 跳过真实截图库压测（需要本机装了游戏）
#   .\scripts\verify-all.ps1 -SkipMsi        # 跳过 MSI 安装/卸载测试
[CmdletBinding()]
param(
    [switch]$SkipE2e,
    [switch]$SkipReal,
    [switch]$SkipMsi,
    [string]$MsiPath
)

$ErrorActionPreference = 'Continue'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\GameGallery\bin\Release\net8.0-windows10.0.19041.0\win-x64\GameGallery.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "还没构建：$exe（先跑 scripts\build.ps1）" }

$data = Join-Path $root '.verify\data'
$test = Join-Path $root '.verify\test'
if (-not $MsiPath) { $MsiPath = Join-Path $root 'dist\GameGallery-0.1.2.msi' }

$logDir = Join-Path $root 'build\verify'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$plan = @(
    @{ Name = 'verify-appicon'; Skip = $false; Args = @() },
    @{ Name = 'verify-nav';     Skip = $false; Args = @() },
    @{ Name = 'verify-icons';   Skip = $false; Args = @('-Exe', $exe, '-DataDir', $data) },
    @{ Name = 'verify-gallery'; Skip = $false; Args = @('-Exe', $exe, '-DataDir', $data, '-TestDir', $test) },
    @{ Name = 'verify-e2e';     Skip = [bool]$SkipE2e; Args = @('-Exe', $exe, '-DataDir', $data, '-TestDir', $test) },
    @{ Name = 'verify-real';    Skip = [bool]$SkipReal; Args = @('-Exe', $exe, '-DataDir', $data) },
    @{ Name = 'verify-msi';     Skip = [bool]$SkipMsi; Args = @('-MsiPath', $MsiPath) }
)

$results = New-Object System.Collections.Generic.List[object]

foreach ($s in $plan) {
    if ($s.Skip) {
        Write-Host ("跳过 {0}" -f $s.Name) -ForegroundColor DarkGray
        continue
    }

    $script = Join-Path $PSScriptRoot ("{0}.ps1" -f $s.Name)
    if (-not (Test-Path -LiteralPath $script)) { Write-Host ("找不到 {0}" -f $script) -ForegroundColor Yellow; continue }

    Write-Host ("`n=== {0} ===" -f $s.Name) -ForegroundColor Cyan
    $logFile = Join-Path $logDir ("{0}.log" -f $s.Name)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    $output = & $script @($s.Args) 2>&1
    $output | Set-Content -LiteralPath $logFile -Encoding UTF8
    $sw.Stop()

    $pass = @($output | Where-Object { $_ -match '^\s*PASS' }).Count
    $fail = @($output | Where-Object { $_ -match '^\s*FAIL' }).Count

    # 有些套件用 "OK/NAV OK/APPICON OK" 结尾而不是逐项 PASS
    $okMarker = @($output | Where-Object { $_ -match '(NAV OK|APPICON OK|GALLERY OK|E2E OK|MSI OK)$' }).Count

    $status = if ($fail -eq 0 -and ($pass -gt 0 -or $okMarker -gt 0)) { 'OK' } else { 'FAILED' }
    Write-Host ("  {0}  pass={1} fail={2}  ({3:N1}s)" -f $status, $pass, $fail, $sw.Elapsed.TotalSeconds) `
        -ForegroundColor $(if ($status -eq 'OK') { 'Green' } else { 'Red' })
    @($output | Where-Object { $_ -match '^\s*FAIL' }) | ForEach-Object { Write-Host ("    " + $_) -ForegroundColor Red }

    $results.Add([pscustomobject]@{
        Suite  = $s.Name
        Pass   = $pass
        Fail   = $fail
        Status = $status
        Log    = $logFile
    })
}

Write-Host "`n=== 汇总 ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-String | Write-Host

$bad = @($results | Where-Object { $_.Status -ne 'OK' })
if ($bad.Count -eq 0) {
    Write-Host ("全部通过：{0} 个套件，{1} 项断言" -f $results.Count, (($results | Measure-Object Pass -Sum).Sum)) -ForegroundColor Green
} else {
    Write-Host ("有 {0} 个套件未通过" -f $bad.Count) -ForegroundColor Red
    exit 1
}
