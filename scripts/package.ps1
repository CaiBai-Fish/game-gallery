<#
    打包免安装单文件版。

    产物：dist\GameGallery-<版本>-portable.exe

    注意：安装程序不在这里生成。MSI 由 scripts\package-msi.ps1 构建，
    它装的是「自包含 + 常规多文件」的发布目录，不需要单文件——
    MSI 本来就会把几百个文件收进 CAB 并按文件安装，
    用单文件反而会让程序每次启动都先把自己解压到临时目录。

    单文件只用于这个免安装版：一个 exe 双击就能用。
#>
param(
    [string]$Version = '0.1.1',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\GameGallery\GameGallery.csproj'
$dist = Join-Path $root 'dist'
$publish = Join-Path $root 'build\publish-portable'

function Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        try {
            $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null
            foreach ($f in @($found)) { if ($f -and (Test-Path -LiteralPath $f.Trim())) { return $f.Trim() } }
        } catch { }
    }

    foreach ($p in @(
            'D:\Visual Studio\MSBuild\Current\Bin\MSBuild.exe',
            'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
            'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe',
            'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe',
            'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
        )) {
        if (Test-Path -LiteralPath $p) { return $p }
    }

    return $null
}

if (-not $SkipBuild) {
    Step '构建自包含单文件（免安装版用）'

    $msbuild = Find-MSBuild
    if (-not $msbuild) { throw '找不到 MSBuild.exe，请先运行 scripts\build.ps1 确认环境。' }
    Write-Host "   MSBuild: $msbuild"

    Get-Process -Name 'GameGallery' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

    # -restore：干净检出（CI）里没有 obj\project.assets.json，Publish 会直接失败
    & $msbuild $project -t:Publish -restore -p:Configuration=Release `
        -p:EnableMsixTooling=true `
        -p:PublishSingleFile=true `
        -p:SelfContained=true `
        -p:WindowsAppSDKSelfContained=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:RuntimeIdentifier=win-x64 `
        -p:PublishDir="$publish\" `
        -v:m -nologo
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
}

$singleExe = Join-Path $publish 'GameGallery.exe'
if (-not (Test-Path -LiteralPath $singleExe)) { throw "找不到单文件产物：$singleExe" }

New-Item -ItemType Directory -Force -Path $dist | Out-Null
$portableExe = Join-Path $dist "GameGallery-$Version-portable.exe"
Copy-Item -LiteralPath $singleExe -Destination $portableExe -Force

Step '产物'
Write-Host ('   ' + [System.IO.Path]::GetFileName($portableExe) + '   ' + [math]::Round((Get-Item -LiteralPath $portableExe).Length / 1MB, 1) + ' MB')
Write-Host ''
Write-Host '   安装程序请运行 scripts\package-msi.ps1 生成 MSI。'
