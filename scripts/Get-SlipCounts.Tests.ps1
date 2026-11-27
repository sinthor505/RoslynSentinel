# Regression check for Get-SlipCounts.ps1 (slip counts per model-eval run; T5a synthetic transcripts).
#
# Run:  pwsh -NoProfile -File scripts\Get-SlipCounts.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Plain assert script, same style as Get-ScenarioSummary.Tests.ps1. Writes synthetic run folders (one scenario per
# folder) under <scratch>\plan_slip_variants\t5a-<guid>, runs Get-SlipCounts.ps1 -Path on that folder and deletes it in
# a finally block. No model and no LM Studio host is used.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'roslynsentinel-scratch.ps1')

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

# Compares the named properties of one run row. Booleans, numbers and strings are compared by type.
function Assert-Fields([string]$Run, [hashtable]$Expected) {
    $row = $byName[$Run]
    if ($null -eq $row) { Assert-That $false "${Run}: run row present"; return }
    foreach ($k in $Expected.Keys) {
        $want = $Expected[$k]
        $actual = $row.$k
        if ($want -is [bool]) { $ok = ([bool]$actual -eq $want) }
        elseif ($want -is [string]) { $ok = ([string]$actual -eq $want) }
        else { $ok = ([math]::Abs([double]$actual - [double]$want) -lt 1e-9) }
        Assert-That $ok "$Run.$k = $want (actual: $actual)"
    }
}

$slipScript = Join-Path $PSScriptRoot 'Get-SlipCounts.ps1'
$scratch = Get-RoslynSentinelScratchRoot -RepoRoot (Split-Path -Parent $PSScriptRoot)
$root = Join-Path (Join-Path $scratch 'plan_slip_variants') ('t5a-' + [guid]::NewGuid().ToString('N'))

$fp = 'C:/synth/Foo.cs'
$okJson = '{"successData":{"applied":true}}'

function New-JsonArgs($table) { return ($table | ConvertTo-Json -Compress -Depth 5) }

function New-ErrJson([string]$Code, [string]$Message) {
    return (@{ errorData = @{ errorCode = $Code; message = $Message } } | ConvertTo-Json -Compress -Depth 5)
}

function New-SynthCall([string]$Tool, [string]$ArgumentsJson, [bool]$IsError, [string]$ResultJson) {
    return [ordered]@{ ToolName = $Tool; ArgumentsJson = $ArgumentsJson; IsError = $IsError; ResultJson = $ResultJson }
}

# Writes <root>\<Name>\ with scenario.txt, step-1.outcome.txt and transcript.json (one turn holding $Calls).
function New-SynthRun([string]$Name, [string]$Scenario, [string]$Variant, [string]$Outcome, [object[]]$Calls) {
    $dir = Join-Path $root $Name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText((Join-Path $dir 'scenario.txt'), "$Scenario`r`n", $utf8)
    [System.IO.File]::WriteAllText((Join-Path $dir 'step-1.outcome.txt'), "outcome=$Outcome stopReason=Completed promptVariant=$Variant`r`n", $utf8)
    $transcript = [ordered]@{ Turns = @( [ordered]@{ TurnNumber = 1; ToolCalls = @($Calls) } ) }
    [System.IO.File]::WriteAllText((Join-Path $dir 'transcript.json'), ($transcript | ConvertTo-Json -Depth 10), $utf8)
}

$byName = @{}
$pool = @()
$slipWarnings = @()
$summaries = @()

try {
    New-Item -ItemType Directory -Path $root -Force | Out-Null

    # Pass precedence: a passing run is Pass even with a write slip and a read slip-free read.
    New-SynthRun 'pass' 'T5a-pass' 'VA' 'Pass' @(
        (New-SynthCall 'ReadFile' (New-JsonArgs @{ filePath = $fp }) $false $okJson),
        (New-SynthCall 'ReplaceSnippet' (New-JsonArgs @{ filePath = $fp; oldContent = 'a'; newContent = 'b' }) $true (New-ErrJson 'NotFound' 'oldContent not found. Closest windows in the file: score 0.95 at line 4.'))
    )

    # Read slip only: one unparseable ReadFile.
    New-SynthRun 'readslip' 'T5a-readslip' 'VA' 'Fail' @(
        (New-SynthCall 'ReadFile' '{"filePath": ' $false $okJson)
    )

    # No slips: a Member view (Read) and a Build (Other).
    New-SynthRun 'noslip' 'T5a-noslip' 'VA' 'Fail' @(
        (New-SynthCall 'Member' (New-JsonArgs @{ filePath = $fp; operation = 'view'; memberName = 'Foo' }) $false '{"successData":{"member":"Foo"}}'),
        (New-SynthCall 'Build' '{}' $false $okJson)
    )

    # ReadFile refused InvalidArgument (ReadMalformedArgs).
    New-SynthRun 'readrefused' 'T5a-readrefused' 'VA' 'Fail' @(
        (New-SynthCall 'ReadFile' (New-JsonArgs @{ filePath = $fp; startLine = 1 }) $true (New-ErrJson 'InvalidArgument' 'startLine must be positive.'))
    )

    # NearWindowNotFound: score 0.95 counts, score 0.60 does not.
    New-SynthRun 'near' 'T5a-near' 'VA' 'Fail' @(
        (New-SynthCall 'ReplaceSnippet' (New-JsonArgs @{ filePath = $fp; oldContent = 'alpha'; newContent = 'beta' }) $true (New-ErrJson 'NotFound' 'oldContent not found. Closest windows in the file: score 0.95 at line 12.')),
        (New-SynthCall 'ReplaceSnippet' (New-JsonArgs @{ filePath = $fp; oldContent = 'gamma'; newContent = 'delta' }) $true (New-ErrJson 'NotFound' 'oldContent not found. Closest windows in the file: score 0.60 at line 30.'))
    )

    # One write hitting two columns (NearWindowNotFound and SwallowedKey) counts once in SlippedWrites.
    New-SynthRun 'double' 'T5a-double' 'VA' 'Fail' @(
        (New-SynthCall 'ReplaceSnippet' (New-JsonArgs @{ filePath = $fp; oldContent = 'x "newContent": y'; newContent = 'z' }) $true (New-ErrJson 'NotFound' 'oldContent not found. Closest windows in the file: score 0.95 at line 7.'))
    )

    # SwallowedKey alone: a successful write whose oldContent holds the key.
    New-SynthRun 'swallow' 'T5a-swallow' 'VA' 'Fail' @(
        (New-SynthCall 'ReplaceSnippet' (New-JsonArgs @{ filePath = $fp; oldContent = 'x "newContent": y'; newContent = 'z' }) $false $okJson)
    )

    # Escape copy: a successful ReadFile caches the file, then a write copies a backslash-quote line from the source.
    $src = "public class Foo`n{`n" + '        var s = "hello";' + "`n}"
    $escOld = '        var s = \"hello\";'
    New-SynthRun 'escape' 'T5a-escape' 'VA' 'Fail' @(
        (New-SynthCall 'ReadFile' (New-JsonArgs @{ filePath = $fp }) $false (New-JsonArgs @{ successData = @{ source = $src } })),
        (New-SynthCall 'ReplaceSnippet' (New-JsonArgs @{ filePath = $fp; oldContent = $escOld; newContent = '        var s = 1;' }) $false $okJson)
    )

    # MalformedArgs: unparseable ArgumentsJson on a write.
    New-SynthRun 'malwrite' 'T5a-malwrite' 'VA' 'Fail' @(
        (New-SynthCall 'ReplaceSnippet' '{"filePath": ' $true (New-ErrJson 'InvalidArgument' 'Arguments are not valid JSON.'))
    )

    # MalformedArgs: a write refused InvalidArgument with parsed arguments.
    New-SynthRun 'invalidwrite' 'T5a-invalidwrite' 'VA' 'Fail' @(
        (New-SynthCall 'ReplaceSnippet' (New-JsonArgs @{ filePath = $fp; oldContent = 'a'; newContent = 'b' }) $true (New-ErrJson 'InvalidArgument' 'newContent is required.'))
    )

    # GateRefusals only: ValidationFailed is not a slip.
    New-SynthRun 'gate' 'T5a-gate' 'VA' 'Fail' @(
        (New-SynthCall 'ReplaceSnippet' (New-JsonArgs @{ filePath = $fp; oldContent = 'a'; newContent = 'b' }) $true (New-ErrJson 'ValidationFailed' 'Result does not parse as C#.'))
    )

    # UnparsedNotFound: NotFound with oldContent in the text but no window list and no "No candidates".
    New-SynthRun 'unparsed' 'T5a-unparsed' 'VA' 'Fail' @(
        (New-SynthCall 'ReplaceSnippet' (New-JsonArgs @{ filePath = $fp; oldContent = 'a'; newContent = 'b' }) $true (New-ErrJson 'NotFound' 'The oldContent text was not found in the file.'))
    )

    # Write and Other categories: ApplyDiff (unifiedDiff), Member replace (newMemberSource), Build (Other).
    New-SynthRun 'categories' 'T5a-categories' 'VA' 'Fail' @(
        (New-SynthCall 'ApplyDiff' (New-JsonArgs @{ filePath = $fp; unifiedDiff = "--- a/Foo.cs`n+++ b/Foo.cs" }) $false $okJson),
        (New-SynthCall 'Member' (New-JsonArgs @{ filePath = $fp; operation = 'replace'; memberName = 'Foo'; newMemberSource = 'void Foo() { }' }) $false $okJson),
        (New-SynthCall 'Build' '{}' $false $okJson)
    )

    # Other malformed: ChangeSignature refused InvalidArgument counts in OtherMalformedArgs only.
    New-SynthRun 'changesig' 'T5a-changesig' 'VA' 'Fail' @(
        (New-SynthCall 'ChangeSignature' (New-JsonArgs @{ filePath = $fp; methodName = 'Foo'; signature = 'int Foo(int a)' }) $true (New-ErrJson 'InvalidArgument' 'signature does not name a method.'))
    )

    # Pooling: two VP runs of one scenario and a VQ run of the same scenario.
    New-SynthRun 'pool-a' 'T5a-pool' 'VP' 'Fail' @(
        (New-SynthCall 'ReplaceSnippet' (New-JsonArgs @{ filePath = $fp; oldContent = 'alpha'; newContent = 'beta' }) $true (New-ErrJson 'NotFound' 'oldContent not found. Closest windows in the file: score 0.95 at line 2.')),
        (New-SynthCall 'ReplaceSnippet' (New-JsonArgs @{ filePath = $fp; oldContent = 'gamma'; newContent = 'delta' }) $false $okJson)
    )
    New-SynthRun 'pool-b' 'T5a-pool' 'VP' 'Fail' @(
        (New-SynthCall 'ReplaceSnippet' (New-JsonArgs @{ filePath = $fp; oldContent = 'epsilon'; newContent = 'zeta' }) $false $okJson),
        (New-SynthCall 'ReadFile' '{"filePath": ' $false $okJson)
    )
    New-SynthRun 'pool-c' 'T5a-pool' 'VQ' 'Pass' @(
        (New-SynthCall 'ReadFile' (New-JsonArgs @{ filePath = $fp }) $false $okJson)
    )

    # Per-run output, one root invocation over every synthetic folder. Warnings are captured.
    $rowsRaw = @(& $slipScript -Path $root -WarningVariable slipWarnings)
    foreach ($r in $rowsRaw) { $byName[(Split-Path -Leaf $r.RunFolder)] = $r }
    Assert-That ($rowsRaw.Count -eq 17) "one row per synthetic run folder (17, actual $($rowsRaw.Count))"

    Assert-Fields 'pass' @{ Outcome = 'Pass'; Passed = $true; RunClass = 'Pass'; Scenario = 'T5a-pass'; PromptVariant = 'VA'; Writes = 1; SlippedWrites = 1; NearWindowNotFound = 1; Reads = 1; ReadMalformedArgs = 0 }
    Assert-Fields 'readslip' @{ Outcome = 'Fail'; Passed = $false; RunClass = 'FailWithSlips'; Reads = 1; ReadMalformedArgs = 1; ReadSlipRate = 1; Writes = 0; SlipRate = 0; SlippedWrites = 0 }
    Assert-Fields 'noslip' @{ RunClass = 'FailNoSlips'; Reads = 1; ReadMalformedArgs = 0; OtherCalls = 1; OtherMalformedArgs = 0; Writes = 0; SlipRate = 0; SlippedWrites = 0 }
    Assert-Fields 'readrefused' @{ RunClass = 'FailWithSlips'; Reads = 1; ReadMalformedArgs = 1; ReadSlipRate = 1; SlippedWrites = 0 }
    Assert-Fields 'near' @{ Writes = 2; NearWindowNotFound = 1; SwallowedKey = 0; MalformedArgs = 0; UnparsedNotFound = 0; SlippedWrites = 1; SlipRate = 0.5; RunClass = 'FailWithSlips' }
    Assert-Fields 'double' @{ Writes = 1; NearWindowNotFound = 1; SwallowedKey = 1; SlippedWrites = 1; SlipRate = 1 }
    Assert-Fields 'swallow' @{ Writes = 1; SwallowedKey = 1; NearWindowNotFound = 0; SlippedWrites = 1; SlipRate = 1 }
    Assert-Fields 'escape' @{ Writes = 1; Reads = 1; EscapeCopyWrites = 1; EscapeCopyLines = 1; SlippedWrites = 1; SlipRate = 1; ReadMalformedArgs = 0; RunClass = 'FailWithSlips' }
    Assert-Fields 'malwrite' @{ Writes = 1; MalformedArgs = 1; SlippedWrites = 1; NearWindowNotFound = 0; SwallowedKey = 0; EscapeCopyWrites = 0 }
    Assert-Fields 'invalidwrite' @{ Writes = 1; MalformedArgs = 1; SlippedWrites = 1; GateRefusals = 0; NearWindowNotFound = 0 }
    Assert-Fields 'gate' @{ Writes = 1; GateRefusals = 1; MalformedArgs = 0; SlippedWrites = 0; SlipRate = 0; RunClass = 'FailNoSlips' }
    Assert-Fields 'unparsed' @{ Writes = 1; UnparsedNotFound = 1; NearWindowNotFound = 0; SlippedWrites = 0; RunClass = 'FailNoSlips' }
    Assert-Fields 'categories' @{ Writes = 2; Reads = 0; OtherCalls = 1; SlippedWrites = 0; SlipRate = 0; ReadSlipRate = 0; RunClass = 'FailNoSlips' }
    Assert-Fields 'changesig' @{ Writes = 0; Reads = 0; OtherCalls = 1; OtherMalformedArgs = 1; MalformedArgs = 0; ReadMalformedArgs = 0; SlippedWrites = 0; RunClass = 'FailNoSlips' }
    Assert-Fields 'pool-a' @{ Writes = 2; SlippedWrites = 1; SlipRate = 0.5; RunClass = 'FailWithSlips' }
    Assert-Fields 'pool-b' @{ Writes = 1; SlippedWrites = 0; Reads = 1; ReadMalformedArgs = 1; RunClass = 'FailWithSlips' }
    Assert-Fields 'pool-c' @{ Passed = $true; RunClass = 'Pass'; Reads = 1; ReadSlipRate = 0; PromptVariant = 'VQ' }

    $warnTexts = @($slipWarnings | ForEach-Object { [string]$_ })
    Assert-That ($warnTexts.Count -eq 1 -and $warnTexts[0] -match 'oldContent') 'UnparsedNotFound emits one Write-Warning naming oldContent'

    # Summary: one object per Scenario and PromptVariant, pooled values.
    $summaries = @(& $slipScript -Path $root -Summary -WarningAction SilentlyContinue)
    $pool = @($summaries | Where-Object { $_.Scenario -eq 'T5a-pool' })
    Assert-That ($pool.Count -eq 2) "Summary: T5a-pool gives one object per PromptVariant (2, actual $($pool.Count))"

    $vp = @($pool | Where-Object { $_.PromptVariant -eq 'VP' })
    if ($vp.Count -eq 1) {
        $s = $vp[0]
        Assert-That ($s.Runs -eq 2 -and $s.FailWithSlips -eq 2 -and $s.Pass -eq 0) 'Summary VP: 2 runs, both FailWithSlips'
        Assert-That ($s.Writes -eq 3 -and $s.SlippedWrites -eq 1) 'Summary VP: pooled Writes 3, SlippedWrites 1'
        Assert-That ([math]::Abs([double]$s.SlipRate - (1 / 3)) -lt 1e-4) 'Summary VP: SlipRate pooled 1/3 (not mean of per-run rates 0.25)'
        Assert-That ($s.Reads -eq 1 -and $s.ReadMalformedArgs -eq 1 -and [math]::Abs([double]$s.ReadSlipRate - 1) -lt 1e-9) 'Summary VP: pooled read slip 1 of 1'
    }
    else { Assert-That $false 'Summary VP: exactly one object' }

    $vq = @($pool | Where-Object { $_.PromptVariant -eq 'VQ' })
    if ($vq.Count -eq 1) {
        Assert-That ($vq[0].Runs -eq 1 -and $vq[0].Pass -eq 1 -and $vq[0].SlipRate -eq 0) 'Summary VQ: 1 Pass run, SlipRate 0 with no writes'
    }
    else { Assert-That $false 'Summary VQ: exactly one object' }

    $near = @($summaries | Where-Object { $_.Scenario -eq 'T5a-near' })
    Assert-That ($near.Count -eq 1 -and $near[0].Writes -eq 2 -and $near[0].SlippedWrites -eq 1 -and [math]::Abs([double]$near[0].SlipRate - 0.5) -lt 1e-9) 'Summary T5a-near: Writes 2, SlippedWrites 1, SlipRate 0.5'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) assertion(s) failed."
    exit 1
}
Write-Host 'All Get-SlipCounts assertions passed.'
exit 0
