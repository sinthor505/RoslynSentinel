<#
.SYNOPSIS
    One-time owner action per clone: points git at .githooks so that commits, pulls and rebases start a
    background server prebuild (scripts\Start-ServerPrebuild.ps1). Windows PowerShell 5.1.

.DESCRIPTION
    Install (default) sets two values in this clone's .git\config:
      core.hooksPath                 .githooks
      roslynsentinel.instancesRoot   <InstancesRoot>
    The hooks are .githooks\post-commit, post-merge and post-rewrite. Each one runs the starter, which
    returns at once and never fails or delays a git operation. Run this once per clone; it changes git
    config, so do it deliberately.

    Uninstall (-Uninstall) unsets both values; the hooks then do nothing.

.PARAMETER InstancesRoot
    Prod-instances root, for example C:\Users\Administrator\RoslynSentinelInstances. Defaults to
    $env:ROSLYNSENTINEL_INSTANCES_ROOT. Required for install.

.PARAMETER Uninstall
    Remove both settings instead of setting them.
#>
[CmdletBinding()]
param(
    [string]$InstancesRoot,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot

if ($Uninstall) {
    foreach ($name in @('core.hooksPath', 'roslynsentinel.instancesRoot')) {
        & git -C $repo config --unset $name 2>$null
        if ($LASTEXITCODE -eq 0) { Write-Output "Unset $name" }
        else { Write-Output "$name was not set (nothing to unset)" }
    }
    exit 0
}

if (-not $InstancesRoot) { $InstancesRoot = $env:ROSLYNSENTINEL_INSTANCES_ROOT }
if (-not $InstancesRoot) {
    Write-Error 'No instances root: pass -InstancesRoot <path> or set ROSLYNSENTINEL_INSTANCES_ROOT.'
    exit 1
}
if (-not (Test-Path -LiteralPath $InstancesRoot)) {
    Write-Warning "Instances root does not exist yet: $InstancesRoot"
}

& git -C $repo config core.hooksPath .githooks
if ($LASTEXITCODE -ne 0) { Write-Error 'git config core.hooksPath failed.'; exit 1 }
& git -C $repo config roslynsentinel.instancesRoot $InstancesRoot
if ($LASTEXITCODE -ne 0) { Write-Error 'git config roslynsentinel.instancesRoot failed.'; exit 1 }

Write-Output ("core.hooksPath = {0}" -f (& git -C $repo config --get core.hooksPath))
Write-Output ("roslynsentinel.instancesRoot = {0}" -f (& git -C $repo config --get roslynsentinel.instancesRoot))
exit 0
