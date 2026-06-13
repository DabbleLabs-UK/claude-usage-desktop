# ClaudeUsage installer (Inno Setup)

`ClaudeUsage.iss` builds a **per-user** Windows installer for ClaudeUsage from the
published `dist\` folder. This must be compiled and tested **on the Windows host**
(not in the VM/Linux shell) — Inno Setup is a Windows GUI toolchain.

## What it packages

The contents of `dist\` — the self-contained single-file `ClaudeUsage.exe`, the
loose native DLLs, and `wwwroot\`. It **excludes** the same cruft as the release
zip: the runtime `ClaudeUsage.exe.WebView2\` profile dir, `_pkg\`, `*.pdb`, the
`*.old* / *.bak* / *.stuck* / *.prev* / *_old.exe` rename-dance backups, old
`*.zip` release artifacts, `RELEASE_NOTES*`, and stray dotfiles. (The empty
single-file language dirs `cs\ de\ es\ …` are skipped automatically — they hold
no files, so `recursesubdirs` never recreates them.)

## 1. Install Inno Setup on the host (one-time)

Download from <https://jrsoftware.org/isdl.php> and run the installer, **or** via
winget:

```powershell
winget install JRSoftware.InnoSetup
```

The command-line compiler is `iscc.exe`, typically at:

```
C:\Program Files (x86)\Inno Setup 6\ISCC.exe
```

## 2. Compile

First make sure `dist\` holds a current publish (see the release/packaging notes).
Then, from the **repo root**:

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DAppVersion=0.2.0 installer\ClaudeUsage.iss
```

- `/DAppVersion=0.2.0` sets the version shown in Add/Remove Programs and the
  output filename. **Bump it each release** to match `ClaudeUsage.csproj`'s
  `<Version>`.
- If you omit `/DAppVersion`, the script reads the version from
  `dist\ClaudeUsage.exe`'s file-version metadata at compile time instead.

Output: `installer\Output\ClaudeUsage-Setup-0.2.0.exe`.

Paths inside the `.iss` are resolved relative to the script file, so `iscc` can be
invoked from any working directory; only the payload root (`..\dist`) and the icon
(`..\design\logo_icon.ico`) must exist.

## Install location & elevation (design notes)

- **Install dir:** `%LOCALAPPDATA%\Programs\ClaudeUsage` (per-user). The wizard
  runs with `PrivilegesRequired=lowest`, so **install and upgrade never prompt for
  admin**. Tradeoff vs Program Files: this installs for the current user only —
  which matches the app's per-user firewall-rule + autostart model. To go
  all-users instead, switch `DefaultDirName` to `{autopf}\ClaudeUsage` and
  `PrivilegesRequired=admin` (then both install and upgrade prompt for UAC).
- **Uninstall cleanup elevation:** the uninstaller itself stays per-user. The
  `--uninstall-cleanup` step **self-elevates** for the firewall rule only — the
  exe does its own `runas`, so the user sees **one UAC prompt** for the firewall
  during uninstall. Declining it is non-fatal (exit code 2: everything else is
  still cleaned), and the uninstall continues.

## Upgrade vs uninstall (the important part)

- **Upgrade** (running a newer setup over an existing install): a stable `AppId`
  GUID makes Inno do an in-place file replace of the program files only. It
  **does not** touch `%APPDATA%\ClaudeUsage` (settings + usage logs), the firewall
  rule, or autostart. `[UninstallRun]` does **not** fire on upgrade, so cleanup is
  never invoked here.
- **Uninstall** (via Add/Remove Programs): `[UninstallRun]` runs
  `ClaudeUsage.exe --uninstall-cleanup` **before** the files are removed, tearing
  down the firewall rule, autostart, settings, and diagnostic logs via the shared
  teardown. **Usage logs are kept by default.** An up-front Yes/No prompt
  (defaulting to No) lets the user also pass `--purge-usage-logs` to delete the
  usage history.

## 3. Manual test checklist (host)

Run the built `ClaudeUsage-Setup-<ver>.exe` and verify:

**Fresh install**
- [ ] No UAC prompt during install (per-user).
- [ ] App installs to `%LOCALAPPDATA%\Programs\ClaudeUsage`.
- [ ] Start Menu shortcut exists; desktop shortcut only if the task was ticked.
- [ ] "ClaudeUsage" appears in **Settings → Apps** / Add-Remove Programs with the
      right version and publisher **DabbleLabs-UK**.
- [ ] Launch the app; let it create its firewall rule, autostart entry, and write
      some settings/usage data under `%APPDATA%\ClaudeUsage`.

**Upgrade** (bump `/DAppVersion`, rebuild, run the newer setup over the install)
- [ ] No second/parallel entry in Add/Remove Programs — version updates in place.
- [ ] `%APPDATA%\ClaudeUsage\settings.json` and `usage-log\` are **unchanged**.
- [ ] The firewall rule `ClaudeUsage-<port>` still exists.
- [ ] The HKCU `…\Run\ClaudeUsage` autostart entry still exists.
- [ ] No UAC prompt fired for cleanup (cleanup must NOT run on upgrade).

**Uninstall — keep logs (default)**
- [ ] Close the app first (or accept Inno's close-app prompt).
- [ ] At the "also delete usage history?" prompt choose **No**.
- [ ] One UAC prompt appears (firewall removal); accept it.
- [ ] Firewall rule gone (`netsh advfirewall firewall show rule name="ClaudeUsage-<port>"`
      → "No rules match").
- [ ] HKCU autostart entry gone; `settings.json` gone; program files gone.
- [ ] `%APPDATA%\ClaudeUsage\usage-log\` **still present**.
- [ ] Audit log written to `%TEMP%\ClaudeUsage-uninstall-cleanup.log`.

**Uninstall — purge logs**
- [ ] Reinstall, then uninstall again and choose **Yes** at the prompt.
- [ ] `%APPDATA%\ClaudeUsage\usage-log\` (and the now-empty `ClaudeUsage` folder)
      are gone.
