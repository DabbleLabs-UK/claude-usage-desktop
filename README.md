# Claude Usage Desktop

A Windows desktop app that shows your Claude Code subscription usage in real time
-- session limits, weekly caps, per-model (Sonnet / Opus) usage, projected
end-of-period values, and reset countdowns. The same data is also served as a
local web page you can open on your phone from the same Wi-Fi network.

---

> **Requires Claude Code running on this machine.**
> The app reads Claude Code's local credential and usage data directly from your
> machine. It cannot fetch your usage remotely. You must be running Claude Code
> (the CLI tool or desktop app) on the same Windows PC for this app to have
> anything to show.

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
- **No remote access.** The app reads local credentials only. It does not connect
  to any third-party service or send your usage data anywhere.

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

**Dry run** (build + verify + package, but no commit/tag/push/release — use this
to exercise the verify gate safely):

```powershell
pwsh -File .\release.ps1 0.2.2 -DryRun
```

Options: `-NotesFile <path>` (release-notes markdown; defaults to
`dist\RELEASE_NOTES_vX.Y.Z.md` if present), `-Branch <name>` (branch to push;
defaults to the current branch).

Prerequisites on the host: the [.NET 10 SDK](https://dotnet.microsoft.com/download),
[NSIS](https://nsis.sourceforge.io/Download) (`winget install NSIS.NSIS`), and an
authenticated [GitHub CLI](https://cli.github.com/) (`gh auth login`). If the
verify gate (or any step) fails, the script aborts loudly and leaves nothing
published; undo the version bump with `git checkout -- ClaudeUsage.csproj`.

---

## Licence

MIT
