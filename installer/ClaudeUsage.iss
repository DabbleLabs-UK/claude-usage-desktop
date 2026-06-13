; ============================================================================
;  ClaudeUsage - Inno Setup installer script
;  Publisher: DabbleLabs-UK
;
;  Packages the contents of dist\ (the self-contained single-file
;  ClaudeUsage.exe + loose native DLLs + wwwroot\) into a per-user installer.
;
;  Compile (from the repo root or anywhere; paths below are relative to THIS
;  .iss file, so iscc resolves ..\dist correctly regardless of CWD):
;
;     iscc /DAppVersion=0.2.0 installer\ClaudeUsage.iss
;
;  If you omit /DAppVersion the version is read from dist\ClaudeUsage.exe's
;  file-version metadata at compile time (see the #ifndef below).
;
;  You CANNOT build/test this in the Linux/VM shell - iscc is a Windows GUI
;  toolchain. See installer\README.md for the host install + test checklist.
; ============================================================================

; ---- Where the published payload lives (relative to this script) -----------
#ifndef SourceDir
  #define SourceDir "..\dist"
#endif

; ---- Version: explicit /DAppVersion wins; else read it from the exe --------
#ifndef AppVersion
  #define AppVersion GetFileVersion(AddBackslash(SourcePath) + AddBackslash(SourceDir) + "ClaudeUsage.exe")
#endif

#define AppName        "ClaudeUsage"
#define AppPublisher   "DabbleLabs-UK"
#define AppExeName     "ClaudeUsage.exe"
#define AppUrl         "https://github.com/DabbleLabs-UK/claude-usage-desktop"

[Setup]
; A STABLE AppId is what lets Inno recognise an existing install and perform an
; in-place UPGRADE (rather than a parallel second install). NEVER change this
; GUID across releases of this product.
AppId={{B0F0B650-45A0-485C-AC50-D2500FE69CBF}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}

; ---- Per-user install: NO admin required for install or upgrade ------------
; Install under %LOCALAPPDATA%\Programs\ClaudeUsage (the conventional per-user
; "Programs" location, as used by VS Code's user installer, Python, etc.).
; PrivilegesRequired=lowest => the wizard never asks for elevation.
;
; TRADEOFF vs Program Files: a per-user install needs no admin and upgrades
; silently, but it installs for the CURRENT USER ONLY and the firewall rule /
; autostart it manages are per-user too - which matches this app's model.
; If you ever want an all-users install, switch to:
;     DefaultDirName={autopf}\ClaudeUsage
;     PrivilegesRequired=admin
; ...but then install/upgrade both prompt for UAC, and the uninstall cleanup
; would run already-elevated (no separate firewall prompt). We deliberately
; choose per-user here.
DefaultDirName={localappdata}\Programs\{#AppName}
PrivilegesRequired=lowest
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; ---- Add/Remove Programs registration --------------------------------------
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}

; ---- Output + build settings -----------------------------------------------
OutputDir={#SourcePath}Output
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile={#SourcePath}..\design\logo_icon.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; If ClaudeUsage is running during an upgrade its exe is locked; let Inno's
; restart manager detect it and offer to close it. We do NOT auto-restart it.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; Package EVERYTHING under dist\ EXCEPT the runtime WebView2 profile dir and the
; build/release cruft (same exclusions as the release zip). recursesubdirs
; (without createallsubdirs) recreates only the subdirs that actually contain
; shipped files, so the empty single-file language dirs (cs\ de\ es\ ...) and
; the excluded dirs are skipped automatically.
;
; Excludes (case-insensitive; a name pattern excludes a matching dir entirely):
;   *.WebView2     -> the runtime ClaudeUsage.exe.WebView2\ user-data/profile dir
;   _pkg           -> staging dir left in dist\
;   *.pdb          -> debug symbols
;   *.old*  *.bak* *.stuck* *.prev*  *_old.exe -> rename-dance backups
;   *.zip          -> previously built release zips
;   RELEASE_NOTES* -> loose release-notes markdown
;   .*             -> stray dotfiles (e.g. the .ffff...X marker)
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion; \
  Excludes: "*.WebView2,_pkg,*.pdb,*.old,*.old*,*.bak*,*.stuck*,*.prev*,*_old.exe,*.zip,RELEASE_NOTES*,.*"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Optional: offer to launch the app at the end of a fresh install.
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName} now"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; ============================================================================
;  TEARDOWN ON UNINSTALL (NOT on upgrade).
;
;  Inno fires [UninstallRun] ONLY during a genuine uninstall (Add/Remove
;  Programs), and these entries execute BEFORE the application's files are
;  removed - so ClaudeUsage.exe still exists to run its own --uninstall-cleanup.
;  An in-place UPGRADE never touches this section: it is a plain [Files]
;  overwrite, so settings / usage logs / firewall rule / autostart all survive.
;
;  --uninstall-cleanup reuses the app's shared teardown to remove the firewall
;  rule, the HKCU autostart entry, settings.json, the diagnostic logs and the
;  WebView2 cache. The firewall removal SELF-ELEVATES via UAC (the exe does its
;  own "runas"), so the per-user uninstaller does not need to be elevated; the
;  user sees one UAC prompt for the firewall step. If they decline, the exe
;  returns exit code 2 (everything else cleaned) - non-fatal, uninstall
;  continues. Usage logs are KEPT unless the user opts in below.
;
;  Exactly one of the two entries runs, chosen by the prompt in [Code].
; ============================================================================
Filename: "{app}\{#AppExeName}"; Parameters: "--uninstall-cleanup --purge-usage-logs"; \
  RunOnceId: "ClaudeUsageCleanupPurge"; Check: ShouldPurge; \
  Flags: waituntilterminated runhidden skipifdoesntexist
Filename: "{app}\{#AppExeName}"; Parameters: "--uninstall-cleanup"; \
  RunOnceId: "ClaudeUsageCleanupKeep"; Check: ShouldKeep; \
  Flags: waituntilterminated runhidden skipifdoesntexist

[Code]
var
  PurgeUsageLogs: Boolean;

// Ask once, up front, whether to also wipe the usage history. DEFAULT = No
// (MB_DEFBUTTON2 highlights "No"), so a careless Enter keeps the logs.
function InitializeUninstall(): Boolean;
begin
  PurgeUsageLogs := False;
  if MsgBox('Also permanently delete your saved usage history (the usage-log folder)?'
      + #13#10#13#10
      + 'No   = keep usage logs. Settings, the firewall rule and autostart are still removed.'
      + #13#10
      + 'Yes  = remove everything, including the usage logs.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    PurgeUsageLogs := True;
  Result := True;
end;

function ShouldPurge(): Boolean;
begin
  Result := PurgeUsageLogs;
end;

function ShouldKeep(): Boolean;
begin
  Result := not PurgeUsageLogs;
end;
