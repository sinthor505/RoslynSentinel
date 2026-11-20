# Regression checks for roslynsentinel-evalruns.ps1 (run index rows in <ResultsRoot>\<suffix>\runs.csv).
#
# Run:  pwsh -NoProfile -File scripts\roslynsentinel-evalruns.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Plain assert script, same style as roslynsentinel-evalhosts.Tests.ps1. Every check writes under a temp results
# root that is removed at the end. Concurrent appends use Start-ThreadJob when available, else two child pwsh
# processes.

$ErrorActionPreference = 'Stop'
$libPath = Join-Path $PSScriptRoot 'roslynsentinel-evalruns.ps1'
. $libPath

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-evalruns-tests-" + [guid]::NewGuid().ToString('N'))
$resultsRoot = Join-Path $tempRoot 'ModelTestingResults'
$null = New-Item -ItemType Directory -Force -Path $resultsRoot

$expectedHeader = 'jobId,machine,host,baseUrl,backend,test,model,sessions,purpose,pinnedCommit,state,passed,startedUtc,endedUtc,durationSeconds,reason,runFolders'
# Export-Csv quotes every header name, so the raw first line is the quoted form.
$expectedHeaderLine = (($expectedHeader -split ',') | ForEach-Object { '"' + $_ + '"' }) -join ','

function Get-RunsCsv([string]$Suffix) {
    return Join-Path (Join-Path $resultsRoot $Suffix) 'runs.csv'
}

function Find-Row($Rows, [string]$JobId) {
    return @($Rows | Where-Object { $_.jobId -eq $JobId })[0]
}

$purpose = 'Compare "A", then B'
$finished = [pscustomobject]@{
    id = 'job-0001'; suffix = '112'; machine = '112'; host = '112u'; baseUrl = 'http://192.168.1.112:8888/v1'
    backend = 'llama-server'; test = 'ModelEvalAlpha'; model = 'ornith-test'; sessions = @('s1', 's2')
    purpose = $purpose; pinnedCommit = 'abc1234'; state = 'Finished'; passed = $true
    startedUtc = '2026-11-20T10:00:00Z'; endedUtc = '2026-11-20T10:05:00Z'; durationSeconds = 300; reason = ''
    runFolders = @('ModelEvalAlpha\20261120-100000', 'ModelEvalAlpha\20261120-100001')
}
# Refused job: single 'session' string instead of 'sessions', and no runFolders property at all.
$refused = [pscustomobject]@{
    id = 'job-0002'; suffix = '112'; machine = '112'; host = '112u'; baseUrl = 'http://192.168.1.112:8888/v1'
    backend = 'llama-server'; test = 'ModelEvalBeta'; model = 'ornith-test'; session = 's3'
    purpose = 'Refused case'; pinnedCommit = 'abc1234'; state = 'Refused'; passed = $false
    startedUtc = ''; endedUtc = ''; durationSeconds = 0; reason = 'Host busy'
}
# Job lacking every newer field.
$bare = [pscustomobject]@{ id = 'job-0100'; suffix = '85'; state = 'Finished'; test = 'ModelEvalBare' }

# 1. Two appends to a new suffix folder both succeed and create the csv.
$ok1 = Add-EvalRunIndexRow -Job $finished -ResultsRoot $resultsRoot
$ok2 = Add-EvalRunIndexRow -Job $refused -ResultsRoot $resultsRoot
Assert-That (($ok1 -eq $true) -and ($ok2 -eq $true)) 'two appends to a new suffix folder return $true'

$csv112 = Get-RunsCsv '112'
Assert-That (Test-Path -LiteralPath $csv112) 'runs.csv created under the <suffix> folder'

# 2. Header written once, as the first line.
$headerLines = @(Get-Content -LiteralPath $csv112 | Where-Object { $_ -eq $expectedHeaderLine })
Assert-That ($headerLines.Count -eq 1) 'header line written exactly once'
Assert-That ((Get-Content -LiteralPath $csv112 -TotalCount 1) -eq $expectedHeaderLine) 'first line is the fixed header'

# 3. Column order and data rows.
$rows112 = @(Import-Csv -LiteralPath $csv112)
Assert-That ($rows112.Count -eq 2) 'two data rows after two appends'
Assert-That (((@($rows112[0].PSObject.Properties.Name)) -join ',') -eq $expectedHeader) 'column order matches the fixed list'

$rowFinished = Find-Row $rows112 'job-0001'
Assert-That ($rowFinished.state -eq 'Finished') 'finished job row has state Finished'
Assert-That ($rowFinished.purpose -eq $purpose) 'purpose with a comma and a double quote round-trips through Import-Csv'
Assert-That ($rowFinished.sessions -eq 's1+s2') 'sessions array is joined with +'
Assert-That ($rowFinished.runFolders -eq 'ModelEvalAlpha\20261120-100000;ModelEvalAlpha\20261120-100001') 'runFolders are joined with ;'
Assert-That (($rowFinished.machine -eq '112') -and ($rowFinished.pinnedCommit -eq 'abc1234') -and ($rowFinished.durationSeconds -eq '300')) 'machine, pinnedCommit and durationSeconds are written'

$rowRefused = Find-Row $rows112 'job-0002'
Assert-That ($rowRefused.state -eq 'Refused') 'refused job row has state Refused'
Assert-That ($rowRefused.sessions -eq 's3') 'single session string is used when there is no sessions array'
Assert-That ($rowRefused.reason -eq 'Host busy') 'reason is written for a refused job'
Assert-That ($rowRefused.runFolders -eq '') 'absent runFolders leaves the cell empty'

# 4. A job lacking the newer fields appends with empty cells and does not throw.
$okBare = Add-EvalRunIndexRow -Job $bare -ResultsRoot $resultsRoot
$rowBare = Find-Row @(Import-Csv -LiteralPath (Get-RunsCsv '85')) 'job-0100'
Assert-That ($okBare -eq $true) 'job lacking newer fields returns $true'
Assert-That (($null -ne $rowBare) -and ($rowBare.machine -eq '') -and ($rowBare.baseUrl -eq '') -and ($rowBare.sessions -eq '') -and ($rowBare.purpose -eq '') -and ($rowBare.runFolders -eq '')) 'job lacking newer fields leaves those cells empty'

# 5. Two concurrent appends to the same runs.csv both land.
$jobA = [pscustomobject]@{ id = 'job-c1'; suffix = '113'; state = 'Finished'; test = 'ModelEvalConcurrent'; purpose = 'concurrent a' }
$jobB = [pscustomobject]@{ id = 'job-c2'; suffix = '113'; state = 'Finished'; test = 'ModelEvalConcurrent'; purpose = 'concurrent b' }

if (Get-Command Start-ThreadJob -ErrorAction SilentlyContinue) {
    $threadJobs = @(
        Start-ThreadJob -ScriptBlock { param($lib, $job, $root) . $lib; Add-EvalRunIndexRow -Job $job -ResultsRoot $root } -ArgumentList $libPath, $jobA, $resultsRoot
        Start-ThreadJob -ScriptBlock { param($lib, $job, $root) . $lib; Add-EvalRunIndexRow -Job $job -ResultsRoot $root } -ArgumentList $libPath, $jobB, $resultsRoot
    )
    $concResults = @($threadJobs | Receive-Job -Wait -AutoRemoveJob)
    Assert-That (($concResults.Count -eq 2) -and ($concResults[0] -eq $true) -and ($concResults[1] -eq $true)) 'two concurrent appends (thread jobs) both return $true'
}
else {
    $childCommand = '. $env:RS_EVALRUNS_LIB; $j = [pscustomobject]@{ id = $env:RS_EVALRUNS_ID; suffix = ''113''; state = ''Finished''; test = ''ModelEvalConcurrent'' }; if (Add-EvalRunIndexRow -Job $j -ResultsRoot $env:RS_EVALRUNS_ROOT) { exit 0 } else { exit 1 }'
    $children = @()
    foreach ($id in @('job-c1', 'job-c2')) {
        $env:RS_EVALRUNS_LIB = $libPath
        $env:RS_EVALRUNS_ROOT = $resultsRoot
        $env:RS_EVALRUNS_ID = $id
        $children += Start-Process -FilePath 'pwsh' -ArgumentList ('-NoProfile -Command "' + $childCommand + '"') -PassThru -NoNewWindow
    }
    $childCodes = @()
    foreach ($child in $children) {
        $child.WaitForExit()
        $childCodes += $child.ExitCode
        $child.Dispose()
    }
    Remove-Item Env:RS_EVALRUNS_LIB, Env:RS_EVALRUNS_ROOT, Env:RS_EVALRUNS_ID -ErrorAction SilentlyContinue
    Assert-That (($childCodes.Count -eq 2) -and ($childCodes[0] -eq 0) -and ($childCodes[1] -eq 0)) 'two concurrent appends (child pwsh) both exit 0'
}

$rowsConc = @(Import-Csv -LiteralPath (Get-RunsCsv '113'))
Assert-That ($rowsConc.Count -eq 2) 'concurrent appends produce exactly 2 data rows'
Assert-That (((@($rowsConc | ForEach-Object { $_.jobId }) | Sort-Object) -join ',') -eq 'job-c1,job-c2') 'both concurrent job ids are present'

# 6. Read-only runs.csv returns $false and does not throw.
$csv85 = Get-RunsCsv '85'
(Get-Item -LiteralPath $csv85).IsReadOnly = $true
$threwReadOnly = $false
$readOnlyResult = $null
try { $readOnlyResult = Add-EvalRunIndexRow -Job $bare -ResultsRoot $resultsRoot }
catch { $threwReadOnly = $true }
(Get-Item -LiteralPath $csv85).IsReadOnly = $false
Assert-That ((-not $threwReadOnly) -and ($readOnlyResult -eq $false)) 'read-only runs.csv returns $false without throwing'

# 7. ResultsRoot under a file (no directory can be created) returns $false and does not throw.
$blockerFile = Join-Path $tempRoot 'not-a-folder.txt'
Set-Content -LiteralPath $blockerFile -Value 'x'
$threwUnderFile = $false
$underFileResult = $null
try { $underFileResult = Add-EvalRunIndexRow -Job $finished -ResultsRoot (Join-Path $blockerFile 'ModelTestingResults') }
catch { $threwUnderFile = $true }
Assert-That ((-not $threwUnderFile) -and ($underFileResult -eq $false)) 'ResultsRoot under a file returns $false without throwing'

Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue

if ($script:failures -eq 0) {
    Write-Host 'All passed'
    exit 0
}
Write-Host "$($script:failures) check(s) failed"
exit 1
