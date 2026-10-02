# Test suite for the tool-experience journal hooks (journal-session-start.ps1,
# journal-log-call.ps1, journal-stop-nudge.ps1).
#
# Run:  pwsh -NoProfile -File .claude/hooks/journal-hooks.Tests.ps1
#
# Each hook runs under powershell.exe exactly as settings.json launches it, against a temp
# journal directory (ROSLYNSENTINEL_JOURNAL_DIR). Payload shapes mirror the ones captured
# live in Step 0 of docs/current/plans/plan_mcp_tool_experience_journal.md.

$ErrorActionPreference = 'Continue'
$hooks = $PSScriptRoot
$dir = Join-Path ([System.IO.Path]::GetTempPath()) ('rs-journal-test-' + [guid]::NewGuid().ToString('N'))
$env:ROSLYNSENTINEL_JOURNAL_DIR = $dir
$env:ROSLYNSENTINEL_JOURNAL_NUDGE_THRESHOLD = '3'

$pass = 0; $fail = 0
function Check([string]$name, [bool]$cond, [string]$info = '') {
    if ($cond) { $script:pass++; $mark = 'ok  ' } else { $script:fail++; $mark = 'FAIL' }
    Write-Host ('{0} {1}{2}' -f $mark, $name, $(if (-not $cond -and $info) { "  [$info]" } else { '' }))
}

function Invoke-JournalHook([string]$script, $payload) {
    $stdin = if ($payload -is [string]) { $payload } else { $payload | ConvertTo-Json -Depth 6 -Compress }
    $out = $stdin | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $hooks $script) 2>&1
    [pscustomobject]@{ Exit = $LASTEXITCODE; Out = (($out | Out-String).Trim()) }
}

$sid = 'aaaabbbb-1111-2222-3333-444455556666'
$sid8 = 'aaaabbbb'
function Get-Base { $f = Get-ChildItem -LiteralPath $dir -Filter "*_$sid8.md" -ErrorAction SilentlyContinue | Select-Object -First 1; if ($f) { $f.FullName -replace '\.md$', '' } }
function Get-CallLines { $b = Get-Base; if ($b -and (Test-Path "$b.calls.jsonl")) { @(Get-Content "$b.calls.jsonl" | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }) } else { @() } }

function Mcp([string]$tool, [string]$text, [string]$event = 'PostToolUse', $extra = @{}) {
    $p = @{ session_id = $sid; hook_event_name = $event; tool_name = "mcp__root_roslyn_sentinel_advanced_stdio__$tool"
            tool_input = @{ reason = 'test call here' }; duration_ms = 42; mcp_server = @{ name = 'root_roslyn_sentinel_advanced_stdio'; source = 'project' } }
    if ($event -eq 'PostToolUseFailure') { $p.error = $text; $p.is_interrupt = $false } else { $p.tool_response = @{ type = 'text'; text = $text } }
    foreach ($k in $extra.Keys) { $p[$k] = $extra[$k] }
    Invoke-JournalHook 'journal-log-call.ps1' $p
}
function Builtin([string]$tool, $toolInput, $extra = @{}) {
    $p = @{ session_id = $sid; hook_event_name = 'PostToolUse'; tool_name = $tool; tool_input = $toolInput; tool_response = @{}; duration_ms = 5 }
    foreach ($k in $extra.Keys) { $p[$k] = $extra[$k] }
    Invoke-JournalHook 'journal-log-call.ps1' $p
}
function Stop([bool]$active = $false) { Invoke-JournalHook 'journal-stop-nudge.ps1' @{ session_id = $sid; hook_event_name = 'Stop'; stop_hook_active = $active } }
function IsBlock($r) { $r.Out -match '"decision"\s*:\s*"block"' }

$okText   = '{"toolCall":{"name":"ReadFile"},"isSuccess":true,"successData":{}}'
$failText = '{"toolCall":{"name":"ReadFile"},"isSuccess":false,"errorData":{"errorCode":"SolutionNotLoaded","message":"x"}}'

try {
    # --- fail-open on bad input, before anything exists --------------------------------
    foreach ($h in 'journal-session-start.ps1', 'journal-log-call.ps1', 'journal-stop-nudge.ps1') {
        foreach ($bad in @('', 'not json', '{}')) {
            $r = Invoke-JournalHook $h $bad
            Check "fail-open $h on '$bad'" ($r.Exit -eq 0 -and -not $r.Out) "exit=$($r.Exit) out=$($r.Out)"
        }
    }
    $r = Stop
    Check 'stop: no journal dir -> no block' ($r.Exit -eq 0 -and -not (IsBlock $r))

    # --- SessionStart ------------------------------------------------------------------
    $r = Invoke-JournalHook 'journal-session-start.ps1' @{ session_id = $sid; hook_event_name = 'SessionStart'; source = 'startup' }
    $base = Get-Base
    Check 'start: creates journal' ([bool]$base -and (Test-Path "$base.md"))
    Check 'start: injects journal path' ($r.Out -like "*$base.md*") $r.Out
    $r = Invoke-JournalHook 'journal-session-start.ps1' @{ session_id = $sid; hook_event_name = 'SessionStart'; source = 'compact' }
    Check 'start (compact): re-injects path' ($r.Out -like "*$base.md*")
    Check 'start (compact): no duplicate header' ((@(Get-Content "$base.md" | Where-Object { $_ -like '# *' })).Count -eq 1)

    # --- call log ----------------------------------------------------------------------
    $null = Mcp 'ReadFile' $okText
    $l = (Get-CallLines)[-1]
    Check 'log: MCP success' ($l.kind -eq 'mcp' -and $l.tool -eq 'ReadFile' -and $l.ok -eq $true -and $l.agent -eq 'main' -and $l.durationMs -eq 42 -and $l.responseChars -eq $okText.Length) ($l | ConvertTo-Json -Compress)

    $null = Mcp 'ReadFile' $failText 'PostToolUseFailure'
    $l = (Get-CallLines)[-1]
    Check 'log: PostToolUseFailure -> ok=false + errorCode' ($l.ok -eq $false -and $l.errorCode -eq 'SolutionNotLoaded') ($l | ConvertTo-Json -Compress)

    $null = Mcp 'ReadFile' $failText
    $l = (Get-CallLines)[-1]
    Check 'log: isSuccess:false on PostToolUse -> ok=false' ($l.ok -eq $false -and $l.errorCode -eq 'SolutionNotLoaded')

    $before = (Get-CallLines).Count
    $null = Builtin 'Read' @{ file_path = 'C:\repo\docs\x.md' }
    $null = Builtin 'Bash' @{ command = 'Get-ChildItem docs/current -Filter *.md' }
    $null = Builtin 'Grep' @{ pattern = 'TODO'; path = 'docs' }
    $null = Builtin 'Read' @{ file_path = 'C:\runs\Worktree\Foo.cs' }
    $null = Invoke-JournalHook 'journal-log-call.ps1' @{ session_id = $sid; hook_event_name = 'PostToolUse'; tool_name = 'mcp__other_server__Thing'; tool_input = @{}; tool_response = @{ type = 'text'; text = 'x' }; mcp_server = @{ name = 'other_server' } }
    Check 'log: non-C# / Worktree / other-MCP calls not logged' ((Get-CallLines).Count -eq $before) "count=$((Get-CallLines).Count) before=$before"

    $null = Builtin 'Bash' @{ command = 'cat Foo.csproj' }
    Check 'log: .csproj is not a .cs fallback' ((Get-CallLines).Count -eq $before)

    $null = Builtin 'Read' @{ file_path = 'C:\repo\Common\Foo.cs' }
    $l = (Get-CallLines)[-1]
    Check 'log: Read .cs -> fallback, file name only' ($l.kind -eq 'fallback' -and $l.tool -eq 'Read' -and $l.detail -eq 'Read Foo.cs') ($l | ConvertTo-Json -Compress)

    $null = Builtin 'PowerShell' @{ command = 'dotnet test RoslynSentinel.slnx' }
    $l = (Get-CallLines)[-1]
    Check 'log: dotnet test via shell -> fallback' ($l.kind -eq 'fallback' -and $l.tool -eq 'PowerShell')

    $null = Builtin 'Glob' @{ pattern = '**/*.cs' }
    Check 'log: Glob *.cs -> fallback' ((Get-CallLines)[-1].tool -eq 'Glob')

    $null = Mcp 'Search' $okText 'PostToolUse' @{ agent_id = 'a06a86'; agent_type = 'implementer' }
    $l = (Get-CallLines)[-1]
    Check 'log: subagent call tagged with agent' ($l.agent -eq 'implementer:a06a86')

    # --- Stop nudge --------------------------------------------------------------------
    # Journal written now -> everything above is "already journaled".
    Add-Content -LiteralPath "$base.md" -Value '- 10:00 + ReadFile: test entry'
    Start-Sleep -Milliseconds 50
    $r = Stop
    Check 'stop: nothing since last entry -> no block' (-not (IsBlock $r)) $r.Out

    $null = Mcp 'Search' $okText; $null = Mcp 'Search' $okText
    Check 'stop: below threshold (2 < 3) -> no block' (-not (IsBlock (Stop)))

    $null = Mcp 'Search' $okText 'PostToolUse' @{ agent_id = 'x1'; agent_type = 'implementer' }
    $null = Mcp 'Search' $okText 'PostToolUse' @{ agent_id = 'x1'; agent_type = 'implementer' }
    Check 'stop: subagent calls do not count' (-not (IsBlock (Stop)))

    $null = Mcp 'Search' $okText
    $r = Stop
    Check 'stop: threshold reached -> block' (IsBlock $r) $r.Out
    Check 'stop: block reason names the journal path' ($r.Out -like "*$($base.Replace('\', '\\'))*" -or $r.Out -like "*$base*") $r.Out
    Check 'stop: stop_hook_active -> no block' (-not (IsBlock (Stop $true)))
    Check 'stop: ignored nudge does not repeat' (-not (IsBlock (Stop)))

    Start-Sleep -Milliseconds 50
    $null = Mcp 'ReadFile' $failText 'PostToolUseFailure'
    $r = Stop
    Check 'stop: one failed call -> block' ((IsBlock $r) -and $r.Out -like '*ReadFile*') $r.Out

    Add-Content -LiteralPath "$base.md" -Value '- 10:05 - ReadFile: test entry'
    Start-Sleep -Milliseconds 50
    $null = Builtin 'Read' @{ file_path = 'C:\repo\Common\Foo.cs' }
    $r = Stop
    Check 'stop: one fallback -> block' ((IsBlock $r) -and $r.Out -like '*fallback via Read*') $r.Out

    # A session already running when the hooks were installed has a call log but no journal.
    $sid = 'ccccdddd-0000-0000-0000-000000000000'; $sid8 = 'ccccdddd'
    $null = Builtin 'Read' @{ file_path = 'C:\repo\Common\Foo.cs' }
    $r = Stop
    Check 'stop: no journal yet -> block and create it' ((IsBlock $r) -and [bool](Get-Base)) $r.Out
}
finally {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:ROSLYNSENTINEL_JOURNAL_DIR, Env:ROSLYNSENTINEL_JOURNAL_NUDGE_THRESHOLD -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "pass=$pass fail=$fail"
if ($fail -gt 0) { exit 1 }
exit 0
