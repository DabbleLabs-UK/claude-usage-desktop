# Claude Usage Desktop

A Windows desktop app that shows your Claude Code subscription usage in real time
-- session limits, weekly caps, per-model (Sonnet / Opus) usage, projected
end-of-period values, and reset countdowns. The same data is also served as a
local web page you can open on your phone from the same Wi-Fi network.

---

> **Requires Claude Code running on this machine, or an optional relay.**
> The app reads Claude Code's local credential and usage data directly from your
> machine. By default it is completely local. If you use Claude Code on another
> Windows PC, you can opt in to its data-only relay described below; the remote
> PC keeps its Claude sign-in private.

---

## Download

**[Releases page](../../releases)** -- download the latest
`ClaudeUsage-vX.Y.Z-win-x64.zip`, extract it, and run `ClaudeUsage.exe`.

### System requirements

- Windows 10 (x64) or later
- Microsoft Edge / WebView2 runtime (pre-installed on most machines via Windows
  Update; if the app fails to start, download the WebView2 Evergreen runtime from
  [Microsoft](https://developer.microsoft.com/en-us/microsoft-edge/webview2/))
- Claude Code must be installed and signed in on the same machine

---

## How to use

1. **Download** the zip from the Releases page, extract it anywhere.
2. **Run** `ClaudeUsage.exe`. The app starts minimised to the **system tray**.
3. **Open the window** by double-clicking the tray icon, or right-click ->
   *Open*.
4. Usage data refreshes automatically every few minutes.

### Phone / tablet access

Open **Settings** (gear icon in the toolbar) to see the LAN URL and QR code.
On any device connected to the same Wi-Fi, scan the QR code or browse to the
URL shown (e.g. `http://192.168.1.x:5005`). The app prompts once to open the
Windows firewall port -- click *Yes* in the UAC prompt to allow it.

### Optional: monitor Claude Code on another Windows PC

This is for people who use Claude Code in a terminal on one Windows PC and keep
Claude Usage Desktop open on another. It is opt-in: no computer discovery, SSH,
cloud service, or credential copying is involved.

1. Put the same Claude Usage Desktop build on the PC where Claude Code runs.
2. On that PC, run the relay from a terminal, choosing a long random token:

   ```bash
   ClaudeUsage.exe --relay --relay-port 5055 --relay-token "choose-a-long-random-token"
   ```

   The relay exposes only `GET /v1/usage`, requires the token in a request
   header, and returns usage windows plus their timestamp. It never returns a
   Claude access token, refresh token, prompts, responses, command history, or
   local file paths.
3. Allow TCP port 5055 through the relay PC's firewall only for the private LAN
   you trust.
4. On the viewing PC, add this to `%APPDATA%\ClaudeUsage\settings.json` while
   the app is closed (substitute the relay PC's LAN name or IP):

   ```json
   {
     "usageRelaySources": [
       {
         "name": "Work PC",
         "url": "http://work-pc:5055/v1/usage",
         "accessToken": "choose-a-long-random-token"
       }
     ]
   }
   ```

   Existing settings may remain in the file. Claude Usage prefers the freshest
   authenticated relay snapshot. If no relay is healthy, it falls back to the
   local Claude Code login exactly as before.

---

## Features

- **Session bar** -- 5-hour rolling window usage and projection
- **Weekly bar** -- 7-day window usage and projection
- **Sonnet and Opus bars** -- per-model weekly usage and projections
- **Projected-usage bars** -- dual-bar display: actual (ghosted) and projected
  (solid), on a shared scale that rescales when projected usage exceeds 100%
- **Spare-ratio label** -- how much headroom remains as a share of the time left
  in the period, colour-coded from green (plenty of room) to red (danger zone)
- **Live reset countdowns** -- ticking second-by-second (e.g. "1h 59m 04s")
- **Phone / LAN access** -- web UI served locally, accessible from any device on
  the same network via QR code
- **System tray** -- runs quietly in the background; shows on demand
- **Configurable alert thresholds** -- set the % spare-ratio levels at which
  colours change (red / orange / yellow / yellow-green)
- **Auto-start with Windows** -- optional, via Settings

---

## Screenshots

*Coming soon -- will add screenshots here.*

---

## Caveats

- **Unofficial API.** The usage data comes from an undocumented internal endpoint
  used by Claude Code. Anthropic may change or remove it at any time without
  notice. Use at your own risk; the app may break after a Claude Code update.
- **Windows only for now.** Cross-platform (macOS / Linux) support is planned but
  not yet implemented.
- **Remote relays are optional.** By default the app reads only local
  credentials. An explicitly configured relay sends a data-only snapshot over
  your own LAN; it does not contact a third-party service or copy credentials.

---

## Build from source

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download)

```bash
git clone https://github.com/DabbleLabs-UK/claude-usage-desktop.git
cd claude-usage-desktop

# Run (dev mode):
dotnet run

# Build release exe + dependencies (win-x64, self-contained):
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
# Output: bin/Release/net10.0-windows/win-x64/publish/
```

The publish output contains the exe and a small set of required WPF native DLLs
(these cannot be embedded into the single-file exe -- it is a .NET WPF limitation).
Bundle the entire `publish/` folder to distribute.

---

## Cutting a release

Releases are cut with **`release.ps1`** (repo root). Run it **on the Windows
host**, not in the VM — it drives `dotnet publish`, `makensis` and `gh`, all of
which are Windows-native. One command does the whole release atomically:

```powershell
pwsh -File .\release.ps1 0.2.2
```

(`powershell -File .\release.ps1 0.2.2` works too — the script supports both
Windows PowerShell 5.1 and pwsh 7.)

What it does, in order:

1. Sets `<Version>` / `<AssemblyVersion>` / `<FileVersion>` in `ClaudeUsage.csproj`.
2. Publishes win-x64 self-contained single-file to `dist\`. If the vboxsf
   **bundle lock** stops a direct publish, it publishes to a temp dir and does a
   **required, hash-checked copy-back** into `dist\` (never best-effort).
3. **VERIFY GATE** — asserts `dist\ClaudeUsage.exe`'s FileVersion **exactly
   equals** the release version. This is the check that catches the v0.2.1
   stale-binary bug; if it fails, the release **aborts** before anything is
   zipped, compiled, tagged or uploaded.
4. Zips the verified `dist\` into `ClaudeUsage-vX.Y.Z-win-x64.zip` (excluding the
   `ClaudeUsage.exe.WebView2\` profile dir, `_pkg\`, and `.old/.bak/.stuck/.prev/
   .pdb/.zip` cruft).
5. Compiles the NSIS installer **from the same verified `dist\`**
   (`makensis /DAPP_VERSION=X.Y.Z`), so the zip and installer can never drift.
6. Commits the version bump, tags `vX.Y.Z`, pushes, and creates the GitHub
   release with **both** artifacts attached, marked **latest**.
7. **Post-release verify** — re-checks the exe inside the built zip and the
   installer both carry the right version, confirms both assets are attached on
   GitHub, and prints a summary (version, tag, asset names, release URL).
8. **Relaunch local app** (real runs only, unless `-NoRelaunch` is set) — kills
   any running `ClaudeUsage.exe`, waits for it to fully exit (respects the
   single-instance mutex), relaunches from the freshly-built `dist\`, then polls
   `/api/version` to confirm the new version is live. Warns on timeout or version
   mismatch; never hard-fails the release (the release itself already succeeded).

**Dry run** (build + verify + package, but no commit/tag/push/release — use this
to exercise the verify gate safely):

```powershell
pwsh -File .\release.ps1 0.2.2 -DryRun
```

Options: `-NotesFile <path>` (release-notes markdown; defaults to
`dist\RELEASE_NOTES_vX.Y.Z.md` if present), `-Branch <name>` (branch to push;
defaults to the current branch), `-NoRelaunch` (skip step 8 — don't kill/restart
the running app after the release; useful if you want to defer the restart).

Prerequisites on the host: the [.NET 10 SDK](https://dotnet.microsoft.com/download),
[NSIS](https://nsis.sourceforge.io/Download) (`winget install NSIS.NSIS`), and an
authenticated [GitHub CLI](https://cli.github.com/) (`gh auth login`). If the
verify gate (or any step) fails, the script aborts loudly and leaves nothing
published; undo the version bump with `git checkout -- ClaudeUsage.csproj`.

---

## Licence

MIT
