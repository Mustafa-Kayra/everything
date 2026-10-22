; Everything Arama kurulum betiği (Inno Setup 6).
; Derleme: ISCC.exe /DAppVersion=0.3.0 /DSourceExe=..\artifacts\publish\EverythingArama.exe installer\EverythingArama.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\artifacts\publish\EverythingArama.exe"
#endif
#define AppName "Everything Arama"
#define AppExe "EverythingArama.exe"
#define TaskName "Everything Arama"

[Setup]
AppId={{8F3A6C2E-4B71-4E0B-9C55-6A1E2D7B9F10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Mustafa Kayra
AppPublisherURL=https://github.com/Mustafa-Kayra/everything
AppSupportURL=https://github.com/Mustafa-Kayra/everything/issues
AppUpdatesURL=https://github.com/Mustafa-Kayra/everything/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts
OutputBaseFilename=Everything-Setup
SetupIconFile=..\src\Everything.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "startup"; Description: "Windows açılınca yönetici olarak tepside başlat (hızlı NTFS modu, önerilir)"; GroupDescription: "Başlangıç:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "{#AppExe}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Kurulum yönetici olarak çalıştığından görev UAC sorulmadan en yüksek yetkiyle oluşturulur.
Filename: "{app}\{#AppExe}"; Parameters: "--register-startup"; Tasks: startup; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Parameters: "--unregister-startup"; Tasks: not startup; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "StopApp"
Filename: "{app}\{#AppExe}"; Parameters: "--unregister-startup"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveStartup"

[UninstallDelete]
; İndeks önbelleği yeniden oluşturulabilir; ayarlar da onunla birlikte silinir.
Type: filesandordirs; Name: "{localappdata}\EverythingArama"

[Code]
// Uygulama içinden yapılan sessiz güncellemeden sonra uygulamayı yeniden başlat:
// başlangıç görevi varsa onunla (yönetici, UAC'siz), yoksa normal kullanıcı olarak.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and WizardSilent then
  begin
    if not (Exec(ExpandConstant('{sys}\schtasks.exe'), '/Run /TN "{#TaskName}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0)) then
      ExecAsOriginalUser(ExpandConstant('{app}\{#AppExe}'), '--restarted', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
end;
