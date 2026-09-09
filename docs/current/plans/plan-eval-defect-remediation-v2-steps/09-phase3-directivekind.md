# Step 3.1 — Additive `DirectiveKind` enum

## Prior state

Phases 1 and 2 are complete and their gates passed. `Build` no longer fabricates verdicts;
`ChangeAccessibility`/`ModifyModifier` no longer lose trivia or inject foreign EOLs. No Phase 3
code has landed yet.

## Context

`Directive` is a bare `string` in three places, but they aren't equivalent:
- `OperationSummary.Directive` — a short single sentence.
- `BatchResultSummary.Directive` (`BatchTypes.cs`) — long free-form prose authored at ~15 call
  sites in `SentinelAsyncifyTools.cs`.
- `BreakerStatusReport.Directive` (`BreakerStatusReport.cs`) — same, free-form prose.

**Decision already made (do not re-litigate):** add a **new** `DirectiveKind` enum property
alongside the existing string on all three types. Do **not** replace the string, do **not** drop
the prose — two of these three call sites carry real free-form guidance text that a typed enum
alone can't represent. This mirrors the existing pattern already used for
`BatchResultSummary.Severity` (a keyed field living next to human-readable prose, per its own doc
comment) — not a new convention being introduced.

**Files this step touches:**
- `RoslynSentinel.Common/OperationSummary.cs`
- `RoslynSentinel.Common/BatchTypes.cs` (`BatchResultSummary`)
- `RoslynSentinel.Common/BreakerStatusReport.cs`
- `RoslynSentinel.Server.Advanced/SentinelAsyncifyTools.cs` (the ~15 `BatchResultSummary`
  authoring sites)

## Task

Add this enum (in a location consistent with the codebase's existing small shared-enum
conventions, e.g. alongside `OperationSummary.cs` or in its own file):

```csharp
public enum DirectiveKind { Proceed, ReviewRequired }
```

### A. `OperationSummary`

Add a `DirectiveKind` property. Derive it in `FromCounts` from `Outcome`:
- `CompletedFully` / `CompletedWithNoOps` → `Proceed`
- `PartialProgress` / `NoProgress` / `NothingToDo` → `ReviewRequired`

Do not take it as a new constructor parameter — derive it, so existing callers of `FromCounts`
don't need to change.

### B. `BatchResultSummary` (`BatchTypes.cs`)

Add `DirectiveKind` (default `Proceed`). Then go through each of the ~15 authoring sites in
`SentinelAsyncifyTools.cs` (search for `BatchResultSummary` construction) and classify each one
individually by reading its message text: precondition-failure/empty-target messages →
`ReviewRequired`; genuine-success messages → `Proceed`. Do not bulk-default all 15 to `Proceed`
and call it done — read each one.

### C. `BreakerStatusReport.cs`

Add `DirectiveKind` as a new record parameter (not optional/derived — this type's construction
sites are expected to be few). Update its construction sites positionally.

This is a positional (non-optional) parameter, so every existing construction site will fail to
compile until updated — that's expected, not a sign something went wrong. Run a build
(quickBuild scope) right after making this change, before moving on to the Gate below. Confirm
every reported error is a `BreakerStatusReport` construction site (missing/misordered argument),
find and fix each one, then rebuild to confirm those errors are cleared before proceeding — don't
rely on a single search to have found every site; let the build be the source of truth.

## Gate

Build the solution clean (`Build` MCP tool). No test changes are expected to be needed yet for
this step alone, but if any existing test constructs one of these three types positionally and
now fails to compile due to the added parameter, fix that construction site — don't skip it.
Proceed to [10-phase3-finding-type.md](10-phase3-finding-type.md).
