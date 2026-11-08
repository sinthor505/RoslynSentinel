<#
.SYNOPSIS
    Regenerates the golden wire-envelope payloads used by the ResultPager tests
    (RoslynSentinel.Tests.Server/Resources/Golden/*.json).

.DESCRIPTION
    The payloads are synthetic but follow the real wire envelope of SentinelCallToolResult as the server emits it
    (camelCase; toolCall echo, solutionRoot, isServerBinaryStale, isError, successData | errorData, findings,
    totalRecords, hasMoreData, workspaceVersion, statusMessage, listSummary). Sizes match the cases named in
    docs/current/plans/closed/plan_wire_result_marshaller.md Step 3: a 2000-error Build, a 300-hit Search, a 44 KB
    Git(diff), a 122-site SemanticFindReplace preview and a big error. Deterministic: no randomness, no clock.
    The JSON is checked in; rerun this script only to regenerate it after a deliberate shape change.
#>
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\RoslynSentinel.Tests.Server\Resources\Golden')
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)

function New-List { , (New-Object System.Collections.Generic.List[object]) }

function Write-Golden([string]$name, $envelope) {
    $json = $envelope | ConvertTo-Json -Depth 12 -Compress
    [System.IO.File]::WriteAllText((Join-Path $OutDir $name), $json, $utf8)
    '{0}: {1} chars' -f $name, $json.Length
}

function New-Envelope([string]$tool, $arguments, [bool]$isError) {
    $e = [ordered]@{
        toolCall = [ordered]@{ toolCallId = 'a1b2c3d4e5f6'; name = $tool; arguments = $arguments }
        solutionRoot = 'C:\Users\Administrator\source\repos\RoslynSentinel'
        isServerBinaryStale = $true
        isError = $isError
    }
    return $e
}

# ---- 1. Build with 2000 errors ----
$details = New-List
for ($i = 1; $i -le 2000; $i++) {
    $file = ('RoslynSentinel.Tools.Basic/Generated/Module{0:D3}.cs' -f ($i % 250))
    $details.Add([ordered]@{
        id = 'CS0103'
        severity = 'Error'
        message = "The name 'value$i' does not exist in the current context"
        filePath = $file
        startLine = 10 + ($i % 400)
        startColumn = 9 + ($i % 31)
        endLine = 10 + ($i % 400)
        endColumn = 20 + ($i % 31)
        project = 'RoslynSentinel.Tools.Basic'
    })
}
$tail = (1..40 | ForEach-Object { "  Module{0:D3}.cs({1},{2}): error CS0103: The name 'value{3}' does not exist in the current context [RoslynSentinel.Tools.Basic.csproj]" -f $_, (10 + $_), 9, $_ }) -join "`n"
$e = New-Envelope 'Build' ([ordered]@{ level = 'fullBuildThrowaway'; maxDetails = 20 }) $false
$e.successData = [ordered]@{
    errors = 2000
    warnings = 0
    details = $details
    buildVerification = [ordered]@{ success = $false; level = 'fullBuildThrowaway'; errorCount = 2000; warningCount = 0; durationMs = 41873; outputTail = $tail }
}
$e.findings = @()
$e.totalRecords = 2000
$e.hasMoreData = $false
$e.workspaceVersion = 7
$e.statusMessage = 'Build Failed: 2000 error(s), 0 warning(s) in 0m41s.'
$e.listSummary = [ordered]@{ totalCount = 2000; fileCount = 250; byFile = @([ordered]@{ filePath = 'RoslynSentinel.Tools.Basic/Generated/Module000.cs'; count = 8 }); truncatedFileCount = 249 }
Write-Golden 'build_2000_errors.json' $e

# ---- 2. Search with 300 hits ----
$hits = New-List
for ($i = 1; $i -le 300; $i++) {
    $hits.Add([ordered]@{
        filePath = ('RoslynSentinel.Common/Area{0:D2}/Type{1:D3}.cs' -f ($i % 17), $i)
        line = 20 + ($i * 3)
        column = 13 + ($i % 20)
        preview = "var result$i = await _service.ExecuteAsync(request$i, cancellationToken).ConfigureAwait(false);"
        matchedAs = 'Regex'
        enclosingMember = "Execute$i"
    })
}
$e = New-Envelope 'Search' ([ordered]@{ mode = 'text'; query = 'ExecuteAsync'; reason = 'find callers of ExecuteAsync' }) $false
$e.successData = [ordered]@{ literalResults = @(); regexResults = $hits; regexOverlapCount = 0; regexPatternValid = $true }
$e.findings = @()
$e.totalRecords = 300
$e.hasMoreData = $false
$e.workspaceVersion = 7
$e.warningDetails = '300 matches found - returning first (300) matches scanned.'
$e.statusMessage = '300 matches across 300 files.'
$e.listSummary = [ordered]@{ totalCount = 300; fileCount = 300; byFile = @([ordered]@{ filePath = 'RoslynSentinel.Common/Area01/Type001.cs'; count = 1 }); truncatedFileCount = 299 }
Write-Golden 'search_300_hits.json' $e

# ---- 3. Git diff, about 44 KB ----
$sb = New-Object System.Text.StringBuilder
for ($f = 1; $f -le 7; $f++) {
    [void]$sb.Append("diff --git a/RoslynSentinel.Common/File$f.cs b/RoslynSentinel.Common/File$f.cs`n")
    [void]$sb.Append("index 1a2b3c$f..4d5e6f$f 100644`n")
    [void]$sb.Append("--- a/RoslynSentinel.Common/File$f.cs`n")
    [void]$sb.Append("+++ b/RoslynSentinel.Common/File$f.cs`n")
    for ($h = 1; $h -le 12; $h++) {
        [void]$sb.Append("@@ -$($h * 40),7 +$($h * 40),8 @@ public sealed class File${f}Hunk$h`n")
        [void]$sb.Append("     private readonly IService _service$h;`n")
        [void]$sb.Append("-    public int Compute$h(int value) => value * $h;`n")
        [void]$sb.Append("+    public int Compute$h(int value, int scale) => checked(value * $h * scale); // caf$([char]0xE9) <&> 'q'`n")
        [void]$sb.Append("+    public int Offset$h { get; init; } = $($h + $f);`n")
        [void]$sb.Append("     // context line number $h in file $f of the synthetic diff used by the ResultPager golden tests`n")
        [void]$sb.Append("     // another context line that keeps each hunk at a realistic width for review output $h`n")
    }
}
$diffText = $sb.ToString()
while ($diffText.Length -lt 44000) { $diffText += "+// padding line $($diffText.Length) to reach the golden size`n" }
$e = New-Envelope 'Git' ([ordered]@{ operation = 'diff'; reason = 'review working tree' }) $false
$e.successData = [ordered]@{ operation = 'diff'; success = $true; diff = $diffText; filesChanged = 7 }
$e.findings = @()
$e.hasMoreData = $false
$e.workspaceVersion = 7
$e.statusMessage = 'diff: 7 file(s) changed.'
Write-Golden 'git_diff_44kb.json' $e

# ---- 4. SemanticFindReplace preview with 122 sites ----
$sites = New-List
for ($i = 1; $i -le 122; $i++) {
    $refs = New-List
    foreach ($r in 1..3) { $refs.Add([ordered]@{ line = $i * 4 + $r; text = "usage $r of OldName$i" }) }
    $sites.Add([ordered]@{
        filePath = ('RoslynSentinel.Tools.Basic/Area{0:D2}/Consumer{1:D3}.cs' -f ($i % 12), $i)
        line = 30 + $i
        column = 17
        kind = 'invocation'
        before = "OldApi.Run(request$i)"
        after = "NewApi.RunAsync(request$i, cancellationToken)"
        context = "    var plan = Prepare($i);`n    var outcome = OldApi.Run(request$i);`n    Publish(outcome);"
        references = $refs
    })
}
$e = New-Envelope 'SemanticFindReplace' ([ordered]@{ pattern = 'OldApi.Run($x)'; replacement = 'NewApi.RunAsync($x, cancellationToken)'; apply = $false }) $false
$e.successData = [ordered]@{ mode = 'preview'; applied = $false; siteCount = 122; fileCount = 91; sites = $sites }
$e.findings = @()
$e.totalRecords = 122
$e.hasMoreData = $false
$e.workspaceVersion = 7
$e.statusMessage = 'Preview: 122 site(s) in 91 file(s); nothing written.'
$e.listSummary = [ordered]@{ totalCount = 122; fileCount = 91; byFile = @([ordered]@{ filePath = 'RoslynSentinel.Tools.Basic/Area01/Consumer001.cs'; count = 2 }); truncatedFileCount = 90 }
Write-Golden 'semantic_find_replace_preview_122_sites.json' $e

# ---- 5. A big error ----
$candidates = New-List
for ($i = 1; $i -le 400; $i++) {
    $candidates.Add([ordered]@{
        filePath = ('RoslynSentinel.Engines.Basic/Area{0:D2}/Candidate{1:D3}.cs' -f ($i % 9), $i)
        line = 15 + $i
        kind = 'method'
        signature = "void Run$($i % 7)(string name, int count$i)"
        container = "Candidate$i"
        isExact = ($i % 5 -eq 0)
    })
}
$detail = (1..220 | ForEach-Object { "closest window $_ : the requested snippet differs from this text at column $($_ % 60) after whitespace normalization" }) -join "`n"
$e = New-Envelope 'ReplaceSnippet' ([ordered]@{ action = 'apply'; filePath = 'RoslynSentinel.Common/Foo.cs' }) $true
$e.errorData = [ordered]@{
    errorCode = 'Ambiguous'
    message = 'ReplaceSnippet: oldContent matched 400 locations in RoslynSentinel.Common/Foo.cs; narrow it with matchLine, withinMember or lineBefore/lineAfter.'
    detail = $detail
    structuredDetail = $candidates
}
$e.findings = @()
$e.hasMoreData = $false
$e.workspaceVersion = 7
$e.statusMessage = 'ReplaceSnippet failed: Ambiguous.'
Write-Golden 'big_error.json' $e
