# Run index for eval queue jobs. One row per job in <ResultsRoot>\<suffix>\runs.csv, appended under a named
# mutex so concurrent sessions and workers do not interleave rows. The csv sits beside the run folders; the
# folder matchers in Archive-ModelTestingResults.ps1 walk directories only, so the file is inert to them.
#
# Dot-source this file. It defines functions only and has no top-level side effects:
#   . (Join-Path $PSScriptRoot 'roslynsentinel-evalruns.ps1')
#   Add-EvalRunIndexRow -Job $job -ResultsRoot $root    # returns $true or $false, never throws

# Reads a property from a PSCustomObject or a hashtable. Returns $null when the property is absent.
function Get-EvalRunValue($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] }
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

# Mutex name for one runs.csv: a hash of its lower-cased full path, so every writer of the same file agrees.
function Get-EvalRunsMutexName([string]$CsvPath) {
    $full = [System.IO.Path]::GetFullPath($CsvPath).ToLowerInvariant()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($full)) }
    finally { $sha.Dispose() }
    $hex = -join ($bytes | ForEach-Object { $_.ToString('x2') })
    return 'Global\RoslynSentinelEvalRuns_' + $hex.Substring(0, 16)
}

# Appends one row for $Job to <ResultsRoot>\<Job.suffix>\runs.csv, writing the header when the file is new.
# Fields absent from the job (machine, baseUrl, backend, sessions, purpose, pinnedCommit, runFolders, ...) become
# empty cells. A single 'session' string is used when there is no 'sessions' array. Returns $true on success and
# $false on any failure (read-only target, a path under a file, mutex timeout). Never throws into the caller.
function Add-EvalRunIndexRow {
    param(
        [Parameter(Mandatory)] $Job,
        [Parameter(Mandatory)] [string]$ResultsRoot
    )

    $mutex = $null
    $acquired = $false
    try {
        $suffix = [string](Get-EvalRunValue $Job 'suffix')
        if ([string]::IsNullOrWhiteSpace($suffix)) { return $false }

        $csvPath = Join-Path (Join-Path $ResultsRoot $suffix) 'runs.csv'
        $mutex = New-Object System.Threading.Mutex($false, (Get-EvalRunsMutexName $csvPath))
        try { $acquired = $mutex.WaitOne(30000) }
        catch [System.Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { return $false }

        $sessions = Get-EvalRunValue $Job 'sessions'
        if ($null -eq $sessions) { $sessions = Get-EvalRunValue $Job 'session' }
        $runFolders = Get-EvalRunValue $Job 'runFolders'

        $row = [pscustomobject][ordered]@{
            jobId           = (Get-EvalRunValue $Job 'id')
            machine         = (Get-EvalRunValue $Job 'machine')
            host            = (Get-EvalRunValue $Job 'host')
            baseUrl         = (Get-EvalRunValue $Job 'baseUrl')
            backend         = (Get-EvalRunValue $Job 'backend')
            test            = (Get-EvalRunValue $Job 'test')
            model           = (Get-EvalRunValue $Job 'model')
            sessions        = (@($sessions) -join '+')
            purpose         = (Get-EvalRunValue $Job 'purpose')
            pinnedCommit    = (Get-EvalRunValue $Job 'pinnedCommit')
            state           = (Get-EvalRunValue $Job 'state')
            passed          = (Get-EvalRunValue $Job 'passed')
            startedUtc      = (Get-EvalRunValue $Job 'startedUtc')
            endedUtc        = (Get-EvalRunValue $Job 'endedUtc')
            durationSeconds = (Get-EvalRunValue $Job 'durationSeconds')
            reason          = (Get-EvalRunValue $Job 'reason')
            runFolders      = (@($runFolders) -join ';')
        }

        $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $csvPath) -ErrorAction Stop
        if (Test-Path -LiteralPath $csvPath) {
            $row | Export-Csv -LiteralPath $csvPath -Append -NoTypeInformation -Encoding utf8 -ErrorAction Stop
        }
        else {
            $row | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding utf8 -ErrorAction Stop
        }
        return $true
    }
    catch {
        return $false
    }
    finally {
        if ($null -ne $mutex) {
            if ($acquired) { try { $mutex.ReleaseMutex() } catch { } }
            $mutex.Dispose()
        }
    }
}
