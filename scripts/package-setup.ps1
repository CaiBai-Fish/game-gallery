<#
    用 Inno Setup 6 构建标准 EXE 安装程序。

    产物：dist\GameGallery-<版本>-setup.exe

    为什么要有它（见 docs/UPDATE-LOGIC.md）：
      * 自动更新优先"下载官方安装程序并运行它"，而不是在应用内解压覆盖程序目录；
        安装程序是官方产物，自带卸载入口与注册表记录，装完的程序在「设置 → 应用」里能正常管理。
      * per-user 安装 + {localappdata}\Programs\<App>：不弹 UAC，且程序目录对当前用户可写，
        这样"下载新版本覆盖程序目录"的自动更新才成立（装到 Program Files 就只能退化成手动更新）。
      * 安装目录固定、不带版本号，升级不会堆出多个版本目录。

    依赖 Inno Setup 6 的 ISCC.exe：本机 per-user 安装时在 %LOCALAPPDATA%\Programs\Inno Setup 6，
    CI 上用 `choco install innosetup -y`（装在 Program Files (x86)）。两个位置都会找。
#>
param(
    [string]$Version = '1.0.2',
    [string]$PublishDir = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\GameGallery\GameGallery.csproj'
$dist = Join-Path $root 'dist'
$buildDir = Join-Path $root 'build'
$publish = if ($PublishDir) { $PublishDir } else { Join-Path $buildDir 'publish-msi' }
$iss = Join-Path $root 'installer\GameGallery.iss'
$isl = Join-Path $root 'installer\ChineseSimplified.isl'

function Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Fail([string]$text) { Write-Host $text -ForegroundColor Red; exit 1 }

function Find-ISCC {
    foreach ($p in @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        )) {
        if (Test-Path -LiteralPath $p) { return $p }
    }

    return $null
}

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

# ---------------------------------------------------------------- 检查工具
$iscc = Find-ISCC
if (-not $iscc) {
    Fail @'
找不到 Inno Setup 6 的 ISCC.exe（找过 Program Files (x86)、Program Files、%LOCALAPPDATA%\Programs）。

  CI：choco install innosetup -y
  本机：https://jrsoftware.org/isdl.php （per-user 安装即可，不需要管理员）
'@
}

if (-not (Test-Path -LiteralPath $iss)) { Fail "找不到脚本：$iss" }

# ---------------------------------------------------------------- 发布目录
if ($SkipBuild -and (Test-Path -LiteralPath (Join-Path $publish 'GameGallery.exe'))) {
    Step '复用已有发布目录'
    Write-Host "   $publish"
} else {
    Step '发布自包含多文件版本'

    $msbuild = Find-MSBuild
    if (-not $msbuild) { Fail '找不到 MSBuild.exe（vswhere 和常见安装路径都试过了）。' }
    Write-Host "   MSBuild: $msbuild"

    Get-Process -Name 'GameGallery' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

    # -restore：干净检出（CI）里没有 obj\project.assets.json，Publish 会直接失败
    & $msbuild $project -t:Publish -restore -p:Configuration=Release `
        -p:EnableMsixTooling=true -p:PublishSingleFile=false -p:SelfContained=true `
        -p:WindowsAppSDKSelfContained=true -p:RuntimeIdentifier=win-x64 `
        -p:PublishDir="$publish\" -v:m -nologo
    if ($LASTEXITCODE -ne 0) { Fail '发布失败。' }
}

$mainExe = Join-Path $publish 'GameGallery.exe'
if (-not (Test-Path -LiteralPath $mainExe)) { Fail "发布目录里没有 GameGallery.exe：$publish" }

$fileCount = (Get-ChildItem $publish -Recurse -File).Count
$totalMb = [math]::Round(((Get-ChildItem $publish -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Write-Host "   发布文件数：$fileCount，合计 $totalMb MB"

# ---------------------------------------------------------------- 编译安装程序
Step '编译 EXE 安装程序（ISCC）'

New-Item -ItemType Directory -Force -Path $dist | Out-Null

$isccArgs = @("/DAppVersion=$Version")
if (Test-Path -LiteralPath $isl) {
    $isccArgs += "/DChineseIsl=$isl"
    Write-Host "   简体中文语言文件：$isl"
} else {
    Write-Host '   没找到简体中文语言文件，安装向导将退回英文'
}

& $iscc @isccArgs $iss
if ($LASTEXITCODE -ne 0) { Fail 'ISCC 编译失败。' }

# ---------------------------------------------------------------- 校验产物
$setup = Join-Path $dist "GameGallery-$Version-setup.exe"
if (-not (Test-Path -LiteralPath $setup)) { Fail "没有生成安装程序：$setup" }

Step '产物'
Write-Host ('   ' + [System.IO.Path]::GetFileName($setup) + '   ' + [math]::Round((Get-Item -LiteralPath $setup).Length / 1MB, 1) + ' MB')
