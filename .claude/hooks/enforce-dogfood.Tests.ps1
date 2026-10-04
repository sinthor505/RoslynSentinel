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

function Invoke-Hook([string]$payload, [string]$hookPath = $hook) {
    $payload | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $hookPath 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 2) { 'DENY' } else { 'allow' }
}

# The commit rule reads the real index (git diff --cached), and the hook resolves its repo
# root from its own location (..\.. from the hook folder). To fake "empty index" vs "paths
# staged" without touching the real repo, run a temp copy of the hook inside a throwaway git
# repo. fx = 'empty' -> nothing staged; fx = 'staged' -> one staged path; fx = 'norepo' -> git fails.
$fixtureRoots = @{}
function Get-FixtureHook([string]$kind) {
    if ($fixtureRoots.ContainsKey($kind)) { return Join-Path $fixtureRoots[$kind] '.claude\hooks\enforce-dogfood.ps1' }
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-dogfood-fx-$kind-" + [guid]::NewGuid().ToString('N'))
    $hookDir = Join-Path $root '.claude\hooks'
    New-Item -ItemType Directory -Path $hookDir -Force | Out-Null
    Copy-Item $hook (Join-Path $hookDir 'enforce-dogfood.ps1') -Force
    if ($kind -ne 'norepo') { & git -C $root init -q 2>&1 | Out-Null }
    if ($kind -eq 'staged') {
        Set-Content -LiteralPath (Join-Path $root 'a.txt') -Value 'x'
        & git -C $root add -- a.txt 2>&1 | Out-Null
    }
    $fixtureRoots[$kind] = $root
    Join-Path $hookDir 'enforce-dogfood.ps1'
}

$msg = "Fix it`n`nCo-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"

# The hook only blocks .cs paths INSIDE its repo root (out-of-repo is auto-exempt), so the
# deny cases need real in-repo paths.
$repo    = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path.TrimEnd('\', '/')
$repoFwd = $repo.Replace('\', '/')

$cases = @(
    # --- .cs edits: deny ---
    @{ n = 'Edit .cs (backslash path)'; want = 'DENY'
       p = @{ tool_name = 'Edit';  tool_input = @{ file_path = "$repo\Foo.cs" } } }
    @{ n = 'Write .cs (forward path)'; want = 'DENY'
       p = @{ tool_name = 'Write'; tool_input = @{ file_path = "$repoFwd/Bar.cs" } } }
    @{ n = 'MultiEdit .cs'; want = 'DENY'
       p = @{ tool_name = 'MultiEdit'; tool_input = @{ file_path = "$repo\Baz.cs" } } }
    @{ n = 'Edit .CS (case-insensitive)'; want = 'DENY'
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = "$repo\Qux.CS" } } }
    @{ n = 'Edit .cs relative path (cannot tell: enforce)'; want = 'DENY'
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = 'Foo.cs' } } }
    @{ n = 'Edit .cs via .. out of repo and back in'; want = 'DENY'
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = "$repo\sub\..\Foo.cs" } } }

    # --- .cs outside this repo: auto-exempt, no keyword needed ---
    @{ n = 'Edit .cs out-of-repo (C:\tmp)'; want = 'allow'
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = 'C:\tmp\Foo.cs' } } }
    @{ n = 'Write .cs out-of-repo (git-bash style)'; want = 'allow'
       p = @{ tool_name = 'Write'; tool_input = @{ file_path = '/c/tmp/Foo.cs' } } }
    @{ n = 'Edit .cs via .. escaping the repo'; want = 'allow'
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = "$repo\..\elsewhere\Foo.cs" } } }
    @{ n = 'Edit .cs in sibling dir sharing repo-name prefix'; want = 'allow'
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = "$repo-TestRuns\Foo.cs" } } }

    # --- Grep ---
    @{ n = 'Grep *.cs glob, no path (in-repo)'; want = 'DENY'
       p = @{ tool_name = 'Grep'; tool_input = @{ pattern = 'foo bar'; glob = '*.cs' } } }
    @{ n = 'Grep *.cs glob, out-of-repo path'; want = 'allow'
       p = @{ tool_name = 'Grep'; tool_input = @{ pattern = 'foo bar'; glob = '*.cs'; path = 'C:\tmp' } } }
    @{ n = 'Grep *.cs glob, in-repo path'; want = 'DENY'
       p = @{ tool_name = 'Grep'; tool_input = @{ pattern = 'foo bar'; glob = '*.cs'; path = "$repo\RoslynSentinel.Common" } } }

    # --- deliberate bypass: inline keyword (Bash/PowerShell). Runs in a fixture so the
    #     bypass log never lands in the real repo's .claude/journal. ---
    @{ n = 'bypass inline: cat .cs + keyword + reason'; want = 'allow'; fx = 'tok'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'cat Vendor/Foo.cs # DeliberateHookBypass: reading vendored file' } } }
    @{ n = 'bypass inline: keyword in description'; want = 'allow'; fx = 'tok'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = 'Get-Content Vendor\Foo.cs'; description = 'DeliberateHookBypass: need raw bytes of a vendored file' } } }
    @{ n = 'bypass inline: git status + keyword'; want = 'allow'; fx = 'tok'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git status # DeliberateHookBypass: MCP Git is halted by drift latch' } } }
    @{ n = 'bypass inline: keyword case-insensitive'; want = 'allow'; fx = 'tok'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'cat Foo.cs # deliberatehookbypass: because reasons' } } }
    @{ n = 'bypass inline: bare keyword, no reason'; want = 'DENY'; fx = 'tok'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'cat Foo.cs # DeliberateHookBypass' } } }
    @{ n = 'bypass inline: keyword with empty reason'; want = 'DENY'; fx = 'tok'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'cat Foo.cs # DeliberateHookBypass:   ' } } }
    @{ n = 'bypass inline: keyword with 1-char reason'; want = 'DENY'; fx = 'tok'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'cat Foo.cs # DeliberateHookBypass: x' } } }

    # --- deliberate bypass: token file (Edit/Write/Grep have no free-text field) ---
    @{ n = 'bypass token: none present'; want = 'DENY'; fx = 'tok'
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = '{ROOT}\Foo.cs' } } }
    @{ n = 'bypass token: valid, Edit .cs'; want = 'allow'; fx = 'tok'; token = @{ reason = 'deliberate manual edit' }
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = '{ROOT}\Foo.cs' } } }
    @{ n = 'bypass token: valid, Grep *.cs'; want = 'allow'; fx = 'tok'; token = @{ reason = 'deliberate manual grep' }
       p = @{ tool_name = 'Grep'; tool_input = @{ pattern = 'foo bar'; glob = '*.cs' } } }
    @{ n = 'bypass token: valid, shell cat .cs'; want = 'allow'; fx = 'tok'; token = @{ reason = 'deliberate manual read' }
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'cat Foo.cs' } } }
    @{ n = 'bypass token: expired (11 min old)'; want = 'DENY'; fx = 'tok'; token = @{ reason = 'deliberate manual edit' }; tokenAgeMin = 11
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = '{ROOT}\Foo.cs' } } }
    @{ n = 'bypass token: no reason'; want = 'DENY'; fx = 'tok'; token = @{ tools = @('Edit') }
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = '{ROOT}\Foo.cs' } } }
    @{ n = 'bypass token: tools list excludes this tool'; want = 'DENY'; fx = 'tok'; token = @{ reason = 'deliberate manual edit'; tools = @('Write') }
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = '{ROOT}\Foo.cs' } } }
    @{ n = 'bypass token: paths list matches'; want = 'allow'; fx = 'tok'; token = @{ reason = 'deliberate manual edit'; paths = @('Foo.cs') }
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = '{ROOT}\Foo.cs' } } }
    @{ n = 'bypass token: paths list does not match'; want = 'DENY'; fx = 'tok'; token = @{ reason = 'deliberate manual edit'; paths = @('Foo.cs') }
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = '{ROOT}\Bar.cs' } } }
    @{ n = 'bypass token: paths list vs shell (no target)'; want = 'DENY'; fx = 'tok'; token = @{ reason = 'deliberate manual read'; paths = @('Foo.cs') }
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'cat Foo.cs' } } }
    @{ n = 'bypass token: does not cover commit-trailer check'; want = 'DENY'; fx = 'tok'; token = @{ reason = 'deliberate manual edit' }
       p = @{ tool_name = 'Git'; tool_input = @{ operation = 'commit'; files = 'A.cs'; message = 'Fix it' } } }

    # --- tool-experience journal writes quoting git in the note text ---
    @{ n = 'journal Add-Content quoting git diff'; want = 'allow'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = "Add-Content -LiteralPath '.claude/journal/2026-10-01_aaaabbbb.md' -Value '- 18:41 - hook: blocked a git diff I tucked into a command'" } } }
    @{ n = 'journal echo >> quoting git commit (Bash)'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = "echo '- 18:41 ~ Git: git commit via tool worked' >> .claude/journal/2026-10-01_aaaabbbb.md" } } }
    @{ n = 'journal path plus a real git call'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = "cat .claude/journal/x.md; git diff" } } }

    # --- shell text search/read of C#: deny ---
    @{ n = 'Bash grep --include=*.cs (seen live)'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'grep -rn "public DiscoveryEngine(" --include=*.cs .' } } }
    @{ n = 'Bash rg on a .cs path'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'rg -n Foo RoslynSentinel.Common/Foo.cs' } } }
    @{ n = 'Bash cat .cs'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'cat RoslynSentinel.Common/Foo.cs' } } }
    @{ n = 'Bash find | xargs grep .cs'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'find . -name "*.cs" | xargs grep -l Foo' } } }
    @{ n = 'PowerShell Get-Content .cs'; want = 'DENY'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = 'Get-Content RoslynSentinel.Common\Foo.cs -TotalCount 40' } } }
    @{ n = 'PowerShell gci *.cs | Select-String'; want = 'DENY'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = 'Get-ChildItem -Recurse -Filter *.cs | Select-String -Pattern Foo' } } }
    @{ n = 'Bash sed -i on .cs'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = "sed -i 's/Foo/Bar/' RoslynSentinel.Common/Foo.cs" } } }

    # --- shell commands that mention C# but read no C# content: allow ---
    @{ n = 'dotnet build .csproj'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'dotnet build RoslynSentinel.Common/RoslynSentinel.Common.csproj' } } }
    @{ n = 'grep .md'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'grep -n Foo docs/current/TODO.md' } } }
    @{ n = 'gci *.cs for mtimes (no content)'; want = 'allow'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = 'Get-ChildItem -Recurse -Filter *.cs | Sort-Object LastWriteTime | Select-Object -Last 5' } } }
    @{ n = 'grep .cs inside Worktree/'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'grep -n Foo C:/runs/x/Worktree/Foo.cs' } } }

    # --- non-C# and harness clones: allow ---
    @{ n = 'Edit .md'; want = 'allow'
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = "$repo\CLAUDE.md" } } }
    @{ n = 'Edit .ps1'; want = 'allow'
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = "$repo\build.ps1" } } }
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

    # --- git status/log/diff on a path outside this repo: allow, MCP Git tool can't reach it ---
    @{ n = 'git -C <out-of-repo temp dir> status'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = "git -C `"$env:TEMP`" status --short" } } }
    @{ n = 'git -C <out-of-repo temp dir> log'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = "git -C `"$env:TEMP`" log --oneline -5" } } }
    @{ n = 'git -C <out-of-repo temp dir> diff'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = "git -C `"$env:TEMP`" diff" } } }
    @{ n = 'git -C <out-of-repo temp dir> commit stays covered'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = "git -C `"$env:TEMP`" commit -m x" } } }
    @{ n = 'git -C <in-repo path> status stays covered'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = "git -C `"$PSScriptRoot`" status" } } }
    @{ n = 'git -C <nonexistent path> status stays covered (fail-safe)'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git -C /no/such/path/at/all status' } } }
    @{ n = 'git -C <sibling dir sharing repo-name prefix> status allowed (not a substring match)'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = "git -C `"$((Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path.TrimEnd('\','/'))-TestRuns`" status" } } }
    @{ n = 'cd <out-of-repo temp dir> && git log (no -C flag)'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = "cd `"$env:TEMP`" && git log --oneline -5 somebranch" } } }
    @{ n = 'cd <out-of-repo temp dir> && git commit stays covered'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = "cd `"$env:TEMP`" && git commit -m x" } } }
    @{ n = 'cd <in-repo path> && git status stays covered'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = "cd `"$PSScriptRoot`" && git status" } } }

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

    # --- MCP Git(commit): scope check uses the staged set, not the dirty worktree ---
    @{ n = 'commit, no scope/files, empty index'; want = 'DENY'; fx = 'empty'
       p = @{ tool_name = 'Git'; tool_input = @{ operation = 'commit'; message = $msg } } }
    @{ n = 'commit, no scope/files, paths staged'; want = 'allow'; fx = 'staged'
       p = @{ tool_name = 'Git'; tool_input = @{ operation = 'commit'; message = $msg } } }
    @{ n = 'commit, files only (no scope), empty index'; want = 'allow'; fx = 'empty'
       p = @{ tool_name = 'Git'; tool_input = @{ operation = 'commit'; files = 'A.cs,B.cs'; message = $msg } } }
    @{ n = 'commit, paths only (no scope), empty index'; want = 'allow'; fx = 'empty'
       p = @{ tool_name = 'Git'; tool_input = @{ operation = 'commit'; paths = 'A.cs'; message = $msg } } }
    @{ n = 'commit, scope=listed + files, empty index'; want = 'allow'; fx = 'empty'
       p = @{ tool_name = 'Git'; tool_input = @{ operation = 'commit'; scope = 'listed'; files = 'A.cs'; message = $msg } } }
    @{ n = 'commit, scope=all + files (trusted as before)'; want = 'allow'; fx = 'empty'
       p = @{ tool_name = 'Git'; tool_input = @{ operation = 'commit'; scope = 'all'; files = 'A.cs'; message = $msg } } }
    @{ n = 'commit, files only, missing trailer'; want = 'DENY'; fx = 'staged'
       p = @{ tool_name = 'Git'; tool_input = @{ operation = 'commit'; files = 'A.cs'; message = 'Fix it' } } }
    @{ n = 'commit, no scope/files, staged, missing trailer'; want = 'DENY'; fx = 'staged'
       p = @{ tool_name = 'Git'; tool_input = @{ operation = 'commit'; message = 'Fix it' } } }
    @{ n = 'commit, amend (reword) with empty index'; want = 'allow'; fx = 'empty'
       p = @{ tool_name = 'Git'; tool_input = @{ operation = 'commit'; amend = $true; message = $msg } } }
    @{ n = 'commit, git fails (not a repo): fail open'; want = 'allow'; fx = 'norepo'
       p = @{ tool_name = 'Git'; tool_input = @{ operation = 'commit'; message = $msg } } }

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
    $hookPath = if ($c.fx) { Get-FixtureHook $c.fx } else { $hook }
    $json = $c.p | ConvertTo-Json -Depth 5 -Compress
    $tokenPath = $null
    if ($c.fx) {
        # {ROOT} -> the fixture's repo root, so a case can name an in-repo path.
        $fxRoot = Split-Path (Split-Path (Split-Path $hookPath))
        $json = $json.Replace('{ROOT}', $fxRoot.Replace('\', '\\'))
        if ($c.token) {
            $tokenPath = Join-Path $fxRoot '.claude\bypass.local.json'
            Set-Content -LiteralPath $tokenPath -Value ($c.token | ConvertTo-Json -Depth 5 -Compress) -Encoding UTF8
            if ($c.tokenAgeMin) { (Get-Item -LiteralPath $tokenPath).LastWriteTime = (Get-Date).AddMinutes(-$c.tokenAgeMin) }
        }
    }
    $got = Invoke-Hook $json $hookPath
    if ($tokenPath) { Remove-Item -LiteralPath $tokenPath -Force -ErrorAction SilentlyContinue }

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

# Accepted bypasses must leave an audit trail in the fixture's journal dir.
$bypassLog = Join-Path $fixtureRoots['tok'] '.claude\journal\hook-bypass.jsonl'
$logged = (Test-Path -LiteralPath $bypassLog) -and ((Get-Content -LiteralPath $bypassLog -Raw) -match '"reason":"reading vendored file"')
if ($logged) { $pass++; $mark = 'ok  ' } else { $fail++; $mark = 'FAIL' }
'{0} {1,-42}' -f $mark, 'bypass log records accepted bypass' | Write-Host

foreach ($r in $fixtureRoots.Values) { Remove-Item -LiteralPath $r -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ''
Write-Host "pass=$pass fail=$fail"
if ($fail -gt 0) { exit 1 }
exit 0
