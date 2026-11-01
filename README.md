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
3. **Open the window** by double-clicking the tray icon, or left/right-click ->
   *Open*.
4. Usage data refreshes automatically every few minutes.

### OpenRouter account spend

The optional **OpenRouter Account Spend** card shows provider-reported spending
across all API keys in the connected account: this week's actual cost and request
count, rolling 24-hour/seven-day pace, month-to-date totals, and model/provider
breakdowns. It replaces the former launcher-only card and keeps its saved position.
This week starts on Sunday in the exporter's configured timezone; rolling seven-day
totals are a different period. Monthly pace assumes 30 days, not a billing forecast.

The exporter keeps the management credential on its source machine and sends only
aggregate totals to the desktop. Missing or failed reads show unavailable/stale,
never a made-up zero. [Setup and data scope](docs/openrouter-spend.md).

### Usage statistics

Open **Stats** from the bottom toolbar to see Claude and Codex quota-usage
graphs for the same period. Select 1, 7 or 30 days, use **Previous** and **Next**
to move by that duration, or choose **Latest** to return to the present. The
displayed dates identify the selected period; navigation stops at the present
and the beginning of recorded history.

Each line starts at zero and adds observed increases in a quota window's used
percentage. The values are percentage points of that quota, rather than tokens,
money or a percentage of the graph's selected time span. Each line has its own
vertical scale, so equal heights do not mean equal consumption. Reset drops do
not subtract usage, and unobserved activity cannot be reconstructed.
Horizontal gridlines use equal, rounded steps (for example 0, 500, 1000), with
each line's scale extended to fit its observed values. Legend totals stay exact
to the displayed precision; they are not rounded to the gridline steps.

Claude uses the existing local usage log. Codex records successful quota readings
from the existing poll, with separate lines for the actual window durations
reported by Codex. Its history starts with this version, stays separate for each
account and survives restarts. The credit-balance history is used by the credits
card separately. Missing readings, long gaps and reset changes can leave Codex
usage uncounted; sparse or empty periods are shown explicitly. **Clear data ->
Usage logs** removes both providers' history.

If either provider is unavailable, a compact shared status row identifies which
one needs attention without obscuring the other provider's graphs. Expand its
status for the last successful reading, diagnostics and recovery controls.
Unavailable readings do not establish that a subscription has expired. The
graph explanations are grouped under **About these graphs**.

Codex access rejections are not proof of an expired subscription or a need to
sign in again. Expanded status distinguishes an expired local token from HTTP
401/403 rejection, and shows the last and approximate next automatic check.
CU still checks Codex about every three minutes without refreshing credentials.
**Refresh status** acknowledges the click and rereads only CU's cached state;
it does not force a new provider request or bypass that interval.

Codex's last-known home cards are kept for the current app session, not restored
after restarting CU. Saved quota and credit histories remain on disk; Statistics
can show recorded quota usage even while a new reading is unavailable. The older
Claude reading shown after restart is an independent Claude snapshot, not a
fallback source for Codex. The footer labels its Claude timestamp explicitly.

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
4. On the viewing PC, add this to `%APPDATA%\ClaudeUsage\settings.json`
   (substitute the relay PC's LAN name or IP). Changes load on the next poll,
   or immediately using **Retry now** inside the expanded Claude status:

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

The app identifies the computer providing the current reading. If all sources
fail, it keeps the last reading dimmed and describes each remote connection
failure. It retries every 30 seconds, without claiming that a remote login has
expired based on this PC's credentials. Only configure sources using the same
Claude account; snapshots are selected, never added together.

HTTP relays are for a trusted private LAN: their traffic is not encrypted. Do
not forward the relay port to the public internet. The host polls Claude once
every three minutes and serves a cached result to all viewers. A failed host
poll, stale snapshot, or future-dated timestamp cannot be displayed as live.

### Relay startup without an open terminal

The package includes `tools/install-relay-startup.ps1`. Run it as the Windows
user who uses Claude Code (replace the executable path):

```bash
powershell.exe -NoProfile -File ./tools/install-relay-startup.ps1 -ExecutablePath 'C:\Apps\ClaudeUsage\ClaudeUsage.exe'
```

This creates a Windows login task called `Claude Usage Relay`, starts it now,
and configures automatic restart after failure. The app runs silently without
an open terminal. It starts after that user signs into Windows, not before.
The script creates a user-restricted token file and prints its path; copy that
file's value into the viewing PC's `accessToken` setting. To keep an existing
pairing, supply `-TokenFile` pointing to a file containing its existing relay
key. Stop a previously manually launched relay before starting the task, so
both do not try to bind the same port.

Use the same script with `-Remove -ExecutablePath '<existing exe path>'` to
stop the task and remove its startup registration; files are preserved.

The browser settings API omits Claude and relay secrets. Relay addresses and
keys remain editable only in the local settings file. The data inventory is
documented in `docs/data-handling.json`.

---

## Features

- **Rearrange cards** -- use the up/down icon in the bottom toolbar, then drag a handle or use a card's arrow buttons. Choose Done when finished. Positions save automatically on the desktop host and are used by the phone view too. Provider names stay with their cards.
- **Codex credits** -- credit balance, approximate days remaining at the last 24-hour and seven-day spending rates, and observed credit spending for today plus the previous six UTC dates. Today's spending is day-to-date as of the last reading, not a full-day projection. Estimates need sufficient history and pause for stale or uncertain readings; zero spending does not imply infinite credits. Balance changes may also include grants, expiry or corrections, so these are estimates rather than a billing ledger. History stays locally with the usage logs and is separated when the Codex account changes.
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
