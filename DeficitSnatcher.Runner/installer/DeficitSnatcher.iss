; The PC helper's Windows installer (Inno Setup), built by .github/workflows/PcHelper.yml:
;   iscc /DMyVersion=1.0.123 /DSourceDir=..\..\out\win-x64 DeficitSnatcher.iss
; For this user only (no admin): installs into %LOCALAPPDATA%\DeficitSnatcher\app, where the helper runs from and updates
; itself; adds a Start menu entry and an entry in Settings -> Apps (uninstall there, or `deficitsnatcher uninstall`, removes
; everything). Run again over an installed helper, it updates it and keeps its setup.

#ifndef MyVersion
  #define MyVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\out\win-x64"
#endif

[Setup]
AppId={{8F3A2C1E-5B7D-4E9A-9C61-2D4F8B7A1E30}
AppName=Deficit Snatcher PC helper
AppVersion={#MyVersion}
AppPublisher=Deficit Snatcher
DefaultDirName={localappdata}\DeficitSnatcher\app
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=.
OutputBaseFilename=DeficitSnatcher-PC-helper-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Deficit Snatcher PC helper
UninstallDisplayIcon={app}\DeficitSnatcher.Runner.exe
; The helper is stopped by PrepareToInstall below; nothing else of ours holds files.
CloseApplications=no

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

#if !FileExists(AddBackslash(SourceDir) + ".playwright\package\package.json")
  #error The helper's .playwright folder (the driver it runs Chrome with) is missing from SourceDir
#endif

[Icons]
Name: "{userprograms}\Deficit Snatcher PC helper"; Filename: "{app}\DeficitSnatcher.Runner.exe"

[Run]
; First install: asks for the setup code (from the site's Helpers tab). Already set up: starts it again. Not when run
; silently: the one-line install command does that part itself.
Filename: "{app}\DeficitSnatcher.Runner.exe"; Description: "Start the helper"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; Stops it and its Chrome, removes autostart and the deficitsnatcher command.
Filename: "{app}\DeficitSnatcher.Runner.exe"; Parameters: "uninstall --yes --from-installer --quiet"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveHelper"

[UninstallDelete]
; Everything else: settings, logs, its Chrome profile, a downloaded Chromium, files from its own updates.
Type: filesandordirs; Name: "{localappdata}\DeficitSnatcher"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Exe: String;
begin
  // An installed helper is stopped first, so its files can be replaced.
  Exe := ExpandConstant('{localappdata}\DeficitSnatcher\app\DeficitSnatcher.Runner.exe');
  if FileExists(Exe) then
    Exec(Exe, 'stop --quiet', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
