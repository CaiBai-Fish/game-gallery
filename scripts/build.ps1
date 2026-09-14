#requires -Version 5.1
<#
    构建 GameGallery。

    必须用 Visual Studio 自带的 MSBuild，而不是 `dotnet build`：
    WinUI 3 需要生成 resources.pri，对应的 MSBuild 任务
    （Microsoft.Build.Packaging.Pri.Tasks）只随 Visual Studio 的
    “Windows 应用开发 / 通用 Windows 平台” 工作负载安装，
    .NET SDK 自带的 MSBuild 里没有，会报 MSB4062。
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\GameGallery\GameGallery.csproj'

function Find-MSBuild {
    $candidates = New-Object System.Collections.Generic.List[string]

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        try {
            $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null
            foreach ($f in @($found)) { if ($f) { $candidates.Add($f.Trim()) } }
        } catch { }
    }

    foreach ($p in @(
            'D:\Visual Studio\MSBuild\Current\Bin\MSBuild.exe',
            'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe',
            'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe',
            'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
            'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
            'C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe',
            'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
        )) {
        $candidates.Add($p)
    }

    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) { return $c }
    }

    return $null
}

$msbuild = Find-MSBuild
if (-not $msbuild) {
    Write-Host "找不到 MSBuild.exe。" -ForegroundColor Red
    Write-Host "请安装 Visual Studio（勾选“使用 C++ 的桌面开发”或“Windows 应用开发”工作负载），"
    Write-Host "或用 -p:MSBuildExtensionsPath 指定包含 PRI 任务的 MSBuild。"
    exit 1
}

Write-Host "使用 MSBuild: $msbuild"
Write-Host "构建配置:     $Configuration"
Write-Host ""

& $msbuild $project -p:Configuration=$Configuration -restore -v:m -nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "构建失败。" -ForegroundColor Red
    Write-Host "如果错误是 MSB4062（找不到 Microsoft.Build.Packaging.Pri.Tasks），"
    Write-Host "说明当前 MSBuild 缺少 WinUI 需要的 PRI 生成任务，请在 Visual Studio 安装器里"
    Write-Host "补上“通用 Windows 平台开发”组件。"
    exit $LASTEXITCODE
}

$out = Join-Path $root "src\GameGallery\bin\$Configuration\net8.0-windows10.0.19041.0\win-x64\GameGallery.exe"
Write-Host ""
Write-Host "构建成功：" -ForegroundColor Green
Write-Host "  $out"
