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

## Licence

MIT
