# Test suite for enforce-dto-justification.ps1.
#
# Run:  powershell -NoProfile -File .claude/hooks/enforce-dto-justification.Tests.ps1
#
# Exit 2 from the hook means DENY; anything else means the call is allowed through.
# Plain script, not Pester. Every case runs a fixture copy of the hook inside a temp root, so the
# dto-hook.jsonl log lands in that temp root and never in the real repo's .claude\journal.

# Deliberately NOT 'Stop': a denying hook writes its reason to stderr, and under
# Windows PowerShell 5.1 an ErrorActionPreference of Stop turns native-command stderr into a
# terminating error - the expected path for deny cases.
$ErrorActionPreference = 'Continue'
$hook         = Join-Path $PSScriptRoot 'enforce-dto-justification.ps1'
$settingsPath = Join-Path $PSScriptRoot '..\settings.json'

function Invoke-Hook([string]$payload, [string]$hookPath = $hook) {
    # Output kept in $script:lastHookOut so a case can assert WHY it was denied (see 'match' below).
    $script:lastHookOut = ($payload | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $hookPath 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 2) { 'DENY' } else { 'allow' }
}

# Fixture: a temp root with a copy of the hook under .claude\hooks (so the hook's repo root is the
# temp root) and one on-disk file, RoslynSentinel.Common\Existing.cs, declaring OldResult.
# 'dtothrow' additionally injects a throw into the hook body to prove the hook fails open.
$fixtureRoots = @{}
function Get-FixtureHook([string]$kind) {
    if ($fixtureRoots.ContainsKey($kind)) { return Join-Path $fixtureRoots[$kind] '.claude\hooks\enforce-dto-justification.ps1' }
    $root    = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-dto-fx-$kind-" + [guid]::NewGuid().ToString('N'))
    $hookDir = Join-Path $root '.claude\hooks'
    New-Item -ItemType Directory -Path $hookDir -Force | Out-Null
    $hp = Join-Path $hookDir 'enforce-dto-justification.ps1'
    Copy-Item -LiteralPath $hook -Destination $hp -Force
    $srcDir = Join-Path $root 'RoslynSentinel.Common'
    New-Item -ItemType Directory -Path $srcDir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $srcDir 'Existing.cs') -Value 'public record OldResult(int A);' -Encoding UTF8
    if ($kind -eq 'dtothrow') {
        $txt    = Get-Content -LiteralPath $hp -Raw
        $anchor = '$toolInput = $payload.tool_input'
        if (-not $txt.Contains($anchor)) { throw 'dtothrow fixture: injection anchor not found in hook' }
        Set-Content -LiteralPath $hp -Value $txt.Replace($anchor, "throw 'injected test failure'; $anchor") -Encoding UTF8
    }
    $fixtureRoots[$kind] = $root
    $hp
}

# Payload builders. Paths use the {ROOT} placeholder, replaced per case with the fixture root.
$newDto = "NEW-DTO: FooResult (dto, common.results)`nEvaluated: FooInfo - carries no result shape; none - nothing reusable`nWire impact: none"
$gen    = 'Add the result type for the new flow'
$inFile = '{ROOT}\RoslynSentinel.Common\Foo.cs'

function New-MemberPayload([string]$op, [string]$src, [string]$reason, [string]$file = $inFile) {
    @{ tool_name = 'mcp__x__Member'; tool_input = @{ operation = $op; filePath = $file; newMemberSource = $src; reason = $reason } }
}
function New-CreatePayload([string]$kind, [string]$name, [string]$reason) {
    @{ tool_name = 'mcp__x__CreateFile'; tool_input = @{ filePath = '{ROOT}\RoslynSentinel.Common\New.cs'; typeKind = $kind; typeName = $name; reason = $reason } }
}
function New-WritePayload([string]$op, [string]$file, [string]$content, [string]$reason) {
    @{ tool_name = 'mcp__x__WriteFile'; tool_input = @{ operation = $op; filePath = $file; content = $content; reason = $reason } }
}

function New-SnippetPayload([string]$file, [string]$old, [string]$new, [string]$reason) {
    @{ tool_name = 'mcp__x__ReplaceSnippet'; tool_input = @{ filePath = $file; oldContent = $old; newContent = $new; reason = $reason } }
}

$cases = @(
    # --- DENY: triggering types with no justification ---
    @{ n = 'Member addTopLevelType record, generic reason'; want = 'DENY'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public record FooResult(int A);' $gen) }
    @{ n = 'Member addTopLevelType sealed class FooInfo'; want = 'DENY'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public sealed class FooInfo { public int A {get;set;} }' $gen) }
    @{ n = 'Member addMember nested record'; want = 'DENY'; fx = 'dto'
       p = (New-MemberPayload 'addMember' '    public sealed record Nested(int A);' $gen) }
    @{ n = 'CreateFile typeKind record'; want = 'DENY'; fx = 'dto'
       p = (New-CreatePayload 'record' 'FooResult' $gen) }
    @{ n = 'CreateFile typeKind class'; want = 'DENY'; fx = 'dto'
       p = (New-CreatePayload 'class' 'FooInfo' $gen) }
    @{ n = 'WriteFile CreateFile with a new record'; want = 'DENY'; fx = 'dto'
       p = (New-WritePayload 'CreateFile' '{ROOT}\RoslynSentinel.Common\New.cs' 'public record FooResult(int A);' $gen) }
    @{ n = 'WriteFile ReplaceFile adds record not on disk'; want = 'DENY'; fx = 'dto'
       p = (New-WritePayload 'ReplaceFile' '{ROOT}\RoslynSentinel.Common\Existing.cs' "public record OldResult(int A);`npublic record NewResult(int B);" $gen) }
    @{ n = 'NEW-DTO for A, content declares A and B'; want = 'DENY'; fx = 'dto'
       p = (New-WritePayload 'CreateFile' '{ROOT}\RoslynSentinel.Common\New.cs' "public record FooResult(int A);`npublic record BarRow(int B);" $newDto) }
    @{ n = 'NEW-DTO present but no Evaluated line'; want = 'DENY'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public record FooResult(int A);' "NEW-DTO: FooResult (dto, common.results)`nWire impact: none") }
    @{ n = 'NEW-DTO names a different type than declared'; want = 'DENY'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public record FooResult(int A);' "NEW-DTO: BarResult (dto, common.results)`nEvaluated: X - no`nWire impact: none") }
    @{ n = 'DTO-EXEMPT with fewer than 3 chars'; want = 'DENY'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public record FooResult(int A);' 'DTO-EXEMPT: ab') }
    @{ n = 'deny output carries NEW-DTO template'; want = 'DENY'; fx = 'dto'; match = 'NEW-DTO:'
       p = (New-MemberPayload 'addTopLevelType' 'public record FooResult(int A);' $gen) }
    @{ n = 'log probe: deny (generic reason)'; want = 'DENY'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public record LogProbeDeny(int A);' 'generic log probe deny') }

    # --- ALLOW: valid justification routes ---
    @{ n = 'DTO-EXEMPT reason'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public record FooResult(int A);' 'DTO-EXEMPT: legit non-dto thing') }
    @{ n = 'NEW-DTO full form (name + Evaluated + Wire impact)'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public record FooResult(int A);' $newDto) }
    @{ n = 'log probe: allow-exempt'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public record LogProbeExempt(int A);' 'DTO-EXEMPT: log probe exempt') }

    # --- ALLOW: types the hook does not treat as DTOs ---
    @{ n = 'static class FooMath'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public static class FooMath { }' $gen) }
    @{ n = 'abstract class FooBase'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public abstract class FooBase { }' $gen) }
    @{ n = 'class FooEngine (suffix)'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public class FooEngine { }' $gen) }
    @{ n = 'class FooImpl (suffix)'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public class FooImpl { }' $gen) }
    @{ n = 'class FooException (suffix)'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public class FooException : System.Exception { }' $gen) }
    @{ n = 'interface IFoo'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public interface IFoo { }' $gen) }
    @{ n = 'enum FooKind'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public enum FooKind { A, B }' $gen) }
    @{ n = 'CreateFile typeKind staticClass'; want = 'allow'; fx = 'dto'
       p = (New-CreatePayload 'staticClass' 'FooMath' $gen) }
    @{ n = 'CreateFile typeKind interface'; want = 'allow'; fx = 'dto'
       p = (New-CreatePayload 'interface' 'IFoo' $gen) }

    # --- ALLOW: Member operations that never trigger ---
    @{ n = 'TypedMember property typed as a record'; want = 'allow'; fx = 'dto'
       p = @{ tool_name = 'mcp__x__TypedMember'; tool_input = @{ filePath = $inFile; typedKind = 'property'; typedName = 'Result'; typedType = 'FooResult'; reason = $gen } } }
    @{ n = 'Member remove'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'remove' 'public record FooResult(int A);' $gen) }
    @{ n = 'Member replace of a type already on disk'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'replace' 'public record OldResult(int A, int B);' $gen '{ROOT}\RoslynSentinel.Common\Existing.cs') }
    @{ n = 'Member replace introducing a new record'; want = 'DENY'; fx = 'dto'
       p = (New-MemberPayload 'replace' 'public record FooResult(int A);' $gen) }
    @{ n = 'Member view'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'view' 'public record FooResult(int A);' $gen) }

    # --- ALLOW: WriteFile ReplaceFile where the type already exists on disk ---
    @{ n = 'WriteFile ReplaceFile, record already on disk'; want = 'allow'; fx = 'dto'
       p = (New-WritePayload 'ReplaceFile' '{ROOT}\RoslynSentinel.Common\Existing.cs' 'public record OldResult(int A);' $gen) }

    # --- ALLOW: out-of-scope paths ---
    @{ n = 'path in RoslynSentinel.Tests.Foo project'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public record FooResult(int A);' $gen '{ROOT}\RoslynSentinel.Tests.Foo\Bar.cs') }
    @{ n = 'path under Worktree\'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public record FooResult(int A);' $gen '{ROOT}\Worktree\Foo.cs') }
    @{ n = 'path under bin\'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public record FooResult(int A);' $gen '{ROOT}\RoslynSentinel.Common\bin\Foo.cs') }
    @{ n = 'path under Samples\'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public record FooResult(int A);' $gen '{ROOT}\Samples\Foo.cs') }
    @{ n = 'path outside the repo (C:\tmp)'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public record FooResult(int A);' $gen 'C:\tmp\Foo.cs') }
    @{ n = 'non-.cs path'; want = 'allow'; fx = 'dto'
       p = (New-MemberPayload 'addTopLevelType' 'public record FooResult(int A);' $gen '{ROOT}\RoslynSentinel.Common\Foo.md') }

    # --- ReplaceSnippet / ApplyDiff ---
    @{ n = 'ReplaceSnippet adds a new record'; want = 'DENY'; fx = 'dto'
       p = (New-SnippetPayload '{ROOT}\RoslynSentinel.Common\Existing.cs' 'public record OldResult(int A);' "public record OldResult(int A);`npublic record NewResult(int B);" $gen) }
    @{ n = 'ReplaceSnippet edits a type already on disk'; want = 'allow'; fx = 'dto'
       p = (New-SnippetPayload '{ROOT}\RoslynSentinel.Common\Existing.cs' 'record OldResult(int A)' 'record OldResult(int A, int B)' $gen) }
    @{ n = 'ReplaceSnippet batchEdits adds a new record'; want = 'DENY'; fx = 'dto'
       p = @{ tool_name = 'mcp__x__ReplaceSnippet'; tool_input = @{ reason = $gen; batchEdits = @(
            @{ filePath = '{ROOT}\RoslynSentinel.Common\Existing.cs'; oldContent = 'int A'; newContent = 'int A, int C' },
            @{ filePath = '{ROOT}\RoslynSentinel.Common\Other.cs'; oldContent = ''; newContent = 'public record MovedResult(int A);' }) } } }
    @{ n = 'ReplaceSnippet with no type token'; want = 'allow'; fx = 'dto'
       p = (New-SnippetPayload '{ROOT}\RoslynSentinel.Common\Existing.cs' 'int A' 'int B' $gen) }
    @{ n = 'ApplyDiff files format adds a new record'; want = 'DENY'; fx = 'dto'
       p = @{ tool_name = 'mcp__x__ApplyDiff'; tool_input = @{ changesetFormat = 'files'; reason = $gen; changes = @{ '{ROOT}\RoslynSentinel.Common\New.cs' = 'public record FooResult(int A);' } } } }
    @{ n = 'ApplyDiff files format, type already on disk'; want = 'allow'; fx = 'dto'
       p = @{ tool_name = 'mcp__x__ApplyDiff'; tool_input = @{ changesetFormat = 'files'; reason = $gen; changes = @{ '{ROOT}\RoslynSentinel.Common\Existing.cs' = 'public record OldResult(int A, int B);' } } } }
    @{ n = 'ApplyDiff diff format adds a new record'; want = 'DENY'; fx = 'dto'
       p = @{ tool_name = 'mcp__x__ApplyDiff'; tool_input = @{ changesetFormat = 'diff'; reason = $gen; filePath = '{ROOT}\RoslynSentinel.Common\Existing.cs'; unifiedDiff = "--- a/Existing.cs`n+++ b/Existing.cs`n@@ -1 +1,2 @@`n public record OldResult(int A);`n+public record NewResult(int B);" } } }
    @{ n = 'ApplyDiff diff format, record renamed within the diff'; want = 'allow'; fx = 'dto'
       p = @{ tool_name = 'mcp__x__ApplyDiff'; tool_input = @{ changesetFormat = 'diff'; reason = $gen; filePath = '{ROOT}\RoslynSentinel.Common\Existing.cs'; unifiedDiff = "--- a/Existing.cs`n+++ b/Existing.cs`n@@ -1 +1 @@`n-public record OldResult(int A);`n+public record OldResult(int A, int B);" } } }

    # --- ALLOW: other tools never checked ---
    @{ n = 'Edit payload with record text'; want = 'allow'; fx = 'dto'
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = '{ROOT}\RoslynSentinel.Common\Foo.cs'; new_string = 'public record FooResult(int A);' } } }
    @{ n = 'Bash payload with record text'; want = 'allow'; fx = 'dto'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = "echo 'public record FooResult(int A);'" } } }
)

$pass = 0; $fail = 0

foreach ($c in $cases) {
    $hookPath = Get-FixtureHook $c.fx
    $fxRoot   = Split-Path (Split-Path (Split-Path $hookPath))
    # {ROOT} -> the fixture root, JSON-escaped so the backslashes survive ConvertFrom-Json.
    $json = ($c.p | ConvertTo-Json -Depth 5 -Compress).Replace('{ROOT}', $fxRoot.Replace('\', '\\'))
    $got  = Invoke-Hook $json $hookPath

    $why = ''
    if ($got -eq $c.want -and $c.match -and $got -eq 'DENY' -and $script:lastHookOut -notmatch [regex]::Escape($c.match)) {
        $got = 'DENY(wrong reason)'; $why = " expected text: $($c.match)"
    }
    if ($got -eq $c.want) { $pass++; $mark = 'ok  ' }
    else                  { $fail++; $mark = 'FAIL' }
    '{0} {1,-50} want={2,-5} got={3}{4}' -f $mark, $c.n, $c.want, $got, $why | Write-Host
}

# Robustness: the hook must never block on bad input (fail open).
$robustHook = Get-FixtureHook 'dto'
foreach ($bad in @('', 'not json at all', '{"tool_name":"Member"}')) {
    $got = Invoke-Hook $bad $robustHook
    $label = if ($bad -eq '') { '(empty stdin)' } else { $bad }
    if ($got -eq 'allow') { $pass++; $mark = 'ok  ' } else { $fail++; $mark = 'FAIL' }
    '{0} {1,-50} want={2,-5} got={3}' -f $mark, "fail-open: $label", 'allow', $got | Write-Host
}

# Fail open on an internal error: the injected throw would otherwise deny this addTopLevelType record.
$throwHook = Get-FixtureHook 'dtothrow'
$throwRoot = Split-Path (Split-Path (Split-Path $throwHook))
$throwJson = (New-MemberPayload 'addTopLevelType' 'public record FooResult(int A);' $gen | ConvertTo-Json -Depth 5 -Compress).Replace('{ROOT}', $throwRoot.Replace('\', '\\'))
$got = Invoke-Hook $throwJson $throwHook
if ($got -eq 'allow') { $pass++; $mark = 'ok  ' } else { $fail++; $mark = 'FAIL' }
'{0} {1,-50} want={2,-5} got={3}' -f $mark, 'fail-open: injected exception in hook body', 'allow', $got | Write-Host

# Log: the deny probe and the allow-exempt probe must each leave a line in the fixture's journal.
$logPath = Join-Path $fixtureRoots['dto'] '.claude\journal\dto-hook.jsonl'
$logLines = @()
if (Test-Path -LiteralPath $logPath) { $logLines = @(Get-Content -LiteralPath $logPath) }
$denyLogged   = @($logLines | Where-Object { $_ -match 'generic log probe deny' -and $_ -match '"outcome":"deny"' }).Count -gt 0
$exemptLogged = @($logLines | Where-Object { $_ -match 'log probe exempt' -and $_ -match '"outcome":"allow-exempt"' }).Count -gt 0
if ($denyLogged)   { $pass++; $mark = 'ok  ' } else { $fail++; $mark = 'FAIL' }
'{0} {1,-50}' -f $mark, 'log: deny line written to dto-hook.jsonl' | Write-Host
if ($exemptLogged) { $pass++; $mark = 'ok  ' } else { $fail++; $mark = 'FAIL' }
'{0} {1,-50}' -f $mark, 'log: allow-exempt line written to dto-hook.jsonl' | Write-Host

# Registration: the real settings.json must run the hook for Member, CreateFile and WriteFile.
$regOk = $false; $regDetail = 'no PreToolUse entry runs enforce-dto-justification.ps1'
try {
    $cfg = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    foreach ($entry in @($cfg.hooks.PreToolUse)) {
        $runs = @($entry.hooks | Where-Object { (@($_.args) -join ' ') -match 'enforce-dto-justification\.ps1' })
        if ($runs.Count -eq 0) { continue }
        $matcher = [string]$entry.matcher
        $names = @('mcp__srv__Member', 'mcp__srv__CreateFile', 'mcp__srv__WriteFile', 'mcp__srv__ReplaceSnippet', 'mcp__srv__ApplyDiff')
        $missing = @($names | Where-Object { -not [regex]::IsMatch($_, "^(?:$matcher)$") })
        if ($missing.Count -eq 0) { $regOk = $true; $regDetail = "matcher '$matcher'" }
        else { $regDetail = "matcher '$matcher' does not match: $($missing -join ', ')" }
        break
    }
}
catch { $regDetail = "could not parse settings.json: $($_.Exception.Message)" }
if ($regOk) { $pass++; $mark = 'ok  ' } else { $fail++; $mark = 'FAIL' }
'{0} {1,-50} {2}' -f $mark, 'registration: PreToolUse entry covers Member/CreateFile/WriteFile', $regDetail | Write-Host

foreach ($r in $fixtureRoots.Values) { Remove-Item -LiteralPath $r -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ''
Write-Host "pass=$pass fail=$fail"
if ($fail -gt 0) { exit 1 }
exit 0
