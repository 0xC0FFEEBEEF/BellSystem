; SchoolBellSetup.iss — Inno Setup 6 script for the SchoolBell Windows client.
; Build (Windows): "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DMyAppVersion=0.9.0 /DStage=..\..\dist\stage\windows install\windows\SchoolBellSetup.iss
; build/release.sh stages the files and runs ISCC (in the lab VM or under a Windows build host).
;
; Unattended deployment (exactly as documented in README/OPERATIONS):
;   SchoolBellSetup.exe --silent --server 10.0.2.50 --enrollment-file C:\Deploy\schoolbell-enroll.json --ca-file C:\Deploy\school-ca.cer
; Native Inno equivalents also work: /VERYSILENT /SERVER=... /ENROLLMENTFILE=... /CAFILE=... /LOG=path
; Upgrade/repair: run any newer/same SchoolBellSetup.exe again (credentials and settings are kept).
; Uninstall: Settings > Apps > SchoolBell > Uninstall, or "unins000.exe /VERYSILENT" (exit code 0).
;
; Exit codes: 0 ok; 2 missing/invalid argument; 10 files installed but enrollment failed (re-run
; with a valid --enrollment-file to finish); 11 service could not be started; other = Inno Setup.

#ifndef MyAppVersion
  #define MyAppVersion "0.9.0"
#endif
#ifndef Stage
  #define Stage "..\..\dist\stage\windows"
#endif
#define MyAppName "SchoolBell"
#define MyAppPublisher "SchoolBell pilot"
#define ServiceName "SchoolBell"

[Setup]
AppId={{7E1B4C1C-6C7A-4C1A-9F4E-01D2C3B4A5F6}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments=Plays school bells and office announcements on this computer and shows announcement history.
AppContact=School IT office
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription=SchoolBell client installer
DefaultDirName={autopf}\SchoolBell
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#Stage}\..\..\windows
OutputBaseFilename=SchoolBellSetup
SetupIconFile=schoolbell.ico
LicenseFile={#Stage}\LICENSE
UninstallDisplayIcon={app}\SchoolBellCompanion.exe
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
; we stop our own processes in PrepareToInstall; Restart Manager must not kill the update runner
CloseApplications=no
RestartApplications=no
SetupLogging=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#Stage}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "update-runner.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "SchoolBellCompanion.task.xml"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\release-signing.pub.pem"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "schoolbell.ico"; DestDir: "{app}"; Flags: ignoreversion
#if DirExists(Stage + "\provisioning")
Source: "{#Stage}\provisioning\schoolbell-enroll.json"; Flags: dontcopy
Source: "{#Stage}\provisioning\school-ca.cer"; Flags: dontcopy
#endif

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  EXIT_BADARG = 2;
  EXIT_ENROLL = 10;
  EXIT_SERVICE = 11;

var
  ServerUrl, EnrollmentFile, CaFile: String;
  CustomExit: Integer;
  ServerPage: TInputQueryWizardPage;
  FilesPage: TInputFileWizardPage;

procedure ExitProcess(uExitCode: UINT); external 'ExitProcess@kernel32.dll stdcall';

function DataDir(): String; begin Result := ExpandConstant('{commonappdata}\SchoolBell'); end;
function ConfigPath(): String; begin Result := DataDir() + '\config.json'; end;
function Enrolled(): Boolean; begin Result := FileExists(ConfigPath()); end;

function HasArg(const Name: String): Boolean;
var I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then begin Result := True; Exit; end;
end;

{ --name value | --name=value | /INNONAME=value }
function ArgValue(const LongName, InnoName: String): String;
var I: Integer; P: String;
begin
  Result := '';
  for I := 1 to ParamCount do
  begin
    P := ParamStr(I);
    if (CompareText(P, '--' + LongName) = 0) and (I < ParamCount) then begin Result := ParamStr(I + 1); Exit; end;
    if CompareText(Copy(P, 1, Length(LongName) + 3), '--' + LongName + '=') = 0 then begin Result := Copy(P, Length(LongName) + 4, MaxInt); Exit; end;
    if CompareText(Copy(P, 1, Length(InnoName) + 2), '/' + InnoName + '=') = 0 then begin Result := Copy(P, Length(InnoName) + 3, MaxInt); Exit; end;
  end;
end;

function Quote(const S: String): String; begin Result := '"' + S + '"'; end;

function RunHidden(const Exe, Params: String): Integer;
var RC: Integer;
begin
  if not Exec(Exe, Params, '', SW_HIDE, ewWaitUntilTerminated, RC) then RC := -1;
  Log('exec ' + Exe + ' -> ' + IntToStr(RC));
  Result := RC;
end;

function PowerShell(const Script: String): Integer;
begin
  Result := RunHidden(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ' + Quote(Script));
end;

function RunBounded(const Exe, Params: String; Seconds: Integer): Integer;
var ScriptPath, SafeExe, SafeParams: String;
begin
  SafeExe := Exe; StringChangeEx(SafeExe, '''', '''''', True);
  SafeParams := Params; StringChangeEx(SafeParams, '''', '''''', True);
  ScriptPath := ExpandConstant('{tmp}\bounded.ps1');
  SaveStringToFile(ScriptPath,
    '$ErrorActionPreference = ''Stop''' + #13#10 +
    '$p = Start-Process -FilePath ''' + SafeExe + ''' -ArgumentList ''' + SafeParams + ''' -PassThru -WindowStyle Hidden' + #13#10 +
    'if (-not $p.WaitForExit(' + IntToStr(Seconds) + '000)) { taskkill /PID $p.Id /T /F | Out-Null; exit 124 }' + #13#10 +
    '$p.WaitForExit(); exit $p.ExitCode' + #13#10, False);
  Log('starting bounded process: ' + Exe);
  Result := RunHidden(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + Quote(ScriptPath));
end;

function InitializeSetup(): Boolean;
var Params, V, CopyPath: String; RC: Integer;
begin
  Result := True;
  CustomExit := 0;
  { --silent: relaunch ourselves in Inno's very-silent mode and hand back its exit code }
  if HasArg('--silent') and not WizardSilent then
  begin
    Params := '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-';
    V := ArgValue('server', 'SERVER'); if V <> '' then Params := Params + ' ' + Quote('/SERVER=' + V);
    V := ArgValue('enrollment-file', 'ENROLLMENTFILE'); if V <> '' then Params := Params + ' ' + Quote('/ENROLLMENTFILE=' + V);
    V := ArgValue('ca-file', 'CAFILE'); if V <> '' then Params := Params + ' ' + Quote('/CAFILE=' + V);
    V := ArgValue('log', 'LOG'); if V <> '' then Params := Params + ' ' + Quote('/LOG=' + V + '.install.txt') else Params := Params + ' /LOG';
    { A running Setup cannot open its own EXE (access denied), so PowerShell starts the silent
      child and hands back its exit code. }
    Log('srcexe=' + ExpandConstant('{srcexe}') + ' exists=' + IntToStr(Ord(FileExists(ExpandConstant('{srcexe}')))));
    CopyPath := ExpandConstant('{tmp}\relaunch.ps1');
    SaveStringToFile(CopyPath,
      '$p = Start-Process -FilePath ''' + ExpandConstant('{srcexe}') + ''' -ArgumentList ''' + Params + ''' -Wait -PassThru' + #13#10 +
      'exit $p.ExitCode' + #13#10, False);
    Log('relaunching silently via PowerShell: ' + Params);
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + Quote(CopyPath), '', SW_HIDE, ewWaitUntilTerminated, RC) then
    begin
      Log('relaunch failed: ' + SysErrorMessage(RC));
      RC := 1;
    end;
    Log('silent install finished with exit code ' + IntToStr(RC));
    ExitProcess(RC);
  end;

  ServerUrl := Trim(ArgValue('server', 'SERVER'));
  EnrollmentFile := Trim(ArgValue('enrollment-file', 'ENROLLMENTFILE'));
  CaFile := Trim(ArgValue('ca-file', 'CAFILE'));
#if DirExists(Stage + "\provisioning")
  if EnrollmentFile = '' then
  begin
    ExtractTemporaryFile('schoolbell-enroll.json');
    EnrollmentFile := ExpandConstant('{tmp}\schoolbell-enroll.json');
  end;
  if CaFile = '' then
  begin
    ExtractTemporaryFile('school-ca.cer');
    CaFile := ExpandConstant('{tmp}\school-ca.cer');
  end;
#endif
  if (EnrollmentFile <> '') and not FileExists(EnrollmentFile) then
  begin
    Log('enrollment file not found: ' + EnrollmentFile);
    if not WizardSilent then MsgBox('Enrollment file not found: ' + EnrollmentFile, mbCriticalError, MB_OK);
    CustomExit := EXIT_BADARG; ExitProcess(EXIT_BADARG);
  end;
  if (CaFile <> '') and not FileExists(CaFile) then
  begin
    Log('CA file not found: ' + CaFile);
    if not WizardSilent then MsgBox('CA certificate file not found: ' + CaFile, mbCriticalError, MB_OK);
    ExitProcess(EXIT_BADARG);
  end;
  if WizardSilent and not Enrolled() and (EnrollmentFile = '') then
  begin
    Log('not enrolled and no --enrollment-file given');
    ExitProcess(EXIT_BADARG);
  end;
end;

procedure InitializeWizard();
begin
  if Enrolled() then Exit;   { upgrade/repair: identity is kept, nothing to ask }
  ServerPage := CreateInputQueryPage(wpWelcome, 'School server', 'Which SchoolBell server should this computer use?',
    'Enter the server name or address given by the school office (for example bells.school.example). ' +
    'It may be left empty if the enrollment file already names the server.');
  ServerPage.Add('Server address:', False);
  ServerPage.Values[0] := ServerUrl;
  FilesPage := CreateInputFilePage(ServerPage.ID, 'Enrollment', 'Enrollment file and school certificate',
    'The enrollment file (schoolbell-enroll.json) comes from SchoolBell /admin > Enrollment. The CA certificate is only needed if the school certificate is not already trusted.');
  FilesPage.Add('Enrollment file:', 'Enrollment files|*.json;*.txt|All files|*.*', '.json');
  FilesPage.Add('School CA certificate (optional):', 'Certificates|*.cer;*.crt;*.pem|All files|*.*', '.cer');
  FilesPage.Values[0] := EnrollmentFile;
  FilesPage.Values[1] := CaFile;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (FilesPage <> nil) and (CurPageID = FilesPage.ID) then
  begin
    ServerUrl := Trim(ServerPage.Values[0]);
    EnrollmentFile := Trim(FilesPage.Values[0]);
    CaFile := Trim(FilesPage.Values[1]);
    if (EnrollmentFile = '') or not FileExists(EnrollmentFile) then
    begin
      MsgBox('Choose the enrollment file from the school office.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { stop our own processes so files can be replaced (upgrade/repair) }
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}');
  PowerShell('$s = Get-Service {#ServiceName} -ErrorAction SilentlyContinue; if ($s) { $s.WaitForStatus(''Stopped'', ''00:00:30'') }');
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM SchoolBellCompanion.exe');
  Result := '';
end;

procedure SecureDataDir();
begin
  ForceDirectories(DataDir() + '\data\cache');
  ForceDirectories(DataDir() + '\updates');
  ForceDirectories(DataDir() + '\logs');
  { device credential lives here: SYSTEM + Administrators only (no inherited Users access) }
  RunHidden(ExpandConstant('{sys}\icacls.exe'), Quote(DataDir()) + ' /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F');
  { the signed-in user's tray companion may only read cached bell sounds }
  RunHidden(ExpandConstant('{sys}\icacls.exe'), Quote(DataDir() + '\data') + ' /grant *S-1-5-32-545:(X)');
  RunHidden(ExpandConstant('{sys}\icacls.exe'), Quote(DataDir() + '\data\cache') + ' /grant *S-1-5-32-545:(OI)(CI)RX');
end;

procedure InstallCa();
var Content: AnsiString; Thumb: String;
begin
  if CaFile = '' then Exit;
  { add to LocalMachine\Root; remember the thumbprint only if WE added it (uninstall removes only ours) }
  PowerShell('$c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 ''' + CaFile + '''; ' +
    '$r = if (Test-Path (''Cert:\LocalMachine\Root\'' + $c.Thumbprint)) { ''existing:'' + $c.Thumbprint } ' +
    'else { Import-Certificate -FilePath ''' + CaFile + ''' -CertStoreLocation Cert:\LocalMachine\Root | Out-Null; ''added:'' + $c.Thumbprint }; ' +
    '$r | Out-File -Encoding ascii ''' + ExpandConstant('{tmp}\ca.txt') + '''');
  if LoadStringFromFile(ExpandConstant('{tmp}\ca.txt'), Content) then
  begin
    Thumb := Trim(String(Content));
    Log('school CA: ' + Thumb);
    if Copy(Thumb, 1, 6) = 'added:' then
      RegWriteStringValue(HKLM, 'Software\SchoolBell', 'InstalledCaThumbprint', Copy(Thumb, 7, MaxInt));
  end;
end;

function Enroll(): Boolean;
var Args: String; RC: Integer; Details: AnsiString;
begin
  Result := True;
  if Enrolled() then begin Log('already enrolled; identity kept'); Exit; end;
  Args := '--token-file ' + Quote(EnrollmentFile) + ' --device-name ' + Quote(GetComputerNameString()) + ' --out ' + Quote(ConfigPath());
  if ServerUrl <> '' then Args := '--server ' + Quote(ServerUrl) + ' ' + Args;
  RC := RunBounded(ExpandConstant('{app}\EnrollTool.exe'), Args, 120);
  if RC <> 0 then
  begin
    Log('enrollment failed with exit code ' + IntToStr(RC) + ' (2 usage, 3 server unreachable or TLS untrusted, 4 rejected)');
    { the tool's own explanation (also kept in ProgramData\SchoolBell\logs\enroll.log) }
    if LoadStringFromFile(DataDir() + '\logs\enroll.log', Details) then Log('enroll.log: ' + String(Details));
    Result := False;
  end;
end;

function StartService(): Boolean;
var I, RC: Integer;
begin
  Result := False;
  for I := 1 to 3 do
  begin
    RunHidden(ExpandConstant('{sys}\sc.exe'), 'start {#ServiceName}');
    RC := PowerShell('$s = Get-Service {#ServiceName}; try { $s.WaitForStatus(''Running'', ''00:01:00'') } catch { exit 1 }; exit 0');
    if RC = 0 then begin Result := True; Exit; end;
    Log('service start attempt ' + IntToStr(I) + ' did not reach Running');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var App: String; RC: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  App := ExpandConstant('{app}');
  SecureDataDir();
  InstallCa();
  if not Enroll() then
  begin
    CustomExit := EXIT_ENROLL;
    if not WizardSilent then
      MsgBox('SchoolBell was installed but could not enroll with the server. Check the server address, the enrollment file and the school certificate, then run setup again.', mbError, MB_OK);
  end;

  { first launch after new files can exceed the 30 s service start timeout while antivirus scans them }
  RunBounded(App + '\SchoolBellService.exe', '--warmup', 120);
  RunBounded(App + '\SchoolBellCompanion.exe', '--warmup', 120);

  RC := RunHidden(ExpandConstant('{sys}\sc.exe'), 'create {#ServiceName} binPath= ' + Quote(App + '\SchoolBellService.exe') + ' start= auto DisplayName= "SchoolBell"');
  if RC = 1073 then RunHidden(ExpandConstant('{sys}\sc.exe'), 'config {#ServiceName} binPath= ' + Quote(App + '\SchoolBellService.exe') + ' start= auto');
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'description {#ServiceName} "SchoolBell: school bells and announcements (server connection, bell cache, keep-awake)."');
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'failure {#ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000');
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'failureflag {#ServiceName} 1');

  PowerShell('Register-ScheduledTask -TaskName SchoolBellCompanion -Xml (Get-Content -Raw ''' + App + '\SchoolBellCompanion.task.xml'') -Force | Out-Null');

  if Enrolled() then
  begin
    if not StartService() then CustomExit := EXIT_SERVICE;
  end;
  { tray companion for whoever is signed in now (later sign-ins start it from the task) }
  RunHidden(ExpandConstant('{sys}\schtasks.exe'), '/run /tn SchoolBellCompanion');

  { keep a copy of this installer: the client updater reinstalls it if an update fails }
  FileCopy(ExpandConstant('{srcexe}'), DataDir() + '\updates\installed-setup.exe', False);
end;

function GetCustomSetupExitCode(): Integer;
begin
  Result := CustomExit;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Thumb: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}');
  PowerShell('$s = Get-Service {#ServiceName} -ErrorAction SilentlyContinue; if ($s) { try { $s.WaitForStatus(''Stopped'', ''00:00:30'') } catch {} }');
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}');
  PowerShell('Unregister-ScheduledTask -TaskName SchoolBellCompanion -Confirm:$false -ErrorAction SilentlyContinue');
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM SchoolBellCompanion.exe');
  if RegQueryStringValue(HKLM, 'Software\SchoolBell', 'InstalledCaThumbprint', Thumb) and (Thumb <> '') then
    PowerShell('Remove-Item -Path ''Cert:\LocalMachine\Root\' + Thumb + ''' -ErrorAction SilentlyContinue');
  RegDeleteKeyIncludingSubkeys(HKLM, 'Software\SchoolBell');
  { device credential, bell cache, history, logs, update staging }
  DelTree(DataDir(), True, True, True);
  { per-user tray logs }
  PowerShell('Get-ChildItem C:\Users -Directory -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item -Recurse -Force (Join-Path $_.FullName ''AppData\Local\SchoolBell'') -ErrorAction SilentlyContinue }');
  { the EventLog source }
  PowerShell('if ([System.Diagnostics.EventLog]::SourceExists(''SchoolBell'')) { [System.Diagnostics.EventLog]::DeleteEventSource(''SchoolBell'') }');
end;
