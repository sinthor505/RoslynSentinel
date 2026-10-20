# Instance-move preview: classify call sites through a position map, not by line number (item D2)

**Status:** PROPOSED 2026-10-15. Direction leans to option c (bind-based classification over a position map); not decided. Facts come from an exploration agent's read of source and tests on 2026-10-15; nothing was run. Re-anchor line numbers with `Search` before acting.

## Motivation

`MemberRefactoringEngine.PreviewInstanceMoveCallSitesAsync` (`RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs:4504`)
returns one row per reference to the member being moved, with a status of `Valid`, `Ambiguous`,
`NoCandidateIntroducible`, `NoCandidateBlocked` or `MoveOrderDependent`. A `Valid` row carries one in-scope receiver
(field, property, parameter or local of the destination type). `MoveInstanceMembersAsync` (`:3996-4124`) then rewrites
only the receiver span.

The preview decides which references fail by matching compile diagnostics to references **by start line**
(`MemberRefactoringEngine.cs:4616`). The reference line comes from the original text; the diagnostic line comes from a
trial text. That mismatch causes two defects:

1. **Wrapped calls are misclassified.** When the error lands on a later line than the reference start (for example a
   named argument on its own line), the call is classified `Valid` with no suggested fix. It is not rewritten and not
   reported as skipped; the apply then passes or fails the compile gate with no explanation. This is a derived
   scenario, not a captured run.
2. **Line shifts.** The trial edits only the source document (member removed) and the existing target document (member
   inserted): `AddInstanceMoveStructuralEditsAsync` (`:3933-3972`), `MaterializeDocumentEditsAsync` (`:3978-3994`), trial
   built at about `:4585-4592`. Diagnostics in those two documents are at shifted lines after the removal or insertion, but
   references are compared using original lines.

Supporting facts:
- `DiagnosticInfo` carries 1-based line and column only, no offsets.
- `MoveMemberTextEdits.MapPositionThroughEdits` (`MoveMemberTextEdits.cs:320-332`) maps one position through one
  document's edits; it cannot map a position inside the removed member into the target document.
- `MoveOrderDependent` appears to be a dead branch (unverified; confirm with `Search(mode: references)`).
- A related edge, unverified: the receiver rewrite for `a\n    .Count(x)` matches the start line of the whole member access,
  so a receiver on a different line from the member name may never be rewritten.
- Tests today: `RoslynSentinel.Tests.Battery.Basic/PreviewInstanceMoveCallSitesTests.cs` and
  `MoveMemberPreservesUntouchedTextTests.cs`.

## Proposal

Classify by binding in the trial compilation, located through an explicit position map built from the engine's own edits.

### 1. Position map (decided in outline)

Built as a by-product of the edit builder, not by diffing text. The engine already knows every `TextChange`.
- Unchanged text maps to itself plus the cumulative edit delta in that document.
- The moved member's text maps **across documents**: source offset `o` inside the member maps to
  `targetInsertionOffset + (o - memberStart)`, adjusted for any re-indentation the engine applies. Line-wise the mapping is
  one to one even when columns change.
- Keep a per-line table (`SourceLineNumber -> DestinationLineNumber`, per document) as well as offsets, so results can be
  reported in user-facing line numbers.

### 2. Validation by line-text comparison (decided in outline)

After the trial text is built, compare each mapped destination line with its source line, ignoring leading whitespace.
Any mismatch means the map is untrusted (line count changed, blank-line trimming, or a builder bug): fall back to
`Ambiguous` with a note instead of guessing. This catches map bugs cheaply. Diffing text to infer the map is rejected (see
Alternatives).

### 3. Bind-based classification (leaning)

For each reference: map its start through the table into the trial text, find the enclosing invocation node there, bind it,
and check it resolves to the moved member on the destination type. Wrapped calls then need no special handling (the
invocation node spans the lines) and no diagnostic-to-line matching remains. Diagnostics become a secondary signal.

### 4. References inside the moved body

Before the move, record each reference to the moved member that sits inside the member's own body (recursion, a bare cref)
as an offset relative to the member start. After the move its position is the insertion offset plus that relative offset,
and it goes through the same map and bind check. No special case.

### 5. Reporting

References that cannot be mapped, fail validation, or bind to something unexpected are reported explicitly (a note listing
file, line, and reason). They are not silently classified `Valid`.

## Alternatives considered, not pursued

- **a. Span overlap against the trial text.** Convert diagnostics to offsets and map reference spans through the edits.
  Fixes both defects but still depends on diagnostics, which only report errors, and needs the same cross-document mapping
  for the moved body.
- **b. Match any diagnostic inside the enclosing invocation or statement span.** Fixes wrapped calls; needs (a)'s mapping for
  the shift.
- **d. Widen the line match to the statement's line range.** Cheap; fixes wrapped calls only, leaves the line-shift skew.
- **Infer the line map by diffing source and trial text.** The engine already knows the mapping exactly; a diff can mis-pair
  repeated lines.
- **Leave as is and rely on the compile gate.** Leaves the unexplained apply failure.

## Open questions

1. **How does the preview get a trial compilation?** It calls `ValidationEngine.ValidateChangesAsync`, which returns
   diagnostics; `ValidationEngine.cs:125` builds the candidate with `WithDocumentText`. Whether a `Compilation` or
   `SemanticModel` is exposed (or can be, without a layering problem) is not established. If not, add an engine-level way to
   build the candidate solution, or fall back to option a/b.
2. Keep diagnostic matching as a secondary signal, or remove it entirely?
3. Does re-indentation ever change line counts (for example wrapped lines re-flowed)? If so, the line-text validation will
   reject those cases; decide whether that is acceptable.
4. Is `MoveOrderDependent` reachable? Remove it or give it a defined meaning.
5. The `a\n    .Count(x)` receiver-rewrite edge: reproduce it first; if real, fix it in the same slice.
6. Whether Q3 of `plans/plan_cref_qualifier_helper_and_tool.md` and the B3 slice of
   `plans/closed/plan_movemember_collateral_change_tripwire.md` can share this map (both need the applied edits).

## Slices (sequential, after B3; senior unless noted)

- **D2.1:** position map builder in `Engines.Basic` (new class), built from the same `TextChange`s the instance route
  applies, with the line-text validation. Unit tests: unchanged region, moved region across documents, re-indented region,
  validation failure.
- **D2.2:** expose the trial solution or compilation to the preview (resolve open question 1), with the bind check helper.
- **D2.3:** switch `PreviewInstanceMoveCallSitesAsync` classification to map plus bind; pre-list self-references; explicit
  reporting for unmappable references. Update `PreviewInstanceMoveCallSitesTests`.
- **D2.4 (test-writer):** wrapped-call test (named argument on its own line), line-shift test (reference below the removed
  member), self-reference test (recursion), receiver-on-previous-line test.

## Cost / risk

- Touches `MemberRefactoringEngine.cs` (the preview and the instance route), as do B3 and Q3; dispatch sequentially.
- Risk: the trial compilation may be costly to expose; the map must stay in sync with the edit builder (the validation step
  guards this).
- Risk: changing classification can change which sites are reported `Valid`; existing preview tests encode current behaviour
  and need review, not blind updates.
- Does not fix the instance move's stale crefs (see the cref plan) or the moved body's bare calls to A's members (TODO.md,
  "qualify receivers").

## Related

- `docs/current/plans/plan_cref_qualifier_helper_and_tool.md` (D1, Q3 shares the instance route)
- `docs/current/plans/closed/plan_movemember_collateral_change_tripwire.md` (B3 touches the same file and edits)
- `docs/current/TODO.md`, "`MoveMember` follow-ups" section (wrapped call sites; qualify receivers)
- `docs/current/blockers/resolved/blocking_error_movemember_reformats_entire_source_and_caller_files.md`
