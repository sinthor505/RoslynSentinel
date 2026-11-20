# Regression harness for roslynsentinel-evalqueue.ps1 (the model-eval run queue).
#
# Run:  pwsh -NoProfile -File scripts\roslynsentinel-evalqueue.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Plain assert script, same style as roslynsentinel-instances-common.Tests.ps1. No model and no LM Studio
# host is used: every job runs a stub eval script inside a THROWAWAY git repo under the temp folder, with
# its own queue root, so the real queue and the dev repo are never touched.

$ErrorActionPreference = 'Stop'
$queueScript = Join-Path $PSScriptRoot 'roslynsentinel-evalqueue.ps1'
. (Join-Path $PSScriptRoot 'roslynsentinel-evalcheckout.ps1')

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-evalqueue-tests-" + [guid]::NewGuid().ToString('N'))
$env:ROSLYNSENTINEL_EVALQUEUE_CHECKOUTS_ROOT = Join-Path $tempRoot 'checkouts'
$repo = Join-Path $tempRoot 'repo'
$stub = Join-Path $repo 'scripts\stub-eval.ps1'
New-Item -ItemType Directory -Path (Join-Path $repo 'scripts') -Force | Out-Null

function Invoke-Git([string[]]$GitArgs) {
    $out = & git -C $repo -c user.name=t -c user.email=t@example.invalid -c core.autocrlf=false @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed: $out" }
}

# Stand-in for roslynsentinel-modeleval.ps1: same parameter names, writes a run folder where the real one does.
Set-Content -LiteralPath $stub -Value @'
param(
    [Parameter(Mandatory)][string]$HostAddress,
    [Parameter(Mandatory)][ValidateSet('Quick', 'Slow', 'NoResult', 'Fail')][string[]]$Test,
    [int]$Repeats = 1,
    [string]$Model,
    [int]$Samples = 1,
    [switch]$Clean,
    [string]$ResumeTranscript
)
$root = Split-Path $PSScriptRoot -Parent
$name = $Test[0]
Write-Host "stub run $name"
if ($name -eq 'NoResult') { exit 1 }
if ($name -eq 'Slow') { Start-Sleep -Seconds 120 } else { Start-Sleep -Milliseconds 300 }
$resultsBase = if ($env:ROSLYNSENTINEL_RESULTS_ROOT) { $env:ROSLYNSENTINEL_RESULTS_ROOT } else { Join-Path $root 'ModelTestingResults' }
$dir = Join-Path $resultsBase "$HostAddress\$name\$([datetime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$marker = ([IO.File]::ReadAllText((Join-Path $root 'Lib.cs'))).Replace("`r`n", "`n").Trim()
Set-Content -LiteralPath (Join-Path $dir 'results.txt') -Value "flag=$env:ROSLYNSENTINEL_TEST_FLAG job=$env:ROSLYNSENTINEL_EVALQUEUE_JOB samples=$Samples model=$Model marker=$marker"
if ($name -eq 'Fail') { exit 1 }
exit 0
'@
Set-Content -LiteralPath (Join-Path $repo 'Lib.cs') -Value 'class Lib { }'
Invoke-Git @('init', '-q', '-b', 'master')
Invoke-Git @('add', '--', 'Lib.cs', 'scripts/stub-eval.ps1')
Invoke-Git @('commit', '-q', '-m', 'one')

$script:queueCount = 0
function New-Queue {
    $script:queueCount++
    $script:queueRoot = Join-Path $tempRoot "queue$script:queueCount"
}

# Runs one queue command against the throwaway repo, stub and current queue root. Text lines come back as-is;
# with Json = $true the parsed object comes back. $script:lastExit holds the command's exit code.
function Invoke-Queue([string]$Command, [hashtable]$Params = @{}) {
    $all = @{ QueueRoot = $script:queueRoot; RepoRoot = $repo; EvalScript = $stub; SkipHostCheck = $true } + $Params
    $global:LASTEXITCODE = 0
    $out = & $queueScript $Command @all
    $script:lastExit = $LASTEXITCODE
    if ($Params['Json'] -and $script:lastExit -ne 2) { return (($out -join "`n") | ConvertFrom-Json) }
    return $out
}

function Submit([hashtable]$Params) {
    $base = @{ HostAddress = 'stub'; Model = 'm1'; Session = 's1'; Purpose = 'test'; NoStart = $true; Json = $true }
    foreach ($key in $Params.Keys) { $base[$key] = $Params[$key] }
    return Invoke-Queue 'submit' $base
}

function Get-AllJobs { return @((Invoke-Queue 'status' @{ Json = $true })[0].jobs) }
function Get-JobById([string]$JobId) { return Get-AllJobs | Where-Object { $_.id -eq $JobId } }
function Run-Worker { $null = Invoke-Queue 'worker' @{ HostAddress = 'stub' } }

try {
    # --- submit validation ---------------------------------------------------------------------------
    New-Queue
    $out = Invoke-Queue 'submit' @{ HostAddress = 'stub'; Test = 'Nope'; Model = 'm'; Session = 's'; Purpose = 'p'; NoStart = $true }
    Assert-That ($script:lastExit -eq 2 -and "$out" -match 'not a test alias') 'Submit: unknown test alias refused, valid ones named'
    $out = Invoke-Queue 'submit' @{ HostAddress = 'stub'; Test = 'Quick'; Session = 's'; Purpose = 'p'; NoStart = $true }
    Assert-That ($script:lastExit -eq 2 -and "$out" -match 'needs -Model') 'Submit: missing -Model refused'
    $out = Submit @{ Test = 'Quick'; EvalArgs = '-Bogus 1'; Json = $false }
    Assert-That ($script:lastExit -eq 2 -and "$out" -match 'not a parameter') 'Submit: unknown parameter in -EvalArgs refused at submit'
    $out = Submit @{ Test = 'Quick'; EvalArgs = '-Model other'; Json = $false }
    Assert-That ($script:lastExit -eq 2 -and "$out" -match 'set by the queue') 'Submit: queue-owned parameter in -EvalArgs refused'
    $out = Submit @{ Test = 'Quick'; EvalArgs = '-Samples 3; calc'; Json = $false }
    Assert-That ($script:lastExit -eq 2 -and "$out" -match 'not allowed') 'Submit: statement separator in -EvalArgs refused'
    $out = Submit @{ Test = 'Quick'; EvalArgs = '-Clean'; Json = $false }
    Assert-That ($script:lastExit -eq 2 -and "$out" -match '-Clean is not allowed') 'Submit: -Clean refused'
    $out = Submit @{ Test = 'Quick'; EvalArgs = '-ResumeTranscript relative\t.json'; Json = $false }
    Assert-That ($script:lastExit -eq 2 -and "$out" -match 'absolute path') 'Submit: relative -ResumeTranscript refused'
    $out = Invoke-Queue 'status'
    Assert-That ("$out" -match 'No eval queue exists yet') 'Submit: refused requests queued nothing'

    # --- dedup ---------------------------------------------------------------------------------------
    New-Queue
    $first = Submit @{ Test = 'Quick'; Repeats = 2 }
    Assert-That (@($first.queued).Count -eq 2 -and @($first.attached).Count -eq 0) 'Dedup: two repeats queue two jobs'
    $second = Submit @{ Test = 'Quick'; Repeats = 3; Session = 's2' }
    Assert-That (@($second.queued).Count -eq 1 -and @($second.attached).Count -eq 2) 'Dedup: same request attaches to both jobs and queues only the third repeat'
    Assert-That ((@($second.attached)[0].sessions -join ',') -eq 's1,s2') 'Dedup: attaching session is recorded on the job'
    $forced = Submit @{ Test = 'Quick'; Repeats = 2; Force = $true }
    Assert-That (@($forced.queued).Count -eq 2) 'Dedup: -Force queues new jobs'
    $otherModel = Submit @{ Test = 'Quick'; Model = 'm2' }
    Assert-That (@($otherModel.queued).Count -eq 1) 'Dedup: a different model is a different run'
    $env:ROSLYNSENTINEL_TEST_FLAG = 'on'
    $otherEnv = Submit @{ Test = 'Quick' }
    Remove-Item Env:\ROSLYNSENTINEL_TEST_FLAG
    Assert-That (@($otherEnv.queued).Count -eq 1) 'Dedup: a different ROSLYNSENTINEL_* variable is a different run'

    # --- cancel a queued job -------------------------------------------------------------------------
    $sharedId = @($first.queued)[0].id
    $out = Invoke-Queue 'cancel' @{ Id = $sharedId; Session = 'stranger' }
    Assert-That ("$out" -match 'not cancelled' -and (Get-JobById $sharedId).state -eq 'Queued') 'Cancel: a session that does not own the job is refused'
    $out = Invoke-Queue 'cancel' @{ Id = $sharedId; Session = 's1' }
    Assert-That ("$out" -match 'continues for s2' -and (Get-JobById $sharedId).state -eq 'Queued') 'Cancel: job stays queued while another session wants it'
    $out = Invoke-Queue 'cancel' @{ Id = $sharedId; Session = 's2' }
    Assert-That ((Get-JobById $sharedId).state -eq 'Cancelled') 'Cancel: job is cancelled when the last session withdraws'

    # --- worker: outcomes, priority order, env, eval args -------------------------------------------------
    New-Queue
    $env:ROSLYNSENTINEL_TEST_FLAG = 'submitter'
    $normal = @((Submit @{ Test = 'Quick'; EvalArgs = '-Samples 3' }).queued)[0]
    $env:ROSLYNSENTINEL_TEST_FLAG = 'worker'
    $urgent = @((Submit @{ Test = 'Fail'; Priority = 1 }).queued)[0]
    $noResult = @((Submit @{ Test = 'NoResult'; Priority = 9 }).queued)[0]
    Run-Worker
    Remove-Item Env:\ROSLYNSENTINEL_TEST_FLAG
    $normalDone = Get-JobById $normal.id
    $urgentDone = Get-JobById $urgent.id
    Assert-That ($normalDone.state -eq 'Finished' -and $normalDone.passed -eq $true -and @($normalDone.runFolders).Count -eq 1) 'Worker: passing run ends Finished with its run folder'
    Assert-That ($urgentDone.state -eq 'Finished' -and $urgentDone.passed -eq $false) 'Worker: failing tests end Finished with passed = false'
    Assert-That ((Get-JobById $noResult.id).state -eq 'Crashed') 'Worker: a run that writes no run folder ends Crashed'
    Assert-That ([datetime]$urgentDone.startedUtc -lt [datetime]$normalDone.startedUtc) 'Worker: priority 1 runs before an earlier priority 5 job'
    $resultText = Get-Content -LiteralPath (Join-Path @($normalDone.runFolders)[0] 'results.txt') -Raw
    Assert-That ($resultText -match 'flag=submitter ') 'Worker: run gets the ROSLYNSENTINEL_* value from submit time, not the worker value'
    Assert-That ($resultText -match "job=$($normal.id) " -and $resultText -match 'samples=3 model=m1') 'Worker: run gets the job id, -EvalArgs and -Model'
    Assert-That (Test-Path -LiteralPath $normalDone.logPath) 'Worker: run output is kept in the job log'
    $again = Submit @{ Test = 'Quick'; EvalArgs = '-Samples 3' }
    Assert-That (@($again.queued).Count -eq 1) 'Dedup: finished run under a different variable value is not reused'
    $null = Invoke-Queue 'wait' @{ Id = $urgent.id }
    Assert-That ($script:lastExit -eq 1) 'Wait: exit code 1 for a finished run with failing tests'
    $null = Invoke-Queue 'wait' @{ Id = $noResult.id }
    Assert-That ($script:lastExit -eq 2) 'Wait: exit code 2 for a run that did not finish'

    # --- dedup against a finished run -----------------------------------------------------------------
    New-Queue
    $ran = @((Submit @{ Test = 'Quick' }).queued)[0]
    Run-Worker
    $reuse = Submit @{ Test = 'Quick'; Session = 's2' }
    Assert-That (@($reuse.queued).Count -eq 0 -and @($reuse.attached)[0].id -eq $ran.id -and @($reuse.attached)[0].state -eq 'Finished') 'Dedup: a finished run on the same tree is reused'
    $out = Invoke-Queue 'wait' @{ Id = $ran.id }
    Assert-That ($script:lastExit -eq 0 -and "$out" -match 'results: ') 'Wait: returns at once for a finished job, exit code 0, with the results folder'
    # One comma-joined string is what 'pwsh -File ... -Id a,b' delivers.
    $out = Invoke-Queue 'wait' @{ Id = "$($ran.id), $($ran.id)" }
    Assert-That ($script:lastExit -eq 0 -and @("$out" | Select-String -Pattern 'results: ' -AllMatches).Matches.Count -eq 2) 'Wait: a comma-joined -Id string is split into job ids'
    $out = Invoke-Queue 'wait' @{ Id = 'no-such-job' }
    Assert-That ($script:lastExit -eq 2 -and "$out" -match "evalqueue error: No job id contains 'no-such-job'") 'Wait: an unknown id prints the error instead of exiting 2 silently'

    # --- orphaned job ----------------------------------------------------------------------------------
    New-Queue
    $orphan = @((Submit @{ Test = 'Quick' }).queued)[0]
    $orphanPath = Join-Path $script:queueRoot "stub\jobs\$($orphan.id).json"
    (Get-Content -LiteralPath $orphanPath -Raw) -replace '"state":\s*"Queued"', '"state": "Running"' | Set-Content -LiteralPath $orphanPath
    $out = Invoke-Queue 'status'
    Assert-That ((Get-JobById $orphan.id).state -eq 'Crashed') 'Orphan: a Running job with no worker is marked Crashed'

    # --- detached worker, cancel while running, time limit ---------------------------------------------
    New-Queue
    $slow = @((Submit @{ Test = 'Slow'; NoStart = $false }).queued)[0]
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and (Get-JobById $slow.id).state -ne 'Running') { Start-Sleep -Milliseconds 500 }
    Assert-That ((Get-JobById $slow.id).state -eq 'Running') 'Detached: submit starts a worker that picks the job up'
    if ((Get-JobById $slow.id).state -ne 'Running') {
        Write-Host "      job: $(Get-JobById $slow.id | ConvertTo-Json -Compress)"
        Write-Host "      worker.log: $(Get-Content -LiteralPath (Join-Path $script:queueRoot 'stub\worker.log') -Raw -ErrorAction SilentlyContinue)"
    }
    $out = Invoke-Queue 'status'
    Assert-That ("$out" -match 'worker running' -and "$out" -match "running  $($slow.id)") 'Status: shows the worker and the running job'
    $null = Invoke-Queue 'cancel' @{ Id = $slow.id; Session = 's1' }
    $null = Invoke-Queue 'wait' @{ Id = $slow.id }
    $slowDone = Get-JobById $slow.id
    Assert-That ($slowDone.state -eq 'Cancelled' -and $slowDone.durationSeconds -lt 60) 'Cancel: a running job is stopped and ends Cancelled'
    Assert-That (-not (Get-Process -Id $slowDone.processId -ErrorAction SilentlyContinue)) 'Cancel: the run process is gone'

    $timed = @((Submit @{ Test = 'Slow'; NoStart = $false; TimeLimitMinutes = 0.05; Force = $true }).queued)[0]
    $null = Invoke-Queue 'wait' @{ Id = $timed.id }
    Assert-That ((Get-JobById $timed.id).state -eq 'TimedOut') 'Time limit: a run past its limit ends TimedOut'

    # A run that writes no run folder within -NoRunFolderMinutes is stuck in SetUp: stopped as Crashed, well before the time limit.
    $stalled = @((Submit @{ Test = 'Slow'; NoStart = $false; NoRunFolderMinutes = 0.05; Force = $true }).queued)[0]
    $null = Invoke-Queue 'wait' @{ Id = $stalled.id }
    $stalledDone = Get-JobById $stalled.id
    Assert-That ($stalledDone.state -eq 'Crashed' -and $stalledDone.reason -like '*SetUp*' -and $stalledDone.durationSeconds -lt 60) 'No run folder: a run stuck before its run folder ends Crashed with a SetUp reason'

    # --- checkout functions (roslynsentinel-evalcheckout.ps1); the repo is clean here -----------------------
    function Get-LfText([string]$Path) { return [IO.File]::ReadAllText($Path).Replace("`r`n", "`n").Trim() }
    $checkoutRoot = Get-EvalCheckoutRoot 3>$null
    Assert-That ($checkoutRoot -eq (Join-Path $tempRoot 'checkouts')) 'Checkout: Get-EvalCheckoutRoot returns the temp checkouts root while the env var is set'
    $headNow = (& git -C $repo rev-parse HEAD).Trim()
    $resolved = Resolve-EvalCommit -RepoRoot $repo -Rev 'HEAD'
    Assert-That ($resolved -match '^[0-9a-f]{40}$' -and $resolved -eq $headNow) 'Checkout: Resolve-EvalCommit HEAD returns the 40-char sha that git rev-parse reports'
    $badRev = $null
    try { Resolve-EvalCommit -RepoRoot $repo -Rev 'no-such-rev' | Out-Null } catch { $badRev = $_.Exception.Message }
    Assert-That ($badRev -match 'no-such-rev') 'Checkout: Resolve-EvalCommit throws on a bad rev and the message names it'

    $sha1 = Resolve-EvalCommit -RepoRoot $repo -Rev 'HEAD'
    Set-Content -LiteralPath (Join-Path $repo 'Lib.cs') -Value 'class Lib { int two; }'
    Invoke-Git @('add', '--', 'Lib.cs')
    Invoke-Git @('commit', '-q', '-m', 'two')
    $sha2 = Resolve-EvalCommit -RepoRoot $repo -Rev 'HEAD'

    $co1 = Get-EvalCheckout -RepoRoot $repo -Sha $sha1 -CheckoutRoot $checkoutRoot
    Assert-That ($co1 -eq (Join-Path $checkoutRoot $sha1.Substring(0, 12))) 'Checkout: checkout of the first commit is <root>\<sha12>'
    Assert-That ((Get-Content -LiteralPath (Join-Path $co1 'complete') -Raw) -match "sha=$sha1") 'Checkout: complete marker records the requested sha'
    Assert-That (Test-Path -LiteralPath (Join-Path $co1 '.git') -PathType Container) 'Checkout: checkout has a real .git directory'
    Assert-That ((& git -C $co1 rev-parse HEAD).Trim() -eq $sha1) 'Checkout: HEAD is detached at the requested commit'
    Assert-That (Test-Path -LiteralPath (Join-Path $co1 'scripts\stub-eval.ps1')) 'Checkout: the stub is in the checkout (committed in the first commit)'
    Assert-That ((Get-LfText (Join-Path $co1 'Lib.cs')) -eq 'class Lib { }') 'Checkout: first commit checkout has the first Lib.cs'
    $co2 = Get-EvalCheckout -RepoRoot $repo -Sha $sha2 -CheckoutRoot $checkoutRoot
    Assert-That ((Get-LfText (Join-Path $co2 'Lib.cs')) -eq 'class Lib { int two; }') 'Checkout: second commit gets its own checkout with the second Lib.cs'

    $dirsBefore = @(Get-ChildItem -LiteralPath $checkoutRoot -Directory).Count
    $lastBefore = (Get-Content -LiteralPath (Join-Path $co1 'lastused') -Raw).Trim()
    $again = Get-EvalCheckout -RepoRoot $repo -Sha $sha1 -CheckoutRoot $checkoutRoot
    $lastAfter = (Get-Content -LiteralPath (Join-Path $co1 'lastused') -Raw).Trim()
    $dirsAfter = @(Get-ChildItem -LiteralPath $checkoutRoot -Directory).Count
    Assert-That ($again -eq $co1 -and $dirsAfter -eq $dirsBefore) 'Checkout: second call for the same sha returns the same path and creates no directory'
    Assert-That ($lastAfter -ge $lastBefore) 'Checkout: second call touches lastused without moving it back'

    # Fake published checkouts (complete + lastused) and partials (<sha12>.<token>), ages set by file times.
    function New-FakeCheckout([string]$Dir, [string]$Sha, [double]$AgeHours) {
        New-Item -ItemType Directory -Path $Dir -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $Dir 'complete'), "sha=$Sha createdUtc=2026-11-01T00:00:00Z")
        $used = Join-Path $Dir 'lastused'
        [IO.File]::WriteAllText($used, '2026-11-01T00:00:00Z')
        [IO.File]::SetLastWriteTimeUtc($used, [DateTime]::UtcNow.AddHours(-$AgeHours))
    }
    function New-FakePartial([string]$Dir, [double]$AgeHours) {
        New-Item -ItemType Directory -Path $Dir -Force | Out-Null
        [IO.Directory]::SetLastWriteTimeUtc($Dir, [DateTime]::UtcNow.AddHours(-$AgeHours))
    }
    $day = [TimeSpan]::FromHours(24)
    $sweepRoot = Join-Path $tempRoot 'sweep'
    $listedDir = Join-Path $sweepRoot '111111111111'
    $youngDir = Join-Path $sweepRoot '222222222222'
    $oldDir = Join-Path $sweepRoot '333333333333'
    $oldPartial = Join-Path $sweepRoot '444444444444.abcdef12'
    $youngPartial = Join-Path $sweepRoot '555555555555.fedcba98'
    New-FakeCheckout $listedDir ('1' * 40) 48
    New-FakeCheckout $youngDir ('2' * 40) 1
    New-FakeCheckout $oldDir ('3' * 40) 48
    New-FakePartial $oldPartial 48
    New-FakePartial $youngPartial 1
    New-Item -ItemType Directory -Path (Join-Path $sweepRoot 'notes') -Force | Out-Null
    $swept = @(Invoke-EvalCheckoutSweep -CheckoutRoot $sweepRoot -KeepShas @('1' * 40) -MinimumAge $day -KeepNewest 0)
    Assert-That (Test-Path -LiteralPath $listedDir) 'Sweep: an old checkout whose sha is listed in -KeepShas is kept'
    Assert-That (Test-Path -LiteralPath $youngDir) 'Sweep: a young checkout is kept'
    Assert-That (-not (Test-Path -LiteralPath $oldDir)) 'Sweep: an old unlisted checkout is removed'
    Assert-That (-not (Test-Path -LiteralPath $oldPartial)) 'Sweep: an old partial is removed'
    Assert-That (Test-Path -LiteralPath $youngPartial) 'Sweep: a young partial is kept'
    Assert-That (Test-Path -LiteralPath (Join-Path $sweepRoot 'notes')) 'Sweep: an unrelated folder is left alone'
    Assert-That (((@($swept) | Sort-Object) -join '|') -eq ((@($oldDir, $oldPartial) | Sort-Object) -join '|')) 'Sweep: the returned list names exactly the removed paths'

    $newestRoot = Join-Path $tempRoot 'sweep-newest'
    $newestOld = Join-Path $newestRoot '666666666666'
    $newestKept = Join-Path $newestRoot '777777777777'
    New-FakeCheckout $newestOld ('6' * 40) 48
    New-FakeCheckout $newestKept ('7' * 40) 30
    $newestSwept = @(Invoke-EvalCheckoutSweep -CheckoutRoot $newestRoot -MinimumAge $day -KeepNewest 1)
    Assert-That ($newestSwept.Count -eq 1 -and $newestSwept[0] -eq $newestOld -and (Test-Path -LiteralPath $newestKept)) 'Sweep: -KeepNewest 1 keeps the most recently used of two old checkouts'
    $missingSwept = @(Invoke-EvalCheckoutSweep -CheckoutRoot (Join-Path $tempRoot 'no-such-root') -MinimumAge $day)
    Assert-That ($missingSwept.Count -eq 0) 'Sweep: a missing root returns nothing'

    # --- tree changed between submit and dispatch (last: it leaves the repo dirty) --------------------------
    New-Queue
    $pinned = @((Submit @{ Test = 'Quick' }).queued)[0]
    Set-Content -LiteralPath (Join-Path $repo 'Lib.cs') -Value 'class Lib { int changed; }'
    Run-Worker
    $pinnedDone = Get-JobById $pinned.id
    Assert-That ($pinnedDone.state -eq 'TreeChanged' -and @($pinnedDone.runFolders).Count -eq 0) 'Tree: a job whose source changed after submit is not run'
    $loose = @((Submit @{ Test = 'Quick'; OnTreeChange = 'RunAnyway' }).queued)[0]
    Set-Content -LiteralPath (Join-Path $repo 'New.cs') -Value 'class New { }'
    Run-Worker
    $looseDone = Get-JobById $loose.id
    Assert-That ($looseDone.state -eq 'Finished' -and $looseDone.testedDirty -eq $true -and $looseDone.testedFingerprint -ne $looseDone.fingerprint) 'Tree: -OnTreeChange RunAnyway runs and records the tree it tested'
    Set-Content -LiteralPath (Join-Path $repo 'notes.md') -Value 'docs only'
    $docOnly = @((Submit @{ Test = 'Quick'; Force = $true }).queued)[0]
    Set-Content -LiteralPath (Join-Path $repo 'notes.md') -Value 'docs changed'
    Run-Worker
    Assert-That ((Get-JobById $docOnly.id).state -eq 'Finished') 'Tree: a change outside source files does not refuse the job'

    # --- roslynsentinel-modeleval.ps1 archive step (the queue's Finished/Crashed verdict reads what it copies) ---
    # Runs the script's own Get-RunLeafDirs / Copy-NewRunDirectories (lifted by AST) on a fake model-eval tree.
    # ShippingKindStep* tests write model-eval\<TestName>(<case>)\<timestamp>\, not model-eval\<TestName>\<timestamp>\.
    $evalAst = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'roslynsentinel-modeleval.ps1'), [ref]$null, [ref]$null)
    $archiveFunctions = $evalAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in 'Get-RunLeafDirs', 'Copy-NewRunDirectories' }, $true)
    Assert-That ($archiveFunctions.Count -eq 2) 'Archive: modeleval script defines Get-RunLeafDirs and Copy-NewRunDirectories'
    function Test-Archive([string]$TestName, [string[]]$RunPaths, [string[]]$Siblings) {
        $base = Join-Path $tempRoot ("archive-" + [guid]::NewGuid().ToString('N'))
        $script:modelEvalRoot = Join-Path $base 'model-eval'
        $script:resultsRoot = Join-Path $base 'ModelTestingResults\url_host'
        $script:resultsDir = Join-Path $script:resultsRoot $TestName
        $script:testName = $TestName
        foreach ($f in $archiveFunctions) { . ([scriptblock]::Create($f.Extent.Text)) }
        $before = @(Get-RunLeafDirs)
        foreach ($rel in @($RunPaths) + @($Siblings)) { New-Item -ItemType Directory -Force -Path (Join-Path $script:modelEvalRoot $rel) | Out-Null }
        Copy-NewRunDirectories -Before $before 6>$null
        return @(Get-ChildItem -LiteralPath $script:resultsRoot -Directory -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^\d{8}-\d{6}' } |
                ForEach-Object { $_.FullName.Substring($script:resultsRoot.Length).TrimStart('\') } | Sort-Object)
    }
    $stepRuns = @(1..3 | ForEach-Object { "Model_RunsShippingKindStepPromptFinal($_)\20261120-010517-526" })
    $stepArchived = Test-Archive 'Model_RunsShippingKindStepPromptFinal' $stepRuns @('Model_RunsShippingKindStepPromptFinalX\20261120-010517-526', 'Model_RunsShippingKindStepPromptFinal0\20261120-010517-526')
    Assert-That ((@($stepArchived) -join '|') -eq (@($stepRuns) -join '|')) 'Archive: a TestCase test''s Name(<case>) folders are copied, a name that merely starts with it is not'
    $flatArchived = Test-Archive 'Model_FixesWholeFileRewriteBug_MinimalGuidance' @('Model_FixesWholeFileRewriteBug_MinimalGuidance\20261120-003848-238') @()
    Assert-That ((@($flatArchived) -join '|') -eq 'Model_FixesWholeFileRewriteBug_MinimalGuidance\20261120-003848-238') 'Archive: a flat <TestName>\<timestamp> run is still copied'
    $nestedArchived = Test-Archive 'Model_SizeThreshold' @('Model_SizeThreshold\n60\20261120-003848-238') @()
    Assert-That ((@($nestedArchived) -join '|') -eq 'Model_SizeThreshold\n60\20261120-003848-238') 'Archive: SizeThreshold''s extra n<size> level is preserved'
}
finally {
    Start-Sleep -Seconds 1
    Remove-Item Env:\ROSLYNSENTINEL_EVALQUEUE_CHECKOUTS_ROOT -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($script:failures -gt 0) { Write-Host "$script:failures FAILED"; exit 1 }
Write-Host 'All passed'
exit 0
