# Run as the Windows user whose Claude Code login the relay should read.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [ValidateRange(1, 65535)][int]$Port = 5055,
    [string]$TokenFile,
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
$taskName = 'Claude Usage Relay'
if ($Remove) {
    $existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($existing) {
        Stop-ScheduledTask -TaskName $taskName
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    Write-Output 'Relay startup disabled. App and token files have been preserved.'
    exit
}
$exe = (Resolve-Path -LiteralPath $ExecutablePath).Path
if ([IO.Path]::GetFileName($exe) -ne 'ClaudeUsage.exe') { throw 'Expected ClaudeUsage.exe.' }
if (-not $TokenFile) { $TokenFile = Join-Path $env:LOCALAPPDATA 'ClaudeUsageRelay\relay-token.txt' }
$TokenFile = [IO.Path]::GetFullPath($TokenFile)
if (-not (Test-Path -LiteralPath $TokenFile)) {
    $parent = Split-Path -Parent $TokenFile
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $random = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($random) } finally { $rng.Dispose() }
    [IO.File]::WriteAllText($TokenFile, [Convert]::ToBase64String($random))
}
if ([string]::IsNullOrWhiteSpace([IO.File]::ReadAllText($TokenFile))) { throw 'Relay token file is empty.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$acl = New-Object Security.AccessControl.FileSecurity
$acl.SetAccessRuleProtection($true, $false)
$acl.SetOwner($identity.User)
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity.User, 'FullControl', 'Allow')))
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule('SYSTEM', 'FullControl', 'Allow')))
Set-Acl -LiteralPath $TokenFile -AclObject $acl
$action = New-ScheduledTaskAction -Execute $exe -Argument "--relay --relay-port $Port --relay-token-file `"$TokenFile`"" -WorkingDirectory (Split-Path -Parent $exe)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity.Name
$principal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Limited
$options = New-ScheduledTaskSettingsSet -StartWhenAvailable -RestartCount 100 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $options -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
Write-Output "Relay startup registered for $($identity.Name). Token file: $TokenFile"
Write-Output 'The relay runs after Windows sign-in and restarts after failure. No terminal needs to stay open.'
