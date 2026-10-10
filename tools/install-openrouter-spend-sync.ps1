# Explicit, opt-in setup. Dot-sourcing this script never registers or starts a task.
[CmdletBinding()]
param(
    [ValidateSet('Remote', 'Local')][string]$Mode = 'Remote',
    [string]$RemoteHost,
    [Parameter(Mandatory = $true)][string]$CredentialFile,
    [string]$PythonPath,
    [string]$SshPath = 'ssh.exe',
    [string]$RemoteExporterPath = '~/.local/share/claude-usage/openrouter-spend-export.py',
    [string]$RemoteOutput = '~/.local/state/claude-usage/openrouter-spend.v1.json',
    [string]$Timezone = 'Europe/London',
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'ClaudeUsage\openrouter-sync'),
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'ClaudeUsage\openrouter-spend.v1.json'),
    [switch]$NoStart
)

if ($MyInvocation.InvocationName -eq '.') { return }
$ErrorActionPreference = 'Stop'
if ($Mode -eq 'Remote' -and $RemoteHost -notmatch '^[A-Za-z0-9][A-Za-z0-9_.@-]*$') { throw 'Supply an explicit SSH host alias with -RemoteHost.' }
if (-not $PythonPath) { $PythonPath = if ($Mode -eq 'Remote') { 'python3' } else { 'python.exe' } }
if ($Mode -eq 'Local') {
    $CredentialFile = (Resolve-Path -LiteralPath $CredentialFile).Path
    $PythonPath = (Get-Command $PythonPath -ErrorAction Stop).Source
}
else { $SshPath = (Get-Command $SshPath -ErrorAction Stop).Source }
$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory)
$Destination = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
$backupDirectory = Join-Path $InstallDirectory ('backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
foreach ($name in @('sync-openrouter-spend.ps1', 'openrouter-spend-export.py', 'config.json', 'run-hidden.vbs')) {
    $existing = Join-Path $InstallDirectory $name
    if (Test-Path -LiteralPath $existing) {
        New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
        Copy-Item -LiteralPath $existing -Destination (Join-Path $backupDirectory $name)
    }
}
foreach ($name in @('sync-openrouter-spend.ps1', 'openrouter-spend-export.py')) {
    $source = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot $name)).Path
    $target = Join-Path $InstallDirectory $name
    if (-not $source.Equals($target, [StringComparison]::OrdinalIgnoreCase)) { Copy-Item -LiteralPath $source -Destination $target -Force }
}
$configPath = Join-Path $InstallDirectory 'config.json'
$config = [ordered]@{
    _comment = 'OpenRouter account-wide analytics only. This file stores paths and connection settings, never API credentials. Refresh every five minutes.'
    schema_version = 1
    mode = $Mode
    remote_host = $RemoteHost
    credential_file = $CredentialFile
    python_path = $PythonPath
    ssh_path = $SshPath
    exporter_path = if ($Mode -eq 'Remote') { $RemoteExporterPath } else { Join-Path $InstallDirectory 'openrouter-spend-export.py' }
    remote_output = $RemoteOutput
    destination = $Destination
    timezone = $Timezone
}
[IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
# WScript supplies a genuinely hidden process and propagates the collector's
# exit code, so Task Scheduler can report failure without a flashing terminal.
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$scriptPath = Join-Path $InstallDirectory 'sync-openrouter-spend.ps1'
$command = '"' + $powershell + '" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $scriptPath + '" -ConfigPath "' + $configPath + '"'
$helper = 'Set shell = CreateObject("WScript.Shell")' + "`r`n" + 'WScript.Quit shell.Run("' + $command.Replace('"', '""') + '", 0, True)' + "`r`n"
$helperPath = Join-Path $InstallDirectory 'run-hidden.vbs'
[IO.File]::WriteAllText($helperPath, $helper, (New-Object Text.UTF8Encoding $false))
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
# All launcher/config paths are absolute, so no task working directory is needed.
$action = New-ScheduledTaskAction -Execute (Join-Path $env:SystemRoot 'System32\wscript.exe') -Argument ('//B //Nologo "' + $helperPath + '"')
$periodic = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
$logon = New-ScheduledTaskTrigger -AtLogOn -User $identity.Name
$principal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 4) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$taskName = 'ClaudeUsage OpenRouter Account Spend Sync'
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($periodic, $logon) -Principal $principal -Settings $settings -Force | Out-Null
if (-not $NoStart) { Start-ScheduledTask -TaskName $taskName }
Write-Output "OpenRouter account spend sync registered for $($identity.Name)."
Write-Output "Summary: $Destination"
Write-Output 'The five-minute task runs invisibly after sign-in. Existing runtime files and configuration were backed up before replacement.'
