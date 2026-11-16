<#
.SYNOPSIS
    Counts misspelled ShippingKinds members (Overnight -> Oovernight/Ownight/Overtime ...) in saved ShippingKind transcripts.

.DESCRIPTION
    Scans transcript.json files of the stepped ShippingKind scenario (and of ShippingKindResume samples) for
    'ShippingKinds.<Name>' references whose <Name> is not a real member, and reports how often the model misspells a
    constant, in which part of the turn, and which misspellings occur.

    The "decision turn" is the model turn right after the turn that read ShippingKinds.cs (a ReadFile whose result holds
    'class ShippingKinds'): that is the first turn where the model writes the constants from what it just read. For each
    transcript the script checks that turn's reasoning, its message content and the arguments of its tool calls, and also
    the whole run. A resume sample (ShippingKindResume) has the kept turns first, so the same rule picks its live turn.

    Real member names come from the ShippingKinds.cs read inside the same transcript (const string X, static T X(...)),
    plus a built-in fallback list. A reference is a 'near' typo when its edit distance to a real name is 1-3, otherwise
    'other'. Only the 'ShippingKinds.<Name>' form is checked, so a bare misspelling in prose is not counted.

    Transcripts are deduplicated by RunId (archived runs are copied under nested folders).

.PARAMETER Path
    Transcript files and/or folders to scan (folders are searched recursively for transcript.json).
    Default: every ShippingKind run folder under ModelTestingResults.

.PARAMETER Model
    Only transcripts whose ModelKey matches this -like pattern (e.g. 'tiel*', 'qwen3.5-9b*').

.PARAMETER Step
    Only runs of this ShippingKind step, read from the run folder name 'Model_...(<step>)'. Samples of ShippingKindResume
    carry no step in their folder name, so leave this off for them.

.PARAMETER Turn
    Check this turn number instead of the auto-detected decision turn.

.PARAMETER Detail
    Also print one line per transcript.

.PARAMETER Csv
    Write one row per transcript to this CSV file.

.EXAMPLE
    pwsh scripts/Get-ShippingKindTypoCounts.ps1 -Step 4 -Model 'tiel*'
    Typo counts for the tiel-coder step 4 runs.

.EXAMPLE
    pwsh scripts/Get-ShippingKindTypoCounts.ps1 -Path ModelTestingResults\113\ResumeShippingKindStep_SamplesTheNextTurns -Detail
    Typo counts over the samples of a ShippingKindResume run.
#>
[CmdletBinding()]
param(
    [string[]]$Path,
    [string]$Model,
    [int]$Step,
    [int]$Turn,
    [switch]$Detail,
    [string]$Csv
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$pathGiven = [bool]$Path
if (-not $Path) { $Path = @(Join-Path $repoRoot 'ModelTestingResults') }

$fallbackNames = @('Standard', 'Express', 'Overnight', 'Pickup', 'SameDay', 'IsKnown', 'IsExpedited', 'IsPickup', 'All', 'Normalize')
$memberRegex = [regex]'ShippingKinds\.([A-Za-z_][A-Za-z0-9_]*)'
$declRegex = [regex]'(?:const\s+string\s+|static\s+[\w<>\?\[\]]+\s+)([A-Za-z_][A-Za-z0-9_]*)\s*(?:=|\()'

function Get-EditDistance([string]$a, [string]$b) {
    $d = New-Object 'int[,]' ($a.Length + 1), ($b.Length + 1)
    for ($i = 0; $i -le $a.Length; $i++) { $d[$i, 0] = $i }
    for ($j = 0; $j -le $b.Length; $j++) { $d[0, $j] = $j }
    for ($i = 1; $i -le $a.Length; $i++) {
        for ($j = 1; $j -le $b.Length; $j++) {
            $cost = if ($a[$i - 1] -ceq $b[$j - 1]) { 0 } else { 1 }
            $d[$i, $j] = [Math]::Min([Math]::Min($d[($i - 1), $j] + 1, $d[$i, ($j - 1)] + 1), $d[($i - 1), ($j - 1)] + $cost)
        }
    }
    return $d[$a.Length, $b.Length]
}

function Get-Misspellings([string]$text, [System.Collections.Generic.HashSet[string]]$known) {
    # Emits each misspelled name to the pipeline; callers wrap the call in @() to get an array (empty when none).
    if ([string]::IsNullOrEmpty($text)) { return }
    foreach ($m in $memberRegex.Matches($text)) {
        $name = $m.Groups[1].Value
        # 'ShippingKinds.cs' is the file name and one- or two-letter names are placeholders in prose ('ShippingKinds.X').
        if ($name.Length -lt 3 -or $name -eq 'cs') { continue }
        if (-not $known.Contains($name)) { $name }
    }
}

function Get-TranscriptFiles {
    foreach ($p in $Path) {
        if (Test-Path -LiteralPath $p -PathType Leaf) { Get-Item -LiteralPath $p; continue }
        if (-not (Test-Path -LiteralPath $p)) { throw "Path not found: $p" }
        # With the default root only ShippingKind run folders are of interest; a folder named by the caller is scanned whole.
        [System.IO.Directory]::EnumerateFiles((Resolve-Path -LiteralPath $p).Path, 'transcript.json', [System.IO.SearchOption]::AllDirectories) |
            Where-Object { $_ -notmatch '[\\/]Worktree[\\/]' -and ($pathGiven -or $_ -match 'ShippingKind') } |
            ForEach-Object { Get-Item -LiteralPath $_ }
    }
}

$seen = [System.Collections.Generic.HashSet[string]]::new()
$rows = [System.Collections.Generic.List[object]]::new()
$skipped = [System.Collections.Generic.List[string]]::new()

foreach ($file in Get-TranscriptFiles) {
    if ($Step) {
        if ($file.FullName -notmatch "\($Step\)") { continue }
    }

    try { $t = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -Depth 100 }
    catch { $skipped.Add("$($file.FullName) :: unreadable ($($_.Exception.Message))"); continue }

    if ($Model -and ($t.ModelKey -notlike $Model)) { continue }
    $id = if ($t.RunId) { "$($t.RunId)|$($file.Name)" } else { $file.FullName }
    if (-not $seen.Add($id)) { continue }

    $turns = @($t.Turns)
    $known = [System.Collections.Generic.HashSet[string]]::new([string[]]$fallbackNames)
    $readTurn = $null
    foreach ($turnItem in $turns) {
        foreach ($call in @($turnItem.ToolCalls)) {
            $result = [string]$call.ResultJson
            if ($call.ToolName -eq 'ReadFile' -and $result -match 'class ShippingKinds') {
                foreach ($m in $declRegex.Matches($result)) { [void]$known.Add($m.Groups[1].Value) }
                if ($null -eq $readTurn) { $readTurn = [int]$turnItem.TurnNumber }
            }
        }
    }

    $decisionTurn = if ($Turn) { $Turn } elseif ($null -ne $readTurn) { $readTurn + 1 } else { $null }

    $reasonTypos = @(); $contentTypos = @(); $callTypos = @(); $runTypos = @(); $cs0117 = 0
    foreach ($turnItem in $turns) {
        $n = [int]$turnItem.TurnNumber
        $r = @(Get-Misspellings ([string]$turnItem.ModelMessage.ReasoningContent) $known)
        $c = @(Get-Misspellings ([string]$turnItem.ModelMessage.Content) $known)
        $a = @()
        foreach ($call in @($turnItem.ModelMessage.ToolCalls)) { $a += @(Get-Misspellings ([string]$call.ArgumentsJson) $known) }
        $runTypos += $r + $c + $a
        if ($n -eq $decisionTurn) { $reasonTypos = $r; $contentTypos = $c; $callTypos = $a }
        foreach ($call in @($turnItem.ToolCalls)) { if ([string]$call.ResultJson -match 'CS0117') { $cs0117++ } }
    }

    $nearOf = { param($name) ($known | ForEach-Object { Get-EditDistance $name $_ } | Measure-Object -Minimum).Minimum }
    $rows.Add([pscustomobject]@{
            Model         = [string]$t.ModelKey
            Folder        = $file.DirectoryName
            RunId         = [string]$t.RunId
            ReadTurn      = $readTurn
            DecisionTurn  = $decisionTurn
            TurnsInFile   = $turns.Count
            ReasoningTypo = ($reasonTypos.Count -gt 0)
            ContentTypo   = ($contentTypos.Count -gt 0)
            CallTypo      = ($callTypos.Count -gt 0)
            DecisionTypo  = (($reasonTypos.Count + $contentTypos.Count + $callTypos.Count) -gt 0)
            RunTypo       = ($runTypos.Count -gt 0)
            Cs0117Results = $cs0117
            Misspellings  = (($runTypos | Sort-Object -Unique) -join ';')
            DecisionTypos = ((@($reasonTypos) + @($contentTypos) + @($callTypos) | Sort-Object -Unique) -join ';')
            Near          = (($runTypos | Sort-Object -Unique | ForEach-Object { $d = & $nearOf $_; if ($d -le 3) { $_ } }) -join ';')
        })
}

if ($Csv) { $rows | Export-Csv -LiteralPath $Csv -NoTypeInformation -Encoding utf8 }

if ($rows.Count -eq 0) {
    'No matching transcripts.'
    foreach ($s in $skipped | Select-Object -First 10) { "SKIPPED: $s" }
    return
}

if ($Detail) {
    $rows | Sort-Object Model, Folder | ForEach-Object {
        '{0} | turn {1} | reasoning={2} content={3} call={4} | run typos: {5} | CS0117 results: {6} | {7}' -f $_.Model, $_.DecisionTurn, $_.ReasoningTypo, $_.ContentTypo, $_.CallTypo, $(if ($_.Misspellings) { $_.Misspellings } else { '-' }), $_.Cs0117Results, (Split-Path -Leaf $_.Folder)
    }
    ''
}

$rows | Group-Object Model | Sort-Object Name | ForEach-Object {
    $g = $_.Group
    $withTurn = @($g | Where-Object { $null -ne $_.DecisionTurn })
    $pct = { param($k, $n) if ($n -eq 0) { 'n/a' } else { '{0}/{1} ({2:P0})' -f $k, $n, ($k / $n) } }
    "Model: $($_.Name)"
    "  Transcripts:                    $($g.Count)"
    "  With a decision turn:           $($withTurn.Count)"
    "  Typo in decision turn:          $(& $pct @($withTurn | Where-Object DecisionTypo).Count $withTurn.Count)"
    "    in reasoning:                 $(& $pct @($withTurn | Where-Object ReasoningTypo).Count $withTurn.Count)"
    "    in message content:           $(& $pct @($withTurn | Where-Object ContentTypo).Count $withTurn.Count)"
    "    in tool-call arguments:       $(& $pct @($withTurn | Where-Object CallTypo).Count $withTurn.Count)"
    "  Typo anywhere in the run:       $(& $pct @($g | Where-Object RunTypo).Count $g.Count)"
    "  Runs with a CS0117 result:      $(& $pct @($g | Where-Object { $_.Cs0117Results -gt 0 }).Count $g.Count)"
    $hist = $g | ForEach-Object { $_.Misspellings -split ';' } | Where-Object { $_ } | Group-Object | Sort-Object Count -Descending | Select-Object -First 8
    if ($hist) { "  Misspellings (runs): " + (($hist | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join ', ') }
    ''
}
foreach ($s in $skipped | Select-Object -First 10) { "SKIPPED: $s" }
