# Visibility of the eval queue to other sessions: the 'hosts' command, the status filters -Session and -ActiveOnly,
# and the live-session warning at submit (plan_eval_host_table_and_liveness, Step 13).
#
# Run:  pwsh -NoProfile -File scripts\roslynsentinel-evalqueue.Visibility.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# No model and no endpoint is probed for real: the table entries point at closed loopback ports, and the submits use
# -NoStart so no worker runs. Throwaway repo and queue root (see the harness); session folders are temp folders.

. (Join-Path $PSScriptRoot 'roslynsentinel-evalqueue.TestHarness.ps1')

# A submit that keeps its warnings: returns the queued jobs and leaves the warning messages in $script:warned.
$missingSessions = Join-Path $tempRoot 'sessions-missing'
function Submit-Keeping([hashtable]$Params) {
    $merged = @{ SessionsDir = $missingSessions }
    foreach ($key in $Params.Keys) { $merged[$key] = $Params[$key] }
    $all = @(Submit $merged 3>&1)
    $script:warned = @($all | Where-Object { $_ -is [System.Management.Automation.WarningRecord] } | ForEach-Object { [string]$_.Message })
    $reply = @($all | Where-Object { $_ -isnot [System.Management.Automation.WarningRecord] })
    return @($reply | ForEach-Object { $_.queued } | Where-Object { $_ })
}

try {
    $closedA = "http://127.0.0.1:$(Get-FreePort)/v1"
    $closedB = "http://127.0.0.1:$(Get-FreePort)/v1"
    $liveDir = Join-Path $tempRoot 'sessions-live'
    $emptyDir = Join-Path $tempRoot 'sessions-empty'
    New-Item -ItemType Directory -Force -Path $liveDir, $emptyDir | Out-Null
    [ordered]@{ name = 'live-one'; pid = $PID } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $liveDir 'live-one.json') -Encoding utf8

    New-Queue
    $null = Write-TestHostTable @(
        (New-HostEntry 'V1' 'vis' $closedA 'llama-server' 'vsfx1' $true),
        (New-HostEntry 'V2' 'vis' $closedB 'lmstudio' 'vsfx2' $false))

    # --- hosts: one row per table entry, each with the queue state of its machine ---------------------------------
    # A Queued job written by hand in the machine folder: no worker, nothing running.
    $handId = '20261120-090000-001'
    $jobsDir = Join-Path $script:queueRoot 'vis\jobs'
    New-Item -ItemType Directory -Force -Path $jobsDir | Out-Null
    [ordered]@{ id = $handId; state = 'Queued'; priority = 5; machine = 'vis'; host = 'V1'; baseUrl = $closedA; backend = 'llama-server'
        suffix = 'vsfx1'; test = 'Quick'; model = 'm1'; session = 'hand'; sessions = @('hand'); purpose = 'hand-written'
        submittedUtc = [datetime]::UtcNow.ToString('o') } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $jobsDir "$handId.json") -Encoding utf8

    $rows = @(Invoke-Queue 'hosts' @{ Json = $true })
    Assert-That ($script:lastExit -eq 0) 'Hosts -Json: exit code 0'
    $aliasText = ((@($rows.Alias | Sort-Object)) -join ',')
    Assert-That ($rows.Count -eq 2 -and $aliasText -eq 'V1,V2') 'Hosts -Json: one row per table entry'
    $queuedRows = @($rows | Where-Object { $_.Queue.QueuedCount -eq 1 -and @($_.Queue.Queued).Count -eq 1 -and $_.Queue.Queued[0].Id -eq $handId })
    Assert-That ($queuedRows.Count -eq 2) 'Hosts -Json: both endpoint rows of machine vis show the hand-queued job'
    Assert-That (@($queuedRows | Where-Object { @($_.Queue.Queued[0].Sessions) -contains 'hand' -and @($_.Queue.Active).Count -eq 0 }).Count -eq 2) 'Hosts -Json: the queued job lists its session and no job is Active'
    Assert-That (@($rows | Where-Object { $_.Reachable }).Count -eq 0) 'Hosts -Json: closed endpoints are reported unreachable'

    # --- status filters: three jobs of three sessions, the third one cancelled ----------------------------------
    $idA = (@(Submit-Keeping @{ HostAddress = 'V1'; Test = 'Quick'; Session = 'a' }))[0].id
    $idB = (@(Submit-Keeping @{ HostAddress = 'V1'; Test = 'Slow'; Session = 'b' }))[0].id
    $idC = (@(Submit-Keeping @{ HostAddress = 'V1'; Test = 'Fail'; Session = 'c' }))[0].id
    $null = Invoke-Queue 'cancel' @{ Id = $idC; Session = 'c' }
    Assert-That ((Get-JobById $idC).state -eq 'Cancelled') 'Setup: the job of session c is Cancelled'

    $idsSessionA = @(@(Invoke-Queue 'status' @{ Json = $true; Session = 'a' }) | ForEach-Object { $_.jobs } | ForEach-Object { $_.id })
    Assert-That ((@($idsSessionA) -join ',') -eq $idA) 'Status -Session a -Json: only the job of session a'

    $idsActive = @(@(Invoke-Queue 'status' @{ Json = $true; ActiveOnly = $true }) | ForEach-Object { $_.jobs } | ForEach-Object { $_.id })
    $expectActive = ((@($idA, $idB, $handId) | Sort-Object) -join ',')
    $gotActive = ((@($idsActive) | Sort-Object) -join ',')
    Assert-That ($gotActive -eq $expectActive) 'Status -ActiveOnly -Json: Queued jobs kept, the cancelled job dropped'

    $idsBoth = @(@(Invoke-Queue 'status' @{ Json = $true; Session = 'a'; ActiveOnly = $true }) | ForEach-Object { $_.jobs } | ForEach-Object { $_.id })
    Assert-That ((@($idsBoth) -join ',') -eq $idA) 'Status -Session a -ActiveOnly -Json: both filters apply'

    $none = @(Invoke-Queue 'status' @{ Json = $true; Session = 'nobody'; ActiveOnly = $true })
    Assert-That ($none.Count -eq 0) 'Status -Session nobody -ActiveOnly -Json: an empty machine list'

    $textAll = (@(Invoke-Queue 'status' @{}) -join "`n")
    Assert-That ($textAll -match [regex]::Escape($idC)) 'Status text without filters: the cancelled job is listed (baseline)'

    $textA = (@(Invoke-Queue 'status' @{ Session = 'a' }) -join "`n")
    Assert-That ($textA -match [regex]::Escape($idA) -and $textA -notmatch [regex]::Escape($idB) -and $textA -notmatch [regex]::Escape($idC)) 'Status -Session a text: only the job of session a'

    $textActive = (@(Invoke-Queue 'status' @{ ActiveOnly = $true }) -join "`n")
    Assert-That ($textActive -match [regex]::Escape($idA) -and $textActive -match [regex]::Escape($handId) -and $textActive -notmatch [regex]::Escape($idC)) 'Status -ActiveOnly text: active jobs listed, the cancelled job not'
    Assert-That ($textActive -notmatch '(?m)^\s+ended\s') 'Status -ActiveOnly text: no recent-jobs lines'

    $textNone = (@(Invoke-Queue 'status' @{ Session = 'nobody'; ActiveOnly = $true }) -join "`n")
    Assert-That ($textNone -match 'no matching jobs') 'Status -Session nobody -ActiveOnly text: says no matching jobs'

    # --- submit: a warning for a session no live Claude Code session carries, never a refusal ------------------
    $unknown = @(Submit-Keeping @{ HostAddress = 'V2'; Test = 'Quick'; Session = 'nobody-here'; SessionsDir = $liveDir })
    $unknownWarnings = @($script:warned | Where-Object { $_ -match 'nobody-here' -and $_ -match 'live-one' })
    Assert-That ($unknownWarnings.Count -eq 1) 'Submit with an unknown -Session: one warning names it and the live session'
    Assert-That ($unknown.Count -eq 1 -and (Get-JobById $unknown[0].id).state -eq 'Queued') 'Submit with an unknown -Session: the job is still queued'

    $null = Submit-Keeping @{ HostAddress = 'V2'; Test = 'Slow'; Session = 'live-one'; SessionsDir = $liveDir }
    Assert-That (@($script:warned | Where-Object { $_ -match 'live sessions' }).Count -eq 0) 'Submit with a live -Session: no warning'

    $null = Submit-Keeping @{ HostAddress = 'V2'; Test = 'NoResult'; Session = 'x-empty'; SessionsDir = $emptyDir }
    Assert-That (@($script:warned | Where-Object { $_ -match 'live sessions' }).Count -eq 0) 'Submit with an empty session folder: no warning'

    $null = Submit-Keeping @{ HostAddress = 'V2'; Test = 'Fail'; Session = 'x-missing'; SessionsDir = (Join-Path $tempRoot 'sessions-none') }
    Assert-That (@($script:warned | Where-Object { $_ -match 'live sessions' }).Count -eq 0) 'Submit with a missing session folder: no warning'
}
finally {
    Close-TestHarness
}

Complete-TestRun
