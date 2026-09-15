# 更新流程端到端验证：拿旧版本的便携版跑起来 → 设置里检查更新 → 下载并安装 → 由独立脚本静默安装 → 自动启动新版本。
#
#   .\scripts\verify-update-e2e.ps1 -FromVersion 1.0.0 -ToVersion 1.0.1
#
# 需要联网（下 Release），耗时数分钟。会往临时目录装一份，跑完自动卸载并清理。
param(
    [string]$FromVersion = '1.0.0',
    [string]$ToVersion = '1.0.1',
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$repo = 'CaiBai-Fish/game-gallery'
$root = Split-Path -Parent $PSScriptRoot
$sandbox = Join-Path $env:TEMP "gg-update-e2e-$FromVersion"
$zip = Join-Path $sandbox "GameGallery-$FromVersion-portable.zip"
$portable = Join-Path $sandbox 'GameGallery.exe'
$installedExe = Join-Path $sandbox 'GameGallery.exe'

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
    foreach ($w in (Get-AppWindows $processId)) {
        $all = @($w.FindAll($TS::Descendants, $byType))
        $hit = if ($Prefix) { $all | Where-Object { $_.Current.Name -like "$name*" } | Select-Object -First 1 }
               else { $all | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1 }
        if ($hit) { return $hit }
    }
    # 设置面板在 Flyout 里，未必挂在窗口的自动化子树下
    $all = @($AE::RootElement.FindAll($TS::Descendants, $byType))
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

function Get-GameGalleryProcesses {
    @(Get-Process -Name GameGallery -ErrorAction SilentlyContinue)
}

Write-Host ("=== 准备：把 {0} 的便携版下到 {1} ===" -f $FromVersion, $sandbox)
Get-Process -Name GameGallery -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path $sandbox) { Remove-Item $sandbox -Recurse -Force }
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

$url = "https://github.com/$repo/releases/download/$FromVersion/GameGallery-$FromVersion-portable.zip"
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
Expand-Archive -LiteralPath $zip -DestinationPath $sandbox -Force
Write-Host ("  已下载并解压 {0} MB" -f [math]::Round((Get-Item $zip).Length / 1MB, 1))

Write-Host "`n=== 1. 启动便携版 ==="
$app = Start-Process -FilePath $portable -PassThru
Start-Sleep -Seconds 15
$app.Refresh()
Check "便携版启动成功" ((-not $app.HasExited) -and $app.MainWindowTitle) ("pid=" + $app.Id)

Write-Host "`n=== 2. 设置 → 检查更新 ==="
$settings = Find-Button $app.Id '设置'
Check "找到设置按钮" ($null -ne $settings)
$settings.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2

$checkButton = Find-Button $app.Id '检查更新'
Check "找到检查更新按钮" ($null -ne $checkButton)
$checkButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

$status = $null
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Seconds 1
    $status = Get-StatusText $app.Id
    if ($status -and $status -notlike '*正在检查*') { break }
}
Write-Host ("  状态行：{0}" -f $status)
Check "检查更新报告发现新版本 $ToVersion" ($status -like "*$ToVersion*") $status

Write-Host "`n=== 3. 下载并安装 ==="
$installButton = Find-Button $app.Id '下载并安装' -Prefix
Check "出现「下载并安装」按钮" ($null -ne $installButton) $(if ($installButton) { $installButton.Current.Name })
$installButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

$sawProgress = $false
for ($i = 0; $i -lt 180; $i++) {
    Start-Sleep -Seconds 2
    $app.Refresh()
    if ($app.HasExited) { break }
    $text = Get-StatusText $app.Id
    if ($text -like '*正在下载*') { $sawProgress = $true }
}
Check "下载过程中有进度反馈" $sawProgress
Check "应用在交接给安装脚本后自行退出" $app.HasExited ("exit=" + $app.ExitCode)

Write-Host "`n=== 4. 等待独立脚本静默安装并启动新版本 ==="
$newProcess = $null
for ($i = 0; $i -lt 180; $i++) {
    Start-Sleep -Seconds 2
    $candidate = Get-GameGalleryProcesses | Where-Object { $_.Path -like "$sandbox*" } | Select-Object -First 1
    if ($candidate -and $candidate.MainWindowHandle -ne 0) { $newProcess = $candidate; break }
}
Check "新版本被自动启动" ($null -ne $newProcess) $(if ($newProcess) { "pid=$($newProcess.Id) path=$($newProcess.Path)" })

$installedVersion = $null
if (Test-Path $installedExe) {
    $installedVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($installedExe).ProductVersion
}
Check "安装到了原目录（免安装形态保持免安装）" (Test-Path $installedExe) $installedExe
Check "版本已变成 $ToVersion" ($installedVersion -like "$ToVersion*") $installedVersion
if (Test-Path $installedExe) {
    $exeVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($installedExe).ProductVersion
    Check "新版本确实启动的是这个 exe" ($null -ne $newProcess -and $newProcess.Path -eq $installedExe) ("$($newProcess.Path)")
}

Write-Host "`n=== 5. 安装记录 ==="
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GameGallery_is1'
if (Test-Path $key) {
    $props = Get-ItemProperty $key
    Write-Host ("  InstallLocation = {0}" -f $props.InstallLocation)
    Check "卸载注册表的 InstallLocation 指向安装目录" ($props.InstallLocation -like "$sandbox*") $props.InstallLocation
} else {
    Check "卸载注册表项存在" $false '没有 GameGallery_is1 键'
}

Write-Host "`n=== 6. 清理 ==="
Get-Process -Name GameGallery -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$uninst = Join-Path $sandbox 'unins000.exe'
if (Test-Path $uninst) {
    $u = Start-Process -FilePath $uninst -ArgumentList '/SILENT', '/NORESTART' -PassThru -Wait
    Write-Host ("  静默卸载退出码 = {0}" -f $u.ExitCode)
    Check "静默卸载没有卡在询问框上" ($u.ExitCode -eq 0)
    Check "程序目录已清理" (-not (Test-Path (Join-Path $sandbox 'GameGallery.exe')))
}
Start-Sleep -Seconds 2
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ("  沙箱已删除：{0}" -f (-not (Test-Path $sandbox)))

Write-Host ''
if ($fails.Count -eq 0) { Write-Host ("UPDATE E2E OK（{0} 项）" -f $pass) }
else { foreach ($f in $fails) { Write-Host ("FAIL: {0}" -f $f) } }
if ($fails.Count -gt 0) { exit 1 }
