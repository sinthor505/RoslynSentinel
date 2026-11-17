<#
.SYNOPSIS
    Finds the streamed tokens where the model writes a misspelled ShippingKinds member (Overnight -> Oovernight/Oversnight ...).

.DESCRIPTION
    agent.log records every streamed text delta of the model's message content as a line "LM Studio streaming: <delta>" with a
    millisecond timestamp (LmStudioAgentClient logs one line per server-sent delta, normally one token). This script rebuilds each
    turn's streamed text from those lines, finds every 'ShippingKinds.<Name>' whose <Name> is not a real member, and shows the token
    sequence around it, so you can see which token commits the typo. For each typo it reports:
      - the tokens around the typo, with the first token that stops being a prefix of a real member marked "<<" (first bad token)
        and the token before it (last good token);
      - the typo's tokenization (the slice of each delta from the '.' to the end of the name, e.g. .O|overn|ight), next to how the
        correct names were tokenized in the same logs (e.g. .Over|night).
    The summary counts first-bad tokens, last-good tokens and tokenizations over all typos, so a recurring token such as ".O" stands out.

    What it can and cannot see: only the CONTENT channel is logged per delta, and tool-call arguments are logged as one line per call.
    Reasoning deltas are accumulated without being logged, so a typo that first appears in the reasoning is visible here only when the
    model copies it into its message content. Lines of the form "LM Studio reasoning: <delta>" are read as well, if a build logs them.
    A log written before a delta logging existed, or a turn with no message content, yields no streams.

    Samples are told apart by the "Agent run starting in <folder>" line, turns by the "Turn N: model responded" line that follows a
    turn's deltas. Multi-line deltas are rejoined from their continuation lines.

.PARAMETER Path
    agent.log files and/or folders (searched recursively for agent.log; Worktree folders are skipped).

.PARAMETER Known
    Real member names. Default: Standard, Express, Overnight, Pickup, SameDay, IsKnown, IsExpedited, IsPickup, All, Normalize.

.PARAMETER Context
    Tokens shown before the first bad token (default 5; 3 are always shown after it).

.PARAMETER MaxExamples
    Print at most this many typo examples (default 12); the summary always counts all of them.

.PARAMETER Csv
    Write one row per typo to this CSV file.

.EXAMPLE
    pwsh scripts/Find-TypoTokenStream.ps1 -Path c:\tmp\replay-tiel-original
    Token view of every misspelled ShippingKinds member in that run's agent.log.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string[]]$Path,
    [string[]]$Known = @('Standard', 'Express', 'Overnight', 'Pickup', 'SameDay', 'IsKnown', 'IsExpedited', 'IsPickup', 'All', 'Normalize'),
    [int]$Context = 5,
    [int]$MaxExamples = 12,
    [string]$Csv
)

$ErrorActionPreference = 'Stop'

# Same exclusions as Get-ShippingKindTypoCounts.ps1: the file name and prose placeholders are not misspellings.
$placeholderNames = @('ConstantName', 'Name', 'Xxx', 'Foo', 'Member', 'Constant')
$memberRegex = [regex]'ShippingKinds\.([A-Za-z_][A-Za-z0-9_]*)'
$lineRegex = [regex]'^(\d{2}:\d{2}:\d{2}\.\d{3}) \[\w+\] [^:]*: (.*)$'
$deltaPrefixes = @(
    @{ Text = 'LM Studio streaming: '; Channel = 'content' },
    @{ Text = 'LM Studio reasoning: '; Channel = 'reasoning' }
)

$typos = [System.Collections.Generic.List[object]]::new()
$correctSignatures = [System.Collections.Generic.List[string]]::new()
$stats = @{ Files = 0; Streams = 0; Deltas = 0 }

function Get-LogFiles {
    foreach ($p in $Path) {
        if (Test-Path -LiteralPath $p -PathType Leaf) { Get-Item -LiteralPath $p; continue }
        if (-not (Test-Path -LiteralPath $p)) { throw "Path not found: $p" }
        [System.IO.Directory]::EnumerateFiles((Resolve-Path -LiteralPath $p).Path, 'agent.log', [System.IO.SearchOption]::AllDirectories) |
            Where-Object { $_ -notmatch '[\\/]Worktree[\\/]' } |
            ForEach-Object { Get-Item -LiteralPath $_ }
    }
}

function Show-Token([string]$text) { '[' + ($text -replace "`r", '\r' -replace "`n", '\n') + ']' }

function Find-DeltaIndex([int[]]$starts, [int]$position) {
    $i = [Array]::BinarySearch($starts, $position)
    # Not found: ~i is the first start beyond the position, so the delta holding it is the one before.
    if ($i -lt 0) { $i = (-bnot $i) - 1 }
    return [Math]::Max(0, $i)
}

function Search-Stream([string]$label, $turn, [string]$channel, $deltas) {
    if ($deltas.Count -eq 0) { return }
    $stats.Streams++
    $stats.Deltas += $deltas.Count

    $starts = [int[]]::new($deltas.Count)
    $builder = [System.Text.StringBuilder]::new()
    for ($i = 0; $i -lt $deltas.Count; $i++) {
        $starts[$i] = $builder.Length
        [void]$builder.Append($deltas[$i].Text)
    }
    $text = $builder.ToString()

    foreach ($m in $memberRegex.Matches($text)) {
        $name = $m.Groups[1].Value
        $isKnown = $Known -ccontains $name
        if (-not $isKnown -and ($name.Length -lt 3 -or $name -eq 'cs' -or $name -in $placeholderNames)) { continue }

        $nameStart = $m.Groups[1].Index
        $dot = $nameStart - 1
        $end = $nameStart + $name.Length
        $first = Find-DeltaIndex $starts $dot
        $last = Find-DeltaIndex $starts ($end - 1)

        # The slice of each delta from the '.' to the end of the name: how this spelling was tokenized.
        $parts = for ($i = $first; $i -le $last; $i++) {
            $from = [Math]::Max($starts[$i], $dot)
            $to = [Math]::Min($starts[$i] + $deltas[$i].Text.Length, $end)
            $text.Substring($from, $to - $from)
        }
        $signature = $parts -join '|'

        if ($isKnown) { $correctSignatures.Add($signature); continue }

        # First k characters of the name that no real member starts with: the typo is committed at character k.
        $k = 0
        for ($n = 1; $n -le $name.Length; $n++) {
            $prefix = $name.Substring(0, $n)
            if (-not ($Known | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) })) { $k = $n; break }
        }
        $truncated = ($k -eq 0)    # a real member's prefix: the name was cut short rather than misspelled
        if ($truncated) { $k = $name.Length }

        $goodPrefix = $name.Substring(0, $k - 1)
        $nearest = $Known | Where-Object { $_.StartsWith($goodPrefix, [StringComparison]::Ordinal) } | Select-Object -First 1
        $expected = if ($nearest -and $nearest.Length -ge $k) { [string]$nearest[$k - 1] } else { '' }
        $bad = Find-DeltaIndex $starts ($nameStart + $k - 1)
        $badText = $deltas[$bad].Text
        $good = if ($bad -gt 0) { $deltas[$bad - 1].Text } else { '' }

        $shown = for ($i = [Math]::Max(0, $bad - $Context); $i -le [Math]::Min($deltas.Count - 1, $bad + 3); $i++) {
            (Show-Token $deltas[$i].Text) + $(if ($i -eq $bad) { '<<' } else { '' })
        }

        $typos.Add([pscustomobject]@{
                Log          = $label
                Turn         = $turn
                Channel      = $channel
                Time         = $deltas[$bad].Time
                Typo         = $name
                Nearest      = [string]$nearest
                Truncated    = $truncated
                BadChar      = [string]$name[$k - 1]
                ExpectedChar = $expected
                FirstBad     = $badText
                LastGood     = $good
                Signature    = $signature
                Tokens       = ($shown -join '')
            })
    }
}

foreach ($file in Get-LogFiles) {
    $stats.Files++
    $label = Split-Path -Leaf (Split-Path -Parent $file.FullName)
    $turn = '?'
    $deltas = [System.Collections.Generic.List[object]]::new()
    $channel = 'content'
    $lastDelta = $null

    foreach ($line in [System.IO.File]::ReadLines($file.FullName)) {
        $m = $lineRegex.Match($line)
        if (-not $m.Success) {
            # Continuation of a delta that contained a line break.
            if ($null -ne $lastDelta) { $lastDelta.Text += "`n" + $line }
            continue
        }

        $lastDelta = $null
        $time = $m.Groups[1].Value
        $message = $m.Groups[2].Value

        $isDelta = $false
        foreach ($p in $deltaPrefixes) {
            if ($message.StartsWith($p.Text, [StringComparison]::Ordinal) -and -not $message.StartsWith($p.Text + 'tool call ', [StringComparison]::Ordinal)) {
                $delta = @{ Time = $time; Text = $message.Substring($p.Text.Length) }
                $deltas.Add($delta)
                $lastDelta = $delta
                $channel = $p.Channel
                $isDelta = $true
                break
            }
        }
        if ($isDelta) { continue }

        if ($message -match '^Agent run starting in (.+?)\. Exposing') {
            Search-Stream $label $turn $channel $deltas
            $deltas.Clear()
            $label = (Split-Path -Leaf $Matches[1])
            $turn = '?'
        }
        elseif ($message -match '^Turn (\d+): model responded') {
            Search-Stream $label $Matches[1] $channel $deltas
            $deltas.Clear()
            $turn = $Matches[1]
        }
    }
    Search-Stream $label $turn $channel $deltas
}

if ($Csv) { $typos | Select-Object Log, Turn, Channel, Time, Typo, Nearest, Truncated, BadChar, ExpectedChar, FirstBad, LastGood, Signature | Export-Csv -LiteralPath $Csv -NoTypeInformation -Encoding utf8 }

"Logs: $($stats.Files)   streams with deltas: $($stats.Streams)   deltas: $($stats.Deltas)   typos: $($typos.Count)   correct member uses: $($correctSignatures.Count)"
if ($stats.Deltas -eq 0) { 'No streamed deltas found: the log has no "LM Studio streaming:" lines (older log, or turns with no message content).' }
''

foreach ($t in $typos | Select-Object -First $MaxExamples) {
    $kind = if ($t.Truncated) { 'cut short' } else { "typo of $($t.Nearest)" }
    "$($t.Log) turn $($t.Turn) @$($t.Time) [$($t.Channel)]: ShippingKinds.$($t.Typo) ($kind)"
    "  tokens: $($t.Tokens)"
    if ($t.Truncated) { "  the name stops after the valid prefix; first token past it: $(Show-Token $t.FirstBad)" }
    else { "  after the valid prefix the model wrote '$($t.BadChar)' where '$($t.ExpectedChar)' was expected; first bad token $(Show-Token $t.FirstBad), last good token $(Show-Token $t.LastGood); tokenized $($t.Signature)" }
}
if ($typos.Count -gt $MaxExamples) { "... $($typos.Count - $MaxExamples) more (raise -MaxExamples or use -Csv)" }
''

function Show-Histogram([string]$title, $values) {
    $rows = @($values | Group-Object | Sort-Object Count -Descending | Select-Object -First 8)
    if ($rows.Count -eq 0) { return }
    $title
    foreach ($r in $rows) { '  {0,4}  {1}' -f $r.Count, $(if ($r.Name -eq '') { '(none)' } else { Show-Token $r.Name }) }
}

if ($typos.Count -gt 0) {
    Show-Histogram 'First bad token (the token that stops matching any real member):' ($typos | ForEach-Object { $_.FirstBad })
    Show-Histogram 'Last good token (the token before it):' ($typos | ForEach-Object { $_.LastGood })
    Show-Histogram 'Typo tokenization (from the "." to the end of the name):' ($typos | ForEach-Object { $_.Signature })
}
if ($correctSignatures.Count -gt 0) {
    ''
    Show-Histogram 'Correct member tokenization, for comparison:' $correctSignatures
}
