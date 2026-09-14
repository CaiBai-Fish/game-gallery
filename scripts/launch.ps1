#requires -Version 5.1
<#
    启动 GameGallery；如果还没构建过，先自动构建一次。
#>
param(
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$configuration = 'Release'
$exe = Join-Path $root "src\GameGallery\bin\$configuration\net8.0-windows10.0.19041.0\win-x64\GameGallery.exe"

function Test-BuildFresh {
    if (-not (Test-Path -LiteralPath $exe)) { return $false }

    $exeTime = (Get-Item -LiteralPath $exe).LastWriteTimeUtc
    $sourceDir = Join-Path $root 'src\GameGallery'
    $newer = Get-ChildItem -LiteralPath $sourceDir -Recurse -File -Include *.cs, *.xaml, *.csproj, *.manifest -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\obj\\' -and $_.FullName -notmatch '\\bin\\' -and $_.LastWriteTimeUtc -gt $exeTime }
    return ($null -eq $newer -or $newer.Count -eq 0)
}

if ($Rebuild -or -not (Test-BuildFresh)) {
    Write-Host "正在构建…"
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "找不到可执行文件：$exe" -ForegroundColor Red
    exit 1
}

Start-Process -FilePath $exe
