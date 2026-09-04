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
;
; Because Uninstallable=no, this installer (unlike a normal Inno installer) has no memory of its own
; between runs - Inno's usual "remember the previous install location/task selections" only works via
; the uninstall registry entry it would otherwise own. Left alone, re-running this installer while a
; Velopack install already exists elsewhere (e.g. a custom directory picked the first time) would show
; the plain %LocalAppData% default again, and running Velopack's Setup.exe --installto against that
; different path creates a second, independent install: a second registry entry under the same
; "GameLauncher" key (last write wins), a second shortcut - and the *original* directory silently
; orphaned, with nothing left pointing at it once the registry key moves. The [Code] section below
; closes this gap directly: it reads the same registry key Velopack itself writes
; (HKCU\...\Uninstall\GameLauncher's InstallLocation) before the destination page ever shows, and if a
; real install is found there, locks the destination field to it instead of leaving it free-form -
; relocating requires uninstalling the existing copy first, exactly as a normal Inno installer would
; behave anyway once something is already registered at a path.

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
ArchitecturesAllowed=x64compatible
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

[Run]
; --silent below suppresses Velopack's own normal post-install auto-launch, so this is the only thing
; that offers to start the app - a standard Inno finish-page checkbox instead, checked by default,
; skipped automatically for a /SILENT or /VERYSILENT run (skipifsilent).
Filename: "{app}\GameLauncher.exe"; Description: "Launch Game Launcher"; Flags: postinstall nowait skipifsilent

[Code]
var
  VelopackSetupPath: string;
  ExistingInstallPath: string;
  HasExistingInstall: Boolean;
  ExistingInstallNotice: TNewStaticText;

const
  UninstallRegKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\GameLauncher';

// True only if the registry points at a directory that still genuinely looks like a Velopack install
// (not just a stale key left behind by a manual delete) - checked via Update.exe's presence, the one
// file every real Velopack install always has at its root regardless of version or install path.
function DetectExistingInstall(): Boolean;
var
  InstallLocation: string;
begin
  Result := False;
  if RegQueryStringValue(HKCU, UninstallRegKey, 'InstallLocation', InstallLocation) then
  begin
    if (InstallLocation <> '') and FileExists(AddBackslash(InstallLocation) + 'Update.exe') then
    begin
      ExistingInstallPath := RemoveBackslashUnlessRoot(InstallLocation);
      Result := True;
    end;
  end;
end;

procedure InitializeWizard();
begin
  HasExistingInstall := DetectExistingInstall();
  if not HasExistingInstall then
    Exit;

  // Locks the destination field to the existing install rather than leaving %LocalAppData% picked
  // freely again - see this file's header remarks for why a free choice here can silently orphan the
  // original install directory instead of updating it in place.
  WizardForm.DirEdit.Text := ExistingInstallPath;
  WizardForm.DirEdit.Enabled := False;
  WizardForm.DirBrowseButton.Enabled := False;

  ExistingInstallNotice := TNewStaticText.Create(WizardForm);
  ExistingInstallNotice.Parent := WizardForm.SelectDirPage;
  ExistingInstallNotice.AutoSize := False;
  ExistingInstallNotice.WordWrap := True;
  ExistingInstallNotice.Left := WizardForm.DirEdit.Left;
  ExistingInstallNotice.Top := WizardForm.DirEdit.Top + WizardForm.DirEdit.Height + 16;
  ExistingInstallNotice.Width := WizardForm.DirEdit.Width;
  ExistingInstallNotice.Height := 48;
  ExistingInstallNotice.Caption :=
    'Game Launcher is already installed here, so this will update that copy in place. To move it ' +
    'to a different folder, first uninstall the existing copy from Windows Settings > Apps, then ' +
    'run this installer again.';
end;

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
    // Disabling the destination field in InitializeWizard only stops the *normal UI* from changing
    // it - a scripted/unattended run (Setup.exe /VERYSILENT /DIR="somewhere else") sets {app} straight
    // from the command line and never touches that disabled control at all. This is the check that
    // actually can't be bypassed: it re-reads the same registry state InitializeWizard used, and
    // compares it against whatever {app} finally resolved to, right before Velopack's real installer
    // ever runs - the one point that actually matters, regardless of how {app} got set.
    if HasExistingInstall and
       (CompareText(RemoveBackslashUnlessRoot(ExpandConstant('{app}')), ExistingInstallPath) <> 0) then
    begin
      MsgBox('Game Launcher is already installed at:' + #13#10 + ExistingInstallPath + #13#10#13#10 +
        'To install it somewhere else, first uninstall the existing copy from Windows Settings > ' +
        'Apps, then run this installer again.', mbCriticalError, MB_OK);
      Abort;
    end;

    if not RunVelopackSetup() then
    begin
      MsgBox('Game Launcher could not be installed. Please try again, or check ' +
        '%LocalAppData%\velopack\velopack.log for details.', mbCriticalError, MB_OK);
      Abort;
    end;
  end;
end;
