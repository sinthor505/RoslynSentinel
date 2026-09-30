# Regression harness: replays 10 historical friction cases pulled from real session
# transcripts (see the transcript survey behind this file's commit) against the current
# hooks, and asserts each one is now redirected/blocked/warned as intended.
#
# Run:  pwsh -NoProfile -File .claude/hooks/friction-cases.Tests.ps1
#
# Each case invokes the actual hook script (enforce-dogfood.ps1 or
# check-build-staleness.ps1) with the real historical tool_input, exactly as
# .claude/settings.json's PreToolUse/PostToolUse wiring would. Kept as a separate file from
# enforce-dogfood.Tests.ps1 (which tests the hook's rules in the abstract) - this file's job
# is proving specific, cited past failures are now caught, not exercising every branch.

$ErrorActionPreference = 'Continue'
$dogfoodHook  = Join-Path $PSScriptRoot 'enforce-dogfood.ps1'
$buildHook    = Join-Path $PSScriptRoot 'check-build-staleness.ps1'

function Invoke-DogfoodHook([string]$payloadJson, [string]$hookPath = $dogfoodHook) {
    $result = $payloadJson | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $hookPath 2>&1
    [pscustomobject]@{
        Verdict = if ($LASTEXITCODE -eq 2) { 'DENY' } else { 'allow' }
        Output  = ($result | Out-String)
    }
}

function Invoke-BuildHook([string]$payloadJson) {
    $result = $payloadJson | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $buildHook 2>&1
    [pscustomobject]@{
        Verdict = if ($LASTEXITCODE -eq 2) { 'DENY' } else { 'allow' }
        Output  = ($result | Out-String)
    }
}

$cases = @()

# Throwaway git repos with a copy of the hook inside (the hook resolves its repo root from its
# own location, ..\.. from the hook folder). 'empty' = nothing staged, 'staged' = one staged path.
$fixtureRoots = @{}
function Get-FixtureHook([string]$kind) {
    if (-not $fixtureRoots.ContainsKey($kind)) {
        $root = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-dogfood-fc-$kind-" + [guid]::NewGuid().ToString('N'))
        $hookDir = Join-Path $root '.claude\hooks'
        New-Item -ItemType Directory -Path $hookDir -Force | Out-Null
        Copy-Item $dogfoodHook (Join-Path $hookDir 'enforce-dogfood.ps1') -Force
        & git -C $root init -q 2>&1 | Out-Null
        if ($kind -eq 'staged') {
            Set-Content -LiteralPath (Join-Path $root 'a.txt') -Value 'x'
            & git -C $root add -- a.txt 2>&1 | Out-Null
        }
        $fixtureRoots[$kind] = $root
    }
    Join-Path $fixtureRoots[$kind] '.claude\hooks\enforce-dogfood.ps1'
}

# --- Category 1: Grep on .cs instead of Search/FindReferences ---------------------------
# Real case: transcript 15c6db84-7045-490a-a35f-290986147527.jsonl line 24 - Grep for a test
# method name across the whole repo instead of Search(mode: symbol) / FindReferences.
$cases += [pscustomobject]@{
    N = 'FC1: Grep for symbol name repo-wide (transcript 15c6db84 line 24)'
    Kind = 'dogfood'
    Payload = @{ tool_name = 'Grep'; tool_input = @{ pattern = 'FindUnsafeLazyInit_Flags_DoubleCheckedLockingWithoutVolatile'; output_mode = 'files_with_matches' } }
    Want = 'DENY'
    MustContainInOutput = @('Search(mode: "symbol"', 'FindUnsafeLazyInit_Flags_DoubleCheckedLockingWithoutVolatile')
}

# Real case: same transcript line 33 - Grep with an explicit .cs path and -A context, still
# a raw-text search over a known C# file instead of GetMethodSource/Search.
$cases += [pscustomobject]@{
    N = 'FC2: Grep with explicit .cs path + line context (transcript 15c6db84 line 33)'
    Kind = 'dogfood'
    Payload = @{ tool_name = 'Grep'; tool_input = @{ pattern = 'FindUnsafeLazyInitAsync'; path = 'RoslynSentinel.Tests.Advanced\AsyncThreadingToolsTests.cs'; output_mode = 'content'; '-n' = $true; '-A' = 40 } }
    Want = 'DENY'
    MustContainInOutput = @('Search(mode:', 'GetMethodSource')
}

# --- Category 2: ReplaceSnippet filePath+batchEdits together / bad ordering --------------
# Real case: transcript 65ec410b... line 247 - server rejected filePath+batchEdits together
# with a generic "supply either .../oldContent/newContent or 'edits', not both" error that
# doesn't show the corrected shape. This hook should catch it before the server round-trip.
$cases += [pscustomobject]@{
    N = 'FC3: ReplaceSnippet filePath + batchEdits together (transcript 65ec410b line 247)'
    Kind = 'dogfood'
    Payload = @{ tool_name = 'ReplaceSnippet'; tool_input = @{ filePath = 'RoslynSentinel.Common\McpToolSchemaPatcher.cs'; batchEdits = @(@{ filePath = 'RoslynSentinel.Common\McpToolSchemaPatcher.cs'; oldContent = 'old'; newContent = 'new' }) } }
    Want = 'DENY'
    MustContainInOutput = @('filePath and batchEdits', 'batchEdits: [')
}

# Real case: transcript 65ec410b line 252 - a batch with two call sites invoking
# ApplyReplaceSnippetLimits before any entry in the batch defines it (CS0103 x2).
$cases += [pscustomobject]@{
    N = 'FC4: batchEdits call sites before the method they reference (transcript 65ec410b line 252)'
    Kind = 'dogfood'
    Payload = @{ tool_name = 'ReplaceSnippet'; tool_input = @{ batchEdits = @(
        @{ filePath = 'A.cs'; oldContent = 'x'; newContent = 'var r = ApplyReplaceSnippetLimits(input);' },
        @{ filePath = 'B.cs'; oldContent = 'y'; newContent = 'var r2 = ApplyReplaceSnippetLimits(other);' }
    ) } }
    Want = 'allow'   # heuristic warning, not a block - must not stop a possibly-good edit
    MustContainInStderr = @('ApplyReplaceSnippetLimits', 'move its definition earlier')
}

# --- Category 3: git via shell (regression check - hook already covers this) -------------
# No genuine bypass was found in the transcript survey (the hook already catches every
# add/commit/diff/log attempt seen); kept here as a live regression guard rather than a
# "new" friction case, per the survey's explicit "no evidence" finding for this category.
$cases += [pscustomobject]@{
    N = 'FC5: git add && commit via Bash (regression guard, no historical bypass found)'
    Kind = 'dogfood'
    Payload = @{ tool_name = 'Bash'; tool_input = @{ command = 'git add . && git commit -m "wip"' } }
    Want = 'DENY'
    MustContainInOutput = @('Git(operation: "stage"', 'Git(operation: "commit"')
}
$cases += [pscustomobject]@{
    N = 'FC6: git log via Bash (regression guard)'
    Kind = 'dogfood'
    Payload = @{ tool_name = 'Bash'; tool_input = @{ command = 'git log --oneline -5' } }
    Want = 'DENY'
    MustContainInOutput = @('Git(operation:')
}

# --- Category 4: Build green but server ran stale binary ---------------------------------
# Real case: transcript 5f8e6021... lines 3219-3238 - McpServerStatus's PID/build timestamp
# was unchanged across two commits; the model had to notice and diagnose this manually.
# Simulated here via the same serverInfo envelope every live tool response actually carries
# (confirmed against a real Git call: buildTimeUtc matches the DLL's on-disk
# LastWriteTimeUtc to the second) - a buildTimeUtc older than the newest source file.
$tmpRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-staleness-fc-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmpRoot -Force | Out-Null
$freshSource = Join-Path $tmpRoot 'RoslynSentinel.Tools.Basic\ServerStatusTools.cs'
New-Item -ItemType Directory -Path (Split-Path $freshSource) -Force | Out-Null
Set-Content -LiteralPath $freshSource -Value '// edited just now'
(Get-Item $freshSource).LastWriteTimeUtc = (Get-Date).ToUniversalTime()
$staleBuildTimeUtc = (Get-Date).ToUniversalTime().AddHours(-2).ToString('o')

$cases += [pscustomobject]@{
    N = 'FC7: Build succeeds but serverInfo.buildTimeUtc predates newest source edit (transcript 5f8e6021 lines 3219-3238)'
    Kind = 'buildstale'
    RepoRootOverride = $tmpRoot
    Payload = @{ tool_name = 'mcp__root_roslyn_sentinel_advanced_stdio__Build'; tool_input = @{}; tool_response = @{ serverInfo = @{ buildTimeUtc = $staleBuildTimeUtc; binaryPath = 'C:\fake\bin-vscode\deadbeef\Advanced\RoslynSentinel.Server.Advanced.dll'; pid = 12345 }; isSuccess = $true } }
    Want = 'allow'   # detect-only, never blocks
    MustContainInStderr = @('running a binary older than', 'McpServerControl(operation: stop)')
}

# --- Category 5: commit missing Co-Authored-By / scope creep -----------------------------
# Real case: transcript 59a28fea... - 6 of 11 committed files were never edited in that
# session's transcript; commit had no scope=listed/explicit files, just a bare commit call.
# The hook's rule changed after the Git tool did: a scope-less commit no longer stages anything
# (it commits exactly what is staged), so the dirty-worktree count stopped measuring sweep risk.
# The category-5 shape (bare commit, nothing listed) is now denied only when the index is empty;
# with paths staged it is allowed. Both run against a throwaway repo (RepoFixture) so the result
# does not depend on the real repo's state.
$cases += [pscustomobject]@{
    N = 'FC8: bare commit, no files/scope, nothing staged (category 5, revised rule)'
    Kind = 'dogfood'
    RepoFixture = 'empty'
    Payload = @{ tool_name = 'mcp__root_roslyn_sentinel_advanced_stdio__Git'; tool_input = @{ operation = 'commit'; message = "Rename HasMorePages to HasMoreData`n`nCo-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>" } }
    Want = 'DENY'
    MustContainInOutput = @('nothing staged', 'files:')
}

$cases += [pscustomobject]@{
    N = 'FC8b: bare commit, no files/scope, paths staged (commits exactly the index)'
    Kind = 'dogfood'
    RepoFixture = 'staged'
    Payload = @{ tool_name = 'mcp__root_roslyn_sentinel_advanced_stdio__Git'; tool_input = @{ operation = 'commit'; message = "Rename HasMorePages to HasMoreData`n`nCo-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>" } }
    Want = 'allow'
}

$cases += [pscustomobject]@{
    N = 'FC8c: commit with files only (no scope) is an explicit listing'
    Kind = 'dogfood'
    RepoFixture = 'empty'
    Payload = @{ tool_name = 'mcp__root_roslyn_sentinel_advanced_stdio__Git'; tool_input = @{ operation = 'commit'; files = 'A.cs,B.cs'; message = "Rename HasMorePages to HasMoreData`n`nCo-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>" } }
    Want = 'allow'
}

# Every real commit sampled in the survey DID have a trailer, so this exercises the rule
# itself (not a cited failure) - included to prove the trailer check fires when it's missing.
$cases += [pscustomobject]@{
    N = 'FC9: commit message missing Co-Authored-By trailer'
    Kind = 'dogfood'
    Payload = @{ tool_name = 'mcp__root_roslyn_sentinel_advanced_stdio__Git'; tool_input = @{ operation = 'commit'; scope = 'listed'; files = 'A.cs,B.cs'; message = 'Fix the thing' } }
    Want = 'DENY'
    MustContainInOutput = @('Co-Authored-By')
}

$cases += [pscustomobject]@{
    N = 'FC10: commit with explicit scope=listed + trailer passes cleanly'
    Kind = 'dogfood'
    Payload = @{ tool_name = 'mcp__root_roslyn_sentinel_advanced_stdio__Git'; tool_input = @{ operation = 'commit'; scope = 'listed'; files = 'A.cs,B.cs'; message = "Fix the thing`n`nCo-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>" } }
    Want = 'allow'
}

$pass = 0; $fail = 0

foreach ($c in $cases) {
    $json = $c.Payload | ConvertTo-Json -Depth 8 -Compress
    $result = if ($c.Kind -eq 'buildstale') {
        # check-build-staleness.ps1 resolves repo root from $PSScriptRoot (..\.. from the
        # hook's own folder) purely to find the newest .cs source file - point a temp copy of
        # the hook at the fixture root instead of touching the real repo tree.
        $hookDir = Join-Path $c.RepoRootOverride '.claude\hooks'
        New-Item -ItemType Directory -Path $hookDir -Force | Out-Null
        Copy-Item $buildHook (Join-Path $hookDir 'check-build-staleness.ps1') -Force
        $output = $json | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $hookDir 'check-build-staleness.ps1') 2>&1
        [pscustomobject]@{ Verdict = if ($LASTEXITCODE -eq 2) { 'DENY' } else { 'allow' }; Output = ($output | Out-String) }
    } else {
        if ($c.PSObject.Properties.Match('RepoFixture').Count -gt 0 -and $c.RepoFixture) {
            Invoke-DogfoodHook $json (Get-FixtureHook $c.RepoFixture)
        } else {
            Invoke-DogfoodHook $json
        }
    }

    $verdictOk = ($result.Verdict -eq $c.Want)
    $contentOk = $true
    foreach ($needle in @($c.MustContainInOutput)) {
        if ($needle -and $result.Output -notmatch [regex]::Escape($needle)) { $contentOk = $false }
    }
    foreach ($needle in @($c.MustContainInStderr)) {
        if ($needle -and $result.Output -notmatch [regex]::Escape($needle)) { $contentOk = $false }
    }

    if ($verdictOk -and $contentOk) { $pass++; $mark = 'ok  ' }
    else { $fail++; $mark = 'FAIL' }
    '{0} {1,-85} want={2,-5} got={3}' -f $mark, $c.N, $c.Want, $result.Verdict | Write-Host
    if (-not ($verdictOk -and $contentOk)) {
        Write-Host "      output: $($result.Output -replace "`r?`n", ' | ')"
    }
}

Remove-Item -LiteralPath $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue
foreach ($r in $fixtureRoots.Values) { Remove-Item -LiteralPath $r -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ''
Write-Host "pass=$pass fail=$fail"
if ($fail -gt 0) { exit 1 }
exit 0
