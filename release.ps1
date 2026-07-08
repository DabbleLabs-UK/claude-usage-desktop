<#
================================================================================
  release.ps1  --  ClaudeUsage atomic release tool  (RUN ON THE WINDOWS HOST)
================================================================================

  WHY THIS EXISTS
  ---------------
  v0.2.1 shipped a STALE binary. The single-file publish hit the vboxsf bundle
  lock, so the build was redirected to a temp dir (C:\temp) -- but the copy-back
  into dist\ silently did not happen. Result:
    * the release ZIP was built from the temp publish and was CORRECT, but
    * local dist\ still held the OLD exe, and
    * the NSIS installer -- compiled from dist\ -- shipped that OLD exe inside a
      correctly-named ClaudeUsage-Setup-0.2.1.exe.
  Nobody noticed until the installed app reported "update available" for its own
  version (its FileVersion was behind the v0.2.1 release it came from).

  This script makes that failure IMPOSSIBLE by routing EVERYTHING through one
  verified dist\, with a hard VERIFY GATE between publish and packaging:

      publish --> [ VERIFY GATE: dist\ClaudeUsage.exe FileVersion == X.Y.Z ] -->
      zip + installer (both from the SAME verified dist\) --> tag/push/release -->
      post-release re-verify of the actual artifacts.

  The gate is the crux: if the copy-back ever silently fails again, dist\ holds
  the old exe, its FileVersion will NOT match the release version, and the script
  ABORTS before zipping, compiling the installer, tagging, or uploading anything.

  USAGE
  -----
      pwsh -File .\release.ps1 0.2.2
      pwsh -File .\release.ps1 0.2.2 -DryRun      # build+verify+package, NO git/gh
      pwsh -File .\release.ps1 0.2.2 -NotesFile .\dist\RELEASE_NOTES_v0.2.2.md

  Run from the repo root on the HOST (not the VM). Requires: dotnet, makensis
  (NSIS), gh (GitHub CLI, authenticated). See "## Cutting a release" in README.md.
================================================================================
#>

[CmdletBinding()]
param(
    # The release version, 3-part X.Y.Z (e.g. 0.2.2). The 4-part exe FileVersion
    # is derived as X.Y.Z.0 to match the csproj convention.
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Version,

    # Path to a markdown release-notes file for the GitHub release body. If
    # omitted, dist\RELEASE_NOTES_vX.Y.Z.md is used when present, else a stub.
    [string] $NotesFile,

    # The git branch to push (default: current branch).
    [string] $Branch,

    # Validate the FULL build + verify + packaging pipeline WITHOUT making any
    # irreversible change: no commit, no tag, no push, no GitHub release.
    # Use this to test the verify gate safely.
    [switch] $DryRun,

    # Skip the post-release relaunch of the local app onto the freshly-built dist.
    # By default (when NOT set) the script kills any running ClaudeUsage.exe,
    # relaunches from dist\, and checks /api/version matches the released version.
    # Use -NoRelaunch if you want to defer the restart yourself.
    [switch] $NoRelaunch
)

# --- Fail loud, fail early ----------------------------------------------------
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve repo root from this script's location so it works from any CWD.
$Repo    = $PSScriptRoot
$Csproj  = Join-Path $Repo 'ClaudeUsage.csproj'
$DistDir = Join-Path $Repo 'dist'
$DistExe = Join-Path $DistDir 'ClaudeUsage.exe'
$Nsi     = Join-Path $Repo 'installer\ClaudeUsage.nsi'
$Version4 = "$Version.0"                                  # 4-part for exe metadata

# ------------------------------------------------------------------------------
#  Small helpers
# ------------------------------------------------------------------------------
function Info  ($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Good  ($m) { Write-Host "[OK] $m" -ForegroundColor Green }
function Step  ($m) { Write-Host ""; Write-Host "### $m" -ForegroundColor White -BackgroundColor DarkBlue }

# Throw a loud, unmistakable abort. The outer trap turns this into a red banner
# and exit 1 -- nothing downstream (zip/installer/tag/upload) runs.
function Fail ($m) { throw $m }

# Assert a condition or ABORT the whole release.
function Assert ($cond, $m) { if (-not $cond) { Fail $m } }

# Run a native exe and ABORT if it returns a non-zero exit code.
function Exec ($file, [string[]] $argList) {
    & $file @argList
    if ($LASTEXITCODE -ne 0) {
        Fail "Command failed (exit $LASTEXITCODE): $file $($argList -join ' ')"
    }
}

# Read the 3-part (X.Y.Z) FileVersion branded into a Windows exe. .NET brands the
# single-file apphost with the app's <FileVersion> at publish time, so this reads
# back "0.2.2.0" for a 0.2.2 build. Returns the normalised 3-part string.
function Get-ExeVersion3 ($path) {
    $fv = (Get-Item $path).VersionInfo.FileVersion       # e.g. "0.2.2.0"
    if (-not $fv) { Fail "No FileVersion resource on: $path" }
    $parts = $fv.Split('.')
    return ($parts[0..2] -join '.')
}

# ------------------------------------------------------------------------------
#  Outer trap: any thrown error => loud red ABORT banner + exit 1
# ------------------------------------------------------------------------------
trap {
    Write-Host ""
    Write-Host "################################################################" -ForegroundColor Red
    Write-Host "#  RELEASE ABORTED                                             #" -ForegroundColor Red
    Write-Host "################################################################" -ForegroundColor Red
    Write-Host ""
    Write-Host ($_ | Out-String) -ForegroundColor Red
    Write-Host "No zip, installer, tag, or GitHub release was produced/uploaded." -ForegroundColor Yellow
    Write-Host "If the csproj version was bumped, undo it with:" -ForegroundColor Yellow
    Write-Host "    git checkout -- ClaudeUsage.csproj" -ForegroundColor Yellow
    exit 1
}

# ==============================================================================
Write-Host ""
Write-Host "ClaudeUsage release  ->  v$Version  (FileVersion $Version4)" -ForegroundColor Magenta
if ($DryRun) { Write-Host "DRY RUN: will build, verify and package but NOT commit/tag/push/release." -ForegroundColor Yellow }

# --- 0. Pre-flight: arg shape + tools + clean-ish tree ------------------------
Step "0. Pre-flight checks"

Assert ($Version -match '^\d+\.\d+\.\d+$') `
    "Version must be X.Y.Z (got '$Version'). Example: .\release.ps1 0.2.2"
Assert (Test-Path $Csproj) "csproj not found: $Csproj"
Assert (Test-Path $Nsi)    "NSIS script not found: $Nsi"

# Locate makensis (PATH first, then the winget/default install path). Avoid the
# ?. operator so this runs on Windows PowerShell 5.1 as well as pwsh 7.
$Makensis = $null
$mk = Get-Command makensis -ErrorAction SilentlyContinue
if ($mk) { $Makensis = $mk.Source }
if (-not $Makensis) {
    $cand = 'C:\Program Files (x86)\NSIS\makensis.exe'
    if (Test-Path $cand) { $Makensis = $cand }
}
Assert $Makensis "makensis (NSIS) not found. Install NSIS: winget install NSIS.NSIS"

foreach ($t in 'dotnet','git','gh') {
    Assert (Get-Command $t -ErrorAction SilentlyContinue) "Required tool not on PATH: $t"
}
Good "dotnet / git / gh / makensis present ($Makensis)"

# Branch + tag sanity.
if (-not $Branch) { $Branch = (& git -C $Repo rev-parse --abbrev-ref HEAD).Trim() }
$existingTag = (& git -C $Repo tag --list "v$Version")
Assert ([string]::IsNullOrWhiteSpace($existingTag)) `
    "Tag v$Version already exists. Pick a new version or delete the tag first."
Good "Branch '$Branch'; tag v$Version is free"

# ==============================================================================
# --- 1. Bump the csproj version (single source of truth) ----------------------
Step "1. Set csproj version to $Version"

$csText = Get-Content $Csproj -Raw
function Set-CsprojTag ([string]$text, [string]$tag, [string]$value) {
    $rx = "<$tag>[^<]*</$tag>"
    # Don't use $matches here -- it's a PowerShell automatic variable.
    $found = [regex]::Matches($text, $rx)
    Assert ($found.Count -eq 1) "Expected exactly one <$tag> in csproj, found $($found.Count)."
    return [regex]::Replace($text, $rx, "<$tag>$value</$tag>")
}
$csText = Set-CsprojTag $csText 'Version'         $Version
$csText = Set-CsprojTag $csText 'AssemblyVersion' $Version4
$csText = Set-CsprojTag $csText 'FileVersion'     $Version4
# Write UTF-8 WITHOUT a BOM (the csproj has none; PS 5.1's Set-Content -Encoding
# UTF8 would inject one). [IO.File]::WriteAllText is BOM-safe on 5.1 and pwsh 7.
[System.IO.File]::WriteAllText($Csproj, $csText, (New-Object System.Text.UTF8Encoding $false))
Good "csproj <Version>=$Version  <AssemblyVersion>/<FileVersion>=$Version4"

# ==============================================================================
# --- 2. Build + publish (single-file, self-contained) with lock fallback ------
Step "2. Publish win-x64 self-contained single-file"

# Clear lingering MSBuild/compiler handles that cause the bundle lock (see the
# vboxsf build-publish memory). Best-effort -- ignore its exit code.
& dotnet build-server shutdown | Out-Null

# Move any existing dist exe out of the way so (a) a fresh publish/copy lands
# cleanly and (b) a FAILED publish leaves NO stale exe to fool the verify gate.
# (On vboxsf, delete sometimes fails but rename works; rename is safe on the host
# too.) The .old-* file is excluded from the zip in step 4.
if (Test-Path $DistExe) {
    $stamp = (Get-Date).Ticks
    Move-Item -LiteralPath $DistExe -Destination "$DistExe.old-$stamp" -Force
    Info "Moved previous dist exe aside -> ClaudeUsage.exe.old-$stamp"
}

# Attempt 1: publish DIRECTLY to dist\ (PublishDir comes from the .pubxml).
Info "dotnet publish -> dist\  (direct)"
& dotnet publish $Csproj -c Release -p:PublishProfile=win-x64-self-contained
$directOk = ($LASTEXITCODE -eq 0) -and (Test-Path $DistExe)

if (-not $directOk) {
    # Attempt 2 (bundle-lock fallback): publish to a fresh TEMP dir, then do the
    # REQUIRED + CHECKED copy-back into dist\. This is precisely the step that
    # silently no-op'd for v0.2.1 -- here it is mandatory and hash-verified.
    Info "Direct publish failed/locked. Falling back to temp publish + checked copy-back."
    $tmp = Join-Path $env:TEMP "ClaudeUsage-publish-$Version-$((Get-Date).Ticks)"
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    & dotnet build-server shutdown | Out-Null

    # Publish into the temp dir (override PublishDir from the .pubxml). ABORT if
    # even the temp publish fails -- there is no further fallback.
    Exec 'dotnet' @('publish', $Csproj, '-c', 'Release',
                    '-p:PublishProfile=win-x64-self-contained', "-p:PublishDir=$tmp\")
    $tmpExe = Join-Path $tmp 'ClaudeUsage.exe'
    Assert (Test-Path $tmpExe) "Temp publish produced no ClaudeUsage.exe in $tmp"

    # REQUIRED copy-back: mirror the whole temp publish into dist\. -Force +
    # ErrorAction Stop => any copy failure throws and ABORTS (not best-effort).
    Info "Copy-back: $tmp -> dist\  (required, checked)"
    Copy-Item -Path (Join-Path $tmp '*') -Destination $DistDir -Recurse -Force -ErrorAction Stop

    # Prove the copy-back actually happened: dist exe must now byte-match temp exe.
    Assert (Test-Path $DistExe) "Copy-back did not place ClaudeUsage.exe into dist\ -- ABORT."
    $hTmp  = (Get-FileHash $tmpExe  -Algorithm SHA256).Hash
    $hDist = (Get-FileHash $DistExe -Algorithm SHA256).Hash
    Assert ($hTmp -eq $hDist) `
        "Copy-back hash mismatch: dist exe != temp exe. (This is the v0.2.1 failure.) ABORT."
    Good "Copy-back verified (SHA256 dist == temp)"
}
Good "Publish complete; dist\ClaudeUsage.exe present"

# ==============================================================================
# --- 3. *** VERIFY GATE *** (the check that would have caught v0.2.1) ----------
Step "3. VERIFY GATE -- dist exe FileVersion must equal $Version"

Assert (Test-Path $DistExe) "dist\ClaudeUsage.exe missing -- cannot verify. ABORT."
$distVer = Get-ExeVersion3 $DistExe
Info "dist\ClaudeUsage.exe FileVersion = $distVer   (expected $Version)"

if ($distVer -ne $Version) {
    Write-Host ""
    Write-Host "**************************************************************" -ForegroundColor Red
    Write-Host "*  VERIFY GATE FAILED                                        *" -ForegroundColor Red
    Write-Host "*  dist\ClaudeUsage.exe is version $distVer, not $Version.   " -ForegroundColor Red
    Write-Host "*  The published binary did NOT make it into dist\ (this is  *" -ForegroundColor Red
    Write-Host "*  exactly the v0.2.1 stale-binary failure). Refusing to     *" -ForegroundColor Red
    Write-Host "*  zip, compile the installer, tag, or upload anything.      *" -ForegroundColor Red
    Write-Host "**************************************************************" -ForegroundColor Red
    Fail "VERIFY GATE: dist exe FileVersion '$distVer' != release '$Version'."
}
Good "VERIFY GATE PASSED -- dist exe is genuinely v$Version. Safe to package."

# ==============================================================================
# --- 4. Build the release ZIP from the VERIFIED dist --------------------------
Step "4. Build ClaudeUsage-v$Version-win-x64.zip from verified dist"

$ZipName = "ClaudeUsage-v$Version-win-x64.zip"
$ZipPath = Join-Path $Repo $ZipName
if (Test-Path $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }

# PowerShell 5.1 needs BOTH assemblies or ZipArchive is "type not found".
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Cruft excluded from the zip (same rules as the installer/README):
#  - the runtime ClaudeUsage.exe.WebView2\ profile dir and _pkg\
#  - *.old* / *.bak* / *.stuck* / *.prev* backups from the rename dance
#  - *.pdb symbols, old *.zip artifacts, *_old.exe, dotfiles, RELEASE_NOTES*
# Directory excludes (match anywhere in the relative path):
$dirExclude  = '(?i)(^|/)(ClaudeUsage\.exe\.WebView2|_pkg)(/|$)'
# File-name excludes:
$nameExclude = '(?i)(\.old|\.bak|\.stuck|\.prev)(\d|-|\.|$)|\.pdb$|\.zip$|_old\.exe$|^\.|^RELEASE_NOTES'

$distFull = (Resolve-Path $DistDir).Path
$files = Get-ChildItem -LiteralPath $distFull -Recurse -File
$added = 0
$fs = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::Create)
try {
    $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in $files) {
            # Relative path via Substring (no TrimEnd lone-backslash literal), then
            # normalise Windows separators to '/' so zip entries are portable.
            $rel = $f.FullName.Substring($distFull.Length + 1)
            $entry = $rel -replace '\\', '/'
            if ($entry -match $dirExclude)            { continue }
            if ($f.Name -match $nameExclude)          { continue }
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $entry) | Out-Null
            $added++
        }
    } finally { $zip.Dispose() }
} finally { $fs.Dispose() }

Assert ($added -ge 10) "Zip only has $added entries -- expected ~15. Something is wrong; ABORT."
Assert (Test-Path $ZipPath) "Zip was not created: $ZipPath"
Good "Wrote $ZipName ($added entries, $([math]::Round((Get-Item $ZipPath).Length/1MB,1)) MB)"

# ==============================================================================
# --- 5. Compile the NSIS installer FROM THE SAME VERIFIED dist ----------------
Step "5. Compile installer from verified dist (makensis /DAPP_VERSION=$Version)"

# Payload-drift guard: warn-and-abort if dist\ has a top-level entry the NSIS
# explicit File list neither ships nor knows as cruft (e.g. a new native DLL from
# a runtime bump). Mirrors installer\README.md's verifier.
$shipped = 'ClaudeUsage.exe','aspnetcorev2_inprocess.dll','D3DCompiler_47_cor3.dll',
    'PenImc_cor3.dll','PresentationNative_cor3.dll','vcruntime140_cor3.dll',
    'WebView2Loader.dll','wpfgfx_cor3.dll','Microsoft.Web.WebView2.Core.xml',
    'Microsoft.Web.WebView2.WinForms.xml','Microsoft.Web.WebView2.Wpf.xml',
    'runtimes','wwwroot'
$unknown = Get-ChildItem -LiteralPath $DistDir | Where-Object {
    $_.Name -notin $shipped -and
    $_.Name -notmatch '\.(old|bak|stuck|prev|pdb|zip)|\.WebView2$|^_pkg$|^\.|^RELEASE_NOTES|_old\.exe$' -and
    -not ($_.PSIsContainer -and @($_.GetFileSystemInfos()).Count -eq 0)
} | Select-Object -ExpandProperty Name
if ($unknown) {
    Fail ("dist\ has entr(y/ies) the installer neither ships nor knows as cruft: " +
          ($unknown -join ', ') +
          ". Add to SEC_CORE (and the uninstaller Delete list) in installer\ClaudeUsage.nsi, or confirm cruft.")
}
Good "Installer payload check clean (no unexpected dist entries)"

Exec $Makensis @("/DAPP_VERSION=$Version", $Nsi)

$SetupName = "ClaudeUsage-Setup-$Version.exe"
$SetupPath = Join-Path $Repo "installer\Output\$SetupName"
Assert (Test-Path $SetupPath) "Installer not produced: $SetupPath"
# The installer exe carries VIProductVersion = $Version.0 -- verify it too.
$setupVer = Get-ExeVersion3 $SetupPath
Assert ($setupVer -eq $Version) "Installer version is $setupVer, expected $Version. ABORT."
Good "Built $SetupName (version $setupVer) from the same verified dist"

# ==============================================================================
# --- 6. Commit, tag, push, and create the GitHub release ----------------------
Step "6. Commit + tag + push + GitHub release"

# Resolve release notes (explicit > dist\RELEASE_NOTES_vX.Y.Z.md > stub).
if (-not $NotesFile) {
    $cand = Join-Path $DistDir "RELEASE_NOTES_v$Version.md"
    if (Test-Path $cand) { $NotesFile = $cand }
}
if (-not $NotesFile) {
    $NotesFile = Join-Path $env:TEMP "ClaudeUsage-notes-$Version.md"
    Set-Content -Path $NotesFile -Value "ClaudeUsage v$Version" -Encoding UTF8
    Info "No notes file found; using a stub. (Pass -NotesFile to customise.)"
}
# gh.exe is native Windows: it needs a Windows path for --notes-file. Resolve to
# a full Windows path so this also works if launched from Git Bash.
$NotesFileWin = (Resolve-Path $NotesFile).Path

if ($DryRun) {
    Write-Host ""
    Info "DRY RUN -- skipping commit/tag/push/release. Would have run:"
    Write-Host "    git add ClaudeUsage.csproj"
    Write-Host "    git commit -m 'Release v$Version'"
    Write-Host "    git tag v$Version"
    Write-Host "    git push origin $Branch"
    Write-Host "    git push origin v$Version"
    Write-Host "    gh release create v$Version `"$ZipPath`" `"$SetupPath`" --latest --title 'v$Version' --notes-file `"$NotesFileWin`""
} else {
    Exec 'git' @('-C', $Repo, 'add', 'ClaudeUsage.csproj')
    # If the version bump was already committed (e.g. by CC before this script
    # ran), 'git add' stages nothing and a plain 'git commit' would fail with
    # "nothing to commit" and abort the whole release via the trap above. Check
    # via 'diff --cached --name-only' (always exits 0) rather than branching on
    # commit's own exit code, so a genuinely clean tree doesn't blow up the trap.
    $staged = (& git -C $Repo diff --cached --name-only -- ClaudeUsage.csproj)
    if ([string]::IsNullOrWhiteSpace($staged)) {
        Info "Version already committed; skipping commit step."
    } else {
        Exec 'git' @('-C', $Repo, 'commit', '-m', "Release v$Version")
    }
    Exec 'git' @('-C', $Repo, 'tag', "v$Version")
    Exec 'git' @('-C', $Repo, 'push', 'origin', $Branch)
    Exec 'git' @('-C', $Repo, 'push', 'origin', "v$Version")
    # Attach BOTH artifacts; mark latest. Both come from the one verified dist.
    Exec 'gh' @('release', 'create', "v$Version", $ZipPath, $SetupPath,
                '--latest', '--title', "v$Version", '--notes-file', $NotesFileWin)
    Good "Tagged v$Version, pushed, and created GitHub release with both assets"
}

# ==============================================================================
# --- 7. POST-RELEASE VERIFY ---------------------------------------------------
Step "7. Post-release verify of the actual artifacts"

# (a) The exe INSIDE the uploaded zip must be the right version. Extract just
#     ClaudeUsage.exe from the local zip (= the uploaded artifact) and read it.
$verifyDir = Join-Path $env:TEMP "ClaudeUsage-verify-$Version-$((Get-Date).Ticks)"
New-Item -ItemType Directory -Force -Path $verifyDir | Out-Null
$zipRead = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $exeEntry = $zipRead.Entries | Where-Object { $_.FullName -eq 'ClaudeUsage.exe' }
    Assert $exeEntry "Uploaded zip has no top-level ClaudeUsage.exe entry. ABORT."
    $extracted = Join-Path $verifyDir 'ClaudeUsage.exe'
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($exeEntry, $extracted, $true)
} finally { $zipRead.Dispose() }
$zipExeVer = Get-ExeVersion3 $extracted
Assert ($zipExeVer -eq $Version) "Zip's ClaudeUsage.exe is $zipExeVer, expected $Version. ABORT."
Remove-Item -LiteralPath $verifyDir -Recurse -Force -ErrorAction SilentlyContinue
Good "Zip exe FileVersion = $zipExeVer"

# (b) Installer carries the right version (re-assert).
Assert ((Get-ExeVersion3 $SetupPath) -eq $Version) "Installer version drifted. ABORT."
Good "Installer FileVersion = $Version"

# (c) If we actually released, confirm BOTH assets are attached on GitHub.
$releaseUrl = '(dry run -- not created)'
if (-not $DryRun) {
    $assetsJson = & gh release view "v$Version" --repo (& git -C $Repo remote get-url origin) `
                    --json assets,url 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $assetsJson) {
        $assetsJson = & gh release view "v$Version" --json assets,url
    }
    $rel = $assetsJson | ConvertFrom-Json
    $assetNames = @($rel.assets | ForEach-Object { $_.name })
    Assert ($assetNames -contains $ZipName)   "GitHub release is missing asset: $ZipName"
    Assert ($assetNames -contains $SetupName) "GitHub release is missing asset: $SetupName"
    $releaseUrl = $rel.url
    Good "GitHub release has both assets attached"
}

# ==============================================================================
# --- 8. Relaunch local app onto the freshly-built dist ------------------------
Step "8. Relaunch local app onto v$Version dist"

if ($DryRun) {
    Info "DRY RUN -- skipping relaunch."
} elseif ($NoRelaunch) {
    Info "-NoRelaunch specified -- skipping. Restart ClaudeUsage.exe manually to pick up v$Version."
} else {
    # (a) Kill any running instance. The single-instance mutex means a relaunch
    #     before the old process fully exits silently fails, so we must confirm
    #     it's actually gone before starting the new one.
    $killed = $false
    $running = Get-Process -Name 'ClaudeUsage' -ErrorAction SilentlyContinue
    if ($running) {
        Info "Stopping running ClaudeUsage.exe (PID $($running.Id)) ..."
        & taskkill /IM ClaudeUsage.exe /F | Out-Null
        # Poll up to 10 s (20 x 500 ms) for the process to exit.
        $waited = 0
        while ($waited -lt 10) {
            Start-Sleep -Milliseconds 500
            $waited += 0.5
            if (-not (Get-Process -Name 'ClaudeUsage' -ErrorAction SilentlyContinue)) {
                $killed = $true
                break
            }
        }
        if (-not $killed) {
            Write-Host "[WARN] ClaudeUsage.exe did not exit within 10 s; relaunch skipped." -ForegroundColor Yellow
            Write-Host "       Restart manually: dist\ClaudeUsage.exe" -ForegroundColor Yellow
        }
    } else {
        # Nothing was running -- relaunch unconditionally.
        $killed = $true
    }

    if ($killed) {
        # (b) Launch detached -- don't block the script on the app's lifetime.
        Info "Launching dist\ClaudeUsage.exe detached ..."
        Start-Process -FilePath $DistExe -WorkingDirectory $DistDir

        # (c) Wait up to ~8 s for Kestrel to bind, then probe /api/version.
        $apiUrl = 'http://localhost:5005/api/version'
        $apiOk  = $false
        for ($i = 0; $i -lt 8; $i++) {
            Start-Sleep -Seconds 1
            try {
                $resp    = Invoke-WebRequest -Uri $apiUrl -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
                $payload = $resp.Content | ConvertFrom-Json
                $runVer  = $payload.version
                if ($runVer -eq $Version) {
                    Good "Running app now on v$Version  (/api/version confirmed)"
                } else {
                    Write-Host "[WARN] /api/version returned '$runVer', expected '$Version'." -ForegroundColor Yellow
                    Write-Host "       The new exe may not have loaded yet -- check manually." -ForegroundColor Yellow
                }
                $apiOk = $true
                break
            } catch {
                # Kestrel not yet ready; keep waiting.
            }
        }
        if (-not $apiOk) {
            Write-Host "[WARN] /api/version did not respond within ~8 s." -ForegroundColor Yellow
            Write-Host "       The app may still be starting. Check it manually." -ForegroundColor Yellow
        }
    }
}

# ==============================================================================
# --- Final summary ------------------------------------------------------------
Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
$doneLine = "  RELEASE COMPLETE" + $(if ($DryRun) { "  (DRY RUN -- nothing published)" } else { "" })
Write-Host $doneLine -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host ("  Version      : {0}  (FileVersion {1})" -f $Version, $Version4)
Write-Host ("  Tag          : v{0}{1}" -f $Version, $(if ($DryRun) { '  (not created)' } else { '' }))
Write-Host ("  Zip asset    : {0}" -f $ZipName)
Write-Host ("  Installer    : {0}" -f $SetupName)
Write-Host ("  Release URL  : {0}" -f $releaseUrl)
Write-Host "================================================================" -ForegroundColor Green
