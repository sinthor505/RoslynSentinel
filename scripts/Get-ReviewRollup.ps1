# Rollup for one model-eval review folder (plan_modeleval_run_review_pipeline, Step 7a).
#
# Reads selection.json, then each selected run's digest.json and review file. A review that fails the schema
# or the vocabulary membership check is listed in invalid[] and not counted. Writes review.md beside each valid
# review, and rollup.json and rollup.md in the review folder. Functions only: dot-source this file and call
# Get-ReviewRollup in-process. Vocabulary lists come from eval-review\vocabulary.json; do not copy them here.
#
# Run:  pwsh -NoProfile -File scripts\Get-ReviewRollup.ps1 -ReviewDir <folder> [-ReviewFileName <name>] [-Vary a,b] [-Force]

param(
    [string]$ReviewDir,
    [string]$ReviewFileName = 'review.json',
    [string[]]$Vary = @(),
    [switch]$Force
)

. (Join-Path $PSScriptRoot 'eval-review\EvalRunReader.ps1')

# Splits each element on commas, trims the pieces and drops empty ones. Returns an array (possibly empty).
function ConvertTo-RollupList {
    param([string[]]$Values)
    $out = @()
    foreach ($value in @($Values)) {
        foreach ($piece in ([string]$value).Split(',')) {
            $trimmed = $piece.Trim()
            if ($trimmed.Length -gt 0) { $out += $trimmed }
        }
    }
    return , $out
}

# Writes text as UTF-8 without a BOM.
function Write-RollupText {
    param([string]$Path, [string]$Text)
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

# Parses a JSON file (throws when it is missing or malformed).
function Read-RollupJson {
    param([string]$Path)
    return (Get-Content -Raw -LiteralPath $Path -ErrorAction Stop | ConvertFrom-Json -Depth 100 -DateKind String -ErrorAction Stop)
}

# Text on one line: line breaks become spaces.
function ConvertTo-RollupLine($Value) {
    if ($null -eq $Value) { return '' }
    return (([string]$Value) -replace '\s*[\r\n]+\s*', ' ')
}

# Turn numbers as a comma list.
function Format-RollupTurns($Turns) {
    return (@($Turns) -join ',')
}

# Checks one review file. Returns { Review, Problem }: Review is the parsed object when every check passes;
# Problem names the failed checks otherwise (null when none failed).
function Read-RollupReview {
    param([string]$ReviewPath, [string]$SchemaPath, $Vocabulary)
    if (-not (Test-Path -LiteralPath $ReviewPath -PathType Leaf)) {
        return [pscustomobject]@{ Review = $null; Problem = 'review file not found' }
    }
    $text = Get-Content -Raw -LiteralPath $ReviewPath
    try { $review = ConvertFrom-Json -InputObject $text -Depth 100 -DateKind String -ErrorAction Stop }
    catch { return [pscustomobject]@{ Review = $null; Problem = 'malformed JSON' } }
    if ($review -isnot [pscustomobject]) {
        return [pscustomobject]@{ Review = $null; Problem = 'review is not a JSON object' }
    }

    $problems = @()
    $schemaOk = $false
    try { $schemaOk = [bool](Test-Json -Json $text -SchemaFile $SchemaPath -ErrorAction Stop) }
    catch { $schemaOk = $false }
    if (-not $schemaOk) { $problems += 'fails review.schema.json' }

    $dimNames = @($Vocabulary.dimensions | ForEach-Object { [string]$_.name })
    $attrNames = @($Vocabulary.attributions | ForEach-Object { [string]$_ })
    $confNames = @($Vocabulary.confidences | ForEach-Object { [string]$_ })
    foreach ($dim in @(Get-EvalProperty $review 'dimensions')) {
        if ($dimNames -notcontains [string](Get-EvalProperty $dim 'name')) { $problems += 'unknown dimension name'; break }
    }
    foreach ($finding in @(Get-EvalProperty $review 'findings')) {
        if ($dimNames -notcontains [string](Get-EvalProperty $finding 'dimension')) { $problems += 'unknown finding dimension'; break }
    }
    foreach ($finding in @(Get-EvalProperty $review 'findings')) {
        if ($attrNames -notcontains [string](Get-EvalProperty $finding 'attribution')) { $problems += 'unknown attribution'; break }
    }
    foreach ($finding in @(Get-EvalProperty $review 'findings')) {
        if ((Test-EvalProperty $finding 'confidence') -and ($confNames -notcontains [string](Get-EvalProperty $finding 'confidence'))) {
            $problems += 'unknown confidence'; break
        }
    }

    $problemText = $null
    if ($problems.Count -gt 0) { $problemText = $problems -join '; ' }
    return [pscustomobject]@{ Review = $review; Problem = $problemText }
}

# One counts row per vocabulary dimension, in vocabulary order. A dimension the review lacks counts as na.
function Get-RollupDimensions {
    param($Reviews, [string[]]$DimNames)
    $out = @()
    foreach ($name in $DimNames) {
        $counts = [ordered]@{ '0' = 0; '1' = 0; '2' = 0; 'na' = 0 }
        foreach ($review in @($Reviews)) {
            $dim = @(Get-EvalProperty $review 'dimensions') | Where-Object { [string](Get-EvalProperty $_ 'name') -eq $name } | Select-Object -First 1
            $key = 'na'
            $score = Get-EvalProperty $dim 'score'
            if ($null -ne $score) { $key = [string]$score }
            $counts[$key] = $counts[$key] + 1
        }
        $out += [ordered]@{ name = $name; counts = $counts }
    }
    return @($out)
}

# Finding counts per vocabulary attribution, in vocabulary order, zero-filled.
function Get-RollupAttributions {
    param($Reviews, [string[]]$AttrNames)
    $counts = [ordered]@{}
    foreach ($attr in $AttrNames) { $counts[$attr] = 0 }
    foreach ($review in @($Reviews)) {
        foreach ($finding in @(Get-EvalProperty $review 'findings')) {
            $attr = [string](Get-EvalProperty $finding 'attribution')
            if ($counts.Contains($attr)) { $counts[$attr] = $counts[$attr] + 1 }
        }
    }
    return $counts
}

# review.md: heading, harness verdict, one line per vocabulary dimension, then findings, grading defects and notExamined.
function ConvertTo-ReviewMarkdown {
    param($Review, [string[]]$DimNames)
    $lines = New-Object System.Collections.Generic.List[string]
    $runId = ConvertTo-RollupLine (Get-EvalProperty $Review 'runId')
    $lines.Add("# Review: $runId")
    $lines.Add('')

    $verdict = Get-EvalProperty $Review 'harnessVerdict'
    $verdictText = 'none'
    if ($null -ne $verdict) {
        $verdictText = (@($verdict.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', ')
    }
    $lines.Add('Harness verdict: ' + (ConvertTo-RollupLine $verdictText))
    $lines.Add('')

    foreach ($name in $DimNames) {
        $dim = @(Get-EvalProperty $Review 'dimensions') | Where-Object { [string](Get-EvalProperty $_ 'name') -eq $name } | Select-Object -First 1
        $text = 'N/A'
        $score = Get-EvalProperty $dim 'score'
        if ($null -ne $score) { $text = [string]$score }
        $lines.Add("- $($name): $text")
    }

    $lines.Add('')
    $lines.Add('## Findings')
    $findings = @(Get-EvalProperty $Review 'findings')
    if ($findings.Count -eq 0) { $lines.Add('- none') }
    foreach ($finding in $findings) {
        $severity = Get-EvalProperty $finding 'severity'
        $dimension = Get-EvalProperty $finding 'dimension'
        $turns = Format-RollupTurns (Get-EvalProperty $finding 'turns')
        $summary = ConvertTo-RollupLine (Get-EvalProperty $finding 'summary')
        $attribution = Get-EvalProperty $finding 'attribution'
        $confidence = Get-EvalProperty $finding 'confidence'
        $worthIt = Get-EvalProperty $finding 'rootCauseWorthIt'
        $lines.Add("- [$severity] $dimension turns $turns`: $summary (attribution $attribution, confidence $confidence, rootCauseWorthIt $worthIt)")
    }

    $lines.Add('')
    $lines.Add('## Grading defects')
    $defects = @(Get-EvalProperty $Review 'gradingDefects')
    if ($defects.Count -eq 0) { $lines.Add('- none') }
    foreach ($defect in $defects) {
        $turns = Format-RollupTurns (Get-EvalProperty $defect 'turns')
        $lines.Add("- turns $turns`: $(ConvertTo-RollupLine (Get-EvalProperty $defect 'summary'))")
    }

    $lines.Add('')
    $lines.Add('## Not examined')
    $notExamined = @(Get-EvalProperty $Review 'notExamined')
    if ($notExamined.Count -eq 0) { $lines.Add('- none') }
    foreach ($item in $notExamined) { $lines.Add("- $(ConvertTo-RollupLine $item)") }

    return ([string]::Join("`r`n", $lines) + "`r`n")
}

# rollup.md: seed, vary and forced, the codes and invalid list, then one section per arm.
function ConvertTo-RollupMarkdown {
    param($Rollup)
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('# Review rollup')
    $lines.Add('')
    $lines.Add("- Review folder: $($Rollup.reviewDir)")
    $lines.Add("- Review file: $($Rollup.reviewFileName)")
    $lines.Add("- Seed: $($Rollup.seed)")
    $lines.Add("- Passes per arm: $($Rollup.passesPerArm)")
    $varyText = 'none'
    if (@($Rollup.vary).Count -gt 0) { $varyText = (@($Rollup.vary) -join ', ') }
    $lines.Add("- Vary: $varyText")
    $lines.Add("- Forced: $($Rollup.forced)")

    $lines.Add('')
    $lines.Add('## Codes')
    if (@($Rollup.codes).Count -eq 0) { $lines.Add('- none') }
    foreach ($code in @($Rollup.codes)) {
        $line = "- $($code.code)"
        if ($null -ne $code.field) { $line += " field=$($code.field)" }
        $lines.Add($line)
    }

    $lines.Add('')
    $lines.Add('## Invalid reviews')
    if (@($Rollup.invalid).Count -eq 0) { $lines.Add('- none') }
    foreach ($entry in @($Rollup.invalid)) { $lines.Add("- $($entry.path): $($entry.code), $($entry.detail)") }

    foreach ($arm in @($Rollup.arms)) {
        $lines.Add('')
        $lines.Add("## Arm $($arm.armKey)")
        $lines.Add("- Runs: $($arm.runs)")
        $lines.Add("- Skipped: $(@($arm.skipped).Count)")
        $lines.Add('')
        $lines.Add('Dimension counts (0 / 1 / 2 / N/A):')
        foreach ($dim in @($arm.dimensions)) {
            $lines.Add("- $($dim.name): $($dim.counts['0']) / $($dim.counts['1']) / $($dim.counts['2']) / $($dim.counts['na'])")
        }
        $lines.Add('')
        $lines.Add('Attributions:')
        foreach ($key in $arm.attributions.Keys) { $lines.Add("- $($key): $($arm.attributions[$key])") }
    }

    return ([string]::Join("`r`n", $lines) + "`r`n")
}

# Reads selection.json and each selected run, validates the reviews, writes review.md per valid review and
# rollup.json and rollup.md in ReviewDir. Returns { ExitCode (always 0), Rollup, RollupPath }.
function Get-ReviewRollup {
    param(
        [Parameter(Mandatory)][string]$ReviewDir,
        [string]$ReviewFileName = 'review.json',
        [string[]]$Vary = @(),
        [switch]$Force
    )
    $folder = [System.IO.Path]::GetFullPath($ReviewDir)
    $vocab = Read-EvalVocabulary
    $dimNames = @($vocab.dimensions | ForEach-Object { [string]$_.name })
    $attrNames = @($vocab.attributions | ForEach-Object { [string]$_ })
    $schemaPath = Join-Path $PSScriptRoot 'eval-review\review.schema.json'
    $varyList = ConvertTo-RollupList $Vary
    $selection = Read-RollupJson (Join-Path $folder 'selection.json')

    $codes = @()
    $invalid = @()
    $armOut = @()
    $armKeys = @()
    $allEntries = @()

    foreach ($arm in @(Get-EvalProperty $selection 'arms')) {
        $armKey = [string](Get-EvalProperty $arm 'armKey')
        $armKeys += $armKey
        $entries = @()
        $samplerTexts = @()
        $keyFields = $null
        $selCount = 0

        foreach ($sel in @(Get-EvalProperty $arm 'selected')) {
            $selCount++
            $runPath = [string](Get-EvalProperty $sel 'runPath')
            $digestPath = [System.IO.Path]::GetFullPath([string](Get-EvalProperty $sel 'digestPath'))
            try { $digest = Read-RollupJson $digestPath }
            catch {
                $invalid += [ordered]@{ path = $digestPath; code = 'InvalidReview'; detail = 'digest.json unreadable' }
                continue
            }
            $header = Get-EvalProperty $digest 'header'
            if ($selCount -eq 1) { $keyFields = Get-EvalProperty $header 'keyFields' }
            $samplerTexts += (ConvertTo-Json -InputObject (Get-EvalProperty $header 'sampler') -Depth 100 -Compress)

            $reviewPath = Join-Path (Split-Path -Parent $digestPath) $ReviewFileName
            $check = Read-RollupReview -ReviewPath $reviewPath -SchemaPath $schemaPath -Vocabulary $vocab
            if ($null -ne $check.Problem) {
                $invalid += [ordered]@{ path = $reviewPath; code = 'InvalidReview'; detail = $check.Problem }
                continue
            }

            $review = $check.Review
            Write-RollupText ([System.IO.Path]::ChangeExtension($reviewPath, '.md')) (ConvertTo-ReviewMarkdown -Review $review -DimNames $dimNames)
            $entries += [pscustomobject]@{
                ArmKey           = $armKey
                RunPath          = $runPath
                Scenario         = [string](Get-EvalProperty $header 'scenario')
                ComparabilityKey = [string](Get-EvalProperty $header 'comparabilityKey')
                Review           = $review
            }
        }

        $distinctSamplers = @($samplerTexts | Sort-Object -Unique)
        if ($distinctSamplers.Count -gt 1) {
            $codes += [ordered]@{ code = 'SamplerDiffers'; field = $armKey; detail = $null }
        }

        $reviews = @($entries | ForEach-Object { $_.Review })
        $allEntries += $entries

        $trace = @()
        $defects = @()
        foreach ($entry in $entries) {
            foreach ($finding in @(Get-EvalProperty $entry.Review 'findings')) {
                if ((Get-EvalProperty $finding 'rootCauseWorthIt') -eq $true) {
                    $trace += [ordered]@{
                        runPath     = $entry.RunPath
                        turns       = @(Get-EvalProperty $finding 'turns')
                        summary     = Get-EvalProperty $finding 'summary'
                        attribution = Get-EvalProperty $finding 'attribution'
                    }
                }
            }
            foreach ($defect in @(Get-EvalProperty $entry.Review 'gradingDefects')) {
                $defects += [ordered]@{ runPath = $entry.RunPath; turns = @(Get-EvalProperty $defect 'turns'); summary = Get-EvalProperty $defect 'summary' }
            }
        }

        $skipped = @(@(Get-EvalProperty $arm 'skipped') | ForEach-Object {
            [ordered]@{ runPath = Get-EvalProperty $_ 'runPath'; reason = Get-EvalProperty $_ 'reason' }
        })

        $armOut += [ordered]@{
            armKey          = $armKey
            keyFields       = $keyFields
            runs            = @($entries).Count
            dimensions      = @(Get-RollupDimensions -Reviews $reviews -DimNames $dimNames)
            attributions    = Get-RollupAttributions -Reviews $reviews -AttrNames $attrNames
            findingsToTrace = @($trace)
            gradingDefects  = @($defects)
            skipped         = $skipped
        }
    }

    $scenarioOut = @()
    $scenarioNames = @($allEntries | ForEach-Object { $_.Scenario } | Select-Object -Unique)
    foreach ($scenario in $scenarioNames) {
        $inScenario = @($allEntries | Where-Object { $_.Scenario -eq $scenario })
        $perArm = @()
        foreach ($armKey in $armKeys) {
            $group = @($inScenario | Where-Object { $_.ArmKey -eq $armKey })
            if ($group.Count -eq 0) { continue }
            $perArm += [ordered]@{
                armKey           = $armKey
                comparabilityKey = $group[0].ComparabilityKey
                runs             = $group.Count
                dimensions       = @(Get-RollupDimensions -Reviews @($group | ForEach-Object { $_.Review }) -DimNames $dimNames)
            }
        }
        $scenarioOut += [ordered]@{ scenario = $scenario; perArm = $perArm; armDifferences = @() }
    }

    $rollup = [ordered]@{
        schemaVersion  = 1
        reviewDir      = $folder
        reviewFileName = $ReviewFileName
        seed           = Get-EvalProperty $selection 'seed'
        passesPerArm   = Get-EvalProperty $selection 'passesPerArm'
        vary           = @($varyList)
        forced         = [bool]$Force
        codes          = @($codes)
        invalid        = @($invalid)
        arms           = @($armOut)
        scenarios      = @($scenarioOut)
    }

    $rollupPath = Join-Path $folder 'rollup.json'
    Write-RollupText $rollupPath (ConvertTo-Json -InputObject $rollup -Depth 100)
    Write-RollupText (Join-Path $folder 'rollup.md') (ConvertTo-RollupMarkdown -Rollup $rollup)
    return [pscustomobject]@{ ExitCode = 0; Rollup = $rollup; RollupPath = $rollupPath }
}

if ($MyInvocation.InvocationName -ne '.') { $r = Get-ReviewRollup @PSBoundParameters; exit $r.ExitCode }
