# Test suite for enforce-dogfood.ps1.
#
# Run:  pwsh -NoProfile -File .claude/hooks/enforce-dogfood.Tests.ps1
#
# Exit 2 from the hook means DENY; anything else means the call is allowed through.
# Kept as a file rather than an inline shell script because a command line containing
# git commands as *test data* trips the hook itself.

# Deliberately NOT 'Stop': a denying hook writes its reason to stderr, and under
# Windows PowerShell 5.1 an ErrorActionPreference of Stop turns any native-command
# stderr into a terminating error - which is the expected path in most cases here.
$ErrorActionPreference = 'Continue'
$hook = Join-Path $PSScriptRoot 'enforce-dogfood.ps1'

function Invoke-Hook([string]$payload) {
    $payload | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $hook 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 2) { 'DENY' } else { 'allow' }
}

$cases = @(
    # --- .cs edits: deny ---
    @{ n = 'Edit .cs (backslash path)'; want = 'DENY'
       p = @{ tool_name = 'Edit';  tool_input = @{ file_path = 'C:\repo\Foo.cs' } } }
    @{ n = 'Write .cs (forward path)'; want = 'DENY'
       p = @{ tool_name = 'Write'; tool_input = @{ file_path = '/c/repo/Bar.cs' } } }
    @{ n = 'MultiEdit .cs'; want = 'DENY'
       p = @{ tool_name = 'MultiEdit'; tool_input = @{ file_path = 'C:\repo\Baz.cs' } } }
    @{ n = 'Edit .CS (case-insensitive)'; want = 'DENY'
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = 'C:\repo\Qux.CS' } } }

    # --- non-C# and harness clones: allow ---
    @{ n = 'Edit .md'; want = 'allow'
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = 'C:\repo\CLAUDE.md' } } }
    @{ n = 'Edit .ps1'; want = 'allow'
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = 'C:\repo\build.ps1' } } }
    @{ n = 'Edit .cs inside Worktree/'; want = 'allow'
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = 'C:\run\Worktree\src\A.cs' } } }
    @{ n = 'Read .cs (not an edit)'; want = 'allow'
       p = @{ tool_name = 'Read'; tool_input = @{ file_path = 'C:\repo\Foo.cs' } } }

    # --- git operations the MCP Git tool covers: deny ---
    @{ n = 'git status'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git status --short' } } }
    @{ n = 'git log'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git log --oneline -3' } } }
    @{ n = 'git add && commit'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git add . && git commit -m x' } } }
    @{ n = 'git -C diff'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git -C /c/r diff --cached' } } }
    @{ n = 'git commit after cd'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'cd /c/r; git commit -m x' } } }

    # --- git operations with no MCP equivalent: allow, or the caller is stranded ---
    @{ n = 'git reset && status (mixed)'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git reset -q && git status' } } }
    @{ n = 'git push'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git push origin master' } } }
    @{ n = 'git worktree'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git worktree remove foo' } } }
    @{ n = 'git checkout'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git checkout -b feat' } } }
    @{ n = 'git stash && diff (mixed)'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git stash && git diff' } } }
    @{ n = 'git check-ignore'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git check-ignore -v x.json' } } }

    # --- unrelated commands: allow ---
    @{ n = 'dotnet build'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'dotnet build' } } }
    @{ n = 'ls'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'ls -la' } } }
    @{ n = 'digit-prefixed word containing status'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'echo legitimate-status-report' } } }
)

$pass = 0; $fail = 0

foreach ($c in $cases) {
    $got = Invoke-Hook ($c.p | ConvertTo-Json -Depth 5 -Compress)

    if ($got -eq $c.want) { $pass++; $mark = 'ok  ' }
    else                  { $fail++; $mark = 'FAIL' }
    '{0} {1,-42} want={2,-5} got={3}' -f $mark, $c.n, $c.want, $got | Write-Host
}

# Robustness: the hook must never block on bad input.
foreach ($bad in @('', 'not json at all', '{"tool_name":"Bash"}')) {
    $got = Invoke-Hook $bad
    $label = if ($bad -eq '') { '(empty stdin)' } else { $bad }
    if ($got -eq 'allow') { $pass++; $mark = 'ok  ' } else { $fail++; $mark = 'FAIL' }
    '{0} {1,-42} want={2,-5} got={3}' -f $mark, "fail-open: $label", 'allow', $got | Write-Host
}

Write-Host ''
Write-Host "pass=$pass fail=$fail"
if ($fail -gt 0) { exit 1 }
exit 0
