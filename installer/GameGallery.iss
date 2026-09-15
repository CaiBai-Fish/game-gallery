; 游戏截图图库 —— Inno Setup 6 安装程序脚本
;
; 构建：scripts\package-setup.ps1（CI 里由 .github/workflows/release.yml 调用）
;
; 设计要点（与 docs/UPDATE-LOGIC.md 一致）：
;   * per-user 安装、不需要管理员（PrivilegesRequired=lowest + {localappdata}\Programs\...），
;     程序目录对当前用户可写，自动更新才能覆盖它
;   * 安装目录固定、不带版本号：自动更新覆盖的就是这个目录，升级也不会堆出多个版本目录
;   * 不自己 taskkill，交给 CloseApplications 让 Inno 提示用户关闭程序
;   * 卸载默认保留用户数据（%LOCALAPPDATA%\GameGallery），只删程序本体
;   * 简体中文语言文件随仓库携带（ChineseSimplified.isl），缺文件时自动退回英文，不让构建挂掉
;   * 不弹"选择安装语言"对话框：按系统界面语言自动匹配，匹配不到才问，重装沿用上次选择

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\build\publish-msi"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef ChineseIsl
  #define ChineseIsl SourcePath + "ChineseSimplified.isl"
#endif

[Setup]
AppId=GameGallery
AppName=游戏截图图库
AppVersion={#AppVersion}
AppVerName=游戏截图图库 {#AppVersion}
AppPublisher=CaiBai-Fish
AppPublisherURL=https://github.com/CaiBai-Fish/game-gallery
AppSupportURL=https://github.com/CaiBai-Fish/game-gallery/issues
AppUpdatesURL=https://github.com/CaiBai-Fish/game-gallery/releases
DefaultDirName={localappdata}\Programs\GameGallery
DefaultGroupName=游戏截图图库
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=GameGallery-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupMutex=GameGallerySetup
AppMutex=Local\GameGallery.SingleInstance.v1
CloseApplications=yes
RestartApplications=no
UninstallDisplayName=游戏截图图库
UninstallDisplayIcon={app}\GameGallery.exe
ShowLanguageDialog=auto
LanguageDetectionMethod=uilanguage
UsePreviousLanguage=yes
VersionInfoVersion={#AppVersion}
VersionInfoProductName=游戏截图图库
VersionInfoDescription=游戏截图图库 安装程序
VersionInfoCompany=CaiBai-Fish
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
#if FileExists(ChineseIsl)
Name: "zhcn"; MessagesFile: "{#ChineseIsl}"
#endif

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\游戏截图图库"; Filename: "{app}\GameGallery.exe"
Name: "{group}\卸载 游戏截图图库"; Filename: "{uninstallexe}"
Name: "{userdesktop}\游戏截图图库"; Filename: "{app}\GameGallery.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GameGallery.exe"; Description: "启动 游戏截图图库"; Flags: nowait postinstall skipifsilent

[Code]
{ 卸载时问一次是否连用户数据一起删。默认「否」= 保留。
  用户数据在 %LOCALAPPDATA%\GameGallery，截图文件本身始终不受影响。
  这里刻意用 MsgBox 而不是自建 TSetupForm：TSetupForm 的控件不暴露 UI Automation，
  而且控件引用留到 CurUninstallStepChanged 里再读会变成悬空指针。 }
function InitializeUninstall(): Boolean;
var
  Response: Integer;
begin
  Result := True;

  { 静默卸载（/SILENT 或 /VERYSILENT）不问，默认保留用户数据，
    否则脚本化卸载会一直卡在这个对话框上。 }
  if UninstallSilent then
  begin
    Exit;
  end;

  Response := MsgBox(
    '是否同时删除用户数据（缩略图缓存、收藏、设置）？' + #13#10 + #13#10 +
    '位置：' + ExpandConstant('{localappdata}\GameGallery') + #13#10 +
    '选择「否」将保留这些数据；你的截图文件始终不受影响。',
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2);

  if Response = IDYES then
  begin
    DelTree(ExpandConstant('{localappdata}\GameGallery'), True, True, True);
  end;
end;
