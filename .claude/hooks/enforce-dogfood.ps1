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
# FAIL-OPEN BY DESIGN: if this hook itself errors, it must never block the session.
# A broken guardrail that halts all work is worse than no guardrail.
#
# KNOWN LIMITATION: git detection is a regex over the whole command line, so a script
# that merely *contains* a covered git command as text (a test fixture, a heredoc, an
# echo) is blocked too. Put such content in a file rather than on the command line -
# see enforce-dogfood.Tests.ps1, which exists in file form for exactly this reason.
#
# Tests: pwsh -NoProfile -File .claude/hooks/enforce-dogfood.Tests.ps1

$ErrorActionPreference = 'Stop'

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
        $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path.TrimEnd('\', '/')
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

status, log, diff, stage/add, commit and revert are covered by the MCP Git tool:

  Git(operation: "status")
  Git(operation: "diff",   target: "staged")
  Git(operation: "stage",  scope: "listed", files: "a.cs,b.cs")
  Git(operation: "commit", message: "...")

It also avoids the shell-quoting and CRLF footguns that Bash hits on Windows paths.

Operations the tool does NOT cover - branch, push, checkout, worktree, rebase,
stash, tag - are not blocked; use the shell for those.

If the Git tool fails or returns wrong data, that is a BLOCKING finding: stop,
write docs/current/blockers/blocking_error_<slug>.md, and end the turn.
"@
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
