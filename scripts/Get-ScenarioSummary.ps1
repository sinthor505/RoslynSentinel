# Per-scenario summary of a Scenario run's results.csv (roslynsentinel-modeleval.ps1 -Test Scenario).
#
# Dot-source this file, then:
#   Get-ScenarioSummary -CsvPath <results.csv>      # one summary object per scenarioId + liftRenames
#   Get-ScenarioSummary -Rows <objects>             # same, from rows you already imported
#   Write-ScenarioSummary -CsvPath <results.csv>    # prints one block per scenario
#   Write-ScenarioSummary -Summary <objects>        # prints summary objects you already have
#
# Rules: an Inconclusive sample (non-empty inconclusiveReason) is never in any denominator. It is only
# counted and listed by reason. CSV booleans are the text true/false; empty numeric fields are skipped.

function ConvertTo-ScenarioNumber([string]$Text) {
    $parsed = 0.0
    if (-not [string]::IsNullOrWhiteSpace($Text) -and
        [double]::TryParse($Text.Trim(), [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        return $parsed
    }
    return $null
}

function Get-ScenarioCounts($Rows, [string]$Column) {
    $counts = [ordered]@{}
    foreach ($row in $Rows) {
        $value = ([string]$row.$Column).Trim()
        if ($value -eq '') { $value = '(blank)' }
        if ($counts.Contains($value)) { $counts[$value]++ } else { $counts[$value] = 1 }
    }
    return $counts
}

function Get-ScenarioSummary {
    [CmdletBinding(DefaultParameterSetName = 'Csv')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Csv')][string]$CsvPath,
        [Parameter(Mandatory, ParameterSetName = 'Rows')][object[]]$Rows
    )

    if ($PSCmdlet.ParameterSetName -eq 'Csv') {
        $Rows = @(Import-Csv -LiteralPath $CsvPath)
    }
    $Rows = @($Rows)

    # Group by scenarioId and liftRenames, so on and off runs are never mixed.
    $groups = [ordered]@{}
    foreach ($row in $Rows) {
        $lift = ([string]$row.liftRenames).Trim()
        $key = "$([string]$row.scenarioId)|$lift"
        if (-not $groups.Contains($key)) {
            $groups[$key] = [System.Collections.Generic.List[object]]::new()
        }
        $groups[$key].Add($row)
    }

    foreach ($key in $groups.Keys) {
        $members = $groups[$key]
        $first = $members[0]
        $lift = ([string]$first.liftRenames).Trim()

        $inconclusive = @($members | Where-Object { -not [string]::IsNullOrWhiteSpace($_.inconclusiveReason) })
        $scoredRows = @($members | Where-Object { [string]::IsNullOrWhiteSpace($_.inconclusiveReason) })
        $scored = $scoredRows.Count
        $passed = @($scoredRows | Where-Object { $_.passed -eq 'true' }).Count
        $firstCallOk = @($scoredRows | Where-Object { $_.firstCallSucceeded -eq 'true' }).Count

        $recoverValues = @($scoredRows |
            Where-Object { $_.recovered -eq 'true' -and $null -ne (ConvertTo-ScenarioNumber $_.callsToRecover) } |
            ForEach-Object { ConvertTo-ScenarioNumber $_.callsToRecover })
        $meanCalls = $null
        if ($recoverValues.Count -gt 0) {
            $meanCalls = ($recoverValues | Measure-Object -Average).Average
        }

        $driftValues = @($scoredRows |
            ForEach-Object { ConvertTo-ScenarioNumber $_.resultDrift } |
            Where-Object { $null -ne $_ })
        $resultDriftSum = 0
        if ($driftValues.Count -gt 0) {
            $resultDriftSum = ($driftValues | Measure-Object -Sum).Sum
        }

        $passRate = $null
        $firstCallRate = $null
        if ($scored -gt 0) {
            $passRate = [double]$passed / $scored
            $firstCallRate = [double]$firstCallOk / $scored
        }

        [pscustomobject]@{
            ScenarioId              = [string]$first.scenarioId
            LiftRenames             = $lift
            Total                   = $members.Count
            Inconclusive            = $inconclusive.Count
            InconclusiveReasons     = Get-ScenarioCounts $inconclusive 'inconclusiveReason'
            Scored                  = $scored
            Passed                  = $passed
            PassRate                = $passRate
            FirstCallSucceeded      = $firstCallOk
            FirstCallSuccessRate    = $firstCallRate
            RecoveredSamples        = $recoverValues.Count
            MeanCallsToRecover      = $meanCalls
            FailureClassCounts      = Get-ScenarioCounts $scoredRows 'failureClass'
            FinalStateVerdictCounts = Get-ScenarioCounts $scoredRows 'finalStateVerdict'
            TurnCapHit              = @($scoredRows | Where-Object { $_.turnCapHit -eq 'true' }).Count
            SchemaDriftCount        = @($scoredRows | Where-Object { $_.schemaDrift -eq 'true' }).Count
            ResultDriftSum          = $resultDriftSum
        }
    }
}

function Format-ScenarioPercent($Part, $Whole) {
    if ($Whole -le 0) { return 'n/a' }
    $pct = [int][math]::Round(100.0 * $Part / $Whole)
    return "$pct%"
}

function Format-ScenarioCounts($Counts) {
    $parts = @($Counts.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
    if ($parts.Count -eq 0) { return 'none' }
    return ($parts -join ', ')
}

function Write-ScenarioSummary {
    [CmdletBinding(DefaultParameterSetName = 'Csv')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Csv')][string]$CsvPath,
        [Parameter(Mandatory, ParameterSetName = 'Summary')][object[]]$Summary
    )

    if ($PSCmdlet.ParameterSetName -eq 'Csv') {
        $Summary = @(Get-ScenarioSummary -CsvPath $CsvPath)
    }
    if ($Summary.Count -eq 0) {
        Write-Host 'Scenario summary: no sample rows.'
        return
    }

    foreach ($s in $Summary) {
        $liftLabel = 'liftRenames unset'
        if ($s.LiftRenames -ne '') { $liftLabel = "liftRenames $($s.LiftRenames)" }

        $reasonText = ''
        if ($s.Inconclusive -gt 0) {
            $reasonParts = @($s.InconclusiveReasons.GetEnumerator() | ForEach-Object { "$($_.Key): $($_.Value)" })
            $reasonText = " ($($reasonParts -join '; '))"
        }
        $passPct = Format-ScenarioPercent $s.Passed $s.Scored

        Write-Host ("Scenario {0} ({1}): {2} samples, {3} Inconclusive{4}, {5} scored, passed {6}/{7} ({8})" -f `
            $s.ScenarioId, $liftLabel, $s.Total, $s.Inconclusive, $reasonText, $s.Scored, $s.Passed, $s.Scored, $passPct)

        $firstPct = Format-ScenarioPercent $s.FirstCallSucceeded $s.Scored
        Write-Host "  first-call success $($s.FirstCallSucceeded)/$($s.Scored) ($firstPct)"

        $meanText = 'n/a'
        if ($null -ne $s.MeanCallsToRecover) {
            $meanText = [string][math]::Round([double]$s.MeanCallsToRecover, 1)
        }
        Write-Host "  callsToRecover mean $meanText over $($s.RecoveredSamples) recovered"

        Write-Host "  failureClass: $(Format-ScenarioCounts $s.FailureClassCounts)"
        Write-Host "  finalStateVerdict: $(Format-ScenarioCounts $s.FinalStateVerdictCounts)"
        Write-Host "  turnCapHit: $($s.TurnCapHit)"
        Write-Host "  drift: schemaDrift $($s.SchemaDriftCount) of $($s.Scored) scored, resultDrift sum $($s.ResultDriftSum)"
    }
}
