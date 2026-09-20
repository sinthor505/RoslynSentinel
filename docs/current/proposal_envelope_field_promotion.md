# Envelope field promotion: flatten SentinelCallToolResult<T> for weak-model discoverability

## Motivation

Every RoslynSentinel MCP tool returns `RoslynSentinel.Common.SentinelCallToolResult<T>`
(`RoslynSentinel.Common/SentinelCallToolResult.cs:81-231`). Today the wire shape mixes three
things at the same nesting depth - server identity, envelope-level status, and the tool-specific
payload - and buries some genuinely top-level information (a human-readable status line, whether a
result was paged/offloaded) one or two levels deeper than it needs to be:

```json
{
  "serverVersion": "1.0.0.0",
  "serverBuildTimeUtc": "2026-09-17T16:57:42.0777623Z",
  "serverBinaryPath": "C:\\...\\RoslynSentinel.Server.Advanced.dll",
  "serverPid": 16672,
  "responseId": "e1db7cd0-4c30-4e29-8b34-1feec604ab9c",
  "success": true,
  "data": { ...tool-specific payload, often itself containing a nested "success" and a "summary" string... },
  "findings": [],
  "directiveKind": "Proceed",
  "hasMorePages": false
}
```

Per this repo's Mission (`CLAUDE.md`), the explicit target audience is weak/self-hosted models
that must drive correct behavior from structured tool output rather than training-data familiarity
or careful reading between fields. A flatter envelope with fewer places a status signal can hide
serves that audience directly: less nesting to walk before finding "did it work" and "what
happened," fewer synonymous-looking fields at different depths (`data.success` next to the
envelope's own `success`), and no mandatory second round-trip just to see the first page of an
oversized result.

This doc captures the shape the repo owner and I already agreed on in a design discussion. It is
not exploratory - the fields to rename/regroup/add are decided; what remains open is called out
explicitly in "Open questions" below.

## Proposed shape (worked example)

Based on a real `ApplyDiff`-style response:

```json
{
  "serverInfo": {
    "version": "1.0.0.0",
    "buildTimeUtc": "2026-09-19T21:23:19.0139159Z",
    "binaryPath": "C:\\...\\RoslynSentinel.Server.Advanced.dll",
    "pid": 15872
  },
  "responseId": "e1db7cd0-4c30-4e29-8b34-1feec604ab9c",

  "isSuccess": true,
  "errorDetails": null,

  "statusMessage": "Applied 1 changes successfully (0 delete(s)). 0 failures.",
  "findings": [],

  "data": {
    "succeededFiles": ["C:\\...\\WorkspaceReadNavigationImpl.cs"],
    "failedFiles": {},
    "workspaceInSync": true,
    "validationResult": { "success": true, "diagnostics": [] }
  },

  "resultId": null,
  "hasMorePages": false,
  "totalRecords": null,
  "workspaceVersion": 11
}
```

`data` holds only the tool-specific payload after this change. `data.success`/`data.summary`
(visible today on types like `ApplyChangesResult`, `RoslynSentinel.Common/ApplyChangesResult.cs:19-30`,
whose `Summary` field is exactly the string in the example above -
`PersistentWorkspaceManager.cs:1471`, `$"Applied {succeeded.Count} changes successfully
({deletePaths.Count} delete(s)). {failed.Count} failures."`) are dropped from tool-specific payload
*types* going forward: the inner `success` duplicated the outer `isSuccess`, and `summary` is
superseded by the new top-level `statusMessage`.

**Known residual wrinkle, out of scope for this proposal:** some tool-specific payload types carry
their own nested `success` field that is a genuinely different concept from the envelope's
`isSuccess`, not a duplicate of it. `ApplyChangesResult.ValidationResult` is a `DiagnosticReport`
(`RoslynSentinel.Common/DiagnosticReport.cs:5-8`, `record DiagnosticReport(bool Success, List<DiagnosticInfo>
Diagnostics)`) whose `Success` means "did post-apply validation of this specific change find zero
diagnostics" - a different question from "did the tool call as a whole succeed." The worked example
above shows this as `data.validationResult.success`. This proposal does not touch `DiagnosticReport`
or any other nested type's own `Success` field (they are different types, not
`SentinelCallToolResult<T>` itself) - flagged here only so a future reader isn't confused seeing
two `success`-shaped fields at different nesting depths in the same response.

## Decided changes

### 1. Group server identity fields under `serverInfo`

`ServerVersion`, `ServerBuildTimeUtc`, `ServerBinaryPath`, `ServerPid`
(`SentinelCallToolResult.cs:89-111`) sit flat at the envelope root today, each backed by a static
field on `ServerBuildInfo` (`SentinelCallToolResult.cs:12-40`) computed once from the entry
assembly. The four always travel together and answer one cohesive question - "which server am I
talking to" - so this proposal nests them as `serverInfo: { version, buildTimeUtc, binaryPath,
pid }` instead of flattening them onto the envelope root.

The rationale for carrying these fields at all is already stated in the existing doc comments and
doesn't change: `ServerVersion`/`ServerBuildTimeUtc` let a caller detect a stale server binary
(`docs/current/feedback_stale_server_before_rebuild.md`); `ServerBinaryPath`/`ServerPid` let a
caller disambiguate multiple running instances and kill the exact stale process by PID
(`SentinelCallToolResult.cs:94-111`). Grouping under one object doesn't lose any of that - it just
scopes four always-co-occurring fields under the concern they actually belong to, rather than
mixing them at the same level as envelope-level status fields a caller checks on every single call.

### 2. Rename `Success` -> `IsSuccess`

Purely cosmetic, no behavior change. The MCP protocol-level `IsError` flag - set by a request
filter in `RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`'s
`AddRoslynSentinelToolsBasic` (filter registered ~line 378, acting ~lines 383-406) - is itself
named with an `Is` prefix; the filter's own comment (`ServiceRegistrationExtensionsBasic.cs:371-377`)
describes it as syncing "domain-failure -> protocol-error" by deserializing the response body and
setting `result.IsError = true` whenever the body's top-level `"success"` field is `false`
(`ServiceRegistrationExtensionsBasic.cs:398-404`). Renaming the domain-level `Success` to
`IsSuccess` makes the two flags read consistently side by side, even though they live at different
layers (domain envelope vs. MCP wire protocol) and `IsError` is mechanically *derived from*
`IsSuccess`/`ErrorDetails` rather than being a redundant duplicate of it.

Explicitly decided **not** to remove `Success`/`IsSuccess` even though `IsError` is derivable from
it: an explicit `isSuccess: false` in the domain envelope is easier for a weak model to key off
directly than inferring domain success from whether `errorDetails` happens to be null.

### 3. Rename `Error` -> `ErrorDetails`

Today: `public ResultError? Error { get; init; }`, doc comment "Error details. Non-null when
`Success` is false." (`SentinelCallToolResult.cs:175-179`). The name `Error` reads as if the field
itself *is* an error - something a caller might be tempted to truthy-check directly - rather than a
container for structured detail that is null when nothing went wrong. `ErrorDetails` disambiguates
this, and is consistent with the same bool-signal-plus-detail-payload pairing this proposal also
uses for `Findings`/`StatusMessage` (see open question below): a `bool`/nullable-signal field named
plainly, paired with a `*Details`/`*Message` field carrying the substance.

### 4. Remove `SentinelCallToolResult<T>.DirectiveKind`

`SentinelCallToolResult<T>` carries a live `DirectiveKind` property
(`SentinelCallToolResult.cs:185`, `public DirectiveKind DirectiveKind { get; init; } =
DirectiveKind.Proceed;`, backed by the `Proceed`/`ReviewRequired` enum in
`RoslynSentinel.Common/DirectiveKind.cs`). Checked via `FindReferences` against the envelope
property specifically (not the enum type or other DTOs that happen to share the name): exactly one
call site sets it - `SentinelWholeFileWriteTools.ApplyUnifiedDiff`
(`SentinelWholeFileWriteTools.cs:640`, `DirectiveKind = diffReport.HasFindings ?
DirectiveKind.ReviewRequired : DirectiveKind.Proceed`) - and that assignment is fully redundant
with the `Findings` list already being set two lines above it in the same object initializer
(`SentinelWholeFileWriteTools.cs:637-639`, `Findings = diffReport.HasFindings ? new[] { ... } :
Array.Empty<Finding>()`): both are driven by the identical `diffReport.HasFindings` condition. A
solution-wide search for any *reader* of the envelope's `DirectiveKind` (as opposed to a writer)
found zero matches - nothing in the codebase inspects `result.DirectiveKind` off a
`SentinelCallToolResult<T>` instance.

Decided: remove the property entirely. It is pure dead weight on the envelope - one writer whose
signal a caller can already get from `Findings.Count > 0`, and no reader anywhere. This is separate
from `BatchResultSummary.DirectiveKind`/`OperationSummary.DirectiveKind`
(`RoslynSentinel.Common/BatchTypes.cs:106`, `RoslynSentinel.Common/OperationSummary.cs:49`) and the
`DirectiveKind` enum itself, which remain untouched - those are nested fields on tool-specific
payload types (used throughout the Asyncify tool suite,
`RoslynSentinel.Server.Advanced/SentinelAsyncifyTools.cs`), a different and still-live concept from
the now-removed envelope-root property.

### 5. Offload restructuring: always inline page 1, drop the nested `LargeResult` object

Today, when a tool's payload exceeds `LargeResultHelper.OffloadThresholdBytes`,
`SentinelCallToolResult<T>.ForPossiblyLargeDataAsync` (`SentinelCallToolResult.cs:149-173`) omits
`Data` entirely and instead populates a nested `LargeResult: LargeResultInfo { ResultType,
WrittenToFile, FilePath, ResultId, SizeBytes, TotalRecords, Message }` object
(`SentinelCallToolResult.cs:247-296`), requiring a mandatory second round-trip via
`GetLargeResult(resultId: ...)` before the caller sees any actual data at all.

Decided direction: an oversized result is always written to disk (offloaded) **and** the envelope
simultaneously returns the first page of results inline in `data`, eliminating the mandatory extra
round-trip for a caller that only needs the first page. The nested `LargeResult` object is removed
entirely. What remains is a top-level `resultId: string?` - present/non-null only when a fuller
result was also written to disk and further pages exist beyond what's inlined; null in the ordinary
non-offloaded case - reusing the envelope's existing `HasMorePages`/`TotalRecords` fields
(`SentinelCallToolResult.cs:197-209`), which already exist and already carry exactly this meaning
for ordinary pagination, as the sole "is there more, and where" signals. No separate
offload-specific pagination concept survives alongside the ordinary one.

## Open questions

### Findings: scalar vs. list, and the new `StatusMessage` field

`Findings` (`IReadOnlyList<Finding> Findings`, `SentinelCallToolResult.cs:181-182`, "Non-fatal
observations surfaced alongside the result. Empty when there are none.") is populated by real call
sites that pass through genuine multi-item lists, not just 0-or-1 items. Confirmed via
`WorkspaceBuildTestImpl.GetDiagnostics`/`Build`/`RunTest`
(`RoslynSentinel.Basic/WorkspaceBuildTestImpl.cs:88-93, 116-147, 149-174`) that `result.Findings`
flows straight through from the underlying build/test engine result and can carry multiple real
compiler diagnostics or test failures in one response.

The original idea in the design discussion was renaming `Findings` to `StatusMessage`/
`ResultMessage` to surface a human-readable status line at the top level, since many tools'
`data.summary`-style text (e.g. the `ApplyChangesResult.Summary` string used in the worked example
above) is currently buried inside tool-specific payloads. Collapsing an actual multi-item
diagnostic list into a single scalar string would be a lossy behavior change, not merely a rename -
so this is left open rather than decided, with two resolutions on the table:

- **(a) Keep `Findings` as a list under its current name/shape, and separately add a NEW top-level
  `statusMessage: string?` field**, sourced from each tool's existing `data.summary`-style text.
  Additive, no rename, no lossy collapse. This is the resolution the worked example above assumes.
- **(b) Confirm whether Build/RunTest/GetDiagnostics's `Findings` lists are in practice always 0-1
  items** before considering any lossy collapse of `Findings` itself into a scalar.

**Recommendation: (a).** It's the safer default until (b) is actually verified against real
call-site data (not yet done), and it doesn't foreclose revisiting a `Findings` shape change later
if (b) turns out to hold.

### Offload trigger: threshold-gated vs. eager

Whether the new "offload to disk and also inline page 1" behavior (decided change #4 above) fires
only when the payload actually exceeds `LargeResultHelper.OffloadThresholdBytes` - preserving
today's conditional trigger, just also inlining page 1 when it fires - versus eagerly writing every
result to disk "just in case" regardless of size.

**Recommendation: preserve the conditional/threshold-gated trigger.** Eager disk writes for small
results that will never need paging is pure waste - I/O and disk-space cost with no corresponding
benefit, since a small result already fits in `data` with nothing left over to page through.

## Cost / risk

- **Every tool call site changes shape on the wire.** Any client (test fixtures in
  `RoslynSentinel.Tests.*`, `ModelAgentRunner`/PlanStepRunner parsing, hand-written prompt examples
  referencing field names like `success`/`error`) that reads `success`, `error`, `serverVersion`,
  etc. directly off the envelope root needs updating in the same change - this is not additive, it's
  a breaking rename plus a regroup.
- **Every call site constructing `SentinelCallToolResult<T>` with `Success =` / `Error =` needs a
  mechanical rename** to `IsSuccess =` / `ErrorDetails =`. Given the number of `Success`/`Error`
  initializer sites found across `RoslynSentinel.Basic`/`RoslynSentinel.Server.*` during citation
  verification for this doc, this is a wide but mechanical (rename-only) change, well suited to
  `RenameSymbol` rather than manual editing.
- **Tool-specific payload types lose their own `Success`/`Summary` fields** (decided change's "drop
  `data.success`/`data.summary`" note above) - this touches the payload record types themselves
  (e.g. `ApplyChangesResult`), not just the envelope, and needs each call site that reads
  `data.success`/`data.summary` updated to read `isSuccess`/`statusMessage` at the envelope level
  instead.
- **`ForPossiblyLargeDataAsync` and `LargeResultHelper.StoreLargeResultAsync` both need real
  logic changes**, not just a rename, to actually inline page 1 alongside writing to disk - today
  the two are mutually exclusive (`SentinelCallToolResult.cs:152-172`: either `Data` is set, or
  `LargeResult` is set, never both). This is the least mechanical part of the proposal.
- Additive/low-risk by comparison: `serverInfo` grouping is a pure reshape of four fields already
  computed once and never mutated per-call.
- Lowest-risk item in this proposal: removing `DirectiveKind` from the envelope. One writer
  (`SentinelWholeFileWriteTools.ApplyUnifiedDiff`, trivially updated to drop the now-redundant
  assignment) and zero readers found solution-wide - no external client can be relying on a signal
  that duplicates `Findings` and that nothing in this codebase itself ever reads back.

## Status

**Phase 1 (decided changes #1-#4 above, plus the new `StatusMessage` field) is COMPLETE and
verified**, implemented against `RoslynSentinel.Common.SentinelCallToolResult<T>`:

1. `ServerVersion`/`ServerBuildTimeUtc`/`ServerBinaryPath`/`ServerPid` grouped under a new
   `ServerInfo` record.
2. `Success` -> `IsSuccess` (via `RenameSymbol`, ~58 files).
3. `Error` -> `ErrorDetails` (via `RenameSymbol`, ~85 files).
4. `DirectiveKind` removed entirely from the envelope, per decided change #4 above.
5. New `StatusMessage` field added (`string?`, top-level), populated at ~15
   `AppliedChangeSummary` call sites across `RefactoringSignatureImpl.cs`,
   `RefactoringExtractionDocsImpl.cs`, and `RefactoringStructuralImpl.cs`. These call sites
   deliberately duplicate the same text into both the new top-level `StatusMessage` and the
   existing nested `AppliedChangeSummary.Summary` field for this POC phase, rather than removing
   the nested field - full removal of `data.summary` per the worked example above is left for a
   later pass.
6. `Data` -> `SuccessDetails` (via `RenameSymbol`, ~59 files). This rename is not part of the
   "Decided changes" list above as originally drafted, but was carried out in this pass alongside
   it.
7. `Warning` -> `WarningDetails` (via `RenameSymbol`, ~18 files). `WarningDetails` itself is not
   populated at any new call sites in this pass, per explicit user decision - existing `Warning =`
   sites just carry over renamed with no new producers added.

Two production wire-format consumer sites that parse raw JSON text (not C# symbols, so
`RenameSymbol` does not reach them) were fixed by hand in lockstep with the renames above:
`RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`'s `AddCallToolFilter`
domain-failure -> protocol-`IsError` sync filter now keys on `"isSuccess"` instead of `"success"`;
`RoslynSentinel.Tools.PlanStepRunner/Program.cs`'s `ContainsFailureMarker` now checks
`"isSuccess":false`. Also updated for consistency (test-only, non-production-breaking):
`RoslynSentinel.Tests.ModelEval/AgentLoop/ModelAgentRunner.cs` and
`RoslynSentinel.Tests.ModelEval/TranscriptReplayTests.cs`, both `BodyReportsFailure` methods.

**A significant defect was found and fixed during verification of this phase.** The `RenameSymbol`
calls for `Error`->`ErrorDetails`, `Warning`->`WarningDetails`, and `Data`->`SuccessDetails` (NOT
`Success`->`IsSuccess`, which was clean) had an unexpected side effect: they also silently rewrote
unrelated string literals, comments, and one dictionary key that textually matched the old
identifier name, even though those were not genuine C# symbol references. This broke production
runtime logic (diagnostic-severity string comparisons in several `RoslynSentinel.Basic` engines
that compared against literal `"Error"`/`"Warning"` got corrupted to compare against
`"ErrorDetails"`/`"WarningDetails"`, which can never match a real `DiagnosticSeverity` value) and
one architecture-layer-name dictionary key in `ArchitecturalEngine.cs`, plus test comments/
assertions in `FiveStarToolTests.cs`. This was caught via `RunTest` (33 of 2576 tests failed)
rather than via `RenameSymbol`'s own reported output, whose `residualMentions` list never flagged
any of it. Full mechanism, root cause, and resolution are recorded at
`docs/current/blockers/resolved/blocking_error_renamesymbol_corrupts_unrelated_string_literals.md`
- see that doc rather than this one for the details; the corrupted literals/comments/dictionary key
were reverted to their original text while preserving the legitimate symbol renames, and this was
independently re-verified this session via a fresh server restart plus re-running
`SearchSolutionText` checks (all clean) and `RunTest` (failure count dropped 33 -> 22).

One additional straggler, unrelated to the string-literal corruption above, was found and fixed
directly in this session: this was a case of `RenameSymbol` correctly *not* touching something,
because it genuinely was not a reference to the renamed symbol.
`RoslynSentinel.Server.Basic/SentinelGitTools.cs`'s `Git` method had 4 anonymous-object return
sites (repoPath-guard rejection, gitRoot-null guard, files/paths-conflict guard, unknown-operation
fallback) that still manually constructed `new { Success = ..., Error = ... }` shapes using the old
field names - freestanding anonymous types, not instances of `SentinelCallToolResult<T>`, so
`RenameSymbol` had no reason to touch them (correct behavior), but they were never manually updated
to match the new `IsSuccess`/`ErrorDetails` naming convention used everywhere else. Fixed via
`ReplaceSnippet` to use `IsSuccess`/`ErrorDetails`, consistent with the rest of the renamed
envelope. This directly fixed test failure `Git_Commit_RepoPath_IsRejectedAsync` (failures dropped
22 -> 21, confirmed via `RunTest`). Build is clean (0 errors, 0 new warnings) after this fix.

**Final verification state:** `RunTest` (full solution) shows 2576 total, 2461 passed, 21 failed,
94 skipped. All 21 remaining failures are confirmed pre-existing/environmental and unrelated to
this rename: 15x LM Studio connectivity failures (BadRequest from a local LM Studio endpoint used
by model-eval fixtures), 2x an enum-container `AddMemberAsync` rejection-message assertion, 2x
`KeyNotFoundException` in `LocateSymbol_Call_PopulatesStructuredContent_MatchingActualData`, 2x
task-vs-sync content-equivalence assertions in `TaskCapableClient` tests. None of these touch
`SentinelCallToolResult`, the renamed properties, or any of the previously-corrupted files.

**Explicitly NOT done / deferred, not part of this pass:**

- The de-genericization follow-up (migrating individual tool method signatures from
  `SentinelCallToolResult<object>` to `SentinelCallToolResult<TheRealDto>`).
- The shared `Ok()`/`Fail()` factory idea - not sized or scoped.
- Decided change #5 above (offload/`LargeResult` restructuring) - untouched by this pass.
- Both open questions above (`Findings` scalar-vs-list, offload trigger threshold-gated vs. eager)
  remain open and unresolved.

**Next planned step: Phase 2** - the `TSuccess`/`TError` two-generic-parameter pilot on
`GetMethodSource` and `MoveMember`, per the plan file
`C:\Users\Administrator\.claude\plans\inherited-orbiting-toucan.md`'s "Phase 2" section. Not yet
started; design detail for Phase 2 lives in that plan file, not here.
