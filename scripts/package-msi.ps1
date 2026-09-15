<#
    用 WiX 构建真正的 MSI 安装包。

    产物：dist\GameGallery-<版本>.msi

    要点：
      * 内容是「自包含 + 常规多文件」的发布目录（不是单文件）。
        MSI 本来就会把几百个文件收进 CAB 并逐个安装，
        用单文件反而会让程序每次启动都先把自己解压到临时目录。
      * 目录清单用 WiX 自带的 heat.exe 自动采集，新增依赖不用改脚本。
      * 做成 per-user：装到 %LOCALAPPDATA%\Programs\GameGallery，不需要管理员权限。

    依赖 WiX 3 的免安装二进制（candle/light/heat）。
    没有的话脚本会给出下载地址；解压到 build\tools\wix 即可，不往系统里装任何东西。
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
$wixDir = Join-Path $buildDir 'tools\wix'
$stageDir = Join-Path $buildDir 'msi'
$publish = if ($PublishDir) { $PublishDir } else { Join-Path $buildDir 'publish-msi' }

function Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Fail([string]$text) { Write-Host $text -ForegroundColor Red; exit 1 }

# WinUI 3 必须用 Visual Studio 的 MSBuild（resources.pri 的生成任务只随 VS 的 UWP 组件提供）。
# 先问 vswhere，问不到再退回到常见安装路径——CI 机器上 VS 的版本/位置和本机不一样。
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

# ---------------------------------------------------------------- WiX 检查
$candle = Join-Path $wixDir 'candle.exe'
$light = Join-Path $wixDir 'light.exe'
$heat = Join-Path $wixDir 'heat.exe'
foreach ($tool in @($candle, $light, $heat)) {
    if (-not (Test-Path -LiteralPath $tool)) {
        Fail @"
找不到 WiX 工具（$wixDir）。

请下载免安装版并解压到该目录（不需要安装任何东西）：
  https://github.com/wixtoolset/wix3/releases/download/wix3112rtm/wix311-binaries.zip

  或用命令行：
  Invoke-WebRequest 'https://github.com/wixtoolset/wix3/releases/download/wix3112rtm/wix311-binaries.zip' -OutFile wix.zip
  Expand-Archive wix.zip -DestinationPath '$wixDir'
"@
    }
}

# ---------------------------------------------------------------- 发布（自包含、多文件）
if (-not $SkipBuild -or -not (Test-Path -LiteralPath (Join-Path $publish 'GameGallery.exe'))) {
    Step '发布自包含多文件版本'

    $msbuild = Find-MSBuild
    if (-not $msbuild) { Fail '找不到 MSBuild.exe（vswhere 和常见安装路径都试过了）。' }
    Write-Host "   MSBuild: $msbuild"

    Get-Process -Name 'GameGallery' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

    # 注意：这里刻意不加 PublishSingleFile —— MSI 装的就是普通的多文件目录
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

# ---------------------------------------------------------------- 准备 WiX 输入
Step '准备 WiX 输入'

Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $publish 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $root 'installer\GameGallery.wxs') -Destination $stageDir -Force
Copy-Item -LiteralPath (Join-Path $root 'installer\GameGallery.zh-CN.wxl') -Destination $stageDir -Force
Copy-Item -LiteralPath (Join-Path $root 'installer\GameGallery.ico') -Destination $stageDir -Force

# 许可页用的 RTF：中文全部写成 \uNNNN? 转义，避免 RTF 编码问题
$licenseText = @"
游戏截图图库 $Version

本软件用于在本地浏览你自己的游戏截图（原神 / 崩坏：星穹铁道 / 绝区零 / 崩坏3）。

- 本软件按“现状”提供，不附带任何形式的明示或暗示担保。
- 本软件只读取你本机的截图文件；删除操作只会把文件移到回收站，可从回收站还原。
- 游戏名称与图标的相关权利归米哈游所有；标签页图标直接读取你本机 HoYoPlay 已下载的图标文件。
- 卸载时不会删除你的缩略图缓存、收藏与设置（位于 %LOCALAPPDATA%\GameGallery），需要时可自行清理。
"@

function ConvertTo-Rtf([string]$text) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('{\rtf1\ansi\deff0{\fonttbl{\f0\fnil\fcharset134 Microsoft YaHei;}}')
    [void]$sb.Append("`r`n")
    [void]$sb.Append('\viewkind4\uc1\pard\f0\fs18 ')
    foreach ($line in ($text -split "`r?`n")) {
        foreach ($ch in $line.ToCharArray()) {
            $code = [int]$ch
            if ($code -lt 128) {
                if ($ch -eq '\' -or $ch -eq '{' -or $ch -eq '}') { [void]$sb.Append('\'); [void]$sb.Append($ch) }
                else { [void]$sb.Append($ch) }
            } elseif ($code -le 32767) {
                [void]$sb.Append('\u' + $code + '?')
            } else {
                [void]$sb.Append('\u' + ($code - 65536) + '?')
            }
        }
        [void]$sb.Append('\par')
        [void]$sb.Append("`r`n")
    }
    [void]$sb.Append('}')
    return $sb.ToString()
}

Set-Content -LiteralPath (Join-Path $stageDir 'License.rtf') -Value (ConvertTo-Rtf $licenseText) -Encoding Ascii
Write-Host '   许可文件已生成'

# ---------------------------------------------------------------- heat 采集文件清单
Step '采集发布目录里的文件（heat.exe）'

# -gg/-g1：按 keypath 生成稳定的组件 GUID，重新打包时不会乱变，升级才正常
# -sreg：不写注册表，直接用文件本身当 keypath
& $heat dir $publish -cg AppFiles -dr INSTALLFOLDER -gg -g1 -sfrag -srd -sreg `
    -var var.PublishDir -o (Join-Path $stageDir 'AppFiles.wxs') -nologo
if ($LASTEXITCODE -ne 0) { Fail 'heat 采集失败。' }
Write-Host '   已生成 AppFiles.wxs'

# ---------------------------------------------------------------- 编译
Step '编译 MSI（candle + light）'

New-Item -ItemType Directory -Force -Path $dist | Out-Null
$msiPath = Join-Path $dist "GameGallery-$Version.msi"
Remove-Item $msiPath -Force -ErrorAction SilentlyContinue

Push-Location $stageDir
try {
    & $candle 'GameGallery.wxs' 'AppFiles.wxs' -o 'out\' -nologo -arch x64 "-dPublishDir=$publish"
    if ($LASTEXITCODE -ne 0) { Fail 'candle 编译失败。' }

    # -sval：跳过 ICE 校验（ICE 需要连上 Windows Installer 服务，受限构建环境里会失败）
    $lightArgs = @('out\GameGallery.wixobj', 'out\AppFiles.wixobj', '-o', $msiPath, '-nologo', '-sval',
        '-ext', (Join-Path $wixDir 'WixUIExtension.dll'),
        '-ext', (Join-Path $wixDir 'WixUtilExtension.dll'))

    $ownWxl = Join-Path $stageDir 'GameGallery.zh-CN.wxl'
    if (Test-Path -LiteralPath $ownWxl) {
        $lightArgs += @('-cultures:zh-CN', '-loc', $ownWxl)
    }

    & $light @lightArgs
    if ($LASTEXITCODE -ne 0) { Fail 'light 链接失败。' }
} finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath $msiPath)) { Fail 'MSI 生成失败。' }

Step '产物'
Write-Host ('   ' + [System.IO.Path]::GetFileName($msiPath) + '   ' + [math]::Round((Get-Item -LiteralPath $msiPath).Length / 1MB, 1) + ' MB')
