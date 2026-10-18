#define MyAppName "NetworkMonitor"
#define MyAppExeName "NetworkMonitor.exe"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
AppId={{8E2F6D41-3C7A-4B19-9F52-6A0D4C1B7E88}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=NetworkMonitor
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 注册/更新 LocalSystem 后台服务需要管理员权限（服务本身仍监听回环地址）。
PrivilegesRequired=admin
OutputDir=Output
OutputBaseFilename=NetworkMonitor-Setup-{#MyAppVersion}
SetupIconFile=..\src\NetworkMonitor.WinUI\Assets\networkmonitor.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
AppMutex=Local\NetworkMonitor.WinUI.Instance.v1
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 升级时先停旧服务（见 [Code]），装完重新注册并发起。
Filename: "{app}\NetworkMonitor.Service.exe"; Parameters: "--install"; StatusMsg: "正在注册后台统计服务…"; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\NetworkMonitor.Service.exe"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveNetworkMonitorService"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    // sc stop needs the elevated token this installer now has.
    Exec('sc.exe', 'stop NetworkMonitorService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(3000);
  end;
end;
