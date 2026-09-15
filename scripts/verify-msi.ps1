param(
    [Parameter(Mandatory = $true)][string]$MsiPath,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\GameGallery')
)

$ErrorActionPreference = 'Continue'
$script:pass = 0; $script:fail = 0
$AppName = '游戏截图图库'
$StartMenuLnk = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$AppName\$AppName.lnk"
$DesktopLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk"

function Check([string]$label, [bool]$ok, [string]$detail) {
    if ($ok) { $script:pass++; Write-Output ("PASS  " + $label + "   " + $detail) }
    else { $script:fail++; Write-Output ("FAIL  " + $label + "   " + $detail) }
}

# 从 MSI 的 Property 表里读 ProductCode
function Get-MsiProperty([string]$msi, [string]$name) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($msi, 0))
    $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @("SELECT Value FROM Property WHERE Property='$name'"))
    $null = $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    if ($null -eq $rec) { return $null }
    $value = $rec.GetType().InvokeMember('StringData', 'GetProperty', $null, $rec, 1)
    return [string]$value
}

# {XXXXXXXX-...} -> 打包 GUID（"compressed" 形式）：
# 取 GUID 的二进制字节，逐字节交换高低半字节，再输出十六进制。
function Get-PackedGuid([string]$guid) {
    $bytes = ([guid]$guid).ToByteArray()
    $sb = New-Object System.Text.StringBuilder
    foreach ($b in $bytes) {
        $swapped = (($b -band 0x0F) -shl 4) -bor (($b -shr 4) -band 0x0F)
        [void]$sb.Append($swapped.ToString('X2'))
    }
    return $sb.ToString()
}

# 5 = INSTALLSTATE_DEFAULT，即已安装
function Get-ProductState([string]$code) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    return [int]$installer.GetType().InvokeMember('ProductState', 'GetProperty', $null, $installer, @($code))
}

$productCode = Get-MsiProperty $MsiPath 'ProductCode'
$productName = Get-MsiProperty $MsiPath 'ProductName'
$productVersion = Get-MsiProperty $MsiPath 'ProductVersion'
$manufacturer = Get-MsiProperty $MsiPath 'Manufacturer'
Write-Output "===== 0. MSI 元数据 ====="
Write-Output ("  ProductCode  = " + $productCode)
Write-Output ("  ProductName  = " + $productName)
Write-Output ("  Version      = " + $productVersion)
Write-Output ("  Manufacturer = " + $manufacturer)
Write-Output ("  大小         = " + [math]::Round((Get-Item -LiteralPath $MsiPath).Length / 1MB, 1) + " MB")
Check "ProductName 正确" ($productName -eq $AppName) $productName
Check "Version 正确" ($productVersion -eq '1.0.2') $productVersion
Check "ProductCode 有效" ($productCode -match '^\{[0-9A-F-]{36}\}$') $productCode



# ---------------------------------------------------------------- 干净起点
Get-Process -Name 'GameGallery' -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path -LiteralPath $InstallDir) { & msiexec.exe /x $productCode /qn /norestart | Out-Null; Start-Sleep -Seconds 5 }
Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $StartMenuLnk -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $DesktopLnk -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Output ""
Write-Output "===== 1. 静默安装 msiexec /i /qn ====="
$log = Join-Path $env:TEMP 'gg-msi-install.log'
Remove-Item $log -Force -ErrorAction SilentlyContinue
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$installArgs = '/i "' + $MsiPath + '" /qn /norestart /l*v "' + $log + '"'
$p = Start-Process -FilePath 'msiexec.exe' -ArgumentList $installArgs -PassThru -Wait
$sw.Stop()
Write-Output ("  msiexec 退出码 = " + $p.ExitCode + "，耗时 " + [math]::Round($sw.Elapsed.TotalSeconds, 1) + " 秒")
Check "静默安装成功（退出码 0）" ($p.ExitCode -eq 0) ("exit=" + $p.ExitCode + " (见 " + $log + ")")

$exe = Join-Path $InstallDir 'GameGallery.exe'
Check "主程序已安装" (Test-Path -LiteralPath $exe) $exe
Check "README 已安装" (Test-Path -LiteralPath (Join-Path $InstallDir 'README.md')) ""
Check "开始菜单快捷方式已创建" (Test-Path -LiteralPath $StartMenuLnk) $StartMenuLnk
Check "桌面快捷方式已创建" (Test-Path -LiteralPath $DesktopLnk) $DesktopLnk

Write-Output ""
Write-Output "===== 2. 系统里的注册情况（应用和功能会列出它）====="
# per-user 的 MSI 不会写 HKCU\...\Uninstall，而是注册在
# HKCU\Software\Microsoft\Installer\Products\<打包GUID>，「应用和功能」就是读这里。
$packed = Get-PackedGuid $productCode
$userProductKey = "HKCU:\Software\Microsoft\Installer\Products\$packed"
$state = Get-ProductState $productCode
Write-Output ("  打包 GUID    = " + $packed)
Write-Output ("  注册表位置   = " + $userProductKey)
Write-Output ("  ProductState = " + $state + "  (5 = 已安装)")

Check "Windows Installer 认为产品已安装（ProductState=5）" ($state -eq 5) ("state=" + $state)
Check "已注册到当前用户的安装产品列表" (Test-Path -LiteralPath $userProductKey) $userProductKey

$up = Get-ItemProperty -Path $userProductKey -ErrorAction SilentlyContinue
if ($up) {
    Write-Output ("    ProductName = " + $up.ProductName)
    Write-Output ("    Version     = " + $up.Version)
    Check "注册的产品名正确" ($up.ProductName -eq $AppName) $up.ProductName
}

Write-Output ""
Write-Output "===== 3. 运行已安装的程序 ====="
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 16
$running = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
Check "已安装的程序能启动并保持运行" ($null -ne $running -and $running.MainWindowHandle -ne 0) $(if ($running) { "title='" + $running.MainWindowTitle + "' ws=" + [math]::Round($running.WorkingSet64 / 1MB, 1) + "MB" } else { "exited" })
if ($running) { $running | Stop-Process -Force }
Start-Sleep -Seconds 2

Write-Output ""
Write-Output "===== 4. 静默卸载 msiexec /x /qn ====="
$log2 = Join-Path $env:TEMP 'gg-msi-uninstall.log'
$uninstallArgs = '/x ' + $productCode + ' /qn /norestart /l*v "' + $log2 + '"'
$p2 = Start-Process -FilePath 'msiexec.exe' -ArgumentList $uninstallArgs -PassThru -Wait
Write-Output ("  msiexec 退出码 = " + $p2.ExitCode)
Check "静默卸载成功（退出码 0）" ($p2.ExitCode -eq 0) ("exit=" + $p2.ExitCode)
Start-Sleep -Seconds 3

Check "程序文件已删除" (-not (Test-Path -LiteralPath $exe)) ""
Check "开始菜单快捷方式已删除" (-not (Test-Path -LiteralPath $StartMenuLnk)) ""
Check "桌面快捷方式已删除" (-not (Test-Path -LiteralPath $DesktopLnk)) ""
$stateAfter = Get-ProductState $productCode
Check "Windows Installer 认为产品已卸载" ($stateAfter -ne 5) ("state=" + $stateAfter)
Check "注册表里的安装记录已清除" (-not (Test-Path -LiteralPath $userProductKey)) $userProductKey

Write-Output ""
Write-Output ("RESULT: pass=" + $script:pass + " fail=" + $script:fail)
