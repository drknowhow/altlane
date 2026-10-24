; Altlane installer: per user, no admin prompt. Build with tools\build-installer.ps1, which publishes
; the app to dist\publish first; this script packs that folder.
;
; What it does beyond copying files:
;   - asks Altlane to quit before it replaces or removes files (the running copy may be elevated)
;   - "Start Altlane when I sign in" writes the per-user Run key, unless admin mode's task already
;     starts it; unticked, it removes the Run key
;   - uninstall removes the admin-mode task (one permission prompt, only if the task exists) and the
;     Run key, and asks before deleting rules, settings and logs

#define Exe "..\dist\publish\Altlane.exe"
#if !FileExists(Exe)
  #error Run tools\build-installer.ps1: dist\publish\Altlane.exe is missing
#endif
; The version comes from the built exe (Altlane.csproj), not from here. "0.1.0.0" -> "0.1.0".
#define VerFull GetVersionNumbersString(Exe)
#define AppVersion Copy(VerFull, 1, RPos(".", VerFull) - 1)

[Setup]
AppId={{D4EA3F34-860B-45EF-90D6-AA46FEC2CD36}
AppName=Altlane
AppVersion={#AppVersion}
AppVerName=Altlane {#AppVersion}
AppPublisher=Dr. Know How
AppPublisherURL=https://github.com/drknowhow/altlane
AppSupportURL=https://github.com/drknowhow/altlane/issues
VersionInfoVersion={#VerFull}
DefaultDirName={localappdata}\Programs\Altlane
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=Altlane-Setup-{#AppVersion}
SetupIconFile=..\src\Altlane\Assets\altlane.ico
UninstallDisplayIcon={app}\Altlane.exe
UninstallDisplayName=Altlane
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Altlane is asked to quit in code below; the Restart Manager can't reach a tray app's hidden window.
CloseApplications=no
RestartApplications=no
#ifdef Sign
; build-installer.ps1 defines Sign and passes the "altlane" sign tool when a certificate is set.
SignTool=altlane
SignedUninstaller=yes
#endif

[Tasks]
Name: "startup"; Description: "Start Altlane when I sign in"

[Files]
Source: "..\dist\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Altlane"; Filename: "{app}\Altlane.exe"; Comment: "Alt+Tab and Win+Tab, grouped by kind of window"

[Run]
Filename: "{app}\Altlane.exe"; Description: "Start Altlane now"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RuntimeUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';

// Framework-dependent build: the .NET 10 Desktop Runtime must be present.
function DesktopRuntimeInstalled(): Boolean;
var
  Found: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), Found);
  if Result then FindClose(Found);
end;

function AdminTaskExists(): Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\schtasks.exe'), '/Query /TN "Altlane admin"', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

// "Altlane.exe --quit" asks the running copy to quit (an elevated one too) and waits up to 8 s for it.
procedure QuitRunningAltlane();
var
  Code: Integer;
begin
  if FileExists(ExpandConstant('{app}\Altlane.exe')) then
    Exec(ExpandConstant('{app}\Altlane.exe'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function InitializeSetup(): Boolean;
var
  Code: Integer;
begin
  Result := DesktopRuntimeInstalled();
  if not Result then
    if SuppressibleMsgBox('Altlane needs the .NET 10 Desktop Runtime (x64), which isn''t installed.' + #13#10#13#10 +
        'Open the download page now? Install the ".NET Desktop Runtime 10" for x64, then run this setup again.',
        mbConfirmation, MB_YESNO, IDNO) = IDYES then
      ShellExec('open', RuntimeUrl, '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  QuitRunningAltlane();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then Exit;
  if not WizardIsTaskSelected('startup') then
    RegDeleteValue(HKCU, RunKey, 'Altlane')
  else if not AdminTaskExists() then
    // The same command Settings writes, so the checkbox there reads it as on.
    RegWriteStringValue(HKCU, RunKey, 'Altlane', '"' + ExpandConstant('{app}\Altlane.exe') + '" --autostart');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    QuitRunningAltlane();
    // Removing the admin task needs elevation; Altlane.exe does it, so it has to happen before the files go.
    if AdminTaskExists() then
      ShellExec('runas', ExpandConstant('{app}\Altlane.exe'), '--admin-task remove', '', SW_HIDE, ewWaitUntilTerminated, Code);
    RegDeleteValue(HKCU, RunKey, 'Altlane');
  end;
  if (CurUninstallStep = usPostUninstall) and DirExists(ExpandConstant('{localappdata}\Altlane')) then
    if SuppressibleMsgBox('Also delete your Altlane rules, settings and logs?' + #13#10 + ExpandConstant('{localappdata}\Altlane'),
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
      DelTree(ExpandConstant('{localappdata}\Altlane'), True, True, True);
end;
