# Regression harness for the source-clone self-heal in roslynsentinel-instances-common.ps1
# (Repair-RoslynSentinelSourceClone, Sync-RoslynSentinelSource, ConvertTo-PullErrorText).
#
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\roslynsentinel-instances-common.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Plain assert script, same style as .claude/hooks/*.Tests.ps1. Every repo here is a THROWAWAY created under
# the temp folder (an origin plus a clone of it standing in for <root>\source); the dev repo and the real
# RoslynSentinelInstances folder are never touched.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'roslynsentinel-instances-common.ps1')

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

function Invoke-Git([string]$Dir, [string[]]$GitArgs) {
    $r = Invoke-NativeCaptured -File 'git' -Arguments (@('-C', $Dir, '-c', 'user.name=t', '-c', 'user.email=t@example.invalid', '-c', 'core.autocrlf=false') + $GitArgs)
    if ($r.ExitCode -ne 0) { throw "git $($GitArgs -join ' ') failed in ${Dir}: $($r.Output)" }
    return $r.Output.Trim()
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-heal-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

# Builds origin (one commit on master) + a clone of it, then advances origin by one commit so the clone is behind.
# Returns { Origin; Clone; NewSha }.
function New-Fixture([string]$Name) {
    $work = Join-Path $tempRoot $Name
    $origin = Join-Path $work 'origin'
    $clone = Join-Path $work 'source'
    New-Item -ItemType Directory -Path $origin -Force | Out-Null
    $null = Invoke-Git $origin @('init', '-q', '-b', 'master')
    Set-Content -LiteralPath (Join-Path $origin 'a.txt') -Value 'one'
    $null = Invoke-Git $origin @('add', '--', 'a.txt')
    $null = Invoke-Git $origin @('commit', '-q', '-m', 'one')
    $null = Invoke-Git $work @('clone', '-q', '--branch', 'master', '--', $origin, $clone)
    Set-Content -LiteralPath (Join-Path $origin 'a.txt') -Value 'two'
    $null = Invoke-Git $origin @('commit', '-q', '-a', '-m', 'two')
    [pscustomobject]@{ Origin = $origin; Clone = $clone; NewSha = (Invoke-Git $origin @('rev-parse', 'HEAD')) }
}

function Set-LockAge([string]$Clone, [int]$Minutes) {
    $lock = Join-Path $Clone '.git\index.lock'
    Set-Content -LiteralPath $lock -Value ''
    (Get-Item -LiteralPath $lock -Force).LastWriteTimeUtc = [DateTime]::UtcNow.AddMinutes(-$Minutes)
}

try {
    # --- Repair: stale lock + half-applied tracked file ---
    $f = New-Fixture 'stale'
    Set-LockAge $f.Clone 20
    Set-Content -LiteralPath (Join-Path $f.Clone 'a.txt') -Value 'half-applied'
    $logged = New-Object System.Collections.Generic.List[string]
    $heal = Repair-RoslynSentinelSourceClone -SourceDir $f.Clone -Log { param($m) $logged.Add($m) }
    Assert-That ($heal.Healed -and -not $heal.FreshLock) 'Repair: stale lock + dirty tree reports Healed'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $f.Clone '.git\index.lock'))) 'Repair: stale lock removed'
    Assert-That ((Get-Content -LiteralPath (Join-Path $f.Clone 'a.txt') -Raw).Trim() -eq 'one') 'Repair: tracked file reset to HEAD'
    Assert-That ($heal.Actions.Count -eq 2 -and $logged.Count -ge 2) 'Repair: both actions taken and logged'

    # --- Repair: fresh lock heals nothing ---
    $f = New-Fixture 'fresh'
    Set-LockAge $f.Clone 1
    Set-Content -LiteralPath (Join-Path $f.Clone 'a.txt') -Value 'maybe-mid-pull'
    $heal = Repair-RoslynSentinelSourceClone -SourceDir $f.Clone
    Assert-That ($heal.FreshLock -and -not $heal.Healed) 'Repair: fresh lock reports FreshLock and no heal'
    Assert-That (Test-Path -LiteralPath (Join-Path $f.Clone '.git\index.lock')) 'Repair: fresh lock left in place'
    Assert-That ((Get-Content -LiteralPath (Join-Path $f.Clone 'a.txt') -Raw).Trim() -eq 'maybe-mid-pull') 'Repair: fresh lock leaves the tree alone'

    # --- Repair: threshold is a parameter ---
    $f = New-Fixture 'threshold'
    Set-LockAge $f.Clone 3
    $heal = Repair-RoslynSentinelSourceClone -SourceDir $f.Clone -LockMaxAge ([TimeSpan]::FromMinutes(2))
    Assert-That ($heal.Healed -and -not (Test-Path -LiteralPath (Join-Path $f.Clone '.git\index.lock'))) 'Repair: lock older than a custom threshold is removed'

    # --- Repair: clean clone, no lock ---
    $f = New-Fixture 'clean'
    $heal = Repair-RoslynSentinelSourceClone -SourceDir $f.Clone
    Assert-That (-not $heal.Healed -and -not $heal.FreshLock) 'Repair: clean clone changes nothing'

    # --- Repair: refuses a folder that is not a clone ---
    $notClone = Join-Path $tempRoot 'not-a-clone'
    New-Item -ItemType Directory -Path $notClone -Force | Out-Null
    $heal = Repair-RoslynSentinelSourceClone -SourceDir $notClone
    Assert-That (-not $heal.Healed) 'Repair: folder without .git is left alone'

    # --- Sync: stale lock + dirty tree, pull heals and succeeds ---
    $f = New-Fixture 'sync-stale'
    Set-LockAge $f.Clone 20
    Set-Content -LiteralPath (Join-Path $f.Clone 'a.txt') -Value 'half-applied'
    $sync = Sync-RoslynSentinelSource -SourceDir $f.Clone -DevRepo $f.Origin -Branch 'master'
    Assert-That ($sync.PullOk -and $sync.Sha -eq $f.NewSha) 'Sync: stale lock healed, pull retried, served the new HEAD'

    # --- Sync: fresh lock, no heal, no throw, failure reported ---
    $f = New-Fixture 'sync-fresh'
    Set-LockAge $f.Clone 1
    $sync = Sync-RoslynSentinelSource -SourceDir $f.Clone -DevRepo $f.Origin -Branch 'master'
    Assert-That (-not $sync.PullOk -and $sync.PullError -and $sync.Sha -ne $f.NewSha) 'Sync: fresh lock -> PullOk false with an error, stays on old HEAD, does not throw'
    Assert-That (Test-Path -LiteralPath (Join-Path $f.Clone '.git\index.lock')) 'Sync: fresh lock still there after the failed sync'

    # --- Sync: clean clone, ordinary pull ---
    $f = New-Fixture 'sync-ok'
    $sync = Sync-RoslynSentinelSource -SourceDir $f.Clone -DevRepo $f.Origin -Branch 'master'
    Assert-That ($sync.PullOk -and $sync.Sha -eq $f.NewSha) 'Sync: ordinary pull still works'

    # --- ConvertTo-PullErrorText ---
    $text = ConvertTo-PullErrorText ("line one`r`nline two " + ([string][char]0x00e9) + (' y' * 400))
    Assert-That ($text.Length -le 300 -and $text -notmatch '[^\x20-\x7E]') 'PullErrorText: flattened, ASCII, capped at 300'
    Assert-That ($null -eq (ConvertTo-PullErrorText '  ')) 'PullErrorText: blank in -> null out'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) FAILED"; exit 1 }
Write-Host 'All passed.'
exit 0
