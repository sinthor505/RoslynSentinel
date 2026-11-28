<#
.SYNOPSIS
    Registers (or removes) the scheduled task RoslynSentinel-NightlyFullRun (plan_nightly_full_test_run, Step 2).

.DESCRIPTION
    The task runs scripts\Invoke-NightlyFullRun.ps1 under pwsh every day at -Time, as the current user with an S4U
    logon (no stored password) and RunLevel Limited. StartWhenAvailable catches up a missed night, the task is
    limited to 3 hours and a new run is not started while one is still running (IgnoreNew).

    Registering needs an elevated PowerShell (Run as administrator). Nothing runs until the owner registers it.

.PARAMETER Time
    Daily start time, HH:mm (default 06:00).

.PARAMETER Unregister
    Removes the task instead of registering it.
#>
param(
    [string]$Time = '06:00',
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'
$taskName = 'RoslynSentinel-NightlyFullRun'

if ($Unregister) {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Write-Host "Unregistered scheduled task $taskName."
    }
    else {
        Write-Host "Scheduled task $taskName is not registered; nothing to remove."
    }
    exit 0
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$nightlyScript = Join-Path $repoRoot 'scripts\Invoke-NightlyFullRun.ps1'
$startAt = [DateTime]::ParseExact($Time, 'HH:mm', [Globalization.CultureInfo]::InvariantCulture)

$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType S4U -RunLevel Limited
$action = New-ScheduledTaskAction -Execute 'pwsh.exe' -Argument ('-NoProfile -File "' + $nightlyScript + '"')
$trigger = New-ScheduledTaskTrigger -Daily -At $startAt
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 3) `
    -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal `
    -Settings $settings -Force | Out-Null

Write-Host "Registered scheduled task $taskName for $env:USERDOMAIN\$env:USERNAME (S4U, RunLevel Limited)."
Write-Host "  Runs daily at $($startAt.ToString('HH:mm')): pwsh.exe -NoProfile -File `"$nightlyScript`""
Write-Host '  StartWhenAvailable on, time limit 3 hours, MultipleInstances IgnoreNew.'
Write-Host 'Registering a task needs an elevated shell (Run as administrator); re-run from one if this was refused.'
exit 0
