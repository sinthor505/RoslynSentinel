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
    # Output kept in $script:lastHookOut so a case can assert WHY it was denied (see 'match' below).
    $script:lastHookOut = ($payload | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $hookPath 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 2) { 'DENY' } else { 'allow' }
}

# The commit rule reads the real index (git diff --cached), and the hook resolves its repo
# root from its own location (..\.. from the hook folder). To fake "empty index" vs "paths
# staged" without touching the real repo, run a temp copy of the hook inside a throwaway git
# repo. fx = 'empty' -> nothing staged; fx = 'staged' -> one staged path; fx = 'norepo' -> git fails.
$fixtureRoots = @{}
$tierJson = @'
{
  "depth": {
    "agent": "implementer",
    "tier": 0,
    "tiers": [
      { "maxFiles": 3, "maxEdits": 10, "maxAcceptance": 1, "maxToolCalls": 25 },
      { "maxFiles": 3, "maxEdits": 15, "maxAcceptance": 1, "maxToolCalls": 35 },
      { "maxFiles": 3, "maxEdits": 20, "maxAcceptance": 2, "maxToolCalls": 45 }
    ]
  },
  "sweep": {
    "agent": "implementer-sweep",
    "tier": 1,
    "tiers": [
      { "maxOperations": 1, "maxFiles": 8, "maxCallSites": 25, "maxProjects": 2, "maxToolCalls": 30 },
      { "maxOperations": 2, "maxFiles": 15, "maxCallSites": 50, "maxProjects": 2, "maxToolCalls": 45 },
      { "maxOperations": 2, "maxFiles": 25, "maxCallSites": 100, "maxProjects": 3, "maxToolCalls": 60 }
    ]
  }
}
'@
function Get-FixtureHook([string]$kind) {
    if ($fixtureRoots.ContainsKey($kind)) { return Join-Path $fixtureRoots[$kind] '.claude\hooks\enforce-dogfood.ps1' }
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-dogfood-fx-$kind-" + [guid]::NewGuid().ToString('N'))
    $hookDir = Join-Path $root '.claude\hooks'
    New-Item -ItemType Directory -Path $hookDir -Force | Out-Null
    Copy-Item $hook (Join-Path $hookDir 'enforce-dogfood.ps1') -Force
    if ($kind -notmatch '^(norepo|notier|tier)') { & git -C $root init -q 2>&1 | Out-Null }
    # Implementer-dispatch fixtures vary the tier file the hook reads from <root>\.claude\. 'tiers' is a
    # known-good copy of the plan's table with depth at tier 0 and sweep at tier 1 (independent of the
    # live file, so promoting a lane there cannot break these tests); 'tierbad' has a current tier
    # outside its tiers list; 'tierjson' is not JSON; 'notier' has no tier file at all.
    $tierPath = Join-Path $root '.claude\implementer-tiers.json'
    if ($kind -eq 'tiers')    { Set-Content -LiteralPath $tierPath -Value $tierJson -Encoding UTF8 }
    if ($kind -eq 'tierbad')  { Set-Content -LiteralPath $tierPath -Value ($tierJson -replace '"tier": 0,', '"tier": 7,') -Encoding UTF8 }
    if ($kind -eq 'tierjson') { Set-Content -LiteralPath $tierPath -Value '{ this is not json' -Encoding UTF8 }
    # Limit-validation variants (all depth-lane edits unless noted; sweep stays valid except 'tiersweepbad').
    $tierVariants = @{
        tiermissingkey = { $tierJson.Replace('"maxFiles": 3, "maxEdits": 15', '"maxEdits": 15') }              # depth tier 1 lacks maxFiles
        tierstring     = { $tierJson.Replace('"maxFiles": 3, "maxEdits": 10', '"maxFiles": "3", "maxEdits": 10') } # JSON string, not a number
        tierzero       = { $tierJson.Replace('"maxFiles": 3, "maxEdits": 10', '"maxFiles": 0, "maxEdits": 10') }
        tierhuge       = { $tierJson.Replace('"maxFiles": 3, "maxEdits": 10', '"maxFiles": 99999999999, "maxEdits": 10') } # whole but overflows [int]
        tiernotier     = { $tierJson.Replace('"tier": 0,', '') }                                                  # depth lane lacks 'tier'
        tiersweepbad   = { $tierJson.Replace('"maxProjects": 2, "maxToolCalls": 30', '"maxToolCalls": 30') }      # sweep tier 0 lacks maxProjects
    }
    if ($tierVariants.ContainsKey($kind)) {
        $v = & $tierVariants[$kind]
        if ($v -eq $tierJson) { throw "fixture '$kind' did not change the tier JSON" }
        Set-Content -LiteralPath $tierPath -Value $v -Encoding UTF8
    }
    # 'tierthrow' = good tier file plus a hook copy that throws inside the dispatch block, to prove that block fails closed.
    if ($kind -eq 'tierthrow') {
        Set-Content -LiteralPath $tierPath -Value $tierJson -Encoding UTF8
        $hp = Join-Path $hookDir 'enforce-dogfood.ps1'
        $txt = Get-Content -LiteralPath $hp -Raw
        $anchor = '$brief = [string]$toolInput.prompt'
        if (-not $txt.Contains($anchor)) { throw 'tierthrow fixture: injection anchor not found in hook' }
        Set-Content -LiteralPath $hp -Value $txt.Replace($anchor, "throw 'injected test failure'; $anchor") -Encoding UTF8
    }
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

# Implementer / implementer-sweep briefs for the tier cases. $files is a count of distinct .cs paths.
$oosText = 'Out of scope: do not commit; no Write/Edit/shell writes on .cs files; reply RESCOPE: if more is needed'
function New-Brief([int]$files, [string]$tier, [string]$limits = 'Limits: see tier file', [string]$operation = '') {
    $list = (1..$files | ForEach-Object { "P/F$_.cs" }) -join ', '
    $b = "Files: $list`nSymbols: x`nCall sites: none`nAcceptance: clean Build"
    if ($tier)      { $b += "`nTier: $tier" }
    if ($limits)    { $b += "`n$limits" }
    if ($operation) { $b += "`nOperation: $operation" }
    $b + "`n$oosText"
}

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

    # --- here-string / heredoc bodies are prose, not commands (Step 1) ---
    @{ n = 'PS here-string doc append naming .cs, (more, git log'; want = 'allow'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = "Add-Content docs/x.md @'`nThe fix touches Foo.cs (more detail below). See also git log for history.`n'@" } } }
    @{ n = 'PS here-string plus a real Get-Content .cs'; want = 'DENY'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = "Add-Content docs/x.md @'`nThe fix touches Foo.cs (more detail below). See also git log for history.`n'@`nGet-Content C:\runs\RoslynSentinel\Foo.cs" } } }
    @{ n = 'Bash heredoc doc append naming .cs and git commit'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = "cat >> docs/x.md <<'EOF'`nSee Bar.cs and run git commit afterwards.`nEOF" } } }
    @{ n = 'PS here-string plus a real git status'; want = 'DENY'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = "Add-Content docs/x.md @'`nprose`n'@`ngit status" } } }
    @{ n = 'Grep negated glob !*.cs, docs path'; want = 'allow'
       p = @{ tool_name = 'Grep'; tool_input = @{ pattern = 'foo bar'; glob = '!*.cs'; path = "$repo\docs" } } }

    # --- shell writes/moves of C# (Step 2) ---
    @{ n = 'PS Set-Content to in-repo .cs'; want = 'DENY'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = "Set-Content -Path $repo\Foo.cs -Value x" } } }
    @{ n = 'PS [IO.File]::WriteAllText to .cs'; want = 'DENY'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = "[IO.File]::WriteAllText('$repo\Foo.cs', `$c)" } } }
    @{ n = 'PS pipe to Out-File .cs'; want = 'DENY'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = "x | Out-File $repo\Foo.cs" } } }
    @{ n = 'Bash echo redirect to .cs'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'echo x > Foo.cs' } } }
    @{ n = 'Bash cp onto .cs'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'cp /tmp/new.txt RoslynSentinel.Common/Foo.cs' } } }
    @{ n = 'PS Move-Item of .cs'; want = 'DENY'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = 'Move-Item RoslynSentinel.Common\A.cs RoslynSentinel.Common\B.cs' } } }
    @{ n = 'PS here-string C# body written via Set-Content'; want = 'DENY'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = "`$content = @'`nnamespace X { class Y {} }`n'@`nSet-Content -Path $repo\Y.cs -Value `$content" } } }
    @{ n = 'PS Set-Content .cs under Worktree/'; want = 'allow'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = 'Set-Content -Path C:\runs\x\Worktree\Foo.cs -Value x' } } }
    # Known false positive of the writer rule (.cs named only as text); the bypass keyword is the escape.
    @{ n = 'PS Set-Content notes.md naming Foo.cs (known FP)'; want = 'DENY'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = "Set-Content notes.md 'Foo.cs'" } } }
    @{ n = 'PS Set-Content notes.md naming Foo.cs + bypass'; want = 'allow'; fx = 'tok'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = "Set-Content notes.md 'Foo.cs' # DeliberateHookBypass: prose mentions a file name" } } }
    @{ n = 'PS Set-Content x.csproj (not .cs)'; want = 'allow'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = 'Set-Content x.csproj -Value y' } } }
    @{ n = 'PS Set-Content .cs + bypass keyword'; want = 'allow'; fx = 'tok'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = 'Set-Content Foo.cs -Value x # DeliberateHookBypass: raw bytes repro' } } }
    @{ n = 'journal write naming .cs and Set-Content'; want = 'allow'
       p = @{ tool_name = 'PowerShell'; tool_input = @{ command = "Add-Content -LiteralPath '.claude/journal/2026-10-01_aaaabbbb.md' -Value '- 18:41 - hook: Set-Content to Foo.cs was blocked'" } } }

    # --- non-C# and harness clones: allow ---
    @{ n = 'Edit .md'; want = 'allow'
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = "$repo\CLAUDE.md" } } }
    @{ n = 'Edit .ps1'; want = 'allow'
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = "$repo\build.ps1" } } }
    @{ n = 'Edit .cs inside Worktree/'; want = 'allow'
       p = @{ tool_name = 'Edit'; tool_input = @{ file_path = 'C:\run\Worktree\src\A.cs' } } }
    @{ n = 'Read .cs outside the repo'; want = 'allow'
       p = @{ tool_name = 'Read'; tool_input = @{ file_path = 'C:\repo\Foo.cs' } } }

    # --- Read of C# (built-in Read is blocked for in-repo .cs) ---
    @{ n = 'Read in-repo .cs'; want = 'DENY'
       p = @{ tool_name = 'Read'; tool_input = @{ file_path = "$repo\RoslynSentinel.Common\Foo.cs" } } }
    @{ n = 'Read in-repo .cs inside Worktree/'; want = 'allow'
       p = @{ tool_name = 'Read'; tool_input = @{ file_path = 'C:\run\Worktree\src\A.cs' } } }
    @{ n = 'Read in-repo .md'; want = 'allow'
       p = @{ tool_name = 'Read'; tool_input = @{ file_path = "$repo\CLAUDE.md" } } }
    @{ n = 'Read in-repo .cs + token bypass tools=Read'; want = 'allow'; fx = 'tok'; token = @{ reason = 'deliberate manual read'; tools = @('Read') }
       p = @{ tool_name = 'Read'; tool_input = @{ file_path = '{ROOT}\Foo.cs' } } }
    @{ n = 'Read in-repo .cs + token bypass tools=Edit only'; want = 'DENY'; fx = 'tok'; token = @{ reason = 'deliberate manual read'; tools = @('Edit') }
       p = @{ tool_name = 'Read'; tool_input = @{ file_path = '{ROOT}\Foo.cs' } } }

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
    @{ n = 'git worktree remove (now covered)'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git worktree remove foo' } } }
    @{ n = 'git checkout'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git checkout -b feat' } } }
    @{ n = 'git stash && diff (mixed, both covered)'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git stash && git diff' } } }
    @{ n = 'git tag v1.0 (covered)'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git tag v1.0' } } }
    @{ n = 'git tag -l (covered)'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git tag -l' } } }
    @{ n = 'git stash list (covered)'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git stash list' } } }
    @{ n = 'git stash pop (covered)'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git stash pop' } } }
    @{ n = 'git worktree list (covered)'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git worktree list' } } }
    @{ n = 'git worktree add (covered)'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git worktree add ../x br' } } }
    @{ n = 'git worktree remove --force (covered)'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git worktree remove --force ../x' } } }
    @{ n = 'git rebase && stash stays allowed (rebase uncovered)'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git rebase --continue && git stash' } } }
    @{ n = 'git stash drop stays shell-only'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git stash drop stash@{0}' } } }
    @{ n = 'git worktree prune stays shell-only'; want = 'allow'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git worktree prune' } } }
    @{ n = 'git -C <out-of-repo temp dir> worktree remove stays covered'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = "git -C `"$env:TEMP`" worktree remove x" } } }
    @{ n = 'git add -p (covered, hunk route)'; want = 'DENY'
       p = @{ tool_name = 'Bash'; tool_input = @{ command = 'git add -p' } } }
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

    # --- implementer dispatch: slice contract ---
    @{ n = 'implementer brief, all fields, 2 files, haiku'; want = 'allow'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = "Files: RoslynSentinel.Common/A.cs, RoslynSentinel.Common/B.cs`nSymbols: A.Foo`nCall sites: none`nAcceptance: clean Build`nTier: D0`nLimits: 3 files / 10 edits / 1 acceptance / 25 tool calls`nOut of scope: do not commit; no Write/Edit/shell writes on .cs files; reply RESCOPE: if more is needed" } } }
    @{ n = 'implementer brief, markdown-bold labels'; want = 'allow'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = "- **Files:** RoslynSentinel.Common/A.cs`n- **Symbols:** A.Foo`n- **Call sites:** none`n- **Acceptance check:** clean Build`n- **Tier:** D0`n- **Limits:** 3 files / 10 edits / 1 acceptance / 25 tool calls`n- **Out of scope:** do not commit; no Write/Edit/shell writes on .cs files; reply RESCOPE: if more is needed" } } }
    @{ n = 'implementer brief, model omitted'; want = 'DENY'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; prompt = "Files: A.cs`nSymbols: x`nCall sites: none`nAcceptance: Build`nTier: D0`nLimits: 3 files / 10 edits / 1 acceptance / 25 tool calls`nOut of scope: no Write/Edit/shell writes on .cs files; reply RESCOPE: if more is needed" } } }
    @{ n = 'implementer brief, model sonnet'; want = 'DENY'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'sonnet'; prompt = "Files: A.cs`nSymbols: x`nCall sites: none`nAcceptance: Build`nTier: D0`nLimits: 3 files / 10 edits / 1 acceptance / 25 tool calls`nOut of scope: no Write/Edit/shell writes on .cs files; reply RESCOPE: if more is needed" } } }
    @{ n = 'implementer brief, missing Acceptance'; want = 'DENY'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = "Files: A.cs`nSymbols: x`nCall sites: none`nTier: D0`nLimits: 3 files / 10 edits / 1 acceptance / 25 tool calls`nOut of scope: no Write/Edit/shell writes on .cs files; reply RESCOPE: if more is needed" } } }
    @{ n = 'implementer brief, free-form prose'; want = 'DENY'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = 'Please refactor the Git tool to use the new result type everywhere.' } } }
    @{ n = 'implementer brief, 4 files in Files section'; want = 'DENY'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = "Files: P/A.cs, P/B.cs, P/C.cs, P/D.cs`nSymbols: x`nCall sites: none`nAcceptance: Build`nTier: D0`nLimits: 3 files / 10 edits / 1 acceptance / 25 tool calls`nOut of scope: no Write/Edit/shell writes on .cs files; reply RESCOPE: if more is needed" } } }
    @{ n = 'implementer brief, call-site files do not count toward the 3'; want = 'allow'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = "Files: P/A.cs`nSymbols: x`nCall sites: P/B.cs:1, P/C.cs:2, P/D.cs:3, P/E.cs:4`nAcceptance: Build`nTier: D0`nLimits: 3 files / 10 edits / 1 acceptance / 25 tool calls`nOut of scope: no Write/Edit/shell writes on .cs files; reply RESCOPE: if more is needed" } } }
    @{ n = 'implementer brief, Out of scope without RESCOPE'; want = 'DENY'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = "Files: P/A.cs`nSymbols: x`nCall sites: none`nAcceptance: Build`nTier: D0`nLimits: 3 files / 10 edits / 1 acceptance / 25 tool calls`nOut of scope: do not commit; no Write/Edit/shell writes on .cs files" } } }
    @{ n = 'implementer brief, RESCOPE but no Write/Edit/shell (warn only)'; want = 'allow'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = "Files: P/A.cs`nSymbols: x`nCall sites: none`nAcceptance: Build`nTier: D0`nLimits: 3 files / 10 edits / 1 acceptance / 25 tool calls`nOut of scope: do not commit; reply RESCOPE: if more is needed" } } }
    @{ n = 'other subagent type: not checked'; want = 'allow'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'Explore'; prompt = 'where is X' } } }

    # --- implementer / implementer-sweep: tiers, lanes and the tier file ---
    # fx 'tiers' = known-good tier file, depth current tier 0, sweep current tier 1 (see Get-FixtureHook).
    @{ n = 'tier: live tier file loads, sweep S0 allowed'; want = 'allow'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer-sweep'; model = 'haiku'; prompt = (New-Brief 2 'S0' 'Limits: 1 operation / 8 files' 'RenameSymbol Foo -> Bar') } } }
    @{ n = 'sweep brief with Operation, S0'; want = 'allow'; fx = 'tiers'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer-sweep'; model = 'haiku'; prompt = (New-Brief 2 'S0' 'Limits: 1 operation / 8 files' 'RenameSymbol Foo -> Bar') } } }
    @{ n = 'sweep brief with Operation, S1 (at current tier)'; want = 'allow'; fx = 'tiers'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer-sweep'; model = 'haiku'; prompt = (New-Brief 2 'S1' 'Limits: 2 operations / 15 files' 'RenameSymbol Foo -> Bar') } } }
    @{ n = 'sweep brief, markdown-bold Tier/Operation labels'; want = 'allow'; fx = 'tiers'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer-sweep'; model = 'haiku'; prompt = "- **Files:** P/A.cs`n- **Symbols:** x`n- **Call sites:** none`n- **Acceptance:** Build`n- **Tier:** S0`n- **Limits:** 1 op`n- **Operation:** RenameSymbol`n- **Out of scope:** no Write/Edit/shell; reply RESCOPE:" } } }
    @{ n = 'sweep brief missing Operation'; want = 'DENY'; fx = 'tiers'; match = "missing field 'Operation:'"
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer-sweep'; model = 'haiku'; prompt = (New-Brief 2 'S0') } } }
    @{ n = 'depth brief does not need Operation'; want = 'allow'; fx = 'tiers'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = (New-Brief 2 'D0') } } }
    @{ n = 'sweep brief missing Tier'; want = 'DENY'; fx = 'tiers'; match = "missing field 'Tier:'"
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer-sweep'; model = 'haiku'; prompt = (New-Brief 2 '' 'Limits: x' 'Rename') } } }
    @{ n = 'depth brief missing Limits'; want = 'DENY'; fx = 'tiers'; match = "missing field 'Limits:'"
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = (New-Brief 2 'D0' '') } } }
    @{ n = 'tier above lane current tier (sweep S2, current 1)'; want = 'DENY'; fx = 'tiers'; match = "above the sweep lane's current tier"
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer-sweep'; model = 'haiku'; prompt = (New-Brief 2 'S2' 'Limits: x' 'Rename') } } }
    @{ n = 'tier above lane current tier (depth D1, current 0)'; want = 'DENY'; fx = 'tiers'; match = "above the depth lane's current tier"
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = (New-Brief 2 'D1') } } }
    @{ n = 'tier number with no entry in tiers (S3)'; want = 'DENY'; fx = 'tiers'; match = 'has no entry'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer-sweep'; model = 'haiku'; prompt = (New-Brief 2 'S3' 'Limits: x' 'Rename') } } }
    @{ n = 'wrong lane letter (implementer with S0)'; want = 'DENY'; fx = 'tiers'; match = 'wrong lane letter'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = (New-Brief 2 'S0') } } }
    @{ n = 'wrong lane letter (implementer-sweep with D0)'; want = 'DENY'; fx = 'tiers'; match = 'wrong lane letter'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer-sweep'; model = 'haiku'; prompt = (New-Brief 2 'D0' 'Limits: x' 'Rename') } } }
    @{ n = 'Tier value not letter+number'; want = 'DENY'; fx = 'tiers'; match = 'Tier: value must be'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = (New-Brief 2 'zero') } } }
    # File-count limit per tier (distinct .cs paths in Files).
    @{ n = 'file limit: sweep S0 allows 8 files'; want = 'allow'; fx = 'tiers'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer-sweep'; model = 'haiku'; prompt = (New-Brief 8 'S0' 'Limits: x' 'Rename') } } }
    @{ n = 'file limit: sweep S0 denies 9 files'; want = 'DENY'; fx = 'tiers'; match = 'at most 8'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer-sweep'; model = 'haiku'; prompt = (New-Brief 9 'S0' 'Limits: x' 'Rename') } } }
    @{ n = 'file limit: sweep S1 allows 15 files'; want = 'allow'; fx = 'tiers'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer-sweep'; model = 'haiku'; prompt = (New-Brief 15 'S1' 'Limits: x' 'Rename') } } }
    @{ n = 'file limit: sweep S1 denies 16 files'; want = 'DENY'; fx = 'tiers'; match = 'at most 15'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer-sweep'; model = 'haiku'; prompt = (New-Brief 16 'S1' 'Limits: x' 'Rename') } } }
    @{ n = 'file limit: depth D0 allows 3 files'; want = 'allow'; fx = 'tiers'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = (New-Brief 3 'D0') } } }
    @{ n = 'file limit: depth D0 denies 4 files'; want = 'DENY'; fx = 'tiers'; match = 'at most 3'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = (New-Brief 4 'D0') } } }
    @{ n = 'file limit: brief tier S0 (not current S1) sets the limit'; want = 'DENY'; fx = 'tiers'; match = 'at most 8'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer-sweep'; model = 'haiku'; prompt = (New-Brief 12 'S0' 'Limits: x' 'Rename') } } }
    # Tier file missing / unreadable / invalid: fail closed with the file named.
    @{ n = 'tier file missing: fail closed'; want = 'DENY'; fx = 'notier'; match = 'implementer-tiers.json'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = (New-Brief 2 'D0') } } }
    @{ n = 'tier file missing: sweep fails closed'; want = 'DENY'; fx = 'notier'; match = 'implementer-tiers.json'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer-sweep'; model = 'haiku'; prompt = (New-Brief 2 'S0' 'Limits: x' 'Rename') } } }
    @{ n = 'tier file not JSON: fail closed'; want = 'DENY'; fx = 'tierjson'; match = 'implementer-tiers.json'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = (New-Brief 2 'D0') } } }
    @{ n = 'tier file current tier outside tiers list: fail closed'; want = 'DENY'; fx = 'tierbad'; match = 'implementer-tiers.json'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = (New-Brief 2 'D0') } } }
    @{ n = 'denial text names lane, current tier and limits'; want = 'DENY'; fx = 'tiers'; match = 'Current tier: S1. Limits at that tier: maxOperations=2, maxFiles=15'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer-sweep'; model = 'haiku'; prompt = (New-Brief 2 'S0' 'Limits: x') } } }

    # --- tier file limit validation (fail closed; dispatching lane only) ---
    @{ n = 'tier limits: depth tier 1 missing maxFiles'; want = 'DENY'; fx = 'tiermissingkey'; match = "lane 'depth' tier 1 has 'maxFiles' = ''"
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = (New-Brief 2 'D0') } } }
    @{ n = 'tier limits: maxFiles is a JSON string "3"'; want = 'DENY'; fx = 'tierstring'; match = "'maxFiles' = '3'"
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = (New-Brief 2 'D0') } } }
    @{ n = 'tier limits: maxFiles is 0'; want = 'DENY'; fx = 'tierzero'; match = "'maxFiles' = '0'"
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = (New-Brief 2 'D0') } } }
    @{ n = 'tier limits: maxFiles beyond int range'; want = 'DENY'; fx = 'tierhuge'; match = "'maxFiles'"
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = (New-Brief 2 'D0') } } }
    @{ n = 'tier limits: depth lane missing current tier'; want = 'DENY'; fx = 'tiernotier'; match = "lane 'depth' has 'tier'"
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = (New-Brief 2 'D0') } } }
    @{ n = 'tier limits: sweep tier 0 missing maxProjects, sweep dispatch'; want = 'DENY'; fx = 'tiersweepbad'; match = "lane 'sweep' tier 0 has 'maxProjects'"
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer-sweep'; model = 'haiku'; prompt = (New-Brief 2 'S0' 'Limits: x' 'Rename') } } }
    @{ n = 'tier limits: broken sweep lane does not block a depth dispatch'; want = 'allow'; fx = 'tiersweepbad'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = (New-Brief 2 'D0') } } }
    @{ n = 'dispatch block: unexpected exception fails closed'; want = 'DENY'; fx = 'tierthrow'; match = 'internal error in dispatch check: injected test failure'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'implementer'; model = 'haiku'; prompt = (New-Brief 2 'D0') } } }
    @{ n = 'non-implementer dispatch unaffected by the dispatch block'; want = 'allow'; fx = 'tierthrow'
       p = @{ tool_name = 'Agent'; tool_input = @{ subagent_type = 'Explore'; prompt = 'where is X' } } }

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

    $why = ''
    if ($got -eq $c.want -and $c.match -and $got -eq 'DENY' -and $script:lastHookOut -notmatch [regex]::Escape($c.match)) {
        $got = 'DENY(wrong reason)'; $why = " expected text: $($c.match)"
    }
    if ($got -eq $c.want) { $pass++; $mark = 'ok  ' }
    else                  { $fail++; $mark = 'FAIL' }
    '{0} {1,-42} want={2,-5} got={3}{4}' -f $mark, $c.n, $c.want, $got, $why | Write-Host
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

# Smoke test: both lanes of the LIVE tier file must satisfy the hook's validation rules, so a bad
# hand-edit (missing key, string value, 0, tier outside the list) fails this suite, not a dispatch.
function Test-LiveTierFile {
    $path = Join-Path $repo '.claude\implementer-tiers.json'
    $problems = @()
    try { $cfg = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { return @("not valid JSON: $($_.Exception.Message)") }
    $whole = { param($v) (($v -is [int] -or $v -is [long]) -and [long]$v -le [int]::MaxValue) }
    $req = @{
        depth = @('maxFiles', 'maxEdits', 'maxAcceptance', 'maxToolCalls')
        sweep = @('maxOperations', 'maxFiles', 'maxCallSites', 'maxProjects', 'maxToolCalls')
    }
    $agents = @{ depth = 'implementer'; sweep = 'implementer-sweep' }
    foreach ($ln in @('depth', 'sweep')) {
        $lane = $cfg.$ln
        if (-not $lane) { $problems += "lane '$ln' missing"; continue }
        if ([string]$lane.agent -ne $agents[$ln]) { $problems += "lane '$ln' agent is '$($lane.agent)', expected '$($agents[$ln])'" }
        $tiers = @($lane.tiers)
        if ($tiers.Count -eq 0) { $problems += "lane '$ln' has no tiers"; continue }
        $tp = $lane.PSObject.Properties['tier']
        if (-not $tp -or -not (& $whole $tp.Value) -or [long]$tp.Value -lt 0) { $problems += "lane '$ln' 'tier' invalid" }
        elseif ([long]$tp.Value -ge $tiers.Count) { $problems += "lane '$ln' 'tier' $($tp.Value) outside tiers list" }
        for ($i = 0; $i -lt $tiers.Count; $i++) {
            foreach ($k in $req[$ln]) {
                $rp = $(if ($tiers[$i]) { $tiers[$i].PSObject.Properties[$k] })
                if (-not $rp -or -not (& $whole $rp.Value) -or [long]$rp.Value -lt 1) { $problems += "lane '$ln' tier $i '$k' invalid" }
            }
        }
    }
    return $problems
}
$liveProblems = @(Test-LiveTierFile)
if ($liveProblems.Count -eq 0) { $pass++; $mark = 'ok  ' } else { $fail++; $mark = 'FAIL' }
'{0} {1,-42} {2}' -f $mark, 'live implementer-tiers.json (both lanes) valid', ($liveProblems -join '; ') | Write-Host

foreach ($r in $fixtureRoots.Values) { Remove-Item -LiteralPath $r -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ''
Write-Host "pass=$pass fail=$fail"
if ($fail -gt 0) { exit 1 }
exit 0
