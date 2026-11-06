# PreToolUse hook enforcing the RoslynSentinel dog-fooding policy.
#
# The policy (CLAUDE.md "Dog-fooding is mandatory") requires every C# read/write and
# every git operation in this repo to go through the RoslynSentinel MCP tools, so that
# real usage surfaces the drift and sequencing bugs isolated tests never reach.
#
# A rule enforced only by remembering decays across hundreds of tool calls per session.
# This hook is the "surface hot" light: it makes the non-compliant call fail at the
# moment it's attempted, with a message naming the tool to use instead.
#
# Contract: stdin receives PreToolUse JSON; exit 2 blocks the call and feeds stderr
# back to the model as the reason. Any other exit code lets the call proceed.
#
# Matcher note: settings.json's PreToolUse matcher for this hook must include Read and Grep, and the
# MCP tool names ReplaceSnippet and Git (matched here by exact name OR a "__Git"/
# "__ReplaceSnippet" suffix, since Claude Code sends the full "mcp__<server>__ToolName" form
# for an actual MCP call but a bare short name in this hook's own unit-test payloads).
#
# FAIL-OPEN BY DESIGN: if this hook itself errors, it must never block the session.
# A broken guardrail that halts all work is worse than no guardrail.
#
# KNOWN LIMITATION: git detection is a regex over the command line, so a script that merely
# *contains* a covered git command as text (a test fixture, an echo, a quoted single-line
# literal) is blocked too. .cs detection is token-based (Test-CsPathToken): only a path-shaped
# .cs token counts (`src/Foo.cs`, `'src/Foo.cs'`, `**/*.cs`), not a name inside prose
# (`see Foo.cs`, `Foo.cs:12`); the residual false positive is a bare path-shaped literal such as
# `.Replace('Foo.cs','Bar.cs')` in a non-.cs write - use the DeliberateHookBypass keyword. PowerShell here-string and bash heredoc BODIES are
# stripped before scanning (Remove-HereStrings), so prose appended to a doc that way is fine.
# Put other such content in a file rather than on the command line - see
# enforce-dogfood.Tests.ps1, which exists in file form for exactly this reason.
#
# Also covers, per the same policy:
#   - Read on an in-repo .cs path -> ReadFile / GetFileOutline / GetMethodSource / Search.
#   - Grep on a .cs path/glob or an obviously C#-symbol-shaped pattern -> Search/FindReferences.
#   - Bash/PowerShell text search, read or stream edit (grep/rg/Select-String/cat/Get-Content/
#     sed...) naming a .cs file or glob (path-shaped token, see above) -> Search/ReadFile/GetMethodSource.
#   - Bash/PowerShell WRITES to a .cs file (Set-Content/Out-File/Copy-Item/Move-Item/tee/cp/mv,
#     [IO.File]::WriteAll*, or a > redirect to a .cs target) -> Member/ReplaceSnippet/WriteFile/...
#   - Agent dispatch of `implementer` (depth lane) or `implementer-sweep` (sweep lane): slice-contract
#     fields (Files, Symbols, Call sites, Acceptance, Out of scope, Tier, Limits; Operation for sweep),
#     Tier letter matching the lane (D/S), tier number not above the lane's current tier and present in
#     .claude/implementer-tiers.json, distinct .cs files in Files within that tier's maxFiles, and
#     RESCOPE in Out of scope. Fails closed when the tier file is missing, unreadable or invalid.
#   - ReplaceSnippet called with filePath and batchEdits together (rejected server-side anyway,
#     but the server's InvalidArgument error doesn't name a corrected example call the way this
#     hook's message does), and a soft warning (not a block - this is a heuristic, not a real
#     symbol resolution) when a batchEdits entry's newContent references an identifier that no
#     earlier entry in the same batch appears to define.
#   - Git(operation: commit) missing a Co-Authored-By trailer, a scope-less commit with nothing
#     staged, or scope=all/tracked with no paths list while the tree is dirty.
#
# Deliberate bypass (out-of-repo auto-exempt, inline keyword, token file): see the
# "Deliberate bypass" block below and CLAUDE.md "Hook bypass".
#
# Tests: pwsh -NoProfile -File .claude/hooks/enforce-dogfood.Tests.ps1

$ErrorActionPreference = 'Stop'

# Top-level so every branch can use it (the git-shell and MCP-Git-commit branches both need
# to know this repo's root), not just the branch that first happened to need it.
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path.TrimEnd('\', '/')

function Deny([string]$reason) {
    Write-BlockLog $reason
    [Console]::Error.WriteLine($reason)
    exit 2
}

# --- Deliberate bypass --------------------------------------------------------------
# A reflexive call stays blocked; a deliberate one can pass. Policy: CLAUDE.md "Hook bypass".
# Three routes, none advertised by the block messages themselves (they only point at CLAUDE.md,
# so the keyword does not become a reflex):
#   1. Out-of-repo path: Edit/Write/Grep on a .cs path outside this repo's root needs nothing -
#      the MCP tools cannot see those files, so blocking them is a false positive.
#   2. Inline keyword (Bash/PowerShell, which have a free-text command/description):
#      "DeliberateHookBypass: <reason>" in the command (as a comment) or in the description.
#      The reason is mandatory (3+ chars); a bare keyword is still blocked.
#   3. Token file .claude/bypass.local.json (local-only, ignored by .claude/*) for tools with no
#      free-text field. {"reason": "...", "tools": ["Edit"], "paths": ["Foo.cs"]} - reason is
#      required; tools/paths optional narrowing (paths = case-insensitive substring of the
#      target path). Valid for 10 minutes from its last write.
# Every accepted keyword/token bypass is appended to .claude/journal/hook-bypass.jsonl.
# Not bypassable: the ReplaceSnippet parameter check and the commit checks - those guard
# correctness, not policy.
$bypassTokenPath  = Join-Path $repoRoot '.claude\bypass.local.json'
$bypassTtlMinutes = 10
$bypassFooter = @"

A deliberate, justified exception is described in CLAUDE.md ("Hook bypass").
"@

function DenyBypassable([string]$reason) { Deny ($reason + $bypassFooter) }

function Test-PathOutsideRepo([string]$p) {
    if (-not $p) { return $false }
    $p = $p.Trim().Trim('"', "'")
    # Git-Bash style /c/foo -> C:\foo
    if ($p -match '^/([A-Za-z])/') { $p = ($Matches[1] + ':\' + $p.Substring(3)) }
    try {
        # Relative or unresolvable means "can't tell": treat as in-repo, i.e. keep enforcing.
        if (-not [System.IO.Path]::IsPathRooted($p)) { return $false }
        $full = [System.IO.Path]::GetFullPath($p).TrimEnd('\', '/')
    }
    catch { return $false }
    return -not ($full -eq $repoRoot -or $full.StartsWith("$repoRoot\", [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith("$repoRoot/", [StringComparison]::OrdinalIgnoreCase))
}

# Returns $cmd with here-string / heredoc BODIES removed, so prose appended to a doc (which may
# name C# files or git commands as text) is not mistaken for a command. Quoted single-line
# literals are deliberately NOT stripped, so `bash -c "git add ."` is still seen.
#   PowerShell @'...'@ -> ''     PowerShell @"..."@ -> ""     bash <<[-]'TAG' ... TAG -> (removed)
function Remove-HereStrings([string]$cmd) {
    if (-not $cmd) { return $cmd }
    $o = [System.Text.RegularExpressions.RegexOptions]::Singleline
    $cmd = [regex]::Replace($cmd, "@'[ \t]*\r?\n.*?\r?\n'@", "''", $o)
    $cmd = [regex]::Replace($cmd, '@"[ \t]*\r?\n.*?\r?\n"@', '""', $o)
    $cmd = [regex]::Replace($cmd, '<<-?\s*[''"]?(\w+)[''"]?[^\r\n]*\r?\n.*?\r?\n\s*\1\b', '', $o)
    return $cmd
}

# True only when the command contains a PATH-SHAPED .cs token: a whole unquoted token or a whole
# quoted literal whose entire content looks like a path/glob ending in .cs (no spaces, nothing after
# .cs). A .cs name that appears only inside prose ('see Foo.cs', 'Foo.cs:12', '(Foo.cs)') does not
# count, so `WriteAllText('docs/x.md', $t.Replace('Foo.cs:1','x'))` is no longer mistaken for a C# write.
# Tokenising is quote-aware: split on whitespace ; | & ( ) { } , = @ outside quotes; a quoted literal is
# one token (quotes stripped). The content of a quoted literal right after -c/-Command/-lc//c (a nested
# shell command line) is scanned recursively, because that is a command, not prose.
# `--include=*.cs` -> `*.cs`, `$p='src/Foo.cs'` -> `src/Foo.cs`, `**/*.cs`, `-Path:src\Foo.cs` all count.
# -QuotedNeedsDir (reader rule only): a QUOTED literal must also contain a / \ or * to count, because a
# quoted bare name is usually the search pattern (`Select-String 'Foo.cs'`); unquoted tokens always count.
# Residual false positive (writer rule): a bare path-shaped literal (`.Replace('Foo.cs','Bar.cs')`) still counts.
# -Collected (deviation, used only by Test-ShellCsTargetsOutsideRepo): when a List[string] is passed, every matching
# token is appended to it and scanning continues instead of returning at the first hit; the return value is then
# meaningless. Without -Collected the behaviour and return value are exactly as before.
function Test-CsPathToken([string]$scan, [switch]$QuotedNeedsDir, [int]$depth = 0, [System.Collections.Generic.List[string]]$Collected = $null) {
    if (-not $scan) { return $false }
    $pathShaped = '^[\w.\\/:~$*-]+\.cs$'
    $seps = " `t`r`n;|&(){},=@"
    $prev = ''
    $tok = New-Object System.Text.StringBuilder
    $i = 0
    $n = $scan.Length
    while ($i -le $n) {
        $ch = if ($i -lt $n) { $scan[$i] } else { [char]10 }
        if ($i -lt $n -and ($ch -eq [char]39 -or $ch -eq [char]34)) {
            $end = $scan.IndexOf($ch, $i + 1)
            if ($end -gt $i) {
                # A quote glued to a token (`--path="x.cs"`) starts a fresh token; flush what precedes it.
                if ($tok.Length -gt 0) {
                    if ($tok.ToString() -match $pathShaped) { if ($null -ne $Collected) { $Collected.Add($tok.ToString()) } else { return $true } }
                    $prev = $tok.ToString().ToLowerInvariant(); [void]$tok.Clear()
                }
                $content = $scan.Substring($i + 1, $end - $i - 1)
                if ($content -match $pathShaped -and (-not $QuotedNeedsDir -or $content -match '[\\/*]')) { if ($null -ne $Collected) { $Collected.Add($content) } else { return $true } }
                if ($depth -lt 3 -and $prev -in @('-c', '-lc', '-ic', '-command', '-cmd', '/c', '-encodedcommand')) {
                    if ($null -ne $Collected) { [void](Test-CsPathToken $content -QuotedNeedsDir:$QuotedNeedsDir -depth ($depth + 1) -Collected $Collected) }
                    elseif (Test-CsPathToken $content -QuotedNeedsDir:$QuotedNeedsDir -depth ($depth + 1)) { return $true }
                }
                $prev = $content.ToLowerInvariant()
                $i = $end + 1
                continue
            }
            # Unterminated quote (an apostrophe in prose): treat it as an ordinary character.
            $i++
            continue
        }
        if ($i -ge $n -or $seps.IndexOf($ch) -ge 0) {
            if ($tok.Length -gt 0) {
                $t = $tok.ToString()
                if ($t -match $pathShaped) { if ($null -ne $Collected) { $Collected.Add($t) } else { return $true } }
                $prev = $t.ToLowerInvariant()
                [void]$tok.Clear()
            }
        }
        else { [void]$tok.Append($ch) }
        $i++
    }
    return $false
}

# --- Out-of-repo locations in shell commands (deviation: shared by the git rule and the shell .cs rules) ---
# The MCP Git/Search/ReadFile tools only see the loaded solution, so a shell command that explicitly targets a
# location outside this repo has no compliant MCP route. These helpers find the explicit locations a command
# names and decide whether they are all outside the repo. "Cannot tell" always means "keep enforcing".

# Strips quotes and maps git-bash /c/foo to C:\foo (same mapping as Test-PathOutsideRepo).
function Convert-LocationPath([string]$p) {
    if (-not $p) { return '' }
    $p = $p.Trim().Trim('"', "'")
    if ($p -match '^/([A-Za-z])/') { $p = ($Matches[1] + ':\' + $p.Substring(3)) }
    return $p
}

# Explicit location paths in a shell command: the path after cd|chdir|sl|pushd|Set-Location|Push-Location
# (optionally after -Path/-LiteralPath; only at a command position: start, or after ; & | ( { or a newline),
# the path after `git -C`, the value of `--git-dir=`/`--git-dir <p>`, and the value of -WorkingDirectory.
# Returned strings are unquoted and git-bash mapped, but NOT resolved; relative ones stay relative.
# Callers must wrap the call in @() (a lone result unrolls to a scalar).
# A path held in a variable is resolved beforehand by Expand-SimpleAssignments (simple single assignments only).
function Get-CommandLocations([string]$scan) {
    $found = New-Object 'System.Collections.Generic.List[string]'
    if (-not $scan) { return @() }
    $pathPat = '(?<p>"[^"]+"|''[^'']+''|[^\s;&|)}]+)'
    $ic = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    $patterns = @(
        "(?:^|[;&|({\r\n])\s*(?:cd|chdir|sl|pushd|Set-Location|Push-Location)\s+(?:-(?:Path|LiteralPath)[\s:]+)?$pathPat",
        "\bgit\s+(?:-c\s+\S+\s+)*-C\s+$pathPat",
        "--git-dir(?:=|\s+)$pathPat",
        "-WorkingDirectory(?:[\s:=]+)$pathPat"
    )
    foreach ($pat in $patterns) {
        foreach ($m in [regex]::Matches($scan, $pat, $ic)) {
            $v = Convert-LocationPath $m.Groups['p'].Value
            if ($v) { $found.Add($v) }
        }
    }
    return $found.ToArray()
}

# Local checkout roots that are known to be outside this repo even when they cannot be resolved (e.g. not yet
# cloned). Missing or invalid .claude/hooks/outside-roots.json => empty list; never throws.
function Get-OutsideRoots {
    try {
        $f = Join-Path $PSScriptRoot 'outside-roots.json'
        if (-not (Test-Path -LiteralPath $f)) { return @() }
        $j = Get-Content -LiteralPath $f -Raw | ConvertFrom-Json
        return @(@($j.roots) | Where-Object { $_ -is [string] -and $_.Trim() } | ForEach-Object { $_.Trim() })
    }
    catch { return @() }
}

# True when $p (rooted) equals or is under a root listed in outside-roots.json. Path-boundary match,
# case-insensitive, same style as Test-PathOutsideRepo.
function Test-UnderOutsideRoot([string]$p) {
    try {
        $p = Convert-LocationPath $p
        if (-not $p -or -not [System.IO.Path]::IsPathRooted($p)) { return $false }
        $full = [System.IO.Path]::GetFullPath($p).TrimEnd('\', '/')
        foreach ($r in (Get-OutsideRoots)) {
            $root = [System.IO.Path]::GetFullPath($r).TrimEnd('\', '/')
            if ($full -eq $root -or $full.StartsWith("$root\", [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith("$root/", [StringComparison]::OrdinalIgnoreCase)) { return $true }
        }
    }
    catch { }
    return $false
}

# True when there is at least one location and EVERY one is rooted and outside this repo (or under an
# outside root). A relative location (`cd ..`) is unresolvable here, so it counts as "cannot tell".
# -ResolveExisting (git rule): use Test-OutOfRepo, which needs the path to exist, so a typo'd path keeps enforcing.
# Without it (.cs rules): any rooted non-repo path counts, matching the built-in Read/Edit/Write rules.
function Test-AllLocationsOutside([string[]]$locs, [switch]$ResolveExisting) {
    if (-not $locs -or $locs.Count -eq 0) { return $false }
    foreach ($l in $locs) {
        $p = Convert-LocationPath $l
        if (-not $p -or -not [System.IO.Path]::IsPathRooted($p)) { return $false }
        $outside = if ($ResolveExisting) { (Test-OutOfRepo $p) -or (Test-UnderOutsideRoot $p) } else { (Test-PathOutsideRepo $p) -or (Test-UnderOutsideRoot $p) }
        if (-not $outside) { return $false }
    }
    return $true
}

# Shell .cs rules (reader and writer) exemption: true when every path-shaped .cs target is outside this repo.
# An absolute token must itself be outside (Test-PathOutsideRepo). A relative/glob token is judged by the
# command's explicit locations (cd/Set-Location/-WorkingDirectory/git -C) and passes only when there is at least
# one and all are outside; a relative token with `..`, `$` or `~` is unresolvable and blocks. No tokens => false.
# -ExtraTokens: redirect targets (`> x.cs`), which the tokenizer does not split on.
function Test-ShellCsTargetsOutsideRepo([string]$scan, [switch]$QuotedNeedsDir, [string[]]$ExtraTokens = @(), [string]$LocationScan = '') {
    $toks = New-Object 'System.Collections.Generic.List[string]'
    [void](Test-CsPathToken $scan -QuotedNeedsDir:$QuotedNeedsDir -Collected $toks)
    foreach ($e in $ExtraTokens) { if ($e) { $toks.Add($e) } }
    if ($toks.Count -eq 0) { return $false }
    $locsOutside = $null
    foreach ($t in $toks) {
        $p = Convert-LocationPath $t
        if ([System.IO.Path]::IsPathRooted($p)) {
            if (-not (Test-PathOutsideRepo $p)) { return $false }
            continue
        }
        if ($p -match '(^|[\\/])\.\.([\\/]|$)' -or $p -match '[$~]') { return $false }
        # -LocationScan: the whole command, when $scan is one statement of it (a `cd` in an earlier statement still counts).
        if ($null -eq $locsOutside) { $locsOutside = Test-AllLocationsOutside @(Get-CommandLocations $(if ($LocationScan) { $LocationScan } else { $scan })) }
        if (-not $locsOutside) { return $false }
    }
    return $true
}

# --- Statements and simple variable assignments (deviation: shared by the reader rule and the git rule) ---

# Splits a shell command into statements at ; && || and newlines that are outside quotes and outside (...) / {...}
# nesting. A single & is PowerShell's call operator and a single | stays inside its pipeline, so neither splits.
# Quote handling matches Test-CsPathToken: a quote with no closing partner is an ordinary character.
# Returns objects with Start (offset into $s) and Text; callers must wrap the call in @().
function Get-ShellStatements([string]$s) {
    $result = New-Object 'System.Collections.Generic.List[object]'
    if ([string]::IsNullOrEmpty($s)) { return $result.ToArray() }
    $n = $s.Length; $start = 0; $depth = 0; $i = 0
    while ($i -lt $n) {
        $ch = $s[$i]
        if ($ch -eq [char]39 -or $ch -eq [char]34) {
            $end = $s.IndexOf($ch, $i + 1)
            if ($end -gt $i) { $i = $end + 1 } else { $i++ }
            continue
        }
        if ($ch -eq [char]40 -or $ch -eq [char]123) { $depth++ }
        elseif ($ch -eq [char]41 -or $ch -eq [char]125) { if ($depth -gt 0) { $depth-- } }
        elseif ($depth -eq 0) {
            $sepLen = 0
            if ($ch -eq [char]59 -or $ch -eq [char]10) { $sepLen = 1 }
            elseif (($ch -eq [char]38 -or $ch -eq [char]124) -and ($i + 1) -lt $n -and $s[$i + 1] -eq $ch) { $sepLen = 2 }
            if ($sepLen -gt 0) {
                $result.Add([pscustomobject]@{ Start = $start; Text = $s.Substring($start, $i - $start) })
                $i += $sepLen; $start = $i
                continue
            }
        }
        $i++
    }
    $result.Add([pscustomobject]@{ Start = $start; Text = $s.Substring($start) })
    return $result.ToArray()
}

# Replaces $name / ${name} in $t with the values in $vals (lower-case name -> literal text), except inside single
# quotes (no expansion there) and where the reference is not a plain value use: followed by `:`, or - outside double
# quotes - by `[`, `(` or `.name` (property or method access). Inside double quotes the bare value is inserted;
# outside, the value is inserted as is, wrapped in double quotes when it contains whitespace.
function Expand-VariableRefs([string]$t, [hashtable]$vals) {
    $sb = New-Object System.Text.StringBuilder
    $refPat = [regex]'\G\$(?:\{(?<n>\w+)\}|(?<n>\w+))'
    $q = [char]0
    $i = 0; $n = $t.Length
    while ($i -lt $n) {
        $ch = $t[$i]
        if ($q -ne [char]0) {
            if ($ch -eq $q) { $q = [char]0 }
        }
        elseif ($ch -eq [char]39 -or $ch -eq [char]34) {
            if ($t.IndexOf($ch, $i + 1) -gt $i) { $q = $ch }
        }
        if ($ch -eq [char]36 -and $q -ne [char]39) {
            $m = $refPat.Match($t, $i)
            if ($m.Success) {
                $key = $m.Groups['n'].Value.ToLowerInvariant()
                if ($vals.ContainsKey($key)) {
                    $after = $i + $m.Length
                    $next = if ($after -lt $n) { $t[$after] } else { [char]0 }
                    $skip = ($next -eq [char]58)
                    if (-not $skip -and $q -ne [char]34) {
                        if ($next -eq [char]91 -or $next -eq [char]40) { $skip = $true }
                        elseif ($next -eq [char]46 -and ($after + 1) -lt $n -and ($t[$after + 1] -match '[A-Za-z_]')) { $skip = $true }
                    }
                    if (-not $skip) {
                        $inner = [string]$vals[$key]
                        $rep = if ($q -eq [char]34 -or $inner -notmatch '\s') { $inner } else { '"' + $inner + '"' }
                        [void]$sb.Append($rep)
                        $i += $m.Length
                        continue
                    }
                }
            }
        }
        [void]$sb.Append($ch)
        $i++
    }
    return $sb.ToString()
}

# Scanned-copy-only substitution of simple variable assignments, so a path held in a variable
# (`$r='C:\other'; git -C $r log`, Bash `r=/c/other; git -C "$r" log`) is judged by its literal value.
# Resolved only when the whole command assigns the name exactly once, as a statement of its own whose value is a
# quoted or bare literal with no `$`, quote or backtick in it; the value replaces later references only. Anything
# else (reassigned, an expression, another variable in the value) is left alone and stays "cannot tell".
# Fail-safe: on any error the original text is returned.
function Expand-SimpleAssignments([string]$cmd) {
    try {
        if ([string]::IsNullOrEmpty($cmd) -or $cmd.IndexOf('=') -lt 0 -or $cmd.IndexOf('$') -lt 0) { return $cmd }
        $stmts = @(Get-ShellStatements $cmd)
        $assignPat = '^\s*(?:\$\{?(?<n>\w+)\}?\s*=\s*|(?<n>\w+)=)(?<v>''[^'']*''|"[^"$`]*"|[^\s;&|$"''`(){}<>*?=,@]+)\s*$'
        $map = @{}
        for ($k = 0; $k -lt $stmts.Count; $k++) {
            $m = [regex]::Match($stmts[$k].Text, $assignPat)
            if (-not $m.Success) { continue }
            $name = $m.Groups['n'].Value
            $key = $name.ToLowerInvariant()
            if ($map.ContainsKey($key)) { continue }
            $v = $m.Groups['v'].Value
            $inner = if ($v.Length -ge 2 -and ($v[0] -eq [char]39 -or $v[0] -eq [char]34)) { $v.Substring(1, $v.Length - 2) } else { $v }
            if ($inner.Length -eq 0 -or $inner -match '[$"''`]') { continue }
            # Every form that could (re)assign the name, anywhere in the command, must be this one statement.
            $esc = [regex]::Escape($name)
            $alts = @(
                ('(?<![\w$])\$\{?' + $esc + '\}?\s*(?:(?:[-+*/%]|\?\?)?=(?!=)|\+\+|--|\s+in\b)'),
                ('\[ref\]\s*\$\{?' + $esc + '\b'),
                ('-(?:Out|Error|Warning|Information)?Variable[\s:]+\$?' + $esc + '\b'),
                ('(?<![\w$.\-])' + $esc + '=(?!=)')
            )
            $count = [regex]::Matches($cmd, ('(?:' + ($alts -join '|') + ')'), [System.Text.RegularExpressions.RegexOptions]::IgnoreCase).Count
            if ($count -ne 1) { continue }
            $map[$key] = @{ Idx = $k; Inner = $inner }
        }
        if ($map.Count -eq 0) { return $cmd }
        $sb = New-Object System.Text.StringBuilder
        $pos = 0
        for ($k = 0; $k -lt $stmts.Count; $k++) {
            $st = $stmts[$k]
            [void]$sb.Append($cmd.Substring($pos, $st.Start - $pos))
            $t = $st.Text
            $active = @{}
            foreach ($key in @($map.Keys)) { if ($map[$key].Idx -lt $k) { $active[$key] = $map[$key].Inner } }
            if ($active.Count -gt 0) { $t = Expand-VariableRefs $t $active }
            [void]$sb.Append($t)
            $pos = $st.Start + $st.Text.Length
        }
        [void]$sb.Append($cmd.Substring($pos))
        return $sb.ToString()
    }
    catch { return $cmd }
}

# Reader rule, evaluated per statement (Get-ShellStatements): returns the reader verb of the first statement that
# has a reader verb at a command position AND a path-shaped .cs token (or a variable assigned from a statement that
# had one, so `$f = Get-ChildItem *.cs; Get-Content $f` is still a C# read) and whose .cs targets are not all outside
# the repo; $null when there is none. A pipeline is one statement, so `Get-ChildItem *.cs | Get-Content` is caught.
function Get-CsReaderHit([string]$scan, [string]$readers) {
    $stmts = @()
    try { $stmts = @(Get-ShellStatements $scan) } catch { $stmts = @() }
    if ($stmts.Count -eq 0) { $stmts = @([pscustomobject]@{ Start = 0; Text = $scan }) }
    $tainted = New-Object 'System.Collections.Generic.List[string]'
    foreach ($st in $stmts) {
        $t = [string]$st.Text
        $hasCs = Test-CsPathToken $t -QuotedNeedsDir
        if (-not $hasCs) {
            foreach ($tv in $tainted) {
                if ($t -match ('(?i)\$\{?' + [regex]::Escape($tv) + '(?!\w)')) { $hasCs = $true; break }
            }
        }
        if (-not $hasCs) { continue }
        # Taint every variable this statement names (not only a leading assignment): `$f = gci *.cs`,
        # `gci *.cs -OutVariable f`, `gci *.cs | % { $l += $_ }` all leave a later `Get-Content $f`/`$l` reading C#.
        # Automatics are skipped. A resolved `$r` was substituted away by Expand-SimpleAssignments, so it is not named here.
        $skipVars = 'env|_|psitem|true|false|null|args|input|this|pwd|home|psscriptroot|matches|lastexitcode|error|host|pid'
        $names = New-Object 'System.Collections.Generic.List[string]'
        foreach ($vm in [regex]::Matches($t, '\$(?:\{(?<n>\w+)\}|(?<n>\w+))(?![\w:])')) { $names.Add($vm.Groups['n'].Value) }
        foreach ($vm in [regex]::Matches($t, '(?i)(?<![\w-])-(?:OutVariable|ov|PipelineVariable|pv)[\s:=]+\$?\{?(?<n>\w+)')) { $names.Add($vm.Groups['n'].Value) }
        if ($t -match '^\s*(?<n>\w+)=') { $names.Add($Matches['n']) }   # Bash assignment: f=$(ls *.cs)
        foreach ($nm in $names) {
            if ($nm -notmatch "(?i)^($skipVars)$" -and -not $tainted.Contains($nm)) { $tainted.Add($nm) }
        }
        if ($t -match "(^\s*|[;&|(\r\n]\s*|\bxargs\s+)($readers)(\s|$)") {
            $verb = $Matches[2]
            if (-not (Test-ShellCsTargetsOutsideRepo $t -QuotedNeedsDir -LocationScan $scan)) { return $verb }
        }
    }
    return $null
}

function Get-InlineBypassReason([string]$text) {
    if ($text -match '(?i)DeliberateHookBypass\s*:\s*(?<r>\S[^\r\n]{2,})') { return $Matches['r'].Trim() }
    return $null
}

function Get-TokenBypassReason([string]$target) {
    if (-not (Test-Path -LiteralPath $bypassTokenPath)) { return $null }
    try {
        $fi = Get-Item -LiteralPath $bypassTokenPath
        if (((Get-Date) - $fi.LastWriteTime).TotalMinutes -gt $bypassTtlMinutes) { return $null }
        $t = Get-Content -LiteralPath $bypassTokenPath -Raw | ConvertFrom-Json
    }
    catch { return $null }
    $reason = ([string]$t.reason).Trim()
    if ($reason.Length -lt 3) { return $null }
    if ($t.tools -and ($toolName -notin @($t.tools))) { return $null }
    if ($t.paths) {
        if (-not $target) { return $null }
        $norm = $target.Replace('/', '\')
        $hit = $false
        foreach ($pp in @($t.paths)) {
            if ($norm.IndexOf(([string]$pp).Replace('/', '\'), [StringComparison]::OrdinalIgnoreCase) -ge 0) { $hit = $true; break }
        }
        if (-not $hit) { return $null }
    }
    return $reason
}

function Write-BypassLog([string]$source, [string]$target, [string]$reason) {
    try {
        $dir = Join-Path $repoRoot '.claude\journal'
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $entry = [ordered]@{
            ts = (Get-Date).ToUniversalTime().ToString('o'); session = [string]$payload.session_id
            tool = $toolName; source = $source; target = $target; reason = $reason
        } | ConvertTo-Json -Compress
        [System.IO.File]::AppendAllText((Join-Path $dir 'hook-bypass.jsonl'), $entry + "`n", (New-Object System.Text.UTF8Encoding($false)))
    }
    catch { }
    [Console]::Error.WriteLine("enforce-dogfood.ps1: DeliberateHookBypass accepted ($source): $reason")
}

# Exits 0 (allow) when an inline keyword or a valid token covers this call; returns otherwise.
function Exit-IfBypassed([string]$inlineText, [string]$target) {
    $r = Get-InlineBypassReason $inlineText
    if ($r) { Write-BypassLog 'inline' $target $r; exit 0 }
    $r = Get-TokenBypassReason $target
    if ($r) { Write-BypassLog 'token' $target $r; exit 0 }
}

# Appends one compact JSON line per denied call to hook-blocks.jsonl (read by scripts/Get-HookBlockReport.ps1).
# Fails open: any logging problem is swallowed so it never changes the exit code or stderr.
function Write-BlockLog([string]$reason) {
    try {
        $dir = if ($env:ROSLYNSENTINEL_JOURNAL_DIR) { $env:ROSLYNSENTINEL_JOURNAL_DIR } else { Join-Path $repoRoot '.claude\journal' }
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

        # Policy and headline come from the first line only: "BLOCKED by <policy> policy: <headline>".
        # The bypass footer sits after the first line, so it is never logged.
        $firstLine = ([string]$reason -split "`r?`n")[0]
        $policy = 'unknown'
        $pm = [regex]::Match($firstLine, '^BLOCKED by (.+?) policy:')
        if ($pm.Success) { $policy = $pm.Groups[1].Value }
        $headline = $firstLine
        $hm = [regex]::Match($firstLine, 'policy: (.*)$')
        if ($hm.Success) { $headline = $hm.Groups[1].Value }
        $headline = $headline.Trim()
        if ($headline.Length -gt 200) { $headline = $headline.Substring(0, 200) }

        $target = ''
        $ti = $toolInput
        if ($ti) {
            if ($ti.file_path)   { $target = [string]$ti.file_path }
            elseif ($ti.command) { $target = [string]$ti.command }
            elseif ($ti.pattern) { $target = [string]$ti.pattern }
            elseif ($ti.prompt)  { $pr = [string]$ti.prompt; $target = $pr.Substring(0, [Math]::Min(100, $pr.Length)) }
        }
        $target = ($target -replace '\s+', ' ').Trim()
        if ($target.Length -gt 100) { $target = $target.Substring(0, 100) }

        $agent = 'main'
        if ($payload.agent_id) { $agent = '{0}:{1}' -f $payload.agent_type, $payload.agent_id }
        $entry = [ordered]@{
            ts = (Get-Date).ToUniversalTime().ToString('o'); session = [string]$payload.session_id
            agent = $agent; tool = [string]$toolName; policy = $policy; headline = $headline; target = $target
        } | ConvertTo-Json -Compress
        [System.IO.File]::AppendAllText((Join-Path $dir 'hook-blocks.jsonl'), $entry + "`n", (New-Object System.Text.UTF8Encoding($false)))
    }
    catch { }
}

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

    try {
        $payload = $raw | ConvertFrom-Json
    }
    catch {
        # Unparseable input means enforcement is silently off for this call. Say so loudly
        # rather than failing open in silence - a guardrail nobody knows has stopped working
        # is worse than a visibly absent one.
        [Console]::Error.WriteLine("enforce-dogfood.ps1: could not parse hook payload; dog-food enforcement SKIPPED for this call.")
        exit 0
    }

    $toolName  = [string]$payload.tool_name
    $toolInput = $payload.tool_input

    # --- C# file reads -----------------------------------------------------------
    # Built-in Read on an in-repo .cs: the MCP read tools (ReadFile, GetFileOutline,
    # GetMethodSource, Search) cover it. Same exemptions as the edit block.
    if ($toolName -eq 'Read') {
        $filePath = [string]$toolInput.file_path
        if ($filePath -and $filePath.TrimEnd().EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase)) {
            if ($filePath -match '[\\/]Worktree[\\/]') { exit 0 }
            if (Test-PathOutsideRepo $filePath) { exit 0 }
            Exit-IfBypassed '' $filePath

            DenyBypassable @"
BLOCKED by dog-fooding policy: Read on a .cs file.

  $filePath

C# is read through the RoslynSentinel MCP tools:

  ReadFile         - whole file or startLine/endLine
  GetFileOutline   - types and members with line ranges
  GetMethodSource  - one method
  Search           - symbols (mode: symbol), callers (mode: references)

Read(file_path, offset, limit) -> ReadFile(filePath: ..., startLine: offset, endLine: offset+limit-1); one method: GetMethodSource(filePath, methodName[, containerName]); a name declared more than once returns a candidate list, never a guess.

If the right MCP tool is broken, unreachable, or has no equivalent operation,
that is a BLOCKING finding: stop, write docs/current/blockers/blocking_error_<slug>.md,
and end the turn. If your agent has no MCP tools, report that as the finding.
Do not route around this hook.
"@
        }
        exit 0
    }

    # --- C# file edits -----------------------------------------------------------
    # Scope is C# only: docs, .ps1 and .md aren't in the Roslyn workspace and have no
    # tool coverage, so the normal file tools are correct for those.
    if ($toolName -in @('Edit', 'Write', 'MultiEdit', 'NotebookEdit')) {
        $filePath = [string]$toolInput.file_path
        if ($filePath -and $filePath.TrimEnd().EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase)) {

            # Never block edits inside a PlanStepRunner harness clone. Those are throwaway
            # worktrees, often not the loaded solution, and blocking there strands the run.
            if ($filePath -match '[\\/]Worktree[\\/]') { exit 0 }

            # Outside this repo the MCP tools cannot see the file: nothing to dog-food.
            if (Test-PathOutsideRepo $filePath) { exit 0 }
            Exit-IfBypassed '' $filePath

            DenyBypassable @"
BLOCKED by dog-fooding policy: $toolName on a .cs file.

  $filePath

C# writes go through the RoslynSentinel MCP tools so that real usage surfaces
drift and sequencing bugs. Use the tool that matches the change:

  Member / MethodSignature / ModifyModifier / ModifyEnum  - structural edits
  ReplaceSnippet / ApplyDiff / ApplyUnifiedDiff           - textual edits
  CreateFile then Member(add)                             - new files
  RenameSymbol                                            - renames

If the right MCP tool is broken, unreachable, or has no equivalent operation,
that is a BLOCKING finding: stop, write docs/current/blockers/blocking_error_<slug>.md,
and end the turn. Do not route around this hook.
"@
        }
        exit 0
    }

    # --- Grep on C# -----------------------------------------------------------------
    # Grep has no notion of C# symbols/references, so it can only ever pattern-match raw
    # text - exactly the "grep, regex search-and-replace" fallback the mission statement
    # calls out as a tool-surface gap when an agent reaches for it. Scope is narrower than
    # the file-edit block above: only deny when the call is clearly aimed at C# (a .cs
    # glob/path, or the RoslynSentinel source tree with no glob at all) rather than every
    # Grep call anywhere in the repo (README hits, docs/current searches, etc. are fine).
    if ($toolName -eq 'Grep') {
        $pattern = [string]$toolInput.pattern
        $path    = [string]$toolInput.path
        $glob    = [string]$toolInput.glob

        $targetsCs = $false
        # A negated glob (!*.cs) EXCLUDES C#; it does not target it.
        if ($glob -and $glob -notmatch '^\s*!' -and $glob -match '\.cs["'']?$') { $targetsCs = $true }
        if ($path -and $path.TrimEnd().EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase)) { $targetsCs = $true }

        # No path/glob at all means a repo-wide search. A pattern shaped like a C# identifier
        # (PascalCase/camelCase word, underscores, no spaces) searched with no scope at all is
        # exactly the real failure case: a symbol/method name grepped across the whole tree
        # instead of Search(mode: symbol)/FindReferences. A pattern containing spaces, regex
        # metacharacters beyond a bare identifier, or matching known non-C# scopes (docs/,
        # .md) is left alone - this only needs to catch the common "identifier, no scope"
        # shape, not classify every possible Grep call.
        if (-not $path -and -not $glob -and $pattern -and $pattern -match '^[A-Za-z_][A-Za-z0-9_]{3,}$' -and $pattern -match '[A-Z_]') {
            $targetsCs = $true
        }
        if ($path -and $path -match '[\\/]Worktree[\\/]') { $targetsCs = $false }

        if ($targetsCs -and $pattern) {
            if ($path -and (Test-PathOutsideRepo $path)) { exit 0 }
            Exit-IfBypassed '' $(if ($path) { $path } else { $glob })

            DenyBypassable @"
BLOCKED by dog-fooding policy: Grep targeting a .cs file/glob.

  pattern: $pattern
  path/glob: $(if ($path) { $path } else { $glob })

Grep only matches raw text; it has no view of C# symbols, references, or
declarations. Use the RoslynSentinel Search tool instead - it covers what Grep
was being asked to approximate:

  Search(mode: "text",             query: "$pattern")   # literal text, same as this Grep call
  Search(mode: "symbol",           query: "$pattern")   # find a type/method/field by name
  Search(mode: "callers",          query: "$pattern")   # callers of a symbol (also: implementations, attribute)
  Search(mode: "declaration-kind", query: "$pattern")   # find declarations of a given kind

Grep options: path/glob -> fileGlob ('Foo.cs' or 'MyProject/**'); head_limit -> maxResults; output_mode, -n, -i, type are not needed (text mode is case-insensitive and always returns file, line and preview).

If the pattern above names a specific method or type, GetMethodSource or
GetFileOutline is probably a more direct route than any Search mode.

If Search is broken, unreachable, or has no equivalent for what
you need, that is a BLOCKING finding: stop, write
docs/current/blockers/blocking_error_<slug>.md, and end the turn.
"@
        }
        exit 0
    }

    # --- Dispatching `implementer` / `implementer-sweep`: slice contract -----------------
    # Skill dispatch-implementer (CLAUDE.md "Dispatching `implementer`"). The implementer lanes are
    # Haiku-tier and stall on briefs that are large or underspecified, and only the orchestrator's own
    # prompt used to carry that rule - a general-purpose dispatcher never saw it. This makes a
    # non-conforming brief fail at dispatch time, naming the missing field. Not bypassable:
    # it guards the dispatch contract, not a policy a human might deliberately waive.
    # Limits come from .claude/implementer-tiers.json (one object per lane: depth = implementer,
    # sweep = implementer-sweep; matched on its 'agent' field). The brief names its tier ("Tier: D0",
    # "Tier: S1"); the hook denies a tier above the lane's current 'tier' and a Files list over that
    # tier's maxFiles. A missing or invalid tier file fails CLOSED (the dispatch is denied, with the
    # file named), unlike the rest of this hook, because an unreadable limit table cannot be enforced.
    # The tier file is validated on load (tier and every limit key a whole number, limits >= 1), and the
    # whole block sits in its own try/catch that denies on any unexpected exception.
    # Resuming an existing agent (SendMessage) is a different tool and is not checked here.
    if ($toolName -in @('Agent', 'Task') -and [string]$toolInput.subagent_type -in @('implementer', 'implementer-sweep')) {
        try {
            $brief = [string]$toolInput.prompt
            $model = [string]$toolInput.model
            $agentType = [string]$toolInput.subagent_type
            $problems = New-Object System.Collections.Generic.List[string]

            # Load this lane's limits. $laneName/$lane stay $null when the file cannot be used; the
            # problem is recorded here and the tier checks below are skipped.
            $tierFilePath = Join-Path $repoRoot '.claude\implementer-tiers.json'
            $tierFileRel  = '.claude/implementer-tiers.json'
            $laneName = $null; $lane = $null; $laneTiers = @(); $laneCurrent = -1
            try {
                if (-not (Test-Path -LiteralPath $tierFilePath)) { throw "file not found" }
                $tierCfg = Get-Content -LiteralPath $tierFilePath -Raw | ConvertFrom-Json
                foreach ($ln in @('depth', 'sweep')) {
                    $cand = $tierCfg.$ln
                    if ($cand -and [string]$cand.agent -eq $agentType) { $laneName = $ln; $lane = $cand }
                }
                if (-not $lane) { throw "no lane has agent '$agentType'" }
                $laneTiers = @($lane.tiers)
                if ($laneTiers.Count -eq 0) { throw "lane '$laneName' has no 'tiers' entries" }
                # Validate before any cast: [int] turns a missing key into 0 and throws on junk, and a throw
                # outside this try would reach the hook's global fail-open catch. JSON strings ("3") are rejected.
                $isWhole = { param($v) (($v -is [int] -or $v -is [long]) -and [long]$v -le [int]::MaxValue) }
                $tierProp = $lane.PSObject.Properties['tier']
                if (-not $tierProp -or -not (& $isWhole $tierProp.Value) -or [long]$tierProp.Value -lt 0) {
                    throw "lane '$laneName' has 'tier' = '$(if ($tierProp) { $tierProp.Value })' (must be a whole number >= 0)"
                }
                $requiredKeys = if ($laneName -eq 'depth') { @('maxFiles', 'maxEdits', 'maxAcceptance', 'maxToolCalls') }
                                else { @('maxOperations', 'maxFiles', 'maxCallSites', 'maxProjects', 'maxToolCalls') }
                for ($ti = 0; $ti -lt $laneTiers.Count; $ti++) {
                    foreach ($rk in $requiredKeys) {
                        $rp = $(if ($laneTiers[$ti]) { $laneTiers[$ti].PSObject.Properties[$rk] })
                        if (-not $rp -or -not (& $isWhole $rp.Value) -or [long]$rp.Value -lt 1) {
                            throw "lane '$laneName' tier $ti has '$rk' = '$(if ($rp) { $rp.Value })' (must be a whole number >= 1)"
                        }
                    }
                }
                $laneCurrent = [int]$lane.tier
                if ($laneCurrent -lt 0 -or $laneCurrent -ge $laneTiers.Count) { throw "lane '$laneName' has 'tier' $laneCurrent outside its 'tiers' list (0..$($laneTiers.Count - 1))" }
            }
            catch {
                $laneName = $null; $lane = $null; $laneTiers = @(); $laneCurrent = -1
                $problems.Add("  tier file $tierFileRel is missing, unreadable or invalid ($($_.Exception.Message)). The limits cannot be enforced, so the dispatch is refused. Restore the file (see docs/current/plans/plan_implementer_lanes_tiers_and_tool_gap_reporting.md, Step 1) and dispatch again.")
            }

            if ($model -ne 'haiku') {
                $problems.Add("  model: pass model: `"haiku`" explicitly (got '$model'). An unpinned dispatch can inherit the caller's tier.")
            }

            # Field labels at line start, tolerant of markdown bullets/bold ("- **Files:**").
            $labels = [ordered]@{
                'Files'        = '(?im)^\W*Files\b[^\r\n:]{0,20}:'
                'Symbols'      = '(?im)^\W*Symbols\b[^\r\n:]{0,20}:'
                'Call sites'   = '(?im)^\W*Call[ -]sites\b[^\r\n:]{0,20}:'
                'Acceptance'   = '(?im)^\W*Acceptance(?: check)?\b[^\r\n:]{0,20}:'
                'Out of scope' = '(?im)^\W*Out of scope\b[^\r\n:]{0,20}:'
                'Tier'         = '(?im)^\W*Tier\b[^\r\n:]{0,20}:'
                'Limits'       = '(?im)^\W*Limits\b[^\r\n:]{0,20}:'
            }
            if ($agentType -eq 'implementer-sweep') {
                $labels['Operation'] = '(?im)^\W*Operation\b[^\r\n:]{0,20}:'
            }
            $found = @{}
            foreach ($k in $labels.Keys) {
                $mm = [regex]::Match($brief, $labels[$k])
                if ($mm.Success) { $found[$k] = $mm } else { $problems.Add("  missing field '${k}:' (use the brief template in the dispatch-implementer skill).") }
            }

            # Tier value: letter (D = depth lane, S = sweep lane) + tier number, e.g. D0, S2.
            $tierEntry = $null; $tierLabel = $null
            if ($found.ContainsKey('Tier')) {
                $tm = [regex]::Match($brief.Substring($found['Tier'].Index + $found['Tier'].Length), '^[ \t*_`''"]*(?<l>[A-Za-z])[ \t]*(?<n>\d+)\b')
                if (-not $tm.Success) {
                    $problems.Add("  Tier: value must be the lane letter plus the tier number, e.g. 'Tier: D0' (depth, implementer) or 'Tier: S0' (sweep, implementer-sweep).")
                }
                elseif ($laneName) {
                    $wantLetter = $laneName.Substring(0, 1).ToUpperInvariant()
                    $gotLetter  = $tm.Groups['l'].Value.ToUpperInvariant()
                    $tierNum    = [int]$tm.Groups['n'].Value
                    $tierLabel  = "$gotLetter$tierNum"
                    if ($gotLetter -ne $wantLetter) {
                        $problems.Add("  Tier: '$tierLabel' uses the wrong lane letter; $agentType is the $laneName lane, so its tiers are written '${wantLetter}0', '${wantLetter}1', ...")
                    }
                    elseif ($tierNum -ge $laneTiers.Count) {
                        $problems.Add("  Tier: '$tierLabel' has no entry in $tierFileRel (the $laneName lane defines tiers 0..$($laneTiers.Count - 1)).")
                    }
                    elseif ($tierNum -gt $laneCurrent) {
                        $problems.Add("  Tier: '$tierLabel' is above the $laneName lane's current tier ($wantLetter$laneCurrent). Use '$wantLetter$laneCurrent' or lower; tiers are raised only by editing 'tier' in $tierFileRel after a ledger review.")
                    }
                    else {
                        $tierEntry = $laneTiers[$tierNum]
                    }
                }
            }

            # Files section = text from the Files label up to the next label; count distinct .cs paths.
            if ($found.ContainsKey('Files') -and $tierEntry) {
                $start = $found['Files'].Index + $found['Files'].Length
                $end = $brief.Length
                foreach ($k in $found.Keys) {
                    if ($k -ne 'Files' -and $found[$k].Index -gt $start -and $found[$k].Index -lt $end) { $end = $found[$k].Index }
                }
                $filesText = $brief.Substring($start, $end - $start)
                $csFiles = @([regex]::Matches($filesText, '[\w.\\/\-]+\.cs\b') | ForEach-Object { $_.Value.ToLowerInvariant() } | Select-Object -Unique)
                $maxFiles = [int]$tierEntry.maxFiles
                if ($csFiles.Count -gt $maxFiles) {
                    $problems.Add("  Files lists $($csFiles.Count) distinct .cs files; the $laneName lane at tier $tierLabel allows at most $maxFiles. Split it into compile-green slices (CLAUDE.md), or send it to implementer-senior if it cannot be split.")
                }
            }

            # Out of scope section = text from the label to the end of the brief. CLAUDE.md requires it to
            # tell the implementer to reply RESCOPE: instead of editing outside the named symbols
            # (db2b9846:L8 - an implementer edited an out-of-brief branch to make a test pass).
            if ($found.ContainsKey('Out of scope')) {
                $oosText = $brief.Substring($found['Out of scope'].Index)
                if ($oosText -notmatch 'RESCOPE') {
                    $problems.Add("  Out of scope must tell the implementer to reply RESCOPE: instead of editing anything outside the named symbols (CLAUDE.md slice contract).")
                }
                if ($oosText -notmatch '(?i)\b(Write|Edit|shell)\b') {
                    [Console]::Error.WriteLine("WARNING (not blocking) from dispatch policy: Out of scope names none of Write/Edit/shell. Consider adding 'no Write/Edit/shell writes on .cs files' so the implementer does not bypass the MCP tools.")
                }
            }

            if ($problems.Count -gt 0) {
                $laneSummary = if ($laneName) {
                    $curEntry = $laneTiers[$laneCurrent]
                    $limitText = (@($curEntry.PSObject.Properties) | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', '
                    "Lane: $laneName ($agentType). Current tier: $($laneName.Substring(0, 1).ToUpperInvariant())$laneCurrent. Limits at that tier: $limitText."
                }
                else {
                    "Lane: $agentType. Limits unavailable: fix $tierFileRel first."
                }
                Deny @"
BLOCKED by dispatch policy: $agentType brief does not meet the slice contract.

$($problems -join "`n")

$laneSummary
$agentType is Haiku-tier. A brief it can finish names, up front: Files (full paths including the
project, within the tier's file limit), Symbols, Call sites (pre-measured with
InspectSymbol(aspect: blastRadius) / Search(mode: references)), one Acceptance check, Out of scope,
Tier (e.g. D0 or S0) and Limits (copied from $tierFileRel), plus Operation for implementer-sweep.
Load the dispatch-implementer skill for the full brief template and slice limits.
Fix the brief and dispatch again. Nothing was dispatched.
"@
            }
            exit 0
        }
        catch {
            # Fail CLOSED for this block only (the rest of the hook keeps its fail-open design): an
            # unexpected error here would otherwise reach the global catch and let a dispatch through
            # unchecked. Deny/exit is not an exception, so the Deny calls above are not caught here.
            Deny @"
BLOCKED by dispatch policy: internal error in dispatch check: $($_.Exception.Message)

The implementer dispatch check could not run to completion, so the dispatch is refused rather than
allowed unchecked. Check .claude/implementer-tiers.json and the brief format, then dispatch again.
Nothing was dispatched.
"@
        }
    }

    # --- ReplaceSnippet parameter/ordering guard --------------------------------------
    if ($toolName -eq 'ReplaceSnippet' -or $toolName -match '__ReplaceSnippet$') {
        $filePath   = $toolInput.filePath
        $batchEdits = $toolInput.batchEdits

        if ($filePath -and $batchEdits) {
            Deny @"
BLOCKED by dog-fooding policy: ReplaceSnippet called with both filePath and batchEdits.

Each batch edit carries its own path - filePath is only for the single-edit form.
Move the top-level filePath into each batchEdits entry instead:

  ReplaceSnippet(reason: ..., action: "apply", batchEdits: [
    { filePath: "$filePath", oldContent: <old>, newContent: <new> }
    // ...additional entries, each with its own filePath
  ])

Or, if this really is a single edit, drop batchEdits entirely and pass
oldContent/newContent alongside filePath instead of a batch.
"@
        }

        # Heuristic-only ordering check: for each batch entry, if its newContent appears to
        # invoke a method/member that isn't defined by an earlier entry's newContent AND isn't
        # already present in the file being edited, warn (don't block - this cannot resolve
        # symbols, only pattern-match, so a false positive here must never stop a good edit).
        if ($batchEdits -is [System.Collections.IEnumerable]) {
            $definedSoFar = New-Object System.Collections.Generic.HashSet[string]
            $warnings = New-Object System.Collections.Generic.List[string]
            $i = 0
            foreach ($entry in $batchEdits) {
                $i++
                $new = [string]$entry.newContent
                if (-not $new) { continue }

                # Collect names this entry defines (method/property/field/enum-member-ish
                # declarations) - deliberately loose, this only needs to catch the common
                # "definition after its first use" ordering bug, not parse C#.
                foreach ($m in [regex]::Matches($new, '\b(?:public|private|protected|internal|static)\b[^;{}]*?\b(\w+)\s*\(')) {
                    [void]$definedSoFar.Add($m.Groups[1].Value)
                }
                foreach ($m in [regex]::Matches($new, '\b(\w+)\s*[:=]\s*(?:\d|"|\w+\s*,|\w+\s*\})')) {
                    [void]$definedSoFar.Add($m.Groups[1].Value)
                }

                # Look for a call-site shape (Identifier() ) referencing something not yet
                # defined in this batch. Only flag identifiers that look purpose-built (not
                # common BCL/keyword names) to keep the false-positive rate low.
                foreach ($m in [regex]::Matches($new, '\b([A-Z]\w{3,})\s*\(')) {
                    $name = $m.Groups[1].Value
                    if ($name -in @('ArgumentException', 'InvalidOperationException', 'NotSupportedException', 'Console', 'Task', 'List', 'Dictionary', 'String', 'Convert', 'Regex')) { continue }
                    if (-not $definedSoFar.Contains($name)) {
                        $warnings.Add("  batchEdits[$i] calls '$name(...)' - if that method/member is defined in a LATER entry of this same batch, move its definition earlier. The compile gate rejects any intermediate state that doesn't compile.")
                    }
                }
            }
            if ($warnings.Count -gt 0) {
                [Console]::Error.WriteLine(@"
WARNING (not blocking) from dog-fooding policy: possible ReplaceSnippet ordering issue.

$($warnings -join "`n")

This is a heuristic text match, not real symbol resolution - it may be a false
positive if the referenced name already exists elsewhere in the file. Proceeding.
"@)
            }
        }
        exit 0
    }

    # --- git via shell -----------------------------------------------------------
    if ($toolName -in @('Bash', 'PowerShell')) {
        $command = [string]$toolInput.command
        if (-not $command) { exit 0 }

        # Tool-experience journal writes (CLAUDE.md "Tool-experience journal") often quote git
        # commands in the note text itself ("blocked a git diff ..."). That is prose, not a
        # git invocation, so a command aimed at .claude/journal/ is exempt - unless git also
        # appears at a command position, which would be a real call riding along.
        if ($command -match '\.claude[\\/]+journal[\\/]' -and $command -notmatch '(^|[;&|(]\s*)git\s') { exit 0 }

        # Rules below scan a copy with here-string/heredoc bodies removed (prose is not a command).
        # $command stays the original for block messages, out-of-repo git checks and bypass lookup.
        # Simple same-command variable assignments are also substituted (Expand-SimpleAssignments), so
        # `$r='C:\other'; git -C $r log` is judged by the literal path.
        $scan = Expand-SimpleAssignments (Remove-HereStrings $command)

        # --- shell text search/read of C# ---------------------------------------------
        # The Grep-tool rule above is trivially sidestepped by running grep/rg/Select-String/
        # cat through the shell (seen 2026-10-01: a subagent ran `grep -rn "public X(" --include=*.cs`).
        # Deny a search/read/stream-edit command at a command position (start, after ; & | (,
        # or after xargs) when the command names a .cs file or glob. \.cs\b does not match
        # .csproj/.cshtml. Listing alone (Get-ChildItem *.cs for mtimes) is left alone: there is
        # no MCP equivalent and it reads no content. Harness worktrees stay exempt.
        $csReaders = 'grep|egrep|fgrep|rg|ag|ack|findstr|Select-String|sls|cat|type|Get-Content|gc|head|tail|less|more|sed|awk'
        # Out-of-repo exemption (deviation): MCP tools cannot see files outside the loaded solution, so when every
        # .cs target is outside this repo (absolute path, or relative under an out-of-repo Set-Location/cd/git -C)
        # the rule does not apply. It is a condition, not an `exit 0`, so the git rule below still runs.
        # Per statement (deviation, Get-CsReaderHit): the reader verb and the .cs token must be in the same statement
        # (split on ; && || and newlines outside quotes; a pipeline is one statement), so `Get-Content notes.txt;
        # Get-ChildItem -Filter *.cs` is allowed. A variable assigned from a statement with a .cs token counts as one.
        $csReaderVerb = if ($command -notmatch '[\\/]Worktree[\\/]') { Get-CsReaderHit $scan $csReaders } else { $null }
        if ($csReaderVerb) {
            $which = $csReaderVerb
            Exit-IfBypassed "$command`n$([string]$toolInput.description)" $null
            DenyBypassable @"
BLOCKED by dog-fooding policy: '$which' via shell on C# source.

  $command

Shell text tools only see raw text. Use the RoslynSentinel tools instead:

  Search(mode: "text",       query: "...", fileGlob: "**/*.cs")   # grep/rg/Select-String
  Search(mode: "symbol",     query: "Name")                       # find a declaration
  Search(mode: "references", query: "Name", ...)                  # find callers
  ReadFile / GetMethodSource / GetFileOutline                     # cat/Get-Content/head

If no MCP tool covers what you need, or the right one is broken, that is a
BLOCKING finding: stop, write docs/current/blockers/blocking_error_<slug>.md,
and end the turn. Do not reword the command to get past this hook.

For docs, notes or scratchpad output use the Edit/Write tool on the non-.cs file; the hook allows that. Do not reword the command to get past this hook.
"@
        }

        # --- shell writes/moves of C#----------------------------------------------------
        # The Edit/Write rule above covers only the built-in tools; a shell writer is the same
        # policy violation by another route (implementers wrote C# via Set-Content / here-strings).
        # Deny, on the here-string-stripped $scan, when the command names a .cs file AND has a
        # writer verb at a command position, a .NET file-write call, or a redirection to a .cs
        # target. "Names a .cs file" means a PATH-SHAPED token (Test-CsPathToken), so a .cs name inside
        # prose (`Set-Content notes.md "see Foo.cs"`, `.Replace('Foo.cs:1','x')`) does not trip it.
        # Residual false positive: a bare path-shaped literal as text (`Set-Content notes.md 'Foo.cs'`,
        # `.Replace('Foo.cs','Bar.cs')`) - use the DeliberateHookBypass keyword for that.
        # .csproj/.cshtml never match. Harness worktrees stay exempt.
        $csWriters = 'Set-Content|Add-Content|Out-File|Copy-Item|Move-Item|Rename-Item|New-Item|tee|cp|mv|copy|move|ren|sc|ac'
        $csWriteApis = '\b(WriteAllText|WriteAllLines|WriteAllBytes|AppendAllText|AppendAllLines)\b'
        $csRedirect = '>{1,2}\s*["'']?[^\s"''|;&]*\.cs\b'
        # Same out-of-repo exemption as the reader rule (deviation); redirect targets are classified too, because
        # the tokenizer does not split on `>`.
        $csRedirectTargets = @([regex]::Matches($scan, '>{1,2}\s*["'']?([^\s"''|;&]*\.cs)\b') | ForEach-Object { $_.Groups[1].Value })
        if ((Test-CsPathToken $scan) -and $command -notmatch '[\\/]Worktree[\\/]' -and
            ($scan -match "(^|[;&|(\r\n]\s*)($csWriters)(\s|$)" -or $scan -match $csWriteApis -or $scan -match $csRedirect) -and
            -not (Test-ShellCsTargetsOutsideRepo $scan -ExtraTokens $csRedirectTargets)) {
            Exit-IfBypassed "$command`n$([string]$toolInput.description)" $null
            DenyBypassable @"
BLOCKED by dog-fooding policy: shell write/move/copy of a C# file.

  $command

C# writes go through the RoslynSentinel MCP tools, not the shell. Use the one that matches:

  Member / ReplaceSnippet / ApplyDiff                      - edit existing code
  WriteFile (operation: CreateFile or ReplaceFile)         - whole-file content
  CreateFile                                               - new empty file, then Member(add)
  MoveMember                                               - move code between files/types
  DeleteFile                                               - delete (never rm / Remove-Item)

If the right tool has no equivalent operation (for example repairing line endings or a BOM),
that is a finding to report - stop, write docs/current/blockers/blocking_error_<slug>.md, and
end the turn. It is not something to do by shell. Do not reword the command to get past this hook.

For docs, notes or scratchpad output use the Edit/Write tool on the non-.cs file; the hook allows that. Do not reword the command to get past this hook.
"@
        }

        # The MCP Git tool only ever operates on whichever solution is currently
        # loaded into the server - it has no parameter to target any other repo or
        # worktree. That makes it structurally unable to cover git status/log/diff
        # for a path outside this repo (e.g. a PlanStepRunner harness worktree under
        # */Worktree/, or a sibling clone such as csharp-sdk, each its own separate
        # git working tree). Blocking those calls here would strand the task on a read
        # with no compliant route at all, which is worse than the drift-detection this
        # hook exists to protect.
        # Deviation from the earlier three-block form (--git-dir / -C / literal `cd`):
        # Get-CommandLocations now collects every explicit location (cd/chdir/sl/pushd/
        # Set-Location/Push-Location, git -C, --git-dir=, -WorkingDirectory). If there is
        # at least one and EVERY one is outside this repo's root (or under a root listed
        # in outside-roots.json), the command is exempt - for any read-only git operation
        # (status, log, diff, describe, tag listing, rev-parse, show ...), not just
        # status/log/diff. Mutating operations (add/commit/revert) stay covered even
        # out-of-repo, since those are exactly the ones this policy most needs to
        # chokepoint. If any location is in-repo, relative, unresolvable or absent, fall
        # through to the normal checks ("cannot tell" => keep enforcing).
        function Test-OutOfRepo([string]$path) {
            if (-not $path) { return $false }
            $path = $path.Trim('"', "'")
            try { $resolved = (Resolve-Path -LiteralPath $path -ErrorAction Stop).Path.TrimEnd('\', '/') }
            catch { return $false }
            # Path-boundary match, not a raw string prefix: "...\RoslynSentinel-TestRuns" must
            # not be mistaken for inside "...\RoslynSentinel" just because the text starts the
            # same way.
            return -not ($resolved -eq $repoRoot -or $resolved.StartsWith("$repoRoot\", [StringComparison]::OrdinalIgnoreCase) -or $resolved.StartsWith("$repoRoot/", [StringComparison]::OrdinalIgnoreCase))
        }
        # git [global options] <mutating op> - never gets the out-of-repo pass: add, commit, revert, stash (except
        # list/show), worktree add/remove/prune/move/lock/unlock/repair, and tag that creates or deletes (read-only
        # `git tag`, `tag -l`, `tag --points-at` stay exempt).
        $gitMutating = '\bgit\s+(?:(?:-C|-c|--git-dir|--work-tree)(?:\s+|=)\S+\s+|--\S+\s+)*(?:(?:add|commit|revert|stash)\b(?!\s+(?:list|show)\b)|worktree\s+(?:add|remove|prune|move|lock|unlock|repair)\b|tag\s+(?:-[adfsm]\b|--delete\b|[^-\s]))'
        if ($scan -notmatch $gitMutating -and (Test-AllLocationsOutside @(Get-CommandLocations $scan) -ResolveExisting)) { exit 0 }

        # Only the operations the MCP Git tool actually implements AND this hook enforces.
        # Everything else (rebase, merge, restore...) has no MCP equivalent, so denying it
        # would strand the task with nowhere to go. tag, stash and worktree moved here when
        # the tool gained them (plan_git_tool_tag_stash_worktree_hunks.md step 14, D-19).
        $covered = 'status|log|diff|add|commit|revert|tag|stash|worktree'

        # An uncovered git operation anywhere in the command line makes the whole line
        # un-blockable: the caller can't split it, and denying it leaves them stranded
        # with no MCP route. `reset` is the live example - Git can stage but not unstage,
        # so blocking a `git reset && git status` traps a mis-stage with no way back.
        $uncovered = 'reset|restore|rm|mv|branch|checkout|switch|push|pull|fetch|clone|rebase|merge|cherry-pick|bisect|reflog|clean|apply|show|update-index|ls-files|check-ignore|rev-parse|config|remote|blame'
        if ($scan -match "(^|[;&|]|\s)git\s+(-C\s+\S+\s+)?($uncovered)\b") { exit 0 }
        # Sub-actions of a covered operation that the tool deliberately does not offer
        # (D-17 stash drop/clear, worktree prune/lock/unlock/move/repair) stay shell-only.
        if ($scan -match "(^|[;&|]|\s)git\s+(-C\s+\S+\s+)?(stash\s+(drop|clear)|worktree\s+(prune|lock|unlock|move|repair))\b") { exit 0 }

        if ($scan -match "(^|[;&|]|\s)git\s+(-C\s+\S+\s+)?($covered)\b") {
            Exit-IfBypassed "$command`n$([string]$toolInput.description)" $null

            DenyBypassable @"
BLOCKED by dog-fooding policy: git via shell.

  $($command.Trim())

status, log, diff, stage/add, commit (incl. amend) and revert are covered by the MCP Git tool; tag, stash and worktree by the MCP GitRemote tool:

  Git(operation: "status")
  Git(operation: "diff",   ref: "staged")
  Git(operation: "stage",  paths: "a.cs,b.cs")
  Git(operation: "hunks",  paths: "a.cs")   # then stage with hunkIds + hunkFingerprint (replaces git add -p)
  Git(operation: "stage",  paths: "a.cs", hunkIds: "1,3", hunkFingerprint: "...")
  Git(operation: "commit", message: "...")
  Git(operation: "commit", amend: true)   # keeps HEAD's message (--no-edit)
  Git(operation: "commit", amend: true, message: "...")   # replaces it
  GitRemote(operation: "tag",      action: "list")
  GitRemote(operation: "stash",    action: "push", message: "...")
  GitRemote(operation: "worktree", action: "add", worktreePath: "<absolute, outside the repo>", branchName: "...", createBranch: true)
  GitRemote(operation: "worktree", action: "remove", worktreePath: "...")
      # pass discardUncommittedChanges: true only to throw away a dirty worktree
  (GitRemote is on demand in claude-lean: McpToolsetControl(toolSet: git, enabled: true) first.)

The Git tool only sees this repo. For a read-only command on another repo, pass its absolute path literally after ``git -C``.

It also avoids the shell-quoting and CRLF footguns that Bash hits on Windows paths.

The tools also implement reset, show and abort (Git) and branch, checkout, push, fetch and pull (GitRemote); the hook does
not block those in the shell yet, but prefer the tool. Still shell-only (not blocked): rebase, merge,
cherry-pick, restore, rm, mv, clean, stash drop/clear, tag push, worktree prune/lock/move.

If the Git tool fails or returns wrong data, that is a BLOCKING finding: stop,
write docs/current/blockers/blocking_error_<slug>.md, and end the turn.
"@
        }
        exit 0
    }

    # --- MCP Git(operation: commit) pre-commit validation -----------------------------
    # Matches the real tool name however it arrives - a bare "Git" in a unit-test payload,
    # or the full "mcp__<server>__Git" the harness sends for an actual MCP call.
    if ($toolName -eq 'Git' -or $toolName -match '__Git$') {
        $operation = [string]$toolInput.operation
        if ($operation -eq 'commit') {
            $message = [string]$toolInput.message
            $amend   = $toolInput.amend -eq $true

            # amend=true with no message keeps HEAD's message (--no-edit) - nothing to check
            # yet, since the trailer would already be on HEAD from when it was first committed.
            if ($message -and $message -notmatch 'Co-Authored-By:') {
                Deny @"
BLOCKED by dog-fooding policy: commit message missing Co-Authored-By trailer.

  message: $message

Every commit here needs the attribution trailer. Add it to the message:

  Git(operation: "commit", message: "$message`n`nCo-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>")
"@
            }

            # Scope check. Since the Git tool change, a commit that names paths (with or without
            # scope) commits exactly those paths, and a scope-less, file-less commit commits
            # exactly what is already staged - it stages nothing itself, so nothing else can be
            # swept in. `paths` (or its alias `files`) alone therefore IS the explicit listing (the tool infers
            # scope=listed from them). The hook cannot tell which session staged what, so for the
            # scope-less, file-less shape the only thing left to catch is an empty index.
            #
            # An explicit scope=all or scope=tracked with no file list is different: the tool
            # pre-stages every dirty path itself, which is a real sweep, so that shape keeps the
            # git-status dirty-count check. scope=all/tracked WITH paths is trusted outright, as
            # before (the tool refuses that combination itself). scope=listed with no paths is
            # left to the tool, which refuses it with its own actionable error.
            $explicitFiles = [string]$toolInput.files   # legacy alias of paths, still accepted
            $explicitPaths = [string]$toolInput.paths
            $scope         = [string]$toolInput.scope
            if (-not $explicitFiles -and -not $explicitPaths) {
                # amend can legitimately commit nothing new (message-only reword), so an empty
                # index is only a problem for a non-amend commit.
                if (-not $scope -and -not $amend) {
                    $staged = $null
                    try {
                        $out = & git -C $repoRoot diff --cached --name-only 2>$null
                        # Fail open if git itself failed (not a repo, git missing): an empty
                        # result from a failed call must not read as "nothing staged".
                        if ($LASTEXITCODE -eq 0) { $staged = @($out | Where-Object { $_ }) }
                    }
                    catch { $staged = $null }

                    if ($null -ne $staged -and $staged.Count -eq 0) {
                        Deny @"
BLOCKED by dog-fooding policy: commit with nothing staged.

A commit with no paths and no scope commits exactly what is already staged, and the
index is empty, so there is nothing to commit. Stage first, or pass paths:

  Git(operation: "commit", paths: "RoslynSentinel.Foo/Bar.cs,RoslynSentinel.Foo/Baz.cs", message: "...")

or:

  Git(operation: "stage", paths: "RoslynSentinel.Foo/Bar.cs")
  Git(operation: "commit", message: "...")

Per CLAUDE.md, list only the files changed in the current session. Nothing was committed.
"@
                    }
                }
                elseif ($scope -and $scope -ne 'listed') {
                    try {
                        $porcelain = & git -C $repoRoot status --porcelain 2>$null
                        $dirtyCount = ($porcelain | Where-Object { $_ }).Count
                    }
                    catch { $dirtyCount = 0 }

                    if ($dirtyCount -gt 0) {
                        Deny @"
BLOCKED by dog-fooding policy: commit with scope="$scope" and no paths.

scope="$scope" stages the whole working tree before committing, and git status shows
$dirtyCount dirty path(s). Per CLAUDE.md, a commit includes only the files changed in the
current session - other sessions or in-flight work may share this worktree.

Name the paths instead (scope is inferred as "listed"):

  Git(operation: "commit", paths: "RoslynSentinel.Foo/Bar.cs,RoslynSentinel.Foo/Baz.cs", message: "...")

If everything currently dirty really was touched this session, list them all
explicitly anyway - that is what makes the scope auditable, not an assumption
this hook has to make on your behalf.
"@
                    }
                }
            }
        }
        exit 0
    }

    exit 0
}
catch {
    # Fail open, but leave a trace so a silently-broken hook is discoverable.
    [Console]::Error.WriteLine("enforce-dogfood.ps1 error (allowing call): $($_.Exception.Message)")
    exit 0
}
