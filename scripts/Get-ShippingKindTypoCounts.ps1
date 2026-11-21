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
    plus a built-in fallback list. Each unknown name is classified against its closest real member M: 'near' (a slip of
    M) when editDistance(name, M) <= max(1, floor(len(M)/3)), or when name and M share a case-sensitive prefix of at least
    4 characters that covers at least half of the shorter one; otherwise 'invented' (a member that does not exist, e.g.
    None, Let or Kind). Only the 'ShippingKinds.<Name>' form is checked, so a bare misspelling in prose is not counted.

    Transcripts are deduplicated by RunId (archived runs are copied under nested folders).

.PARAMETER Path
    Transcript files and/or folders to scan (folders are searched recursively for transcript.json).
    Default: every ShippingKind run folder under <scratch>\ModelTestingResults.

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

.PARAMETER ReplayResults
    results.csv files of ShippingKindReplay runs (folders are searched recursively for results.csv; comma-separated
    entries are accepted). Each row's unknownNames are classified against the fallback member list, and per file the
    script prints the rows, the rows with a near miss, the rows with only invented names, and the near-miss names.
    Transcripts are not scanned unless -Path is also given.

.EXAMPLE
    <scratch> is ROSLYNSENTINEL_SCRATCH_ROOT, or ..\RoslynSentinel-ScratchDir next to the repo.
    pwsh scripts/Get-ShippingKindTypoCounts.ps1 -Step 4 -Model 'tiel*'
    Typo counts for the tiel-coder step 4 runs.

.EXAMPLE
    pwsh scripts/Get-ShippingKindTypoCounts.ps1 -ReplayResults <scratch>\ModelTestingResults\113\ShippingKindReplay_SamplesTheDecisionTurn
    Near-miss and invented counts over the samples of a ShippingKindReplay run.

.EXAMPLE
    pwsh scripts/Get-ShippingKindTypoCounts.ps1 -Path <scratch>\ModelTestingResults\113\ResumeShippingKindStep_SamplesTheNextTurns -Detail
    Typo counts over the samples of a ShippingKindResume run.
#>
[CmdletBinding()]
param(
    [string[]]$Path,
    [string]$Model,
    [int]$Step,
    [int]$Turn,
    [switch]$Detail,
    [string]$Csv,
    [string[]]$ReplayResults
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'roslynsentinel-scratch.ps1')
$pathGiven = [bool]$Path
if (-not $Path -and -not $ReplayResults) { $Path = @(Get-RoslynSentinelResultsRoot -RepoRoot $repoRoot) }

$fallbackNames = @('Standard', 'Express', 'Overnight', 'Pickup', 'SameDay', 'IsKnown', 'IsExpedited', 'IsPickup', 'All', 'Normalize')
$placeholderNames = @('ConstantName', 'Name', 'Xxx', 'Foo', 'Member', 'Constant')
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

function Test-NearMember([string]$name, [string]$member, [int]$distance) {
    # Near when the distance is within a third of the member's length (at least 1), or when the two share a
    # case-sensitive prefix of 4+ characters covering at least half of the shorter name.
    if ($distance -le [Math]::Max(1, [Math]::Floor($member.Length / 3))) { return $true }
    $shared = 0
    $limit = [Math]::Min($name.Length, $member.Length)
    while ($shared -lt $limit -and $name[$shared] -ceq $member[$shared]) { $shared++ }
    return ($shared -ge 4 -and ($shared * 2) -ge $limit)
}

function Get-NameClass([string]$name, [string[]]$members) {
    # 'near' when the closest real member passes Test-NearMember, otherwise 'invented'.
    $closest = $null
    $best = [int]::MaxValue
    foreach ($m in $members) {
        $d = Get-EditDistance $name $m
        if ($d -lt $best) { $best = $d; $closest = $m }
    }
    if ($null -ne $closest -and (Test-NearMember $name $closest $best)) { return 'near' }
    return 'invented'
}

function Select-NameClass([string[]]$names, [string[]]$members, [string]$class) {
    # Emits the names of the given class ('near' or 'invented'); callers wrap the call in @().
    @($names | Where-Object { (Get-NameClass $_ $members) -eq $class })
}

function Get-Misspellings([string]$text, [System.Collections.Generic.HashSet[string]]$known) {
    # Emits each misspelled name to the pipeline; callers wrap the call in @() to get an array (empty when none).
    if ([string]::IsNullOrEmpty($text)) { return }
    foreach ($m in $memberRegex.Matches($text)) {
        $name = $m.Groups[1].Value
        # 'ShippingKinds.cs' is the file name and one- or two-letter names are placeholders in prose ('ShippingKinds.X').
        if ($name.Length -lt 3 -or $name -eq 'cs' -or $name -in $placeholderNames) { continue }
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

    $members = [string[]]@($known)
    $decisionNames = @($reasonTypos) + @($contentTypos) + @($callTypos)
    $runNear = @(Select-NameClass $runTypos $members 'near' | Sort-Object -Unique)
    $runInvented = @(Select-NameClass $runTypos $members 'invented' | Sort-Object -Unique)
    $rows.Add([pscustomobject]@{
            Model            = [string]$t.ModelKey
            Folder           = $file.DirectoryName
            RunId            = [string]$t.RunId
            ReadTurn         = $readTurn
            DecisionTurn     = $decisionTurn
            TurnsInFile      = $turns.Count
            ReasoningNear    = (@(Select-NameClass $reasonTypos $members 'near').Count -gt 0)
            ContentNear      = (@(Select-NameClass $contentTypos $members 'near').Count -gt 0)
            CallNear         = (@(Select-NameClass $callTypos $members 'near').Count -gt 0)
            DecisionNear     = (@(Select-NameClass $decisionNames $members 'near').Count -gt 0)
            DecisionInvented = (@(Select-NameClass $decisionNames $members 'invented').Count -gt 0)
            RunNear          = ($runNear.Count -gt 0)
            RunInvented      = ($runInvented.Count -gt 0)
            Cs0117Results    = $cs0117
            Misspellings     = (($runTypos | Sort-Object -Unique) -join ';')
            DecisionTypos    = (($decisionNames | Sort-Object -Unique) -join ';')
            NearNames        = ($runNear -join ';')
            InventedNames    = ($runInvented -join ';')
        })
}

if ($Csv) { $rows | Export-Csv -LiteralPath $Csv -NoTypeInformation -Encoding utf8 }

if ($ReplayResults) {
    # Replay results: classify each row's unknownNames against the fallback members (no transcript needed).
    $replayEntries = @($ReplayResults | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    foreach ($entry in $replayEntries) {
        if (-not (Test-Path -LiteralPath $entry)) { throw "Path not found: $entry" }
        $csvFiles = if (Test-Path -LiteralPath $entry -PathType Container) {
            @(Get-ChildItem -LiteralPath $entry -Recurse -Filter results.csv -File)
        } else { @(Get-Item -LiteralPath $entry) }
        foreach ($csvFile in $csvFiles) {
            $replayRows = @(Import-Csv -LiteralPath $csvFile.FullName)
            $nearRowCount = 0
            $invRowCount = 0
            $nearCounts = @{}
            foreach ($replayRow in $replayRows) {
                $names = @([string]$replayRow.unknownNames -split '[;,]' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
                $nearNames = @(Select-NameClass $names $fallbackNames 'near')
                $invNames = @(Select-NameClass $names $fallbackNames 'invented')
                if ($nearNames.Count -gt 0) { $nearRowCount++ } elseif ($invNames.Count -gt 0) { $invRowCount++ }
                foreach ($n in $nearNames) { $nearCounts[$n] = 1 + [int]$nearCounts[$n] }
            }
            $csvFile.FullName
            "  Rows:                             $($replayRows.Count)"
            "  Rows with a near miss:            $nearRowCount"
            "  Rows with only invented names:    $invRowCount"
            $nearText = (($nearCounts.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "$($_.Key) x$($_.Value)" }) -join ', ')
            "  Near misses: $(if ($nearText) { $nearText } else { '-' })"
            ''
        }
    }
}

if ($rows.Count -eq 0) {
    if (-not $ReplayResults) { 'No matching transcripts.' }
    foreach ($s in $skipped | Select-Object -First 10) { "SKIPPED: $s" }
    return
}

if ($Detail) {
    $rows | Sort-Object Model, Folder | ForEach-Object {
        '{0} | turn {1} | near: reasoning={2} content={3} call={4} | run names: {5} | CS0117 results: {6} | {7}' -f $_.Model, $_.DecisionTurn, $_.ReasoningNear, $_.ContentNear, $_.CallNear, $(if ($_.Misspellings) { $_.Misspellings } else { '-' }), $_.Cs0117Results, (Split-Path -Leaf $_.Folder)
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
    "  Slip (near miss) in decision turn: $(& $pct @($withTurn | Where-Object DecisionNear).Count $withTurn.Count)"
    "  Invented member in decision turn:  $(& $pct @($withTurn | Where-Object DecisionInvented).Count $withTurn.Count)"
    "    near miss in reasoning:          $(& $pct @($withTurn | Where-Object ReasoningNear).Count $withTurn.Count)"
    "    near miss in message content:    $(& $pct @($withTurn | Where-Object ContentNear).Count $withTurn.Count)"
    "    near miss in tool-call arguments: $(& $pct @($withTurn | Where-Object CallNear).Count $withTurn.Count)"
    "  Slip (near miss) in run:           $(& $pct @($g | Where-Object RunNear).Count $g.Count)"
    "  Invented member in run:            $(& $pct @($g | Where-Object RunInvented).Count $g.Count)"
    "  Runs with a CS0117 result:         $(& $pct @($g | Where-Object { $_.Cs0117Results -gt 0 }).Count $g.Count)"
    $nearHist = $g | ForEach-Object { $_.NearNames -split ';' } | Where-Object { $_ } | Group-Object | Sort-Object Count -Descending | Select-Object -First 8
    if ($nearHist) { "  Near misses (runs): " + (($nearHist | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join ', ') }
    $invHist = $g | ForEach-Object { $_.InventedNames -split ';' } | Where-Object { $_ } | Group-Object | Sort-Object Count -Descending | Select-Object -First 8
    if ($invHist) { "  Invented (runs): " + (($invHist | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join ', ') }
    ''
}
foreach ($s in $skipped | Select-Object -First 10) { "SKIPPED: $s" }
