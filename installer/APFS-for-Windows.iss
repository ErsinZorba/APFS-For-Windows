; APFS for Windows kurulum sihirbazı (Inno Setup 6). Derlemek için: tools\build-installer.ps1
; Kurduğu her şey install.ps1 ile aynıdır: program dosyaları, LocalSystem hizmeti, oturum açılışında tepsi, WinFsp Launcher kaydı.
; WinFsp yoksa (veya 2.1'den eskiyse) içindeki resmi WinFsp kurulum paketini sessizce kurar.

#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif
#define AppName "APFS for Windows"
#define ServiceName "APFSforWindows"
#define SrcDir "..\publish\APFS for Windows"
#define WinFspMsi "winfsp-2.1.25156.msi"

[Setup]
AppId={{8C1B6E2F-4A57-4F0E-9D3B-6A2E5C7F1A90}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Ersin Zorba
AppPublisherURL=https://github.com/ErsinZorba/APFS-for-Windows
AppSupportURL=https://github.com/ErsinZorba/APFS-for-Windows/issues
VersionInfoVersion={#AppVersion}
; Kurulum yeri sabit: hizmet ve "apfs register" Program Files\APFS for Windows yolunu kullanır.
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
; GPL kullanım için kabul şartı koymaz (madde 9): "kabul ediyorum" sayfası yerine kısa bilgi sayfası (dile göre, [Languages]).
; GPL'in tam metni program klasöründeki LICENSE dosyasıdır (build-release.ps1 kopyalar).
OutputDir=..\publish
OutputBaseFilename=APFS-for-Windows-Setup-{#AppVersion}
SetupIconFile=..\src\Apfs.Tray\app.ico
UninstallDisplayIcon={app}\apfs-tray.exe
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Tepsiyi ve hizmeti PrepareToInstall kendisi durdurur.
CloseApplications=no

[Languages]
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"; InfoBeforeFile: "lisans-bilgisi.tr.txt"
Name: "en"; MessagesFile: "compiler:Default.isl"; InfoBeforeFile: "lisans-bilgisi.en.txt"

[CustomMessages]
tr.LaunchApp=APFS for Windows'u aç
en.LaunchApp=Open APFS for Windows
tr.InstallingWinFsp=WinFsp dosya sistemi sürücüsü kuruluyor...
en.InstallingWinFsp=Installing the WinFsp file system driver...
tr.InstallingService=Arka plan hizmeti kuruluyor...
en.InstallingService=Installing the background service...
tr.WinFspFailed=WinFsp kurulamadı (kod %1). Mac diskleri bağlanamaz. WinFsp'i https://winfsp.dev adresinden kurup programı yeniden kurun.
en.WinFspFailed=WinFsp could not be installed (code %1). Mac disks cannot be mounted. Install WinFsp from https://winfsp.dev and run this setup again.
tr.ServiceFailed=Arka plan hizmeti başlatılamadı (kod %1). Ayrıntı: C:\ProgramData\APFS for Windows\log.txt
en.ServiceFailed=The background service could not be started (code %1). Details: C:\ProgramData\APFS for Windows\log.txt
tr.ServiceDescription=Takılan Mac (APFS) disklerini Windows'ta sürücü olarak bağlar (önce salt okunur; isteğe bağlı yazma).
en.ServiceDescription=Mounts attached Mac (APFS) disks as drives in Windows (read-only by default; optional writing).

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "redist\{#WinFspMsi}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWinFsp

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\apfs-tray.exe"; Parameters: "--show"; Comment: "{cm:ServiceDescription}"

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
  ValueData: """{app}\apfs-tray.exe"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\apfs-tray.exe"; Parameters: "--show"; Description: "{cm:LaunchApp}"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM apfs-tray.exe"; Flags: runhidden; RunOnceId: "KillTray"
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteService"
Filename: "{app}\apfs.exe"; Parameters: "unregister"; Flags: runhidden; RunOnceId: "Unregister"
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKLM\SOFTWARE\WOW6432Node\WinFsp\Services\apfs"" /f"; Flags: runhidden; RunOnceId: "DeleteLauncher"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM apfs.exe"; Flags: runhidden; RunOnceId: "KillCli"

[Code]
var
  NeedWinFsp: Boolean;
  RestartNeeded: Boolean;

{ WinFsp yoksa veya bin\winfsp-x64.dll sürümü 2.1'den eskiyse true. (Kayıt defterinde sürüm değeri yok, yalnızca InstallDir.) }
function DetectNeedsWinFsp: Boolean;
var
  Dir: String;
  MS, LS: Cardinal;
begin
  Result := True;
  if not RegQueryStringValue(HKLM32, 'SOFTWARE\WinFsp', 'InstallDir', Dir) then Exit;
  if not GetVersionNumbers(AddBackslash(Dir) + 'bin\winfsp-x64.dll', MS, LS) then Exit;
  Result := MS < $00020001;
end;

function InitializeSetup: Boolean;
begin
  NeedWinFsp := DetectNeedsWinFsp;
  Result := True;
end;

function NeedsWinFsp: Boolean;
begin
  Result := NeedWinFsp;
end;

function Run(const FileName, Params: String): Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then Result := -1;
end;

{ Eski sürüm (veya install.ps1 kurulumu) çalışıyorsa dosyalar kilitli olmasın: tepsiyi kapat, hizmeti durdur (net stop bekler). }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Run(ExpandConstant('{sys}\taskkill.exe'), '/F /IM apfs-tray.exe');
  Run(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}');
  Run(ExpandConstant('{sys}\taskkill.exe'), '/F /IM apfs.exe');
  Result := '';
end;

procedure InstallService;
var
  Sc, Bin: String;
  Code: Integer;
begin
  Sc := ExpandConstant('{sys}\sc.exe');
  Bin := '"\"' + ExpandConstant('{app}') + '\apfs-service.exe\""';
  if Run(Sc, 'query {#ServiceName}') = 0 then
    Run(Sc, 'config {#ServiceName} binPath= ' + Bin + ' start= auto')
  else
    Run(Sc, 'create {#ServiceName} binPath= ' + Bin + ' start= auto DisplayName= "{#AppName}"');
  Run(Sc, 'description {#ServiceName} "' + CustomMessage('ServiceDescription') + '"');
  { Beklenmedik çökmede 5 sn sonra yeniden başlat (install.ps1 ile aynı) }
  Run(Sc, 'failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/30000');
  Code := Run(ExpandConstant('{sys}\net.exe'), 'start {#ServiceName}');
  if (Code <> 0) and (Code <> 2) then
    SuppressibleMsgBox(FmtMessage(CustomMessage('ServiceFailed'), [IntToStr(Code)]), mbError, MB_OK, IDOK);
  Run(ExpandConstant('{app}\apfs.exe'), 'register');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  if NeedWinFsp then
  begin
    WizardForm.StatusLabel.Caption := CustomMessage('InstallingWinFsp');
    Code := Run(ExpandConstant('{sys}\msiexec.exe'), '/i "' + ExpandConstant('{tmp}\{#WinFspMsi}') + '" /qn /norestart');
    if Code = 3010 then RestartNeeded := True
    else if Code <> 0 then
      SuppressibleMsgBox(FmtMessage(CustomMessage('WinFspFailed'), [IntToStr(Code)]), mbError, MB_OK, IDOK);
  end;
  WizardForm.StatusLabel.Caption := CustomMessage('InstallingService');
  InstallService;
end;

function NeedRestart: Boolean;
begin
  Result := RestartNeeded;
end;
