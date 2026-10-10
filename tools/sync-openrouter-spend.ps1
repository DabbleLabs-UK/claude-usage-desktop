# Reads aggregate OpenRouter analytics only. Never prints subprocess stderr or credentials.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$ConfigPath)

if ($MyInvocation.InvocationName -eq '.') { return }
$ErrorActionPreference = 'Stop'

function Get-TextHash([string]$Text) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return -join ($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)) | ForEach-Object { $_.ToString('x2') }) }
    finally { $hash.Dispose() }
}

function ConvertTo-ProcessArgument([string]$Value) {
    # Windows CommandLineToArgvW quoting, including paths ending in backslashes.
    return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}

function ConvertTo-ShellArgument([string]$Value) {
    $quote = [char]39
    return "$quote" + $Value.Replace("$quote", "$quote`"$quote`"$quote") + "$quote"
}

function ConvertTo-RemotePath([string]$Value) {
    if ($Value.StartsWith('~/')) { return '"$HOME"/' + (ConvertTo-ShellArgument $Value.Substring(2)) }
    return ConvertTo-ShellArgument $Value
}

function Invoke-BoundedProcess([string]$Executable, [string[]]$Arguments) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Executable
    $info.Arguments = ($Arguments | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' '
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw 'Collector did not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(190000)) {
            $process.Kill()
            $process.WaitForExit(5000) | Out-Null
            throw 'Collector timed out.'
        }
        if (-not $stdout.Wait(5000) -or -not $stderr.Wait(5000)) { throw 'Collector output timed out.' }
        $text = $stdout.Result
        if ($text.Length -gt 16777216) { throw 'Collector output exceeded the safe limit.' }
        return @{ ExitCode = $process.ExitCode; Output = $text }
    }
    finally { $process.Dispose() }
}

function Test-Summary($Value) {
    if ($null -eq $Value -or $Value.schema_version -ne 1 -or $Value.scope -ne 'openrouter_account') { return $false }
    if ($Value.source.status -notin @('ok', 'error')) { return $false }
    if ($Value.source.status -eq 'error' -and -not $Value.generated_at) { return $true }
    if ($Value.account_scope_id -notmatch '^[0-9a-f]{64}$' -or -not $Value.generated_at -or -not $Value.source.last_success_at) { return $false }
    $stamp = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse([string]$Value.generated_at, [ref]$stamp)) { return $false }
    foreach ($name in @('rolling_24h', 'rolling_7d', 'calendar_week', 'month_to_date')) {
        foreach ($field in @('total_cost', 'priced_runs', 'unpriced_runs')) {
            $number = 0.0
            $raw = $Value.$name.$field
            if ($null -eq $raw -or $raw -is [bool] -or -not [double]::TryParse([string]$raw, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number)) { return $false }
            if ([double]::IsNaN($number) -or [double]::IsInfinity($number) -or $number -lt 0) { return $false }
        }
    }
    return $null -ne $Value.run_rates -and $null -ne $Value.daily_history
}

function Write-AtomicSummary([string]$Path, $Value) {
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $temporary = Join-Path $directory ([IO.Path]::GetRandomFileName())
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 40 -Compress), (New-Object Text.UTF8Encoding $false))
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary, $Path, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $Path) }
    }
    finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}

$mutex = $null
$owned = $false
$exitCode = 1
try {
    $config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    if ($config.mode -notin @('Remote', 'Local')) { throw 'Unknown sync mode.' }
    $destination = [IO.Path]::GetFullPath($config.destination)
    $sourceId = Get-TextHash ($config.mode + '|' + $config.remote_host + '|' + $config.credential_file + '|' + $config.exporter_path)
    $mutex = New-Object Threading.Mutex($false, ('Local\ClaudeUsage.OpenRouterSpend.' + (Get-TextHash $destination)))
    try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { Write-Output 'OpenRouter account sync is already running.'; exit 0 }
    $previous = $null
    if (Test-Path -LiteralPath $destination) {
        try { $previous = Get-Content -LiteralPath $destination -Raw | ConvertFrom-Json } catch { }
    }
    try {
        if ($config.mode -eq 'Remote') {
            if ($config.remote_host -notmatch '^[A-Za-z0-9][A-Za-z0-9_.@-]*$') { throw 'Invalid SSH destination.' }
            $command = (ConvertTo-ShellArgument $config.python_path) + ' ' + (ConvertTo-RemotePath $config.exporter_path) +
                ' --credential-file ' + (ConvertTo-RemotePath $config.credential_file) +
                ' --output ' + (ConvertTo-RemotePath $config.remote_output) +
                ' --timezone ' + (ConvertTo-ShellArgument $config.timezone) +
                ' >/dev/null; result=$?; cat ' + (ConvertTo-RemotePath $config.remote_output) + '; exit "$result"'
            $result = Invoke-BoundedProcess $config.ssh_path @('-T', '-oBatchMode=yes', '-oConnectTimeout=10', '-oServerAliveInterval=15', '-oServerAliveCountMax=2', $config.remote_host, $command)
            $summary = $result.Output | ConvertFrom-Json
        }
        else {
            # Export to a separate working file so transport failure cannot damage
            # the desktop's previous complete snapshot.
            $working = $destination + '.collector.json'
            $result = Invoke-BoundedProcess $config.python_path @($config.exporter_path, '--credential-file', $config.credential_file, '--output', $working, '--timezone', $config.timezone)
            $summary = Get-Content -LiteralPath $working -Raw | ConvertFrom-Json
        }
        if (-not (Test-Summary $summary)) { throw 'Collector returned an invalid summary.' }
        if ($result.ExitCode -ne 0 -and $summary.source.status -ne 'error') { throw 'Collector failed before writing a current result.' }
        $summary | Add-Member -NotePropertyName '_sync_source_id' -NotePropertyValue $sourceId -Force
        Write-AtomicSummary $destination $summary
        if ($summary.source.status -eq 'ok') {
            $exitCode = 0
            Write-Output 'OpenRouter account spend synced.'
        }
        else { Write-Output 'OpenRouter account spend unavailable; last-good data remains marked stale.' }
    }
    catch {
        if ((Test-Summary $previous) -and $previous._sync_source_id -eq $sourceId -and $previous.generated_at) {
            $summary = $previous
            $summary.source.status = 'error'
        }
        else {
            $summary = @{ schema_version = 1; scope = 'openrouter_account'; currency = 'USD'; timezone = $config.timezone; source = @{ status = 'error' }; _sync_source_id = $sourceId }
        }
        Write-AtomicSummary $destination $summary
        Write-Output 'OpenRouter account spend sync failed; no fresh totals were recorded.'
    }
}
catch { Write-Output 'OpenRouter account spend sync could not start; check its configuration and destination access.' }
finally {
    if ($owned) { $mutex.ReleaseMutex() }
    if ($null -ne $mutex) { $mutex.Dispose() }
}
exit $exitCode
