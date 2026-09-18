# `Git(operation: "stage", scope: "listed")` staged and committed a file that was never named in its `files` list

**Status:** OPEN - found 2026-09-18 (overnight session), while implementing Decision 1 of
`docs/current/plans/plan_scoped_operation_ledger.md`. Not a hard stop: this session had explicit
overnight authorization for narrow tool-bypass-plus-blocker-doc when an MCP tool is broken, rather
than halting entirely. The real repo state was confirmed safe (no data loss or corruption - a file
landed in the wrong commit, nothing more) via an authorized narrow `git` CLI cross-check, and the
session continued. This doc is for morning review, not a currently-open blocker on further work.

## What was being attempted

Two real code changes had been made earlier in the session - `RoslynSentinel.Common/PersistentWorkspaceManager.cs`
and `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs`, both part of the new
`ScopedOperationLedgerEngine` (Decision 1 of the plan doc). The intent was to commit a set of
design-doc-only files first, as a separate commit, then commit the two code files afterward as a
second, code-only commit. `scope: "listed"` was used specifically to get that precision.

## Call 1 - staging only the doc files

```
Git(operation: "stage", scope: "listed", files: "docs/current/proposal_movemember_instance_callsite_resolution.md,docs/current/proposal_nonblocking_validation_mode.md,docs/current/proposal_scoped_operation_ledger.md,docs/current/plans/plan_scoped_operation_ledger.md,docs/current/blockers/resolved/blocking_error_loadsolution_missing_msbuild_workspaces_assembly.md")
```

The response's `staged` array correctly listed exactly those 5 doc files. `ServiceRegistrationExtensionsAdvanced.cs`
and `PersistentWorkspaceManager.cs` were not named in `files` and were not in `staged` - they appeared
under `unstaged` (both `status: "modified"`), which looked correct at the time.

## Call 2 - committing

```
Git(operation: "commit", message: "Add scoped operation ledger design + MoveMember dry-run widening\n\n...", ...)
```

Succeeded, hash `725adf91553ac72e4190a745a2b5e0adf47a35ff`.

## Discovery - the second stage call came up short

Several tool calls later, after implementing the rest of Decision 1's code, this call was made to
stage everything for a second, code-only commit:

```
Git(operation: "stage", scope: "listed", files: "RoslynSentinel.Common/PersistentWorkspaceManager.cs,RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs,RoslynSentinel.Common/IScopedOperationLedger.cs,RoslynSentinel.Common/LedgerEntryBase.cs,RoslynSentinel.Common/ScopedOperationLedgerEngine.cs")
```

The response's `staged` array showed only the 3 brand-new untracked files (`IScopedOperationLedger.cs`,
`LedgerEntryBase.cs`, `ScopedOperationLedgerEngine.cs`). `PersistentWorkspaceManager.cs` and
`ServiceRegistrationExtensionsAdvanced.cs` were silently absent from both `staged` and `unstaged` in
that response, and from a follow-up `Git(operation: "status")` call. The first assumption was that
those two files simply had not picked up the latest edits yet.

`PersistentWorkspaceManager.cs` turned out fine on closer look - an edit after the first commit had
made it dirty again, so restaging surfaced it correctly on a second attempt. That one was a red
herring, not itself a bug.

`ServiceRegistrationExtensionsAdvanced.cs` did not surface no matter what was tried:

- `Git(operation: "status")` - never listed the file in staged, unstaged, or untracked, across three
  separate calls.
- `Git(operation: "diff", target: "working", paths: "RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs")` -
  returned `{"diff":"","filesChanged":0}`.
- `Git(operation: "diff", target: "staged", paths: "...")` - also returned `{"diff":"","filesChanged":0}`.
- `Git(operation: "diff", target: "725adf91553ac72e4190a745a2b5e0adf47a35ff", paths: "...")` (diffing
  directly against the docs-only commit hash) - this one returned a real, correct 2-line diff showing
  the `services.AddSingleton<ScopedOperationLedgerEngine>(); services.AddSingleton<IScopedOperationLedger>(...)`
  lines as an addition relative to that commit.

That last result was the tell: if the file differed from the docs-only commit but matched the working
tree with zero uncommitted diff, the file's change must already be inside that commit - despite never
being named in that commit's `stage` call or file list.

## Verification performed outside the `Git` tool (authorized narrow bypass)

Since the `Git` tool's own `status`/`diff`/`stage` reporting was the thing under suspicion, it could
not be used to verify itself. Per tonight's overnight authorization for a narrow shell cross-check
when an MCP tool is suspected broken, the real `git` CLI was used directly, read-only, to confirm repo
state:

1. `git status --porcelain -- RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs` -
   no output, meaning git itself considers the file clean (matches HEAD exactly).
2. `git diff 725adf91553ac72e4190a745a2b5e0adf47a35ff -- RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs` -
   no output, confirming zero difference between the working file and that commit.
3. `grep -n "ScopedOperationLedger" .../ServiceRegistrationExtensionsAdvanced.cs` - confirmed the two
   new lines are present in the file on disk.
4. `git show --stat 725adf91553ac72e4190a745a2b5e0adf47a35ff | grep -i ServiceRegistration` - confirmed
   `ServiceRegistrationExtensionsAdvanced.cs | 2 +` is listed as changed in that commit's stat summary.

All four are consistent with only one explanation: `ServiceRegistrationExtensionsAdvanced.cs` was
staged and committed as part of `725adf9`, even though it was never named in that commit's `stage`
call and its content has nothing to do with that commit's stated subject (design docs +
`MoveMember` dry-run widening).

## Where it happened

- Tool: `Git`, `operation: "stage"`, `scope: "listed"` (Call 1, above) is the suspected point of
  over-staging; the effect was only detected downstream, at the second `stage` call and the
  `status`/`diff` calls that followed it.
- File wrongly included: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs`.
- Commit it landed in: `725adf91553ac72e4190a745a2b5e0adf47a35ff` ("Add scoped operation ledger
  design + MoveMember dry-run widening") - a commit whose message and intent never mention it.
- Server-side implementation (from the related, already-verified sibling doc below, not re-read this
  session): `RoslynSentinel.Server.Basic/SentinelGitTools.cs`, `StageAsync`
  (`SentinelGitTools.cs:606-666`).

## Root cause - working hypothesis, not traced to source this session

**Not confirmed against source.** The `StageAsync` implementation was not re-read in this session to
find the exact code path. What follows is built from the behavioral evidence above only, and is
labeled as a hypothesis per this repo's root-cause discipline.

The most likely candidate: an earlier stage call in this same session - prior to Call 1 above - staged
files more broadly than its own `files`/`paths` argument named, and `ServiceRegistrationExtensionsAdvanced.cs`
rode along in that earlier staging without being named anywhere. Call 1's `scope: "listed"` response
looked entirely correct in isolation (it did not claim to stage the file, and it did not appear in
that response's `staged` list) - but if the file was *already* sitting in the index as staged from an
earlier call, a `git add` invocation scoped only to the newly-named doc paths would leave it staged
and untouched, and a subsequent `git commit` would pick it up along with everything else already in
the index. That would fully explain every observed symptom: Call 1's response naming only the doc
files, the file never separately appearing as "staged" in that response, and the file nonetheless
being present in the resulting commit.

This is unconfirmed. No prior stage call in this session's history was re-examined against source to
identify which specific one (if any) first staged the file, and the `StageAsync` implementation was
not read this session to check whether it clears/replaces the index scope on each `scope: "listed"`
call versus additively staging on top of whatever is already in the index.

## Why this is a real, distinct defect

Per the `scope` parameter's own `[Description]` (quoted in
`docs/current/blockers/blocking_error_git_stage_scope_schema_stale_server.md`, `SentinelGitTools.cs`
around line 24-29, and confirmed already fixed/deployed as of that sibling doc):

> "listed" stages exactly the paths you name in files/paths, untracked ones included - use this
> whenever you know which files you want. Naming files alongside a scope other than "listed" is
> rejected, so a file list can never be silently overridden.

That is a stated hard guarantee: a file list can never be silently overridden. It did not hold here -
a file entirely outside the named list ended up staged and committed anyway, silently, with no error,
warning, or discrepancy visible in the `stage` call's own response. This is not the same defect as
`blocking_error_git_stage_scope_schema_stale_server.md` (a stale-server schema/enum mismatch, already
believed fixed in source) or `blocking_error_changesignature_silent_noop_on_valid_constructor.md` (an
unrelated tool, `ChangeSignature`, silently no-op-ing instead of writing). This is a correctness
defect in the one operation this repo's `CLAUDE.md` names explicitly as the chokepoint for all git
writes, specifically so that what lands in a commit is precise and auditable - and the escape hatch
this session used to detect it (real `git` CLI) is exactly the kind of operation the dog-fooding
policy exists to make unnecessary.

## Impact

No data was lost or corrupted. The content that landed in the wrong commit was itself correct and
intentional (the real DI registration lines, not garbage or a partial write). The actual harm is
commit hygiene and attribution: a docs-only commit's message and stated intent do not match its
actual diff, which undermines exactly the kind of trustworthy audit trail the dog-fooding policy in
`CLAUDE.md` exists to produce. No retroactive fix (e.g. a history rewrite) was attempted - `CLAUDE.md`'s
git safety rules disfavor destructive/history-rewriting operations without explicit authorization, and
this is a local, unpushed commit that the user is already planning to review in the morning per their
own stated overnight-review process. Flagging it here for that review, rather than rewriting history
unilaterally, is the intended resolution path for tonight.

## What unblocks it

A maintainer needs to read the `Git` tool's `stage` operation implementation - specifically
`StageAsync` in `RoslynSentinel.Server.Basic/SentinelGitTools.cs` (around lines 606-666 per the
already-verified sibling doc, not re-confirmed this session) - and the `scope: "listed"` branch in
particular, to find where a file outside the `files`/`paths` argument could end up staged. Candidates
to check, not mutually exclusive:

- Whether `scope: "listed"` runs `git add <named paths>` additively on top of whatever the index
  already contains, versus first resetting/clearing scope to only the named paths - if additive, any
  file staged by an earlier call in the same session (via any `scope`) would silently persist through
  every subsequent `scope: "listed"` call until explicitly unstaged.
- Whether an earlier stage call in this session's own history (not identified here) used a broader
  `scope` (`tracked` or `all`) or a `files`/`paths` list that unintentionally matched
  `ServiceRegistrationExtensionsAdvanced.cs`, and that call's own response should have been (but
  apparently was not) checked for it at the time.
- Whether `StageAsync`'s reported `staged`/`unstaged` lists in its response are computed from the
  same source of truth actually passed to `git commit`, or whether there is a drift between what the
  tool *reports* as staged and what is actually in the git index at commit time.

A repro test: call `stage` with `scope: "listed"` naming file A only, when file B (a different,
already-modified tracked file) is also dirty; commit; then assert via real `git show --stat` that
only file A appears in the resulting commit. Repeat with an intervening `scope: "listed"` call on file
B beforehand (staging B first, then A in a separate call) to test the additive-index hypothesis
specifically.

## Related

- `docs/current/blockers/blocking_error_git_stage_scope_schema_stale_server.md` - establishes that
  `scope: "listed"` and its "file list can never be silently overridden" guarantee are real,
  already-deployed source (not the stale-schema issue that doc is about), which is what makes this
  session's failure to honor that guarantee a genuine regression/defect rather than a documentation
  gap.
- `docs/current/blockers/blocking_error_git_stage_ignores_untracked_files.md` - the original
  `stageAll`-overrides-`files` defect that motivated the `scope` migration in the first place; already
  fixed in source per the sibling doc above. This is a different failure mode (nothing here involves
  untracked files or the old `stageAll` boolean), but establishes this is not the first time the
  stage/scope boundary has broken silently.
- `docs/current/blockers/blocking_error_changesignature_silent_noop_on_valid_constructor.md` - a
  different tool, same session, same "success-shaped response conceals a real defect" pattern.
- `docs/current/plans/plan_scoped_operation_ledger.md`, Decision 1 - the task this defect was found
  during.
- Commit `725adf91553ac72e4190a745a2b5e0adf47a35ff` - contains the unintentionally-included
  `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs` change; local and unpushed
  as of this writing.
</content>
