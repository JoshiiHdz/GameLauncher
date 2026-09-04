; Wraps Velopack's own Setup.exe with a real Windows installer wizard (destination folder page,
; optional desktop-icon checkbox) - things Velopack's "one-click" installer deliberately never shows
; by design (confirmed against Velopack's own docs: Setup.exe "will not show any questions / wizards
; to the user"; the --inst*/--splashImage customization flags apply only to the separate MSI
; installer, which is machine-wide and would break this app's no-admin-required design).
;
; This script does NOT reimplement Velopack's install layout, app identity, or update mechanism in
; any way - it only collects two choices from the user (where, and whether to make a desktop icon)
; and then calls the real, unmodified Velopack Setup.exe (embedded as a payload below) with
; `--silent --installto "<chosen folder>"`, exactly the same call this project's own local dry-run
; testing already verified works correctly: Velopack's own uninstall registry entry, Update.exe, the
; packages/current/ layout, and the in-app auto-updater all end up identical to a default Velopack
; install, just relocated to wherever the user picked.
;
; Uninstallable=no below is equally deliberate: Velopack's Setup.exe already writes a complete, correct
; "Apps & Features" entry (DisplayName, DisplayIcon, UninstallString -> Update.exe --uninstall) the
; moment it runs. If this installer also registered its own uninstall entry, Windows would show two
; separate "Game Launcher" entries. Velopack owns uninstall - this installer does not duplicate it.
;
; The one thing this installer *does* independently own is the optional desktop shortcut (Velopack's
; own --shortcuts pack setting is StartMenuRoot only - see release.yml's Pack step) - and even that
; doesn't need special uninstall handling: Velopack's own uninstall searches the Desktop for *any*
; shortcut whose target points at its install directory and removes it, regardless of who created it,
; confirmed directly against a real installed copy before this script was written.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
; Path to the real, unmodified Velopack Setup.exe to embed - CI passes the actual one just built by
; `vpk pack`; local test builds can drop a copy next to this script and use the default below.
#ifndef VelopackSetupExe
  #define VelopackSetupExe "GameLauncher-win-Setup.exe"
#endif
#define VelopackSetupExeName ExtractFileName(VelopackSetupExe)

[Setup]
; Fixed, randomly-generated - identifies *this wrapper installer* to Windows/Inno, entirely separate
; from Velopack's own "GameLauncher" package id (see release.yml's Pack step remarks). Never reuse
; this GUID for a different application.
AppId={{7F3E9A1C-4D62-4B8F-9A5E-1C8B6D2F0E3A}
AppName=Game Launcher
AppVersion={#AppVersion}
AppPublisher=Game Launcher
DefaultDirName={localappdata}\GameLauncher
DisableProgramGroupPage=yes
Uninstallable=no
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=GameLauncher-Setup
SetupIconFile=..\src\GameLauncher\Assets\AppIcon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
; dontcopy: embedded in the compiled installer, but never auto-extracted to {app} by Inno's normal
; file-copy step - only ExtractTemporaryFile in [Code] below pulls it out, into {tmp}, to run it as the
; real installer. Inno's own [Files] mechanism never touches {app} at all in this script; Velopack's
; Setup.exe is the only thing that ever writes there.
Source: "{#VelopackSetupExe}"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
; The one shortcut this installer creates itself - Velopack's own pack config deliberately omits
; Desktop (see release.yml) so this checkbox is the only thing that controls it.
Name: "{autodesktop}\Game Launcher"; Filename: "{app}\GameLauncher.exe"; Tasks: desktopicon

[Code]
var
  VelopackSetupPath: string;

function RunVelopackSetup(): Boolean;
var
  ResultCode: Integer;
  Params: string;
begin
  ExtractTemporaryFile('{#VelopackSetupExeName}');
  VelopackSetupPath := ExpandConstant('{tmp}\{#VelopackSetupExeName}');
  Params := '--silent --installto "' + ExpandConstant('{app}') + '"';

  Result := Exec(VelopackSetupPath, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if Result then
    Result := (ResultCode = 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    if not RunVelopackSetup() then
    begin
      MsgBox('Game Launcher could not be installed. Please try again, or check ' +
        '%LocalAppData%\velopack\velopack.log for details.', mbCriticalError, MB_OK);
      Abort;
    end;
  end;
end;
