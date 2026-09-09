<#
Parses a TRX file into a Gantt-style timeline: per test start/end, grouped by
executor host, so overlapping (parallel) vs. back-to-back (serial) tests are visible.
#>
param(
    [Parameter(Mandatory)]
    [string]$TrxPath,

    [string]$OutCsv = (Join-Path (Split-Path $TrxPath) "gantt.csv")
)

[xml]$trx = Get-Content -Raw $TrxPath

$ns = New-Object System.Xml.XmlNamespaceManager($trx.NameTable)
$ns.AddNamespace("t", "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")

$results = $trx.SelectNodes("//t:UnitTestResult", $ns)
$defs = @{}
foreach ($d in $trx.SelectNodes("//t:UnitTest", $ns)) {
    $id = $d.id
    $className = $d.TestMethod.className
    $defs[$id] = $className
}

$rows = foreach ($r in $results) {
    $start = [datetime]$r.startTime
    $end   = [datetime]$r.endTime
    $className = $defs[$r.testId]
    $assembly = if ($className) { ($className -split '\.')[0..1] -join '.' } else { "unknown" }
    [pscustomobject]@{
        TestName   = $r.testName
        Assembly   = $assembly
        ClassName  = $className
        ComputerName = $r.computerName
        Start      = $start
        End        = $end
        DurationMs = ($end - $start).TotalMilliseconds
        Outcome    = $r.outcome
    }
}

$rows = $rows | Sort-Object Start
$rows | Export-Csv -NoTypeInformation -Path $OutCsv
Write-Host "Wrote $($rows.Count) rows to $OutCsv"

# Overlap detection: for each host/executor (ComputerName+process not in TRX by default,
# so we approximate concurrency by counting how many tests have overlapping [Start,End] windows).
$sorted = $rows | Sort-Object Start
$maxConcurrent = 0
$active = New-Object System.Collections.Generic.List[object]
$events = @()
foreach ($r in $sorted) {
    $events += [pscustomobject]@{ Time = $r.Start; Delta = 1; Test = $r.TestName }
    $events += [pscustomobject]@{ Time = $r.End; Delta = -1; Test = $r.TestName }
}
$events = $events | Sort-Object Time, Delta
$concurrent = 0
$timeline = foreach ($e in $events) {
    $concurrent += $e.Delta
    if ($concurrent -gt $maxConcurrent) { $maxConcurrent = $concurrent }
    [pscustomobject]@{ Time = $e.Time; Concurrent = $concurrent }
}

Write-Host "Max observed concurrency: $maxConcurrent"
Write-Host ""
Write-Host "Per-assembly total wall time vs. sum of test durations (gap = serialization overhead):"
$rows | Group-Object Assembly | ForEach-Object {
    $asmRows = $_.Group
    $wall = ([datetime]($asmRows | Sort-Object End -Descending | Select-Object -First 1).End - [datetime]($asmRows | Sort-Object Start | Select-Object -First 1).Start).TotalSeconds
    $sumDur = ($asmRows | Measure-Object DurationMs -Sum).Sum / 1000
    [pscustomobject]@{
        Assembly = $_.Name
        TestCount = $asmRows.Count
        WallSeconds = [math]::Round($wall,2)
        SumDurationSeconds = [math]::Round($sumDur,2)
        ParallelEfficiency = if ($wall -gt 0) { [math]::Round($sumDur/$wall,2) } else { 0 }
    }
} | Sort-Object WallSeconds -Descending | Format-Table -AutoSize

Write-Host ""
Write-Host "Slowest 20 individual tests:"
$rows | Sort-Object DurationMs -Descending | Select-Object -First 20 TestName, Assembly, @{N='DurationSec';E={[math]::Round($_.DurationMs/1000,2)}} | Format-Table -AutoSize
