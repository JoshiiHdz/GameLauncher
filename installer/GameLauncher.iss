; Wraps Velopack's own Setup.exe with a real Windows installer wizard (destination folder page,
; optional desktop-icon checkbox) - things Velopack's "one-click" installer deliberately never shows
; by design (confirmed against Velopack's own docs: Setup.exe "will not show any questions / wizards
; to the user"; the --inst*/--splashImage customization flags apply only to the separate MSI
; installer, which is machine-wide and would break this app's no-admin-required design).
;
; This script does NOT reimplement Velopack's install layout, app identity, or update mechanism in
; any way - it only collects choices from the user (where to install, whether to make a desktop icon,
; and - via the maintenance window below - what to do if Game Launcher is already installed) and then
; calls the real, unmodified Velopack Setup.exe (embedded as a payload below) with
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
; orphaned, with nothing left pointing at it once the registry key moves.
;
; InitializeSetup below is the main defense against that for an interactive run: before the normal
; wizard ever shows, it checks the same registry key Velopack itself writes
; (HKCU\...\Uninstall\GameLauncher's InstallLocation), and if a real install is found there, shows a
; compact maintenance window (already installed - Open / Repair-or-Update / Uninstall / Close) instead
; of the install wizard, and never falls through to it afterward. For a silent run, InitializeSetup
; skips straight to the normal wizard flow untouched, where InitializeWizard's destination-lock and
; CurStepChanged's fresh registry re-check (see their own remarks) provide the equivalent protection -
; that path was tested and is deliberately left alone here, not folded into the interactive one.

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
; Per Inno's own docs, a second copy of *this wrapper* launched while one is already running gets a
; "Setup is already running - close it and click OK to continue, or Cancel" prompt at startup. Useful,
; cheap defense-in-depth - but it's a launch-time warning a user could click through without actually
; closing the other copy, it says nothing about a raw Velopack Setup.exe run directly and concurrently,
; and the exact moment the mutex releases isn't documented precisely enough to lean on. None of that
; matters for correctness here, because it isn't what actually prevents orphaning: the fresh registry
; re-checks in InitializeSetup/RunMaintenanceRepair/CurStepChanged, immediately before anything that
; actually touches disk, are the checks that can't be stale by the time they matter, regardless of
; what did or didn't happen at launch.
SetupMutex=GameLauncherWrapperSetup_7F3E9A1C4D624B8F9A5E1C8B6D2F0E3A
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
; Uninstallable=no means Inno has nothing to read a previous task selection back from (the usual
; UsePreviousTasks mechanism needs the uninstall registry entry this installer deliberately doesn't
; create - see this file's header). Accepted, not fixed: on a rerun (of the silent/fresh-install wizard
; path only now - see the maintenance window above for the interactive case) this defaults to checked
; again regardless of what was chosen last time, and unchecking it does not remove a shortcut an
; earlier run already created (Velopack's own uninstall still cleans it up correctly either way, same
; as any shortcut it finds pointing at its install directory - see the header remarks).
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
; skipped automatically for a /SILENT or /VERYSILENT run (skipifsilent). Only reachable via the fresh-
; install wizard path - the maintenance window's own "Open Game Launcher" button covers the
; already-installed case independently (see InitializeSetup).
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

// Numeric X.Y.Z comparison (release.yml enforces exactly that tag shape - see its "Extract version
// from tag" step - so there's no prerelease suffix to worry about on either side here). Missing/non-
// numeric components compare as 0, so this degrades gracefully rather than raising if the installed
// DisplayVersion is ever something unexpected. Returns <0, 0, or >0 the same way CompareText does.
function CompareVersionStrings(const A, B: string): Integer;
var
  ARest, BRest, APart, BPart: string;
  ANum, BNum: Integer;
  DotPos: Integer;
begin
  ARest := A;
  BRest := B;
  Result := 0;
  while (Result = 0) and ((ARest <> '') or (BRest <> '')) do
  begin
    DotPos := Pos('.', ARest);
    if DotPos > 0 then
    begin
      APart := Copy(ARest, 1, DotPos - 1);
      ARest := Copy(ARest, DotPos + 1, MaxInt);
    end
    else
    begin
      APart := ARest;
      ARest := '';
    end;

    DotPos := Pos('.', BRest);
    if DotPos > 0 then
    begin
      BPart := Copy(BRest, 1, DotPos - 1);
      BRest := Copy(BRest, DotPos + 1, MaxInt);
    end
    else
    begin
      BPart := BRest;
      BRest := '';
    end;

    ANum := StrToIntDef(APart, 0);
    BNum := StrToIntDef(BPart, 0);
    if ANum < BNum then
      Result := -1
    else if ANum > BNum then
      Result := 1;
  end;
end;

// True only if S is a plain, non-empty, dot-separated run of numeric parts (e.g. "1.17.1") - used to
// tell a genuinely-parsed version apart from a missing/corrupt one before CompareVersionStrings gets
// anywhere near it. CompareVersionStrings itself can't make that distinction: StrToIntDef silently
// turns any non-numeric or missing part into 0, so an unreadable DisplayVersion would otherwise compare
// as "0.0.0" - lower than virtually any real release - and look like a safe, ordinary downgrade target
// instead of the "we don't actually know" case it really is.
function IsWellFormedVersion(const S: string): Boolean;
var
  Rest, Part: string;
  DotPos, I: Integer;
begin
  Result := (S <> '');
  if not Result then
    Exit;

  Rest := S;
  while Rest <> '' do
  begin
    DotPos := Pos('.', Rest);
    if DotPos > 0 then
    begin
      Part := Copy(Rest, 1, DotPos - 1);
      Rest := Copy(Rest, DotPos + 1, MaxInt);
    end
    else
    begin
      Part := Rest;
      Rest := '';
    end;

    if Part = '' then
    begin
      Result := False;
      Exit;
    end;

    for I := 1 to Length(Part) do
    begin
      if (Part[I] < '0') or (Part[I] > '9') then
      begin
        Result := False;
        Exit;
      end;
    end;
  end;
end;

procedure InitializeWizard();
begin
  HasExistingInstall := DetectExistingInstall();
  if not HasExistingInstall then
    Exit;

  // Only ever reached by a silent run now - an interactive one is intercepted by InitializeSetup's
  // maintenance window before the wizard is even created (see this file's header). Left exactly as
  // originally tested: locks the destination field to the existing install rather than leaving
  // %LocalAppData% picked freely again, since a free choice here can silently orphan the original
  // install directory instead of updating it in place.
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

const
  // RunVelopackSetupTo's result codes - see its own remarks for why these checks live there and not
  // in its callers.
  vsrSuccess = 0;
  vsrPathMismatch = 1;   // something is installed elsewhere, or (RequireExisting) nothing is installed at all
  vsrVersionUnknown = 2; // installed DisplayVersion is missing/malformed - can't prove it's safe not to downgrade
  vsrNewerInstalled = 3; // installed version is newer than {#AppVersion} - running Setup.exe here would downgrade it
  vsrExecFailed = 4;     // the embedded Velopack Setup.exe ran but didn't report success

// Shared by the normal wizard's CurStepChanged and the maintenance window's Repair/Update action -
// both ultimately just need "run the embedded Velopack installer at this exact path", the only
// difference being where that path came from and whether an existing install is required there first
// (RequireExisting - True for Repair/Update, which only makes sense against something that's actually
// still there; False for the ordinary install path, where "nothing installed yet" is the normal case).
//
// The path/version re-validation below deliberately happens *after* ExtractTemporaryFile, immediately
// before Exec, rather than in each caller before calling this function at all (which is how both
// CurStepChanged and RunMaintenanceRepair originally did it, independently). ExtractTemporaryFile
// pulls a multi-ten-megabyte payload out of this compressed installer, which takes real, measurable
// time - and a caller-side check performed *before* that extraction leaves that entire window
// unguarded: another Setup instance, a raw Velopack Setup.exe run directly, or the app's own in-app
// updater could still repoint the registry to a different path (reopening the orphaning risk) or bump
// the installed version past {#AppVersion} (reopening the downgrade risk) while this function was busy
// extracting, before its caller's now-stale check ever gets to matter. Doing the check here instead -
// after extraction, with nothing left between it and Exec - means every caller gets the same guarantee
// automatically and can't accidentally regress it by restructuring their own call site.
function RunVelopackSetupTo(const TargetPath: string; RequireExisting: Boolean): Integer;
var
  ResultCode: Integer;
  Params: string;
  InstallExistsNow: Boolean;
  FreshVersion: string;
begin
  ExtractTemporaryFile('{#VelopackSetupExeName}');
  VelopackSetupPath := ExpandConstant('{tmp}\{#VelopackSetupExeName}');

  InstallExistsNow := DetectExistingInstall();

  if InstallExistsNow then
  begin
    if CompareText(RemoveBackslashUnlessRoot(ExistingInstallPath), RemoveBackslashUnlessRoot(TargetPath)) <> 0 then
    begin
      Result := vsrPathMismatch;
      Exit;
    end;

    // A missing/malformed DisplayVersion is treated as unknown, not as older (see IsWellFormedVersion) -
    // StrToIntDef's silent 0-default in CompareVersionStrings would otherwise make garbage look "less
    // than" any real version and wave a downgrade through.
    if not RegQueryStringValue(HKCU, UninstallRegKey, 'DisplayVersion', FreshVersion) then
      FreshVersion := '';

    if not IsWellFormedVersion(FreshVersion) then
    begin
      Result := vsrVersionUnknown;
      Exit;
    end;

    if CompareVersionStrings(FreshVersion, '{#AppVersion}') > 0 then
    begin
      Result := vsrNewerInstalled;
      Exit;
    end;
  end
  else if RequireExisting then
  begin
    Result := vsrPathMismatch;
    Exit;
  end;

  Params := '--silent --installto "' + TargetPath + '"';
  if Exec(VelopackSetupPath, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
    Result := vsrSuccess
  else
    Result := vsrExecFailed;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  SetupResult: Integer;
begin
  if CurStep = ssInstall then
  begin
    // No pre-check here anymore - RunVelopackSetupTo() now performs its own fresh detection
    // immediately before Exec (see its remarks), which is strictly stronger than a check done here
    // first: this function's own local variables would just be a second, earlier - and therefore
    // equally stale-able - snapshot on top of the one it already takes internally.
    SetupResult := RunVelopackSetupTo(ExpandConstant('{app}'), False);

    case SetupResult of
      vsrPathMismatch:
        begin
          MsgBox('Game Launcher is already installed at:' + #13#10 + ExistingInstallPath + #13#10#13#10 +
            'To install it somewhere else, first uninstall the existing copy from Windows Settings > ' +
            'Apps, then run this installer again.', mbCriticalError, MB_OK);
          Abort;
        end;
      vsrVersionUnknown:
        begin
          MsgBox('Game Launcher''s installed version could not be verified, so this installer will not ' +
            'run over it. Uninstall the existing copy from Windows Settings > Apps, then run this ' +
            'installer again.', mbCriticalError, MB_OK);
          Abort;
        end;
      vsrNewerInstalled:
        begin
          MsgBox('A newer version of Game Launcher is already installed. This installer will not ' +
            'downgrade it.', mbCriticalError, MB_OK);
          Abort;
        end;
      vsrExecFailed:
        begin
          MsgBox('Game Launcher could not be installed. Please try again, or check ' +
            '%LocalAppData%\velopack\velopack.log for details.', mbCriticalError, MB_OK);
          Abort;
        end;
    end;
  end;
end;

// The maintenance window's "Repair"/"Update to vX.Y.Z" action. ExpectedPath is where the maintenance
// window found Game Launcher installed when it was shown, but that's only used to report a meaningful
// error if RunVelopackSetupTo rejects the call - the actual path/version re-verification, immediately
// before Exec rather than here, now lives entirely inside RunVelopackSetupTo (see its remarks), since
// a check performed here first would just be an earlier, equally stale-able snapshot on top of the one
// it already takes internally. RequireExisting=True: unlike a fresh install, "Repair" only makes sense
// against something that's actually still there - if the install vanished entirely (e.g. uninstalled
// from Windows Settings while this window sat open) this must not silently fall back to installing
// fresh.
procedure RunMaintenanceRepair(const ExpectedPath: string);
var
  SetupResult: Integer;
begin
  SetupResult := RunVelopackSetupTo(ExpectedPath, True);

  case SetupResult of
    vsrSuccess:
      MsgBox('Game Launcher has been repaired successfully.', mbInformation, MB_OK);
    vsrPathMismatch:
      MsgBox('Game Launcher''s installed location changed since this window opened. Please run this ' +
        'installer again.', mbCriticalError, MB_OK);
    vsrVersionUnknown:
      MsgBox('Game Launcher''s installed version could not be verified. Please close this window and ' +
        'run Setup again.', mbCriticalError, MB_OK);
    vsrNewerInstalled:
      MsgBox('A newer version of Game Launcher is now installed than this Setup can offer, so the ' +
        'repair was cancelled to avoid downgrading it. Please close this window and run Setup again.',
        mbCriticalError, MB_OK);
    vsrExecFailed:
      MsgBox('Game Launcher could not be repaired. Please try again, or check ' +
        '%LocalAppData%\velopack\velopack.log for details.', mbCriticalError, MB_OK);
  end;
end;

// The maintenance window's "Uninstall" action - runs the *existing* installation's own Update.exe
// --uninstall (Velopack's documented command for removing the application files, shortcuts, and
// registry entry), waits for it to finish, and returns. Deliberately does not fall through to
// anything else afterward - see InitializeSetup, which is what guarantees Setup exits right after
// this rather than continuing into a fresh install.
procedure RunMaintenanceUninstall(const InstallPath: string);
var
  ResultCode: Integer;
  UpdateExePath: string;
begin
  UpdateExePath := AddBackslash(InstallPath) + 'Update.exe';
  if not FileExists(UpdateExePath) then
  begin
    MsgBox('Could not find Game Launcher''s uninstaller - it may already have been removed.',
      mbCriticalError, MB_OK);
    Exit;
  end;

  if MsgBox('This will remove Game Launcher and all its files from:' + #13#10 + InstallPath + #13#10#13#10 +
       'Continue?', mbConfirmation, MB_YESNO) <> IDYES then
    Exit;

  // Re-detect right before Exec, not just before the confirmation prompt above - the confirmation
  // dialog can sit open for a while, and something else (a raw Velopack Setup.exe run directly to a
  // different folder) could repoint the shared HKCU uninstall registry entry at a different install in
  // the meantime. Running InstallPath's Update.exe unconditionally at that point would tear down
  // whichever install currently owns that shared registry key/shortcut, not necessarily the one shown
  // in this window - and could silently orphan the real, current install instead of removing anything
  // the user actually asked to remove.
  if not (DetectExistingInstall() and
          (CompareText(RemoveBackslashUnlessRoot(ExistingInstallPath), RemoveBackslashUnlessRoot(InstallPath)) = 0)) then
  begin
    MsgBox('Game Launcher''s installed location changed since this window opened. Please run this ' +
      'installer again.', mbCriticalError, MB_OK);
    Exit;
  end;

  if Exec(UpdateExePath, '--uninstall --silent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
     (ResultCode = 0) then
    MsgBox('Game Launcher has been uninstalled.', mbInformation, MB_OK)
  else
    MsgBox('Uninstall may not have completed successfully. Check ' +
      '%LocalAppData%\velopack\velopack.log for details.', mbCriticalError, MB_OK);
end;

// A compact, vertically-stacked dialog (grows to fit its content rather than using a fixed height) -
// deliberately not a wizard page: this replaces the wizard entirely for this run (see
// InitializeSetup), not a step within it. RepairCaption is 'Repair', 'Update to vX.Y.Z', or '' (see
// AllowRepair) depending on how the installed version compares to this installer's own - computed by
// the caller, not here, since that comparison has nothing to do with building the dialog itself.
// VersionUnknown mirrors RunVelopackSetupTo's own IsWellFormedVersion check (see its remarks) - kept
// in sync deliberately, so this window never offers a Repair/Update button that a stale/malformed
// DisplayVersion would have made RunMaintenanceRepair reject anyway the moment it was clicked.
// Returns mrYes (Open), mrRetry (Repair/Update), mrNo (Uninstall), or mrCancel (Close/closed).
function ShowMaintenanceForm(const InstalledVersion, InstallPath, RepairCaption: string;
  AllowRepair, NewerInstalled, VersionUnknown: Boolean): Integer;
var
  F: TSetupForm;
  TitleLabel, VersionLabel, LocationLabel, NoticeLabel: TNewStaticText;
  OpenBtn, RepairBtn, UninstallBtn, CloseBtn: TNewButton;
  Y: Integer;
begin
  F := CreateCustomForm(ScaleX(380), ScaleY(320), False, False);
  try
    F.Caption := 'Game Launcher Setup';
    F.Position := poScreenCenter;
    F.BorderStyle := bsDialog;

    TitleLabel := TNewStaticText.Create(F);
    TitleLabel.Parent := F;
    TitleLabel.Caption := 'Game Launcher is already installed';
    TitleLabel.Left := ScaleX(20);
    TitleLabel.Top := ScaleY(20);
    TitleLabel.Font.Style := [fsBold];
    TitleLabel.AutoSize := True;

    VersionLabel := TNewStaticText.Create(F);
    VersionLabel.Parent := F;
    VersionLabel.Caption := 'Installed version: ' + InstalledVersion;
    VersionLabel.Left := ScaleX(20);
    VersionLabel.Top := TitleLabel.Top + TitleLabel.Height + ScaleY(16);
    VersionLabel.AutoSize := True;

    LocationLabel := TNewStaticText.Create(F);
    LocationLabel.Parent := F;
    LocationLabel.Caption := 'Location: ' + InstallPath;
    LocationLabel.Left := ScaleX(20);
    LocationLabel.Top := VersionLabel.Top + VersionLabel.Height + ScaleY(4);
    LocationLabel.Width := F.ClientWidth - ScaleX(40);
    LocationLabel.AutoSize := False;
    LocationLabel.WordWrap := True;
    LocationLabel.Height := ScaleY(32);

    Y := LocationLabel.Top + LocationLabel.Height + ScaleY(4);

    if VersionUnknown or NewerInstalled then
    begin
      NoticeLabel := TNewStaticText.Create(F);
      NoticeLabel.Parent := F;
      if VersionUnknown then
        NoticeLabel.Caption := 'Installed version could not be verified.'
      else
        NoticeLabel.Caption := 'A newer version is already installed - this installer will not downgrade it.';
      NoticeLabel.Left := ScaleX(20);
      NoticeLabel.Top := Y;
      NoticeLabel.Width := F.ClientWidth - ScaleX(40);
      NoticeLabel.AutoSize := False;
      NoticeLabel.WordWrap := True;
      NoticeLabel.Height := ScaleY(32);
      Y := Y + NoticeLabel.Height + ScaleY(4);
    end;

    Y := Y + ScaleY(16);

    OpenBtn := TNewButton.Create(F);
    OpenBtn.Parent := F;
    OpenBtn.Caption := 'Open Game Launcher';
    OpenBtn.Left := ScaleX(20);
    OpenBtn.Top := Y;
    OpenBtn.Width := F.ClientWidth - ScaleX(40);
    OpenBtn.Height := ScaleY(28);
    OpenBtn.ModalResult := mrYes;
    Y := Y + OpenBtn.Height + ScaleY(8);

    if AllowRepair then
    begin
      RepairBtn := TNewButton.Create(F);
      RepairBtn.Parent := F;
      RepairBtn.Caption := RepairCaption;
      RepairBtn.Left := ScaleX(20);
      RepairBtn.Top := Y;
      RepairBtn.Width := F.ClientWidth - ScaleX(40);
      RepairBtn.Height := ScaleY(28);
      RepairBtn.ModalResult := mrRetry;
      Y := Y + RepairBtn.Height + ScaleY(8);
    end;

    UninstallBtn := TNewButton.Create(F);
    UninstallBtn.Parent := F;
    UninstallBtn.Caption := 'Uninstall';
    UninstallBtn.Left := ScaleX(20);
    UninstallBtn.Top := Y;
    UninstallBtn.Width := F.ClientWidth - ScaleX(40);
    UninstallBtn.Height := ScaleY(28);
    UninstallBtn.ModalResult := mrNo;
    Y := Y + UninstallBtn.Height + ScaleY(8);

    CloseBtn := TNewButton.Create(F);
    CloseBtn.Parent := F;
    CloseBtn.Caption := 'Close';
    CloseBtn.Left := ScaleX(20);
    CloseBtn.Top := Y;
    CloseBtn.Width := F.ClientWidth - ScaleX(40);
    CloseBtn.Height := ScaleY(28);
    CloseBtn.ModalResult := mrCancel;
    CloseBtn.Cancel := True;
    Y := Y + CloseBtn.Height + ScaleY(20);

    F.ClientHeight := Y;
    F.ActiveControl := CloseBtn;

    Result := F.ShowModal();
  finally
    F.Free();
  end;
end;

function InitializeSetup(): Boolean;
var
  RawVersion, DisplayedVersion, InstallPath, RepairCaption: string;
  VersionCompare: Integer;
  VersionKnown, AllowRepair, NewerInstalled: Boolean;
  Action, ResultCode: Integer;
begin
  Result := True; // proceed to the normal wizard unless one of the cases below says otherwise

  // Silent runs never show UI, ever - this maintenance window included. The normal wizard flow
  // (InitializeWizard's destination lock + CurStepChanged's fresh recheck) already handles a silent
  // run against an existing install correctly and was tested as such; nothing here changes that path.
  if WizardSilent() then
    Exit;

  if not DetectExistingInstall() then
    Exit; // nothing installed - normal wizard, fresh install

  InstallPath := ExistingInstallPath;
  if not RegQueryStringValue(HKCU, UninstallRegKey, 'DisplayVersion', RawVersion) then
    RawVersion := '';

  // Uses IsWellFormedVersion here too, not just CompareVersionStrings directly - without it, a
  // missing/malformed DisplayVersion used to still produce a "Repair"/"Update to vX.Y.Z" button
  // (CompareVersionStrings' StrToIntDef silently treats it as "0.0.0", i.e. older than almost
  // anything), only for RunMaintenanceRepair's own, stricter check to reject that exact case the
  // moment the button was actually clicked. Deciding it the same way here keeps the button this window
  // offers from ever being a promise a click can't keep.
  VersionKnown := IsWellFormedVersion(RawVersion);
  if VersionKnown then
  begin
    DisplayedVersion := RawVersion;
    VersionCompare := CompareVersionStrings(RawVersion, '{#AppVersion}');
    NewerInstalled := VersionCompare > 0;
    AllowRepair := not NewerInstalled;

    if VersionCompare = 0 then
      RepairCaption := 'Repair'
    else if VersionCompare < 0 then
      RepairCaption := 'Update to v{#AppVersion}'
    else
      RepairCaption := '';
  end
  else
  begin
    DisplayedVersion := 'unknown';
    NewerInstalled := False;
    AllowRepair := False;
    RepairCaption := '';
  end;

  Action := ShowMaintenanceForm(DisplayedVersion, InstallPath, RepairCaption, AllowRepair, NewerInstalled,
    not VersionKnown);

  case Action of
    mrYes: // Open Game Launcher
      Exec(AddBackslash(InstallPath) + 'GameLauncher.exe', '', '', SW_SHOW, ewNoWait, ResultCode);
    mrRetry: // Repair / Update
      RunMaintenanceRepair(InstallPath);
    mrNo: // Uninstall
      RunMaintenanceUninstall(InstallPath);
  end;
  // mrCancel (Close, or the window's own close button): nothing to do.

  // Every action above is a complete, standalone operation - none of them should fall through into
  // the normal install wizard afterward (Uninstall in particular must not - see this function's
  // header remarks and RunMaintenanceUninstall).
  Result := False;
end;
