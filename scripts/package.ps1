<#
    打包免安装版（zip 解压即用）。

    产物：dist\GameGallery-<版本>-portable.zip

    为什么是 zip 而不是单文件 exe（实测结论，别改回去）：
      自包含 + PublishSingleFile 这套组合在本项目上不可靠——本地同样脚本编出来的能跑，
      CI 编出来的却在启动时崩：COMException 0x80040111「ClassFactory 无法供应请求的类」，
      崩在 Microsoft.UI.Xaml.Application.Start（WinRT 激活找不到类）。
      加 IncludeAllContentForSelfExtract=true 能让本地那份恢复，但 CI 那份依旧崩，
      属于"换个环境就翻车"的形态，不能拿来发布。
      zip 用的就是 MSI / Inno 安装程序那份多文件自包含载荷，已反复验证可运行；
      解压后双击 GameGallery.exe 即用，仍然免安装、不写注册表。

    发布目录与安装程序共用（build\publish-msi）：CI 里先跑 package-msi.ps1，
    这里直接复用，省掉一次 publish。
#>
param(
    [string]$Version = '1.0.3',
    [string]$PublishDir = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\GameGallery\GameGallery.csproj'
$dist = Join-Path $root 'dist'
$buildDir = Join-Path $root 'build'
$publish = if ($PublishDir) { $PublishDir } else { Join-Path $buildDir 'publish-msi' }

function Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Fail([string]$text) { Write-Host $text -ForegroundColor Red; exit 1 }

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

# ---------------------------------------------------------------- 发布目录
if (Test-Path -LiteralPath (Join-Path $publish 'GameGallery.exe')) {
    Step '复用已有发布目录'
    Write-Host "   $publish"
} else {
    if ($SkipBuild) { Fail "要求跳过构建，但发布目录不存在：$publish" }

    Step '发布自包含多文件版本'
    $msbuild = Find-MSBuild
    if (-not $msbuild) { Fail '找不到 MSBuild.exe（vswhere 和常见安装路径都试过了）。' }
    Write-Host "   MSBuild: $msbuild"

    Get-Process | Where-Object { $_.Path -like '*GameGallery*' } | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

    # -restore：干净检出（CI）里没有 obj\project.assets.json，Publish 会直接失败
    & $msbuild $project -t:Publish -restore -p:Configuration=Release `
        -p:EnableMsixTooling=true -p:SelfContained=true -p:PublishSingleFile=false `
        -p:WindowsAppSDKSelfContained=true -p:RuntimeIdentifier=win-x64 `
        -p:PublishDir="$publish\" -v:m -nologo
    if ($LASTEXITCODE -ne 0) { Fail '发布失败。' }
}

$mainExe = Join-Path $publish 'GameGallery.exe'
if (-not (Test-Path -LiteralPath $mainExe)) { Fail "发布目录里没有 GameGallery.exe：$publish" }

# ---------------------------------------------------------------- 打包
Step '压缩成 zip'

Add-Type -AssemblyName System.IO.Compression.FileSystem

New-Item -ItemType Directory -Force -Path $dist | Out-Null
$zip = Join-Path $dist "GameGallery-$Version-portable.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue

# includeBaseDirectory=false：解压出来直接就是 GameGallery.exe，不用再进一层目录
# 注意 ZipFile.CreateFromDirectory 在 Windows 上把条目名写成 'Assets\x'（反斜杠），
# 多数解压工具认，但按 '/' 匹配的校验会误判，所以下面两种分隔符都接受。
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $publish, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

# ---------------------------------------------------------------- 校验
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName })
    if ($names -notcontains 'GameGallery.exe') { Fail 'zip 根目录里没有 GameGallery.exe' }

    $assets = @($names | Where-Object { $_ -match '^Assets[\\/]' })
    if ($assets.Count -eq 0) { Fail 'zip 里没有 Assets（窗口图标会退化成 EXE 内嵌图标）' }

    Write-Host ("   条目数：{0}，Assets 条目：{1}" -f $names.Count, $assets.Count)
} finally {
    $archive.Dispose()
}

Step '产物'
Write-Host ('   ' + [System.IO.Path]::GetFileName($zip) + '   ' + [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1) + ' MB')
Write-Host ''
Write-Host '   安装程序请运行 scripts\package-setup.ps1（EXE）或 scripts\package-msi.ps1（MSI）。'
