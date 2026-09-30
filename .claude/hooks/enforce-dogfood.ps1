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
# Matcher note: settings.json's PreToolUse matcher for this hook must include Grep, and the
# MCP tool names ReplaceSnippet and Git (matched here by exact name OR a "__Git"/
# "__ReplaceSnippet" suffix, since Claude Code sends the full "mcp__<server>__ToolName" form
# for an actual MCP call but a bare short name in this hook's own unit-test payloads).
#
# FAIL-OPEN BY DESIGN: if this hook itself errors, it must never block the session.
# A broken guardrail that halts all work is worse than no guardrail.
#
# KNOWN LIMITATION: git detection is a regex over the whole command line, so a script
# that merely *contains* a covered git command as text (a test fixture, a heredoc, an
# echo) is blocked too. Put such content in a file rather than on the command line -
# see enforce-dogfood.Tests.ps1, which exists in file form for exactly this reason.
#
# Also covers, per the same policy:
#   - Grep on a .cs path/glob or an obviously C#-symbol-shaped pattern -> Search/FindReferences.
#   - ReplaceSnippet called with filePath and batchEdits together (rejected server-side anyway,
#     but the server's InvalidArgument error doesn't name a corrected example call the way this
#     hook's message does), and a soft warning (not a block - this is a heuristic, not a real
#     symbol resolution) when a batchEdits entry's newContent references an identifier that no
#     earlier entry in the same batch appears to define.
#   - Git(operation: commit) missing a Co-Authored-By trailer, a scope-less commit with nothing
#     staged, or scope=all/tracked with no files/paths list while the tree is dirty.
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

    # --- C# file edits -----------------------------------------------------------
    # Scope is C# only: docs, .ps1 and .md aren't in the Roslyn workspace and have no
    # tool coverage, so the normal file tools are correct for those.
    if ($toolName -in @('Edit', 'Write', 'MultiEdit', 'NotebookEdit')) {
        $filePath = [string]$toolInput.file_path
        if ($filePath -and $filePath.TrimEnd().EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase)) {

            # Never block edits inside a PlanStepRunner harness clone. Those are throwaway
            # worktrees, often not the loaded solution, and blocking there strands the run.
            if ($filePath -match '[\\/]Worktree[\\/]') { exit 0 }

            Deny @"
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
        if ($glob -and $glob -match '\.cs["'']?$') { $targetsCs = $true }
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
            Deny @"
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

        # Only the operations the MCP Git tool actually implements. Everything else
        # (branch, push, checkout, worktree, rebase, stash...) has no MCP equivalent,
        # so denying it would strand the task with nowhere to go.
        $covered = 'status|log|diff|add|commit|revert'

        # An uncovered git operation anywhere in the command line makes the whole line
        # un-blockable: the caller can't split it, and denying it leaves them stranded
        # with no MCP route. `reset` is the live example - Git can stage but not unstage,
        # so blocking a `git reset && git status` traps a mis-stage with no way back.
        $uncovered = 'reset|restore|rm|mv|branch|checkout|switch|push|pull|fetch|clone|worktree|rebase|merge|stash|tag|cherry-pick|bisect|reflog|clean|apply|show|update-index|ls-files|check-ignore|rev-parse|config|remote|blame'
        if ($command -match "(^|[;&|]|\s)git\s+(-C\s+\S+\s+)?($uncovered)\b") { exit 0 }

        if ($command -match "(^|[;&|]|\s)git\s+(-C\s+\S+\s+)?($covered)\b") {

            Deny @"
BLOCKED by dog-fooding policy: git via shell.

  $($command.Trim())

status, log, diff, stage/add, commit (incl. amend) and revert are covered by the MCP Git tool:

  Git(operation: "status")
  Git(operation: "diff",   target: "staged")
  Git(operation: "stage",  files: "a.cs,b.cs")
  Git(operation: "commit", message: "...")
  Git(operation: "commit", amend: true)   # keeps HEAD's message (--no-edit)
  Git(operation: "commit", amend: true, message: "...")   # replaces it

It also avoids the shell-quoting and CRLF footguns that Bash hits on Windows paths.

Operations the tool does NOT cover - branch, push, checkout, worktree, rebase,
stash, tag - are not blocked; use the shell for those.

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
