# A read chokepoint for `CurrentSolution` access

## Motivation

The write side of this problem was already solved once: before commit `3f8f95e`, several call
sites wrote `.cs` files via raw `File.WriteAllTextAsync`, bypassing
`PersistentWorkspaceManager.ApplyProposedChangesAsync` entirely, which caused the in-memory
`CurrentSolution` and on-disk files to silently diverge. The fix was to make
`ApplyProposedChangesAsync` the single chokepoint every write path routes through — see
[[project_write_path_chokepoint_unified]] / `docs/current/reference-code-file-write-paths-v1.md`.

The read side never got the equivalent treatment. `ISolutionProvider.CurrentSolution` and
`GetCurrentSolutionAsync` hand out the raw Roslyn `Solution` object, and every engine that needs to
read source text, member lists, or symbols independently decides how to navigate it.
`FindReferences(symbolName: "GetCurrentSolutionAsync", kind: callers)` (re-checked 2026-09-24, using
the tool's new grouped `statusMessage` summary) finds **389 caller sites across 78 files** -
symbol-precise, not a text grep, so it excludes comments and unrelated `Solution`-typed locals (e.g.
`AdhocWorkspace().CurrentSolution` in test fixtures). A parallel `SearchSolutionText` sweep for the
literal `CurrentSolution` text turns up 501 matches across 101 files, a superset that includes those
non-call-site occurrences. The 78-file figure is the one this document tracks, since it counts actual
readers of the chokepoint's target, not incidental text matches. (Earlier revisions of this document
cited "84 files" and, before that, "100+"; both were grep-based approximations superseded by the
tool-verified 78/101 split above.) This includes nearly
every `*Engine.cs` in both `RoslynSentinel.Basic` and `RoslynSentinel.Advanced`. This is the same
shape the write side had before unification: a shared resource with no shared gate governing how
it's consumed. `ISolutionProvider` is a *provider* — it hands out access — not a *chokepoint* that
answers the actual read question on the caller's behalf.

Today this is latent, not actively causing bugs, because there is exactly one source of truth:
`ApplyProposedChangesAsync` always writes both `CurrentSolution` (in-memory) and disk together in
the same call (see `PersistentWorkspaceManager.cs:1116-1400`, disk write at line 1363) — there is
currently no code path that updates one without the other. The scattered read access is safe today
only because there is nothing for it to disagree about.

That safety is conditional on staying single-source, and it is the precondition a related proposal
would remove: allowing a tool call to update `CurrentSolution` in memory (staged, speculative edits
a model can build up and either commit or discard) without an immediate disk write — restoring the
staged-changes workflow the `ApplyProposedChanges` name originally implied, before wiring
inconsistencies forced the always-write-through-with-validation model that exists now (see the
PlanStepRunner run `20260911-205633-213` review conversation that motivated this design doc). The
moment `CurrentSolution` can hold uncommitted content that differs from disk, every one of those
100+ scattered read call sites has an ambiguous question it doesn't currently have to ask: *is the
solution I'm holding the last-committed state, or does it include an uncommitted stage?* Without a
single place enforcing a deliberate answer, staging reopens the exact divergence bug the write-side
fix closed — just relocated from write call sites to read call sites.

**This document proposes the read chokepoint as a prerequisite for staged writes, not a peer
feature.** It should land and be substantially swept through before any staging work begins.

## Non-goals

This does not itself propose staged/uncommitted writes, a bypass-validation budget, or any change
to `ApplyProposedChangesAsync`'s current always-write-through behavior. Those are follow-on
proposals this chokepoint would unblock. This document is scoped to: give every read call site an
explicit, auditable way to say which state it wants, while today's actual behavior (one source of
truth) stays unchanged.

## Current shape

- `ISolutionProvider` (`RoslynSentinel.Common/ISolutionProvider.cs`) exposes `CurrentSolution`
  (sync property) and `GetCurrentSolutionAsync` (async, currently just returns `CurrentSolution` or
  throws `SolutionNotLoadedException` if unset — see `PersistentWorkspaceManager.cs:1016-1028`).
  Both return the raw `Solution`.
- Every engine constructor takes `PersistentWorkspaceManager` (concrete class) or, per
  [[project_iworkspacemanager_segregation_audit]], a narrower interface — but in either case, once
  an engine has a `Solution` handle it navigates it directly: `CurrentSolution.GetDocument(...)`,
  `.Projects.SelectMany(...)`, `.GetDocumentIdsWithFilePath(...)`, etc. There is no intermediate
  layer between "have a `Solution`" and "read specific content from it."
  `PersistentWorkspaceManager.cs` itself does this internally at 16 clusters as of 2026-09-24 (line
  references from the current file: 287-300, 328, 403-447, 496-503, 569, 730-841, 878-919, 992-1007,
  1016-1035, 1065-1091, 1178-1224, 1509-1577, 1605-1662, 1831-1836) — the doc's previous line list
  (300, 403-448, 503, 730-841, 1007, 1016-1035, 1067, 1224, 1509-1577, 1662, "and more") missed
  several clusters (878-919, 992-1007, 1065-1091, 1605-1662, 1831-1836) that a fresh sweep turned up;
  still the same order of magnitude and conclusion, just re-verified against the live file rather than
  carried forward.
- `Solution` is immutable (Roslyn's own design — confirmed in `ISolutionProvider`'s XML doc:
  "callers can apply speculative edits ... without affecting this instance or other callers"), so
  the *object* handed out can't be corrupted by a caller holding it. The ambiguity this document
  addresses is about *which* immutable `Solution` snapshot a caller should be looking at, once more
  than one meaningfully exists at once (committed vs. staged).

## Proposed contract

Introduce an `IWorkspaceReader` (name open to bikeshedding) that answers content questions
directly, rather than handing out a `Solution` to navigate:

```csharp
public enum ReadSource
{
    /// Last state written to disk via ApplyProposedChangesAsync (or loaded at solution-open time).
    /// The only value that exists/matters until staged writes are implemented.
    Committed,

    /// Committed state, plus any currently-staged uncommitted edits from this session, if any
    /// exist. Identical to Committed until staging exists. Once staging exists, this is what most
    /// mutating-tool call sites should use — a tool building on a prior uncommitted edit needs to
    /// see it.
    IncludeStaged,
}

public interface IWorkspaceReader
{
    Task<string?> GetDocumentTextAsync(FilePathWrapper path, ReadSource source, CancellationToken ct);
    Task<Solution> GetSolutionAsync(ReadSource source, CancellationToken ct);
    // Additional narrow accessors added as call sites migrate — see "Migration" below for how
    // the method set should grow from actual usage rather than being fully designed up front.
}
```

Design points:

- **`ReadSource` is mandatory, not defaulted.** A caller must say which state it wants. An implicit
  default (e.g. defaulting to `IncludeStaged`) would silently recreate the exact "which source am I
  reading" ambiguity this exists to remove — the whole point is that the choice is visible at every
  call site, the same way `ApplyProposedChangesAsync`'s `validateChanges`/`rollbackOnPartialFailure`
  parameters make chokepoint behavior explicit rather than implicit.
- **Two-valued, not boolean.** `ReadSource` is an enum rather than `bool includeStaged` for the same
  reason `BuildOutcome` replaced a `bool BuildSucceeded` in the motivating PlanStepRunner run: a
  named enum reads correctly at every call site (`ReadSource.Committed` vs. `staged: false`) and
  leaves room to grow a third state later without a breaking signature change.
- **`GetSolutionAsync` stays as an escape hatch**, not a rename of the status quo. Some engines
  (deep Roslyn analysis passes, e.g. `SymbolFinder`-based searches across the whole solution) need
  the actual `Solution` object, not a per-document text answer. The chokepoint's job is to make
  *which* `Solution` snapshot explicit, not to force every engine onto document-text-only access.
- **This interface has nothing to do until staging exists.** With only `Committed` ever being
  materially different from `IncludeStaged` — i.e., today, never — every call converts trivially and
  behavior is unchanged. This is deliberate: the migration (below) should be safe to do and land
  entirely before staging is designed, let alone built, so it can be verified as a pure refactor
  (identical behavior, different call shape) rather than bundled with a behavior change.

## Migration path

Given the scope (78 call-site files per the `FindReferences` count above), a big-bang rewrite is not
proposed. Sequence, mirroring how the write-side unification was executed as its own dedicated pass
rather than folded into feature work:

1. **Add `IWorkspaceReader` alongside `ISolutionProvider`**, implemented by
   `PersistentWorkspaceManager` (which already implements `ISolutionProvider` and half a dozen other
   role interfaces — see the interface list on `PersistentWorkspaceManager`'s class declaration).
   `ISolutionProvider` is not removed at this step, but `CurrentSolution` and `GetCurrentSolutionAsync`
   are marked `[Obsolete("...", error: false)]` (a warning, not an error) pointing callers at the
   `IWorkspaceReader` equivalent. This is a lighter-weight alternative to adding a `ReadSource` toggle
   directly onto `ISolutionProvider`: a toggle would let two ways of asking the same read question
   coexist indefinitely and would force a breaking signature change on `CurrentSolution` (a sync
   property can't take a parameter without becoming a method) across all 78 files in one motion. The
   obsolete warning instead gives every remaining direct-access call site a compiler- and IDE-visible
   nudge with zero behavior change, and turns "how much of the sweep is left" into a build-warning
   count (`CS0618`) rather than a hand-maintained doc line list.
2. **New call sites and any code touched for unrelated reasons adopt `IWorkspaceReader`
   opportunistically** — no dedicated sweep yet, just "don't add new direct `CurrentSolution` reads
   once this exists."
3. **A dedicated sweep pass**, scoped the same way `docs/current/reference-code-file-write-paths-v1.md`
   scoped the write-side inventory: enumerate every direct `CurrentSolution`/`GetCurrentSolutionAsync`
   call site, convert each to the narrowest `IWorkspaceReader` method that fits, and note any that
   generalize the interface (a new accessor method is worth adding once 2-3 call sites want the same
   shape of read). This is expected to be a multi-session effort given the file count — do not
   attempt in one pass.
4. **Only after the sweep is substantially complete** does staged writes become safe to design in
   detail. At that point `ReadSource.IncludeStaged` starts actually diverging from `Committed`, and
   every remaining direct `CurrentSolution` access (anything the sweep didn't reach) becomes a
   concrete, greppable list of known gaps — exactly the "structural gap flagged, not fixed" caveat
   [[project_write_path_chokepoint_unified]] already carries for the write side (no
   compiler-enforced routing, convention only). Closing that gap for reads before staging exists is
   the value this ordering buys: a known, bounded list of pre-existing exceptions instead of an
   unbounded one discovered piecemeal after staging ships.
5. **Document-lookup accessors and a `GetSolutionAsync` reduction sweep.** Next step, added
   2026-10-01. Step 3 turned every direct `CurrentSolution` read into
   `GetSolutionAsync(ReadSource.X)`. That made the snapshot choice explicit, but it left the most
   common read shape un-narrowed: fetch the whole solution, then hand-roll a path-to-Document
   lookup on it. This is the first accessor shape "grown from real call sites", as the Open items
   below asked for. It is also the root cause of
   `blockers/blocking_error_path_lookup_case_sensitive_drive_letter_replacesnippet_file_not_found.md`.
   That blocker counted 117 hand-written LINQ lookups; 7 of them compare plain strings, so they fail
   when the caller's path casing differs from the loaded solution's.

   New members:

   ```csharp
   Task<DocumentLookupResult> GetDocumentAsync(FilePathWrapper path, ReadSource source, CancellationToken cancellationToken);
   Task<IReadOnlyList<Document>> GetDocumentsAsync(DocumentScope scope, ReadSource source, CancellationToken cancellationToken);
   ```

   - `DocumentScope` is one file, one project or the whole solution. That is the selection analysis
     engines hand-roll today (e.g. `ThreadSafetyEngine.FindDoubleCheckedLockingAsync`).
   - **One implementation.** A static `TryGetDocument(Solution, FilePathWrapper)` in Common, built on
     `GetDocumentIdsWithFilePath`, so it inherits Roslyn's OrdinalIgnoreCase comparer.
     - Both reader members and `GetDocumentTextAsync` route through it.
     - It stays public for code that holds a forked or speculative `Solution`, which the reader
       cannot hand out.
     - `FakeWorkspaceManager` delegates to it too.
   - **Snapshot rule.** A caller that needs the `Solution` after the lookup uses
     `document.Project.Solution`, not a second `GetSolutionAsync` call. Same snapshot by
     construction. Once staging exists, two separate reads could straddle a stage; one read cannot.
   - **Ambiguity rule.** Match the exact path first, ignoring case. Fall back to a bare file name
     only if exactly one document has it. Otherwise return an ambiguous result that lists the
     candidates. This deliberately replaces today's first-wins `d.Name == x` matching.

   Call-site shapes among the 392 production `GetSolutionAsync` calls:

   | Use of the `Solution` after the call | Methods | Call sites | After step 5 |
   | --- | --- | --- | --- |
   | Path lookup or scope selection only | 263 | ~270 | `GetSolutionAsync` removed; `GetDocumentAsync`/`GetDocumentsAsync` |
   | Path lookup, then other `Solution` use | 38 | ~40 | `GetSolutionAsync` removed; `document.Project.Solution` |
   | No path lookup (solution-wide analysis) | 79 | ~82 | Unchanged; the escape hatch is the right tool |

   The counts are a heuristic. They come from method-name and variable-name matching over production
   code, not a symbol-precise trace, so re-count each batch before trusting a figure.

   Phasing:
   - **5a, the blocker fix.** The static core, the two reader members, `FakeWorkspaceManager`, and
     the blocker's 7 High sites. It lands with the rest of that blocker's fix plan.
   - **5b, the sweep.** The remaining ~310 sites, in sequential "Lookup batch N" commits that mirror
     step 3. Never dispatch parallel subagents against the shared server for this.
     - Each batch searches three forms together: the `Projects.SelectMany(p => p.Documents)` LINQ
       scan, `GetDocumentIdsWithFilePath(`, and bare `d.Name ==` matches. Step 3's
       sweep-tracking correction (Status below) shows why one form at a time misses sites.
     - Step 3's `[Obsolete]` trick does not carry over. `GetSolutionAsync` stays legitimate for the
       ~82 solution-wide sites, so there is no warning count to track progress. Track the remaining
       work by the hit counts of those three search forms instead.
     - A batch that changes a first-wins bare-name match to "ambiguous" is a behaviour change; note
       it in that batch's commit message.

   Step 5 does not depend on staged writes (step 4). It should land before staging is implemented,
   because the snapshot rule is cheapest to adopt while `Committed` and `IncludeStaged` still return
   the same thing.

## Relationship to other in-flight proposals

- **Staged/uncommitted writes**: this chokepoint is a prerequisite, per Motivation above. Now its
  own document, `docs/current/proposal_staged_writes.md` — written once this chokepoint's Step 3
  sweep completed (see Status below). That document also found the sweep's migrated call sites all
  landed on `ReadSource.Committed` (398 of 398 production-code matches), with `IncludeStaged` used
  nowhere except this file's own doc comment, and proposes a mechanical fix (rename-and-reintroduce)
  to correct the values now that staging gives the two an actual reason to diverge.
- **`PreviewSymbolTypeChangeImpact`/`ChangeSymbolType`** (`docs/current/proposal_changesymboltype_tool.md`):
  independent of this doc. That proposal's reference-finding half already goes through
  `SymbolFinder`/`FindReferences`-style APIs against whatever `Solution` it's handed; once this
  chokepoint exists, it would naturally request `ReadSource.Committed` (a preview should reflect
  committed state, not a speculative stage) — a small, mechanical adjustment once both exist, not a
  redesign of either.
- **Bypass-budget / validation-relock idea** (discussed in the same conversation, not yet its own
  doc): if staged writes ship, this idea may become unnecessary — a model finishing a coordinated
  multi-file change by staging edits and committing once atomically doesn't need a budget of
  unvalidated direct-to-disk calls. Worth revisiting only after staging is designed; do not pursue
  both in parallel.

## Open items

- Exact method set on `IWorkspaceReader` beyond `GetDocumentTextAsync`/`GetSolutionAsync` should
  grow from real call-site shapes found during the sweep (step 3), not be fully speculated here —
  risk of over-designing accessors nothing ends up needing. **First shape found 2026-10-01:**
  path-to-Document lookup (step 5). Further accessors still wait for a real call-site shape.
- ~~Step 5 details still open~~ - **Resolved 2026-10-02 (phase 5a implementation):**
  - **`DocumentLookupResult` is a result type** (a sealed record in `Common/DocumentLookup.cs`), not
    a nullable `Document` plus a side channel. It carries `Status` (`Found`/`NotFound`/`Ambiguous`),
    `RequestedPath`, `Document`, `CandidatePaths` (the closest real paths for not-found, the
    colliding paths for ambiguous) and `SearchedProjectCount`. Tool code calls `ToResultError()`
    (a `ResultError` coded `NotFound`/`Ambiguous`, naming the candidates); engine code calls
    `GetDocumentOrThrow()` (a `ToolNotFoundException`/`ToolAmbiguousMatchException`, which
    `ToolErrorMapper` maps to the same codes, so a missing file no longer surfaces as
    `errorCode: Exception`).
  - **The project variant of `DocumentScope` takes both** a `ProjectId` and a project name
    (`DocumentScope.ForProject(ProjectId)` / `ForProject(string)`, the name matched ignoring case).
    Engines that already hold a `ProjectId` should not have to round-trip through a name; tools only
    ever have the name. Phase 5b may drop one if no call site needs it.
  - **The closest-match search moved to `DocumentLookup.FindClosestPaths` in `Common`.**
    `BuildFileNotFoundError` (`Tools.Basic`) now calls it, and so do the engines through the
    result type. `BuildFileNotFoundError` keeps its own `FileNotFound` code and message so the read
    tools' behavior is unchanged.
  - **Known caveat:** once `FromWire` has run, a nonexistent file directly under the solution root
    cannot be told apart from a bare file name, so it takes the bare-name rule (unique match found,
    several ambiguous). Rooted paths with a directory part never degrade to a file-name guess.
  - **Item 4 (ambient solution root).** The implicit `string -> FilePathWrapper` conversion and
    `FilePathJsonConverter` now resolve a RELATIVE path against an `AsyncLocal` solution root
    (`FilePathWrapper.UseSolutionRoot`), set around each tool call by a request filter
    (`AddSolutionRootScopeFilter`, registered right after the echo filter). It is an AsyncLocal, not
    a process static, so concurrent calls and parallel tests never share a root; rooted paths and
    code outside a scope behave as before. `operator string(FilePathWrapper)` is untouched.
  - **Follow-up:** `FilePathLock` still uses a platform-conditional comparer (see TODO.md).
- Whether `IWorkspaceReader` should be a genuinely separate interface or additional members on
  `ISolutionProvider` itself. Separate is proposed here to keep `ISolutionProvider`'s existing
  narrow "give me solution metadata" contract intact and let call sites adopt the new read-question
  shape independently, but this is a naming/organization choice, not a load-bearing one.
- ~~Whether `PersistentWorkspaceManager`'s own internal direct `CurrentSolution` accesses should be
  swept too~~ — **Resolved 2026-09-20: exempt.** Re-checked as part of this review; nothing since
  the doc's original writing (no staging design, no internal Committed/IncludeStaged need) has
  changed the calculus, so the doc's own lean is adopted as the decision rather than left open.
  `PersistentWorkspaceManager` is `IWorkspaceReader`'s own implementation and has direct access by
  construction; its ~26 internal sites (see "Current shape" above) are not sweep targets. Revisit
  only if staging logic is designed and turns out to need the Committed/IncludeStaged distinction
  internally too.
- No compiler-enforced guarantee is proposed here either (same caveat the write-side chokepoint
  carries) — this is a convention change backed by a sweep and a reference doc, not a type-system
  guarantee that a future engine can't reintroduce direct access. Worth reconsidering only if that
  becomes a repeated problem in practice, the same bar applied to the write side.

## Status

Steps 1-2 implemented 2026-09-24/25: `IWorkspaceReader`/`ReadSource` exist
(`RoslynSentinel.Common/IWorkspaceManager.cs`), `PersistentWorkspaceManager` implements it, and
`ISolutionProvider.CurrentSolution`/`GetCurrentSolutionAsync` are marked `[Obsolete(error: false)]`.
**Step 3 (the dedicated sweep) is complete as of commit `44b1841` (2026-09-25, "Sweep batch 48"),**
which migrated the last 33 sites across 11 test files plus `FakeWorkspaceManager`. Production code
was already fully migrated in batches 1-47. A solution-wide `SearchSolutionText` re-check at that
commit confirmed zero remaining `_workspaceManager.(GetCurrentSolutionAsync|CurrentSolution)`
references. The sweep was batch-committed file by file (see git log "Sweep batch N" commits,
2026-09-24 through 2026-09-25) using two patterns: full-widen the field+constructor to
`IWorkspaceManager` for files with few/no production callers, or a cast at each call site
(`((IWorkspaceReader)_workspaceManager).GetSolutionAsync(...)`, tagged `READCHOKEPOINT-CAST`) for
files with wide production caller graphs where widening the constructor would cascade.

Several defects were found and fixed in the days immediately following the sweep's close, all
adjacent to migrated code rather than in the migration itself: `cf7dfe4`/`30861b8` (dead
null-checks and an uncaught `OperationCanceledException` left over from the old
nullable-return contract in `AsyncifyTools.cs`), `bf22560` (`ToolErrorMapper` still classified
"no solution loaded" via the now-obsolete `CurrentSolution == null` check instead of catching
`SolutionNotLoadedException`), and `ca5738e` (`IWorkspaceReader` was never forwarded in
`ServiceRegistrationExtensionsBasic`'s DI setup, breaking server startup). **Step 4 (staged writes)
can now be designed in detail** per the ordering this document specifies — the sweep is no longer a
blocking precondition. That design is now written up at `docs/current/proposal_staged_writes.md`.

**Step 5 (document-lookup accessors) proposed 2026-10-01; phase 5a implemented 2026-10-02
(uncommitted at the time of writing).** Phase 5a is the fix plan
for `blockers/blocking_error_path_lookup_case_sensitive_drive_letter_replacesnippet_file_not_found.md`:
the static core (`DocumentLookup`), `IWorkspaceReader.GetDocumentAsync`/`GetDocumentsAsync` with
`PersistentWorkspaceManager` and `FakeWorkspaceManager` implementations, the 7 High sites, the
case-insensitive pending/internal/external change sets, the ambient solution root, and regression
tests (`DocumentLookupTests`, `FilePathAmbientRootTests`, `PathCaseLookupRegressionTests`). Phase 5b
is the follow-on sweep (not started).

**Sweep-tracking correction (2026-09-25):** early batches searched only for the
`GetCurrentSolutionAsync(...)` method-call text pattern and missed the sync `CurrentSolution`
property-getter form (e.g. `_workspaceManager.CurrentSolution`), which is an equally-obsolete,
equally-in-scope call shape per "Current shape" above. A file already marked complete
(`AntiPatternEngine.cs`, batch 12) turned out to have one missed `CurrentSolution` site
(`GetAsyncMigrationProgressAsync`), found only via a live `Build`'s warning list, not the original
text sweep — fixed same-day. A follow-up solution-wide search for the property-getter pattern
specifically (`SearchSolutionText` regex `_workspaceManager\.CurrentSolution`, 2026-09-25) found 41
further matches across 12 files not yet in the batch catalogue (`AsyncifyTools.cs` 12,
`BugFixTests.cs` 9 [test-only], `GenerationTools.cs` 4, `MsToolAugmentEngine.cs` 3,
`SentinelAsyncifyToolsTests.cs` 3 [test-only], `AsyncBatchEngine.cs` 2,
`DeepFunctionalVerificationTests.cs` 2 [test-only], `LoadSolutionPathSanitizationTests.cs` 2
[test-only], `TestRunEngine.cs` 1, `WorkspaceHealthMiscImpl.cs` 1, `AdvancedRefactoringTools.cs` 1,
`CommentingTools.cs` 1). These are added to the sweep's remaining-work list; going forward every
batch's `SearchSolutionText` pass covers both the method-call and property-getter forms together
rather than as two separate sweeps.

Line citations and the file-count figure in "Motivation" above were last refreshed 2026-09-24, using
`FindReferences`'s grouped `statusMessage` summary for a symbol-precise (not grep-approximated)
count of the `GetCurrentSolutionAsync` method-call form specifically; that count does not include
the `CurrentSolution` property-getter form's sites, which the correction above tracks separately
pending a combined re-count. Motivated by the same PlanStepRunner run review
(`20260911-205633-213`) that produced `docs/current/proposal_changesymboltype_tool.md`, via a
follow-on discussion about reintroducing staged in-memory writes.
