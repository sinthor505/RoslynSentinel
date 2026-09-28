# Finding: an internal (non-MCP-exposed) MatchStrictness parameter has a real second caller, not just one

**Status (2026-09-28):** Not implemented. This documents an evaluation done alongside the
`FindCallersAsync`/`FindImplementationsForMemberAsync` ambiguous-fallback fix (see "Related" below),
which landed as a hard throw (no configurability). This doc captures the case for *also* adding an
internal strictness parameter later, and why it's justified here in a way it might not be elsewhere.

## What was asked

While comparing fix options for the ambiguous-fallback defect, a third option was raised: a
caller-selectable strictness parameter/enum ("any close enough match" vs "specific match", defaulted
to specific). The idea was initially discussed as if MCP-exposed (a parameter on the public
`FindReferences` tool), then clarified: the intent was an **internal-only** C# parameter on the
engine/helper methods, not part of the public MCP tool schema, and the question was whether it would
be useful for internal (non-tool-layer) callers.

## Method: check internal caller count before judging the abstraction

Per this repo's premature-abstraction guidance ("three similar lines is better than a premature
abstraction"), the deciding fact is how many distinct internal callers exist and whether they have
different correctness requirements - not whether the idea sounds reasonable in the abstract. Ran
`FindReferences(symbolName: "FindCallersAsync"/"FindImplementationsForMemberAsync", kind: callers)`
against `SymbolNavigationEngine.cs`.

## What was found

Both methods have a **second production caller** beyond the single previously-assumed wiring point:

- `SymbolRelationshipImpl.FindReferences` (`RoslynSentinel.Basic/SymbolRelationshipImpl.cs:235,
  247, 259, 260`) - the public `FindReferences` MCP tool's implementation. Read-only/advisory: a
  caller reading a references/implementations report.
- `RefactoringStructuralImpl.Member` (`RoslynSentinel.Basic/RefactoringStructuralImpl.cs:453-454`)
  - calls both methods as a pre-mutation safety check ("does this member have callers/implementations
  I'd break") before `Member(remove)`/`Member(replace)` proceeds.

(Remaining callers are test fixtures - `RegressionTests.cs`, `BugFixTests.cs`, `NewToolTests.cs` -
not production call sites.)

This is not "one caller, premature abstraction." It's two callers with **opposite correctness
requirements**:

- `SymbolRelationshipImpl.FindReferences` is informational. A caller reading a report can tolerate a
  "best-effort, here's my closest guess" answer more easily than a hard stop.
- `RefactoringStructuralImpl.Member` uses the result to decide what's safe to *mutate*. Silently
  resolving to the wrong candidate here doesn't just mislead a report - it can green-light an edit
  while missing the real caller/implementation it should have caught. This is the higher-stakes
  caller, and arguably should never be loose regardless of what `FindReferences` does.

## Conclusion

An internal `MatchStrictness` enum (`Strict` default, `BestEffort` opt-in) on the shared resolution
path has a real justification: not to give `RefactoringStructuralImpl.Member` a *choice* (it should
likely always be `Strict` - mutation safety isn't something to negotiate), but to avoid each of the
two call sites independently re-implementing the same disambiguation-safety check. One
enum-typed helper both callers invoke, instead of relying on every future call site remembering to
check `narrowed.Count > 1` itself, is the concrete benefit - the same shape of drift this repo's
CLAUDE.md failure doctrine already flags as a risk pattern ("a rule enforced only by remembering
decays across hundreds of calls," applied here to internal callers of a shared engine method rather
than to the dog-fooding policy it was originally written about).

`SymbolRelationshipImpl.FindReferences` could plausibly want `BestEffort` as a documented,
opt-in-only internal call (no MCP-exposed way to select it, matching the user's original framing),
while `RefactoringStructuralImpl.Member` always passes `Strict`. Whether `FindReferences` should
actually default away from `Strict` is a separate design decision not resolved by this finding - the
finding only establishes that the internal parameter has more than one real customer with genuinely
different needs, so it clears the premature-abstraction bar this repo applies.

## Not done here

- No enum was added. The shipped fix (see Related) hardcodes strict behavior (throw on ambiguous) for
  both callers uniformly - which is safe for `RefactoringStructuralImpl.Member` and at worst
  slightly more conservative than necessary for `SymbolRelationshipImpl.FindReferences`.
- Whether `SymbolRelationshipImpl.FindReferences` should get a `BestEffort` internal option, and what
  its precise semantics would be (e.g. does it still throw on the true zero-candidate case, only
  relaxing the >1-candidate throw?), is unresolved and would need its own design pass before
  implementation.

## Related

- The ambiguous-fallback defect this evaluation was raised alongside: `FindCallersAsync` and
  `FindImplementationsForMemberAsync`'s by-name-across-solution path silently picked
  `PreferClassMember(...).FirstOrDefault()` / `PreferImplementableMember(...).FirstOrDefault()`
  whenever a supplied `contextSnippet` matched nothing, or when narrowing still left more than one
  candidate. Fixed 2026-09-28 (changeIds `52895b19`, `2b706992`) by throwing
  `InvalidOperationException` in both cases instead, with a new `DescribeCandidateLocations` helper
  listing up to 3 candidate `file:line (Type.Member)` locations in the error message. Full-suite
  regression run after the fix: 2475 passed / 31 failed (all pre-existing and unrelated - LM Studio
  connectivity, `HealthOrchestrationEngine` null-ref, quality-engine count mismatches) / 100 skipped.
- `docs/current/blockers/resolved/blocking_error_member_replace_containername_regression_setup_overload.md`
  - the investigation that surfaced this defect class in the first place (a stale-server false alarm
  in `Member(replace, containerName)`, whose root-cause trace led to auditing every `FirstOrDefault()`
  disambiguation in `SymbolNavigationEngine.cs`).
- `docs/current/proposal_unify_member_lookup_paths.md` - names the general pattern (one shared
  resolver, multiple callers with divergent needs, patched for one caller without the others being
  reconsidered) that this finding is a specific instance of.
