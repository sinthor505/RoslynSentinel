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
# KNOWN LIMITATION: git and .cs detection is a regex over the command line, so a script
# that merely *contains* a covered git command as text (a test fixture, an echo, a quoted
# single-line literal) is blocked too. PowerShell here-string and bash heredoc BODIES are
# stripped before scanning (Remove-HereStrings), so prose appended to a doc that way is fine.
# Put other such content in a file rather than on the command line - see
# enforce-dogfood.Tests.ps1, which exists in file form for exactly this reason.
#
# Also covers, per the same policy:
#   - Read on an in-repo .cs path -> ReadFile / GetFileOutline / GetMethodSource / Search.
#   - Grep on a .cs path/glob or an obviously C#-symbol-shaped pattern -> Search/FindReferences.
#   - Bash/PowerShell text search, read or stream edit (grep/rg/Select-String/cat/Get-Content/
#     sed...) naming a .cs file or glob -> Search/ReadFile/GetMethodSource. Same whole-command
#     regex limitation as git detection above.
#   - Bash/PowerShell WRITES to a .cs file (Set-Content/Out-File/Copy-Item/Move-Item/tee/cp/mv,
#     [IO.File]::WriteAll*, or a > redirect to a .cs target) -> Member/ReplaceSnippet/WriteFile/...
#   - Agent dispatch of `implementer`: slice-contract fields, 3-file limit, RESCOPE in Out of scope.
#   - ReplaceSnippet called with filePath and batchEdits together (rejected server-side anyway,
#     but the server's InvalidArgument error doesn't name a corrected example call the way this
#     hook's message does), and a soft warning (not a block - this is a heuristic, not a real
#     symbol resolution) when a batchEdits entry's newContent references an identifier that no
#     earlier entry in the same batch appears to define.
#   - Git(operation: commit) missing a Co-Authored-By trailer, a scope-less commit with nothing
#     staged, or scope=all/tracked with no files/paths list while the tree is dirty.
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
    # GetMethodSource, Search, FindReferences) cover it. Same exemptions as the edit block.
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
  Search / FindReferences - symbols, callers

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
  Search(mode: "references",       query: "$pattern")   # equivalent to FindReferences
  Search(mode: "declaration-kind", query: "$pattern")   # find declarations of a given kind

If the pattern above names a specific method or type, GetMethodSource or
GetFileOutline is probably a more direct route than any Search mode.

If Search/FindReferences is broken, unreachable, or has no equivalent for what
you need, that is a BLOCKING finding: stop, write
docs/current/blockers/blocking_error_<slug>.md, and end the turn.
"@
        }
        exit 0
    }

    # --- Dispatching `implementer`: slice contract --------------------------------------
    # Skill dispatch-implementer (CLAUDE.md "Dispatching `implementer`"). implementer is Haiku-tier and
    # stalls on briefs that are large or underspecified, and only the orchestrator's own prompt
    # used to carry that rule - a general-purpose dispatcher never saw it. This makes a
    # non-conforming brief fail at dispatch time, naming the missing field. Not bypassable:
    # it guards the dispatch contract, not a policy a human might deliberately waive.
    # Resuming an existing agent (SendMessage) is a different tool and is not checked here.
    if ($toolName -in @('Agent', 'Task') -and [string]$toolInput.subagent_type -eq 'implementer') {
        $brief = [string]$toolInput.prompt
        $model = [string]$toolInput.model
        $problems = New-Object System.Collections.Generic.List[string]

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
        }
        $found = @{}
        foreach ($k in $labels.Keys) {
            $mm = [regex]::Match($brief, $labels[$k])
            if ($mm.Success) { $found[$k] = $mm } else { $problems.Add("  missing field '${k}:' (use the brief template in the dispatch-implementer skill).") }
        }

        # Files section = text from the Files label up to the next label; count distinct .cs paths.
        if ($found.ContainsKey('Files')) {
            $start = $found['Files'].Index + $found['Files'].Length
            $end = $brief.Length
            foreach ($k in $found.Keys) {
                if ($k -ne 'Files' -and $found[$k].Index -gt $start -and $found[$k].Index -lt $end) { $end = $found[$k].Index }
            }
            $filesText = $brief.Substring($start, $end - $start)
            $csFiles = @([regex]::Matches($filesText, '[\w.\\/\-]+\.cs\b') | ForEach-Object { $_.Value.ToLowerInvariant() } | Select-Object -Unique)
            if ($csFiles.Count -gt 3) {
                $problems.Add("  Files lists $($csFiles.Count) distinct .cs files; a slice is at most 3. Split it into compile-green slices (CLAUDE.md), or send it to implementer-senior if it cannot be split.")
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
            Deny @"
BLOCKED by dispatch policy: implementer brief does not meet the slice contract.

$($problems -join "`n")

implementer is Haiku-tier. A brief it can finish names, up front: Files (3 or fewer, full
paths including the project), Symbols, Call sites (pre-measured with
InspectSymbol(aspect: blastRadius) / FindReferences), one Acceptance check, and Out of scope.
Load the dispatch-implementer skill for the full brief template and slice limits.
Fix the brief and dispatch again. Nothing was dispatched.
"@
        }
        exit 0
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
        $scan = Remove-HereStrings $command

        # --- shell text search/read of C# ---------------------------------------------
        # The Grep-tool rule above is trivially sidestepped by running grep/rg/Select-String/
        # cat through the shell (seen 2026-10-01: a subagent ran `grep -rn "public X(" --include=*.cs`).
        # Deny a search/read/stream-edit command at a command position (start, after ; & | (,
        # or after xargs) when the command names a .cs file or glob. \.cs\b does not match
        # .csproj/.cshtml. Listing alone (Get-ChildItem *.cs for mtimes) is left alone: there is
        # no MCP equivalent and it reads no content. Harness worktrees stay exempt.
        $csReaders = 'grep|egrep|fgrep|rg|ag|ack|findstr|Select-String|sls|cat|type|Get-Content|gc|head|tail|less|more|sed|awk'
        if ($scan -match '\.cs\b' -and $command -notmatch '[\\/]Worktree[\\/]' -and
            $scan -match "(^|[;&|(\r\n]\s*|\bxargs\s+)($csReaders)(\s|$)") {
            $which = $Matches[2]
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
"@
        }

        # --- shell writes/moves of C# ----------------------------------------------------
        # The Edit/Write rule above covers only the built-in tools; a shell writer is the same
        # policy violation by another route (implementers wrote C# via Set-Content / here-strings).
        # Deny, on the here-string-stripped $scan, when the command names a .cs file AND has a
        # writer verb at a command position, a .NET file-write call, or a redirection to a .cs
        # target. Known false positive: a writer verb with a .cs name mentioned only as text
        # (e.g. `Set-Content notes.md 'Foo.cs'`) - use the DeliberateHookBypass keyword for that.
        # \.cs\b does not match .csproj/.cshtml. Harness worktrees stay exempt.
        $csWriters = 'Set-Content|Add-Content|Out-File|Copy-Item|Move-Item|Rename-Item|New-Item|tee|cp|mv|copy|move|ren|sc|ac'
        $csWriteApis = '\b(WriteAllText|WriteAllLines|WriteAllBytes|AppendAllText|AppendAllLines)\b'
        $csRedirect = '>{1,2}\s*["'']?[^\s"''|;&]*\.cs\b'
        if ($scan -match '\.cs\b' -and $command -notmatch '[\\/]Worktree[\\/]' -and
            ($scan -match "(^|[;&|(\r\n]\s*)($csWriters)(\s|$)" -or $scan -match $csWriteApis -or $scan -match $csRedirect)) {
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
"@
        }

        # The MCP Git tool only ever operates on whichever solution is currently
        # loaded into the server - it has no parameter to target any other repo or
        # worktree. That makes it structurally unable to cover git status/log/diff
        # for a path outside this repo (e.g. a PlanStepRunner harness worktree under
        # */Worktree/, which is its own separate git working tree). Blocking those
        # calls here would strand the task on a read with no compliant route at all,
        # which is worse than the drift-detection this hook exists to protect - so an
        # explicit `git -C <path>` (or `git --git-dir=...`) pointed outside this repo's
        # root is exempt for the read-only operations. Mutating operations (add/commit/
        # revert) stay covered even out-of-repo, since those are exactly the ones this
        # policy most needs to chokepoint; only status/log/diff get the pass.
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
        if ($command -match "git\s+(?:-C\s+(?<path>""[^""]+""|'[^']+'|\S+)\s+)?.*--git-dir=(?<gd>""[^""]+""|'[^']+'|\S+)") {
            if (Test-OutOfRepo $Matches['gd']) {
                if ($command -match "\b(status|log|diff)\b") { exit 0 }
            }
        }
        if ($command -match "git\s+-C\s+(?<path>""[^""]+""|'[^']+'|\S+)") {
            if (Test-OutOfRepo $Matches['path']) {
                if ($command -match "\b(status|log|diff)\b") { exit 0 }
            }
        }
        # Same out-of-repo exemption, but for `cd <path> && git ...` / `cd <path>; git ...`
        # instead of `git -C <path> ...` - the path context comes from a preceding shell
        # builtin rather than a git flag, but it's functionally identical: the git command
        # that follows operates on whatever repo `<path>` is in, not this one.
        if ($command -match "(^|[;&|]\s*)cd\s+(?<path>""[^""]+""|'[^']+'|\S+)\s*[;&]") {
            if (Test-OutOfRepo $Matches['path']) {
                if ($command -match "\b(status|log|diff)\b") { exit 0 }
            }
        }

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

status, log, diff, stage/add, commit (incl. amend), revert, tag, stash and worktree are covered by the MCP Git tool:

  Git(operation: "status")
  Git(operation: "diff",   target: "staged")
  Git(operation: "stage",  files: "a.cs,b.cs")
  Git(operation: "hunks",  files: "a.cs")   # then stage with hunkIds + hunkFingerprint (replaces git add -p)
  Git(operation: "stage",  files: "a.cs", hunkIds: "1,3", hunkFingerprint: "...")
  Git(operation: "commit", message: "...")
  Git(operation: "commit", amend: true)   # keeps HEAD's message (--no-edit)
  Git(operation: "commit", amend: true, message: "...")   # replaces it
  Git(operation: "tag",      action: "list")
  Git(operation: "stash",    action: "push", message: "...")
  Git(operation: "worktree", action: "add", worktreePath: "<absolute, outside the repo>", branchName: "...", createBranch: true)
  Git(operation: "worktree", action: "remove", worktreePath: "...")
      # pass discardUncommittedChanges: true only to throw away a dirty worktree

It also avoids the shell-quoting and CRLF footguns that Bash hits on Windows paths.

The tool also implements reset, branch, checkout, push, fetch, pull, show and abort; the hook does
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

            # Scope check. Since the Git tool change, a commit that names files (with or without
            # scope) commits exactly those paths, and a scope-less, file-less commit commits
            # exactly what is already staged - it stages nothing itself, so nothing else can be
            # swept in. `files`/`paths` alone therefore IS the explicit listing (the tool infers
            # scope=listed from them). The hook cannot tell which session staged what, so for the
            # scope-less, file-less shape the only thing left to catch is an empty index.
            #
            # An explicit scope=all or scope=tracked with no file list is different: the tool
            # pre-stages every dirty path itself, which is a real sweep, so that shape keeps the
            # git-status dirty-count check. scope=all/tracked WITH files is trusted outright, as
            # before (the tool refuses that combination itself). scope=listed with no files is
            # left to the tool, which refuses it with its own actionable error.
            $explicitFiles = [string]$toolInput.files
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

A commit with no files and no scope commits exactly what is already staged, and the
index is empty, so there is nothing to commit. Stage first, or pass the files:

  Git(operation: "commit", files: "RoslynSentinel.Foo/Bar.cs,RoslynSentinel.Foo/Baz.cs", message: "...")

or:

  Git(operation: "stage", files: "RoslynSentinel.Foo/Bar.cs")
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
BLOCKED by dog-fooding policy: commit with scope="$scope" and no file list.

scope="$scope" stages the whole working tree before committing, and git status shows
$dirtyCount dirty path(s). Per CLAUDE.md, a commit includes only the files changed in the
current session - other sessions or in-flight work may share this worktree.

Name the files instead (scope is inferred as "listed"):

  Git(operation: "commit", files: "RoslynSentinel.Foo/Bar.cs,RoslynSentinel.Foo/Baz.cs", message: "...")

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
