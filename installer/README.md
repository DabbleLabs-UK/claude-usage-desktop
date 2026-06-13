# ClaudeUsage installer (NSIS)

`ClaudeUsage.nsi` builds a **per-user** Windows installer for ClaudeUsage from
the published `dist\` folder, using **NSIS** (Nullsoft Scriptable Install
System).

**Why NSIS (not Inno Setup):** NSIS is licensed under **zlib/libpng** — free for
any use, commercial or not, with no per-seat or commercial licence fee. (Inno
Setup's newer releases push a paid commercial licence, which doesn't fit this
app.) The previous `installer\ClaudeUsage.iss` Inno script has been **removed**;
this `.nsi` is now the single source of truth for packaging.

Compile and test this **on the Windows host** — `makensis` is Windows-only and
produces a GUI installer; it cannot be built or run in the VM/Linux shell.

## What it packages

The published `dist\` payload — the self-contained single-file `ClaudeUsage.exe`,
the loose native DLLs, the WebView2 `.xml` docs, and `wwwroot\`. It **excludes**
the same cruft as the release zip: the runtime `ClaudeUsage.exe.WebView2\`
profile dir, `_pkg\`, the empty single-file locale dirs (`cs\ de\ es\ …`),
`*.pdb`, `*.old* / *.bak* / *.stuck* / *.prev* / *_old.exe`, old `*.zip`
artifacts, `RELEASE_NOTES*`, and stray dotfiles.

> **NSIS uses an explicit `File` list**, not Inno's wildcard-with-excludes. That
> means a *new* file added to `dist\` by a future `dotnet publish` (e.g. a native
> DLL from a .NET runtime bump) could be **silently missed**. Always run the
> verifier below after publishing and before compiling.

### Verify the dist payload (run after each publish, before compiling)

From the repo root, in PowerShell — prints any top-level `dist\` entry that is
neither shipped by the script nor known cruft (empty dirs ignored). **If it
prints anything, add it to `SEC_CORE` in the `.nsi` (and to the uninstaller's
`Delete` list) or confirm it is cruft:**

```powershell
$shipped = 'ClaudeUsage.exe','aspnetcorev2_inprocess.dll','D3DCompiler_47_cor3.dll',
  'PenImc_cor3.dll','PresentationNative_cor3.dll','vcruntime140_cor3.dll',
  'WebView2Loader.dll','wpfgfx_cor3.dll','Microsoft.Web.WebView2.Core.xml',
  'Microsoft.Web.WebView2.WinForms.xml','Microsoft.Web.WebView2.Wpf.xml',
  'runtimes','wwwroot'
Get-ChildItem dist | Where-Object {
  $_.Name -notin $shipped -and
  $_.Name -notmatch '\.(old|bak|stuck|prev|pdb|zip)|\.WebView2$|^_pkg$|^\.|^RELEASE_NOTES|_old\.exe$' -and
  -not ($_.PSIsContainer -and @($_.GetFileSystemInfos()).Count -eq 0)
} | Select-Object -ExpandProperty Name
```

## 1. Install NSIS on the host (one-time)

Download from <https://nsis.sourceforge.io/Download> and install, **or** via
winget:

```powershell
winget install NSIS.NSIS
```

The command-line compiler is `makensis.exe`, typically at:

```
C:\Program Files (x86)\NSIS\makensis.exe
```

## 2. Compile

Make sure `dist\` holds a current publish and the verifier above is clean. Then
from the **repo root**:

```powershell
& "C:\Program Files (x86)\NSIS\makensis.exe" /DAPP_VERSION=0.2.0 installer\ClaudeUsage.nsi
```

- `/DAPP_VERSION=0.2.0` sets the Add/Remove Programs `DisplayVersion`, the
  installer's own version metadata, and the output filename. **Bump it each
  release** to match `ClaudeUsage.csproj`'s `<Version>`. Use a 3-part `X.Y.Z`
  value (the script appends `.0` for the 4-part exe version field).
- If you omit `/DAPP_VERSION` it falls back to `0.2.0` (the `!define` in the
  script).

Output: `installer\Output\ClaudeUsage-Setup-0.2.0.exe`.

Paths inside the `.nsi` are relative to the script file (`..\dist`,
`..\design\logo_icon.ico`), so `makensis` can be invoked from any working
directory as long as those exist.

## Install location & elevation (design notes)

- **Install dir:** `%LOCALAPPDATA%\Programs\ClaudeUsage` (per-user).
  `RequestExecutionLevel user` means **install and upgrade never prompt for
  admin**, and Add/Remove Programs registration goes under **HKCU**. This matches
  the app's per-user firewall-rule + autostart model. The all-users alternative
  (`$PROGRAMFILES64`, `RequestExecutionLevel admin`, HKLM) is described in a
  comment block in the `.nsi`.
- **Uninstall-cleanup elevation:** the uninstaller stays per-user/non-elevated.
  Its `--uninstall-cleanup` step **self-elevates only for the firewall rule** —
  `ClaudeUsage.exe` does its own `runas`, so the user sees **one UAC prompt** for
  the firewall during uninstall. Declining it is non-fatal (cleanup returns exit
  code 2 with everything else removed); the exit code is ignored and the
  uninstall continues.

## Upgrade vs uninstall (the important part)

- **Upgrade** (running a newer setup over an existing install): `.onInit` detects
  the prior install via the HKCU uninstall key and installs **in place** over the
  existing folder. It does **not** run the old uninstaller, so
  `--uninstall-cleanup` never fires — the firewall rule, autostart, settings and
  usage logs all survive. Only program files (exe + DLLs + `wwwroot\`) are
  overwritten; nothing under `%APPDATA%\ClaudeUsage` is touched.
- **Uninstall** (via Add/Remove Programs): the uninstaller runs
  `ClaudeUsage.exe --uninstall-cleanup` **before** deleting files (so the exe
  still exists to run itself), then removes the program files, the HKCU registry
  keys and the shortcuts. **Usage logs are kept by default.** An up-front Yes/No
  prompt (default **No**) optionally adds `--purge-usage-logs` to delete the
  usage history too.

> **Before upgrading, close ClaudeUsage** (tray icon → Exit). A running instance
> locks `ClaudeUsage.exe`, which would make the in-place file overwrite fail.

## 3. Manual test checklist (host)

Run the built `ClaudeUsage-Setup-<ver>.exe` and verify:

**Fresh install**
- [ ] No UAC prompt during install (per-user).
- [ ] App installs to `%LOCALAPPDATA%\Programs\ClaudeUsage`.
- [ ] Components page lets you tick/untick the desktop shortcut; Start Menu
      shortcut always created.
- [ ] "ClaudeUsage" appears in **Settings → Apps** / Add-Remove Programs with the
      right version, publisher **DabbleLabs-UK**, and a working uninstall entry.
- [ ] Finish page "Launch ClaudeUsage now" starts the app; let it create its
      firewall rule, autostart entry, and write settings/usage data under
      `%APPDATA%\ClaudeUsage`.

**Upgrade** (bump `/DAPP_VERSION`, rebuild, run the newer setup over the install)
- [ ] Close the running app first.
- [ ] Installer notes an existing install / upgrades in place (no second,
      parallel Add/Remove Programs entry; `DisplayVersion` updates).
- [ ] `%APPDATA%\ClaudeUsage\settings.json` and `usage-log\` are **unchanged**.
- [ ] Firewall rule `ClaudeUsage-<port>` still exists.
- [ ] HKCU `…\Run\ClaudeUsage` autostart entry still exists.
- [ ] **No UAC prompt** fired (cleanup must NOT run on upgrade).

**Uninstall — keep logs (default)**
- [ ] Close the app first.
- [ ] At the "also delete usage history?" prompt choose **No**.
- [ ] One UAC prompt appears (firewall removal); accept it.
- [ ] Firewall rule gone (`netsh advfirewall firewall show rule name="ClaudeUsage-<port>"`
      → "No rules match").
- [ ] HKCU autostart entry gone; `settings.json` gone; program files +
      install folder gone; Start Menu/desktop shortcuts gone; Add/Remove entry
      gone.
- [ ] `%APPDATA%\ClaudeUsage\usage-log\` **still present**.
- [ ] Audit log written to `%TEMP%\ClaudeUsage-uninstall-cleanup.log`.

**Uninstall — purge logs**
- [ ] Reinstall, then uninstall again and choose **Yes** at the prompt.
- [ ] `%APPDATA%\ClaudeUsage\usage-log\` (and the now-empty `ClaudeUsage` folder)
      are gone.
