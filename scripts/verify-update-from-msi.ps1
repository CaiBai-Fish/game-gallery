# 以「安装版（MSI）」为起点验证更新流程：
#   静默装旧版 → 运行 → 设置里检查更新 → 下载并安装 → 独立脚本静默安装 → 自动启动新版本。
#
#   .\scripts\verify-update-from-msi.ps1 -FromVersion 1.0.1 -ToVersion 1.0.3
#
# 与 verify-update-e2e.ps1 的区别：那个以免安装版（zip）为起点，这个以 MSI 安装版为起点。
# 需要联网（下 Release），耗时数分钟；跑完会卸载并清理。
param(
    [string]$FromVersion = '1.0.1',
    [string]$ToVersion = '1.0.3',
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$repo = 'CaiBai-Fish/game-gallery'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\GameGallery'
$installExe = Join-Path $installDir 'GameGallery.exe'
$downloadDir = Join-Path $env:TEMP "gg-from-msi-$FromVersion"
$msi = Join-Path $downloadDir "GameGallery-$FromVersion.msi"
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GameGallery_is1'

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$pass = 0
$fails = New-Object System.Collections.Generic.List[string]

function Check([string]$label, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  PASS  {0}  {1}" -f $label, $detail) }
    else { $fails.Add($label); Write-Host ("  FAIL  {0}  {1}" -f $label, $detail) }
}

function Get-AppWindows([int]$processId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $processId)
    @($AE::RootElement.FindAll($TS::Children, $cond))
}

function Find-Button([int]$processId, [string]$name, [switch]$Prefix) {
    $byType = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)
    $all = @()
    foreach ($w in (Get-AppWindows $processId)) { $all += @($w.FindAll($TS::Descendants, $byType)) }
    $all += @($AE::RootElement.FindAll($TS::Descendants, $byType))   # 设置面板在 Flyout 里
    if ($Prefix) { return $all | Where-Object { $_.Current.Name -like "$name*" } | Select-Object -First 1 }
    return $all | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
}

function Get-StatusText([int]$processId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'UpdateStatusText')
    foreach ($w in (Get-AppWindows $processId)) {
        $el = $w.FindFirst($TS::Descendants, $cond)
        if ($el) { return $el.Current.Name }
    }
    return $null
}

function Stop-Everything {
    Get-Process | Where-Object { $_.Path -like '*GameGallery*' } | Stop-Process -Force
}

Write-Host ("=== 准备：下载并静默安装 {0} 的 MSI ===" -f $FromVersion)
Stop-Everything
Start-Sleep -Seconds 2
New-Item -ItemType Directory -Force -Path $downloadDir | Out-Null
if (-not (Test-Path $msi)) {
    Invoke-WebRequest -Uri "https://github.com/$repo/releases/download/$FromVersion/GameGallery-$FromVersion.msi" -OutFile $msi -UseBasicParsing
}
Write-Host ("  {0} MB" -f [math]::Round((Get-Item $msi).Length / 1MB, 1))

$installed = Start-Process msiexec.exe -ArgumentList '/i', "`"$msi`"", '/qn', '/norestart' -PassThru -Wait
Check "静默安装 $FromVersion 成功" ($installed.ExitCode -eq 0) ("exit=" + $installed.ExitCode)
Check "程序落在 $installDir" (Test-Path $installExe) $installExe

$beforeVersion = if (Test-Path $installExe) { [System.Diagnostics.FileVersionInfo]::GetVersionInfo($installExe).ProductVersion } else { '' }
Write-Host ("  当前版本：{0}" -f $beforeVersion)
Check "安装版本来对了（$FromVersion）" ($beforeVersion -like "$FromVersion*") $beforeVersion
Check "MSI 装出来的没有 Inno 卸载键（更新程序据此判断原形态）" (-not (Test-Path $uninstallKey))

Write-Host "`n=== 运行旧版并检查更新 ==="
$app = Start-Process -FilePath $installExe -PassThru
for ($i = 0; $i -lt 40 -and -not $app.MainWindowTitle; $i++) { Start-Sleep -Seconds 1; $app.Refresh() }
Check "旧版启动成功" ($null -ne $app.MainWindowTitle) $app.MainWindowTitle

$settings = Find-Button $app.Id '设置'
Check "找到设置按钮" ($null -ne $settings)
$settings.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2

$check = Find-Button $app.Id '检查更新'
Check "找到检查更新按钮" ($null -ne $check)
$check.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

$status = $null
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 1
    $status = Get-StatusText $app.Id
    if ($status -and $status -notlike '*正在检查*') { break }
}
Write-Host ("  状态行：{0}" -f $status)
Check "报告发现新版本 $ToVersion" ($status -like "*$ToVersion*") $status

Write-Host "`n=== 下载并安装 ==="
$install = Find-Button $app.Id '下载并安装' -Prefix
Check "出现「下载并安装」按钮" ($null -ne $install) $(if ($install) { $install.Current.Name })
$install.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

$sawProgress = $false
for ($i = 0; $i -lt 200; $i++) {
    Start-Sleep -Seconds 2
    $app.Refresh()
    if ($app.HasExited) { break }
    if ((Get-StatusText $app.Id) -like '*正在下载*') { $sawProgress = $true }
}
Check "下载过程有进度反馈" $sawProgress
Check "应用交接给安装脚本后自行退出" $app.HasExited

Write-Host "`n=== 等待静默安装并自动启动新版本 ==="
$newVersion = $null
$newProcess = $null
for ($i = 0; $i -lt 200; $i++) {
    Start-Sleep -Seconds 2
    if (Test-Path $installExe) { $newVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($installExe).ProductVersion }
    if ($newVersion -like "$ToVersion*") {
        $newProcess = Get-Process | Where-Object { $_.Path -eq $installExe -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if ($newProcess) { break }
    }
}
Check "版本已变成 $ToVersion" ($newVersion -like "$ToVersion*") $newVersion
Check "新版本被自动启动" ($null -ne $newProcess) $(if ($newProcess) { "pid=$($newProcess.Id)" })

Write-Host "`n=== 安装记录 ==="
if (Test-Path $uninstallKey) {
    $props = Get-ItemProperty $uninstallKey
    Write-Host ("  DisplayVersion  = {0}" -f $props.DisplayVersion)
    Write-Host ("  InstallLocation = {0}" -f $props.InstallLocation)
    Check "Inno 卸载键的 InstallLocation 指向安装目录" ($props.InstallLocation -like "$installDir*") $props.InstallLocation
    Check "Inno 记录的版本是 $ToVersion" ($props.DisplayVersion -eq $ToVersion) $props.DisplayVersion
} else {
    Check "出现了 Inno 卸载键" $false '没有 GameGallery_is1'
}

Write-Host "`n=== 清理 ==="
Stop-Everything
Start-Sleep -Seconds 2
$unins = Join-Path $installDir 'unins000.exe'
if (Test-Path $unins) {
    $u = Start-Process -FilePath $unins -ArgumentList '/SILENT', '/NORESTART' -PassThru -Wait
    Write-Host ("  卸载程序退出码 = {0}" -f $u.ExitCode)
    Check "静默卸载顺利完成" ($u.ExitCode -eq 0)
}
if (Test-Path $uninstallKey) { Start-Process msiexec.exe -ArgumentList '/x', "`"$msi`"", '/qn', '/norestart' -PassThru -Wait | Out-Null }
Start-Sleep -Seconds 2
Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $downloadDir -Recurse -Force -ErrorAction SilentlyContinue
Check "程序目录已清理" (-not (Test-Path $installExe))

Write-Host ''
if ($fails.Count -eq 0) { Write-Host ("UPDATE FROM MSI OK（{0} 项）" -f $pass) }
else { foreach ($f in $fails) { Write-Host ("FAIL: {0}" -f $f) }; exit 1 }
