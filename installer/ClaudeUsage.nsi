; ============================================================================
;  ClaudeUsage - NSIS installer script
;  Publisher: DabbleLabs-UK
;  Licence of NSIS itself: zlib/libpng (free for commercial use, no royalties).
;
;  PER-USER installer (no admin). Packages the published dist\ payload (the
;  self-contained single-file ClaudeUsage.exe + loose native DLLs + wwwroot\)
;  into %LOCALAPPDATA%\Programs\ClaudeUsage.
;
;  Compile ON THE HOST (makensis is Windows-only; cannot build in the VM):
;     makensis /DAPP_VERSION=0.2.0 installer\ClaudeUsage.nsi
;  -> installer\Output\ClaudeUsage-Setup-0.2.0.exe
;  See installer\README.md for the NSIS install + manual test checklist.
;
;  -------------------------------------------------------------------------
;  PAYLOAD MAINTENANCE  --  READ BEFORE EVERY RELEASE
;  -------------------------------------------------------------------------
;  NSIS bundles an EXPLICIT list of files (the [Files] block in SEC_CORE
;  below). Unlike a wildcard copy this can SILENTLY MISS a new file that a
;  future `dotnet publish` adds to dist\ (e.g. a new native DLL pulled in by a
;  .NET runtime bump). To catch that, run the verifier from installer\README.md
;  ("Verify the dist payload") after each publish and BEFORE compiling: it
;  lists any top-level dist\ entry that is neither shipped here nor known cruft.
;  If it prints anything, add it to SEC_CORE (and to the uninstaller's Delete
;  list) or confirm it is cruft.
;
;  Currently shipped (must match dist\ minus the excluded cruft):
;    ClaudeUsage.exe
;    aspnetcorev2_inprocess.dll
;    D3DCompiler_47_cor3.dll
;    PenImc_cor3.dll
;    PresentationNative_cor3.dll
;    vcruntime140_cor3.dll
;    WebView2Loader.dll
;    wpfgfx_cor3.dll
;    Microsoft.Web.WebView2.Core.xml / .WinForms.xml / .Wpf.xml   (WebView2 docs)
;    runtimes\win-x64\native\WebView2Loader.dll
;    wwwroot\favicon.ico, index.html, logo_dabblelabs.png
;
;  Deliberately EXCLUDED (same as the release zip):
;    ClaudeUsage.exe.WebView2\  (runtime profile dir) , _pkg\ , the empty
;    single-file locale dirs (cs\ de\ ...), *.old* *.bak* *.stuck* *.prev*
;    *.pdb *.zip *_old.exe RELEASE_NOTES* and stray dotfiles.
; ============================================================================

Unicode true

; ---- Payload source (relative to this .nsi) and version --------------------
!ifndef DIST_DIR
  !define DIST_DIR "..\dist"
!endif
!ifndef APP_VERSION
  !define APP_VERSION "0.2.0"        ; fallback; pass /DAPP_VERSION=X.Y.Z to override
!endif
; Installer-exe version metadata wants a 4-part X.X.X.X; APP_VERSION is X.Y.Z.
!ifndef APP_VERSION_FULL
  !define APP_VERSION_FULL "${APP_VERSION}.0"
!endif

!define APP_NAME      "ClaudeUsage"
!define APP_PUBLISHER "DabbleLabs-UK"
!define APP_EXE       "ClaudeUsage.exe"
!define APP_URL       "https://github.com/DabbleLabs-UK/claude-usage-desktop"

; Add/Remove Programs registry key (PER-USER -> HKCU, matching the install scope)
!define UNINST_KEY    "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
; Our own key, used to detect a prior install for the upgrade path
!define APP_REG_KEY   "Software\${APP_PUBLISHER}\${APP_NAME}"

!include "MUI2.nsh"
!include "FileFunc.nsh"     ; ${GetSize} for EstimatedSize

Name "${APP_NAME}"
OutFile "Output\${APP_NAME}-Setup-${APP_VERSION}.exe"

; ---- PER-USER install: no elevation for install OR upgrade -----------------
; Matches the app's per-user model (its firewall rule + HKCU autostart are
; per-user). RequestExecutionLevel user => the UAC shield never appears for the
; installer; the only UAC prompt in the whole lifecycle is the firewall step
; that ClaudeUsage.exe self-elevates during --uninstall-cleanup.
;
; ALL-USERS ALTERNATIVE (not used): set InstallDir "$PROGRAMFILES64\${APP_NAME}",
; RequestExecutionLevel admin, SetShellVarContext all, and move the Uninstall /
; APP_REG_KEY writes to HKLM. Then install AND upgrade both prompt for UAC.
RequestExecutionLevel user
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
; If a prior install recorded its location, NSIS pre-loads $INSTDIR from it so an
; upgrade lands in the SAME folder (see the upgrade notes in .onInit).
InstallDirRegKey HKCU "${APP_REG_KEY}" "InstallDir"

; Installer-exe version info
VIProductVersion "${APP_VERSION_FULL}"
VIAddVersionKey  "ProductName"     "${APP_NAME}"
VIAddVersionKey  "FileVersion"     "${APP_VERSION}"
VIAddVersionKey  "ProductVersion"  "${APP_VERSION}"
VIAddVersionKey  "CompanyName"     "${APP_PUBLISHER}"
VIAddVersionKey  "LegalCopyright"  "${APP_PUBLISHER}"
VIAddVersionKey  "FileDescription" "${APP_NAME} Setup"

; ---- Modern UI -------------------------------------------------------------
!define MUI_ICON   "..\design\logo_icon.ico"
!define MUI_UNICON "..\design\logo_icon.ico"
!define MUI_ABORTWARNING

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Launch ${APP_NAME} now"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; ============================================================================
;  Upgrade detection
;  We do NOT run the old uninstaller on upgrade. The old uninstaller invokes
;  --uninstall-cleanup, which tears down the firewall rule + autostart +
;  settings -- exactly what an upgrade must PRESERVE. Instead we just overwrite
;  the program files in place (NSIS default overwrite). %APPDATA%\ClaudeUsage,
;  the firewall rule and autostart are never touched by the install section.
; ============================================================================
Function .onInit
  SetShellVarContext current        ; per-user shell folders ($SMPROGRAMS, $DESKTOP, $LOCALAPPDATA)
  ReadRegStr $0 HKCU "${UNINST_KEY}" "UninstallString"
  StrCmp $0 "" done
    ; Prior install present -> this run is an in-place UPGRADE. Inform, but do
    ; NOT launch $0 (the old uninstaller) -- that would fire the cleanup teardown.
    MessageBox MB_OKCANCEL|MB_ICONINFORMATION \
      "${APP_NAME} is already installed and will be upgraded to version ${APP_VERSION}.$\n$\nYour settings, usage history, firewall rule and autostart are kept." \
      /SD IDOK IDOK done
    Abort
  done:
FunctionEnd

; ============================================================================
;  Install
; ============================================================================
Section "${APP_NAME} (required)" SEC_CORE
  SectionIn RO

  ; --- Program files (the EXPLICIT payload list; keep in sync with dist\) ----
  SetOutPath "$INSTDIR"
  File "${DIST_DIR}\${APP_EXE}"
  File "${DIST_DIR}\aspnetcorev2_inprocess.dll"
  File "${DIST_DIR}\D3DCompiler_47_cor3.dll"
  File "${DIST_DIR}\PenImc_cor3.dll"
  File "${DIST_DIR}\PresentationNative_cor3.dll"
  File "${DIST_DIR}\vcruntime140_cor3.dll"
  File "${DIST_DIR}\WebView2Loader.dll"
  File "${DIST_DIR}\wpfgfx_cor3.dll"
  File "${DIST_DIR}\Microsoft.Web.WebView2.Core.xml"
  File "${DIST_DIR}\Microsoft.Web.WebView2.WinForms.xml"
  File "${DIST_DIR}\Microsoft.Web.WebView2.Wpf.xml"

  SetOutPath "$INSTDIR\runtimes\win-x64\native"
  File "${DIST_DIR}\runtimes\win-x64\native\WebView2Loader.dll"

  SetOutPath "$INSTDIR\wwwroot"
  File "${DIST_DIR}\wwwroot\favicon.ico"
  File "${DIST_DIR}\wwwroot\index.html"
  File "${DIST_DIR}\wwwroot\logo_dabblelabs.png"

  ; --- Start Menu shortcut (working dir = install dir) -----------------------
  SetOutPath "$INSTDIR"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"

  ; --- Uninstaller + registry ------------------------------------------------
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; Our key: install location for upgrade detection / re-install targeting.
  WriteRegStr HKCU "${APP_REG_KEY}" "InstallDir" "$INSTDIR"

  ; Add/Remove Programs (HKCU, per-user scope).
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayName"     "${APP_NAME}"
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayVersion"  "${APP_VERSION}"
  WriteRegStr   HKCU "${UNINST_KEY}" "Publisher"       "${APP_PUBLISHER}"
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayIcon"     "$INSTDIR\${APP_EXE}"
  WriteRegStr   HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKCU "${UNINST_KEY}" "URLInfoAbout"    "${APP_URL}"
  WriteRegStr   HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1

  ; EstimatedSize (KB) for the Add/Remove Programs listing.
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" "$0"
SectionEnd

Section /o "Desktop shortcut" SEC_DESKTOP
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
SectionEnd

; Component descriptions
LangString DESC_SEC_CORE    ${LANG_ENGLISH} "${APP_NAME} program files, Start Menu shortcut and uninstaller (required)."
LangString DESC_SEC_DESKTOP ${LANG_ENGLISH} "Add a shortcut to ${APP_NAME} on the desktop."
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_CORE}    $(DESC_SEC_CORE)
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} $(DESC_SEC_DESKTOP)
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ============================================================================
;  Uninstall
; ============================================================================
Function un.onInit
  SetShellVarContext current
FunctionEnd

Section "Uninstall"
  ; --- 1. Shared teardown FIRST, while the exe still exists -------------------
  ; Runs ClaudeUsage.exe --uninstall-cleanup (firewall rule + autostart +
  ; settings + diagnostic logs + WebView2 cache). USAGE LOGS ARE KEPT by
  ; default; the prompt below (default NO) optionally adds --purge-usage-logs.
  ; The firewall removal SELF-ELEVATES inside the exe (TryRemoveRuleAsync uses
  ; runas), so this per-user uninstaller stays non-elevated and the user sees
  ; ONE UAC prompt for the firewall step. The cleanup's exit code (0 = ok,
  ; 2 = firewall UAC declined -- everything else still cleaned) is intentionally
  ; IGNORED so the uninstall always continues.
  IfFileExists "$INSTDIR\${APP_EXE}" 0 cleanup_done
    StrCpy $0 "--uninstall-cleanup"
    MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 \
      "Also permanently delete your saved usage history (the usage-log folder)?$\n$\nNo  = keep usage logs. Settings, the firewall rule and autostart are still removed.$\nYes = remove everything, including the usage logs." \
      /SD IDNO IDNO run_cleanup
      StrCpy $0 "--uninstall-cleanup --purge-usage-logs"
    run_cleanup:
    ExecWait '"$INSTDIR\${APP_EXE}" $0' $1   ; $1 = exit code, ignored on purpose
  cleanup_done:

  ; --- 2. Remove installed program files (explicit; mirrors SEC_CORE) --------
  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\aspnetcorev2_inprocess.dll"
  Delete "$INSTDIR\D3DCompiler_47_cor3.dll"
  Delete "$INSTDIR\PenImc_cor3.dll"
  Delete "$INSTDIR\PresentationNative_cor3.dll"
  Delete "$INSTDIR\vcruntime140_cor3.dll"
  Delete "$INSTDIR\WebView2Loader.dll"
  Delete "$INSTDIR\wpfgfx_cor3.dll"
  Delete "$INSTDIR\Microsoft.Web.WebView2.Core.xml"
  Delete "$INSTDIR\Microsoft.Web.WebView2.WinForms.xml"
  Delete "$INSTDIR\Microsoft.Web.WebView2.Wpf.xml"
  Delete "$INSTDIR\wwwroot\favicon.ico"
  Delete "$INSTDIR\wwwroot\index.html"
  Delete "$INSTDIR\wwwroot\logo_dabblelabs.png"
  Delete "$INSTDIR\runtimes\win-x64\native\WebView2Loader.dll"
  Delete "$INSTDIR\Uninstall.exe"

  ; WebView2 runtime profile dir (created beside the exe if the app ever ran).
  ; --uninstall-cleanup normally removes it; this is a belt-and-suspenders so
  ; $INSTDIR can be removed cleanly even if cleanup was skipped/declined.
  RMDir /r "$INSTDIR\${APP_EXE}.WebView2"

  ; Remove now-empty directories (RMDir only deletes if empty).
  RMDir "$INSTDIR\wwwroot"
  RMDir "$INSTDIR\runtimes\win-x64\native"
  RMDir "$INSTDIR\runtimes\win-x64"
  RMDir "$INSTDIR\runtimes"
  RMDir "$INSTDIR"

  ; --- 3. Shortcuts ----------------------------------------------------------
  Delete "$SMPROGRAMS\${APP_NAME}.lnk"
  Delete "$DESKTOP\${APP_NAME}.lnk"

  ; --- 4. Registry -----------------------------------------------------------
  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "${APP_REG_KEY}"
SectionEnd
