# Extract the syntax-side target resolver out of SymbolNavigationEngine, then unify hint formatting

**Status:** APPROVED 2026-10-03, in progress. Follow-on to `proposal_universal_symbol_resolver.md`, which finished the resolver migration but left the resolution layer inside the navigation engine and never did its hint-formatter unification.

**Decisions taken at approval (2026-10-03):** class name `SyntaxTargetResolver`; static class if every moved member passes the no-instance-state check; move outright with no forwarding methods (fall back to `[Obsolete]` forwarders only if the `NormalizeTypeName` trial shows `MoveMember` cannot rewrite call sites); step 1 and step 2 ship as separate commits.

## Motivation

`docs/current/proposal_universal_symbol_resolver.md` replaced five drifted lookup helpers with
`ResolveCandidates` (syntax, one file), `ResolveCandidatesWithSemanticAsync` (semantic, solution-wide,
opt-in) and kept `LocateSymbolAsync` as a permanent third method for multi-match reports. Its Status
section reads "Complete", but that covers only its steps 1-4 (resolver migration and deletion). Two
things it proposed or implied were not finished:

1. **Its section 5** -- "one formatting path over `List<SyntaxNodeCandidate>`" for the hint builders --
   was never done (details below).
2. It never asked *where the resolver lives*. Everything it built went into
   `RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs` (3,124 lines), a class whose other job is
   solution-wide navigation: `FindCallersAsync`, `FindAllImplementationsAsync`,
   `FindImplementationsForMemberAsync`, `GetCallGraphAsync`/`GetReverseCallGraphAsync`,
   `GetTypeHierarchyAsync`, `TraceVariableLifetimeAsync`, `FindExtensionMethodsAsync`.

The predecessor also still cites pre-reorg paths (`RoslynSentinel.Basic/...`, `RefactoringEngine.cs:NNNN`);
see Follow-ups.

### Resolving versus navigating

These are two different jobs that currently share one class:

- **Resolving:** caller-supplied text (a name plus `contextSnippet` / `lineBefore` / `lineAfter`) ->
  exactly one declaration, or an actionable "which one did you mean" error. The syntax layer needs only
  `root` / `sourceText`. No workspace, no state.
- **Navigating:** an already-identified symbol -> its relationships across the solution. Needs
  `Compilation` / `SemanticModel` / `SymbolFinder` via `_workspaceManager`.
- **Semantic resolution** (`ResolveCandidatesWithSemanticAsync`, `LocateSymbolAsync`) is the bridge
  layer between the two. It stays where it is in this proposal.

### Evidence: the coupling is large and one-directional

Measured 2026-10-02 with the Search tool. After the engine reorg (commit `72a327e`: `RefactoringEngine`
split into `BasicRefactoringEngine` + `MemberRefactoringEngine`), those two engines make **118 calls**
to `_symbolNavigationEngine.` (`MemberRefactoringEngine` 101, `BasicRefactoringEngine` 17). **91 of the
118** (82 + 9) are syntax-side resolution helpers: `ResolveCandidates`, `ResolveBySnippetOrThrow`,
`Build*Hint*`, `BuildContainerNotFoundMessage`, `GetMemberName`, `NormalizeTypeName`, `FindContainer*`.
`AdvancedStructuralEngine.cs:1365` (`ConvertExpressionBodyAsync`) also calls `ResolveBySnippetOrThrow`.
So the refactoring engines hold a dependency on the *navigation* engine overwhelmingly to reach code that
does no navigation. File sizes for context: `MemberRefactoringEngine.cs` 4,779 lines,
`BasicRefactoringEngine.cs` 2,392, `SymbolNavigationEngine.cs` 3,124.

### Evidence: the shared home already caused drift once

The predecessor's sixth drifted path -- `FindCallersAsync`'s hand-rolled `decls` filter that silently
dropped classes -- is now fixed: line 1488 builds `decls` via `ResolveCandidates`. That bug is the
failure mode this proposal targets. A *navigation* method had grown its own resolution logic because
nothing about the class layout signalled that resolution lived elsewhere in the same file. The fix
repaired the instance; the layout that invited it is unchanged. Per the CLAUDE.md failure doctrine
this is an environment defect (the class has no boundary that says "resolution is here, do not
re-implement it in a navigation method"), not an author mistake.

### Evidence: the hint-formatter duplication is still present

There are seven formatters for "here are the candidates" text:

- `BuildMemberHint` (2843, public)
- `BuildTypeHint` (2908, **private**)
- `BuildMemberHintForCandidates` (2870) and `BuildTypeHintForCandidates` (2931) -- adapters over
  `SyntaxNodeCandidate`
- `DescribeNameOnlyCandidates` (2254, public static)
- `DescribeCandidateLocations` (3058, public static, over `SemanticSymbolCandidate`)
- `DescribeNearMissCandidatesAsync` (2291, public static async, takes a `Solution`)

Two concrete symptoms:

- The engine's own callers at about lines 2405 (`TryGetEnumMemberContainerNameAsync`), 2445
  (`IsEnumContainerAsync`) and 2487 (`GetContainerMembersAsync`) strip `SyntaxNodeCandidate`s back to
  bare nodes (`c.Select(x => x.Node)`) to call the old formatters. That throws away the candidate's
  precomputed `StartLine` and `Preview`, the fields `SyntaxNodeCandidate` was introduced to carry.
- `BuildTypeHintForCandidates` re-implements `BuildTypeHint`'s formatting because `BuildTypeHint` is
  private and the adapter could not call it.

All line numbers in this section are from the 2026-10-02 outline of `SymbolNavigationEngine.cs` and
will drift as soon as anything is edited; the move itself is what removes the need to cite them.

### Verification note on this document

The author of this doc had no RoslynSentinel MCP read tools available when writing it, and CLAUDE.md
forbids reading `.cs` with built-in tools. The line numbers and call counts above are therefore taken
from the 2026-10-02 measurement in the request brief and **were not re-verified** by the doc's author.
Re-check them (`GetFileOutline`, `Search`) when scheduling the work.

## Proposal

### Step 1 -- extract, unchanged (a pure move)

Move the syntax-only, stateless layer into a new stateless (likely `static`) class in
`RoslynSentinel.Engines.Basic`. Working name **`SyntaxTargetResolver`** (the name is an open decision,
see Open questions).

Members to move:

- `ResolveCandidates`
- `NormalizeTypeName`
- `GetMemberName`
- `PreferConstructorOverType`
- `PreferNonInterfaceMember`
- `FilterByContainingType`
- `ResolveBySnippetOrThrow`
- the syntax hint builders: `BuildMemberHint`, `BuildMemberHintForCandidates`, `BuildTypeHint`,
  `BuildTypeHintForCandidates`
- `BuildContainerNotFoundMessage`
- types `CandidateKind` and `SyntaxNodeCandidate`

Members that **stay** in `SymbolNavigationEngine` for now: `ResolveCandidatesWithSemanticAsync`,
`PreferClassMember`, `PreferImplementableMember`, `DescribeNearMissCandidatesAsync` (takes a
`Solution`), `DescribeCandidateLocations`, and types `SemanticSymbolCandidate` / `SymbolCandidate`.
`LocateSymbolAsync` also stays. `DescribeNameOnlyCandidates` was not in the brief's move list; its
side of the line is undecided (Open questions).

**Precondition, checked per member before it moves:** no `_workspaceManager` or other instance-state
use. `GetMemberName` and the instance hint builders (`BuildMemberHint`, `BuildTypeHint`, the two
`*ForCandidates`, `BuildContainerNotFoundMessage`) are instance methods today, so this is a real check,
not a formality. A member that fails it is left behind and listed in the PR, not forced across by
adding a parameter.

Because this is a pure move with no behaviour change, the predecessor's two hard rules carry over
without being re-argued:

- **The resolver never throws on zero matches** -- zero matches is an empty list. Backed by the
  discarded throw-on-zero attempt that regressed four tests (`AddAttribute_ToClass_WithBrackets`,
  `AddAttribute_ToClass_WithBrackets_StringArg`,
  `ModifyAttribute_BatchTwoEditsSameFile_BothApplyAgainstOriginalSnapshotAsync`,
  `ModifyAttribute_BatchAcrossTwoFiles_AppliesBothInOneCallAsync`), per the predecessor's Motivation.
- **Disambiguation stays a la carte**, caller-chosen helpers over `List<SyntaxNodeCandidate>`, not
  branches inside the resolver.

### Step 2 -- unify hint formatting (finishes predecessor section 5)

After the move, add one shared formatter over `List<SyntaxNodeCandidate>` inside the new class:

- Delete `BuildMemberHintForCandidates` and `BuildTypeHintForCandidates` (the adapters).
- Reduce `BuildMemberHint` / `BuildTypeHint` to the shared formatter or delete them, depending on
  whether any caller still holds only bare nodes (to be established when step 2 starts).
- Fix the three internal callers (about lines 2405, 2445, 2487) to pass candidates instead of
  `c.Select(x => x.Node)`.
- The semantic-side formatters (`DescribeCandidateLocations`, `DescribeNearMissCandidatesAsync`) are
  considered for convergence on the same wording but may stay separate because they format
  `ISymbol`-based candidates; that is an open decision.

**Visible behaviour change:** the error text agents see may change slightly (ordering, preview text,
line numbers now sourced from `StartLine`/`Preview`). Tests that assert on hint wording must be found
and checked before step 2 merges. This is why step 2 is a separate step from step 1: step 1 can be
reviewed as "nothing changed", step 2 cannot.

### Migration mechanics

- Do the move with the repo's own MCP tools (`MoveMember` into the new class, which rewrites call
  sites). Dogfooding is mandatory per CLAUDE.md; if `MoveMember` cannot do this, that is itself a
  blocking finding, not a reason to hand-edit.
- Expected call-site impact: about 91 sites across `MemberRefactoringEngine` and
  `BasicRefactoringEngine`, one in `AdvancedStructuralEngine.cs:1365`, plus the internal
  `SymbolNavigationEngine` callers (the three enum/container helpers, `FindCallersAsync` at about
  line 1488, and the semantic-side callers of the `Prefer*` helpers).
- **Recommendation: move outright, no forwarding methods.** Forwarders in `SymbolNavigationEngine`
  would leave two discoverable homes for the same call, which is the drift hazard this proposal
  exists to remove, and `MoveMember` already does the call-site rewrite that forwarders would defer.
  The obsolete-then-sweep staging used in `design_read_chokepoint.md` and the predecessor was justified
  by a multi-session sweep across hundreds of sites with no tool support; here a single tool-driven
  move is expected to cover the whole set in one pass. If `MoveMember` turns out not to rewrite
  call sites reliably (checked on a small first member such as `NormalizeTypeName`), fall back to
  temporary `[Obsolete]` forwarders and the warning-count sweep from that precedent. This is a
  recommendation, not a decision.
- Move one member first as a trial, build, then the rest in dependency order (helpers before callers),
  since the compile gate rejects non-compiling intermediate states.

### Payoffs

- Refactoring engines depend on `SymbolNavigationEngine` only where they actually follow relationships.
- The syntax resolver becomes unit-testable from a source string with no workspace, no DI and no
  engine construction.
- One discoverable home for "how do I find the target", for small models and fresh sessions (the
  CLAUDE.md mission: no falling back to grep or hand edits). The class name carries the signal that
  was missing when `FindCallersAsync` grew its own filter.
- `SymbolNavigationEngine` shrinks toward its navigation job.

### Verification plan (planner-owned, to be confirmed by the reviewer)

A green build and green suite are necessary but not sufficient evidence for a pure move: passing tests
cover only the paths they exercise. The planner of this work is responsible for defining, before the
move, what counts as verified; the reviewer confirms the listed tests actually exist and hit the
changed path rather than a shared helper. An implementer reaching green is correct from its own
vantage point and says nothing about uncovered paths. Proposed minimum, none of it checked yet:

- **Step 1:** at least one test per moved public entry point that calls the *new* class directly with
  a source string (new tests, since today's coverage reaches these only through the engines). Required
  cases: zero matches returns empty (the regression rule), same-name constructor vs type, interface
  member found, enum member found, class (not just method/property/field) found -- the last being
  the `FindCallersAsync` `decls` bug shape. Also confirm the four named attribute tests still pass,
  since they exercise the member-then-type fallback chain through the engines.
- **Step 2:** a test asserting the hint wording for at least member-not-found and type-not-found,
  written before the formatters change so the intended wording change is explicit in the diff.
- Compare full-suite results against the known pre-existing-failure baseline and report only new
  failures.

## Alternatives considered, not pursued

- **Leave as-is (status quo).** Lowest cost, and the engines work. Rejected: the predecessor's drift
  history (five divergent helpers, plus the sixth in `FindCallersAsync`) is the argument that
  resolution logic left inside a navigation class keeps getting re-implemented. 91 of 118 cross-engine
  calls going to a class for code that does not navigate is a standing dependency-direction smell.
- **Move only the hint formatters.** Smallest slice and it enables step 2. Rejected as the whole
  answer: the formatters are the least coupled part, and moving them alone leaves `ResolveCandidates`
  and its `Prefer*` helpers in the navigation class, so the boundary that would have prevented the
  `FindCallersAsync` drift still does not exist. Acceptable as a fallback if the precondition check
  shows the instance-state problem is wider than expected.
- **Split `SymbolNavigationEngine` into a resolution engine and a navigation engine, both holding the
  workspace.** Rejected. The syntax layer needs no state, so a DI-registered engine adds
  constructor plumbing for nothing. Multi-constructor DI was the cause of a real null-engine bug during
  the engine reorg (see [[project_engine_reorg_group6_solution_structure_partial]] and the reorg
  closeout, which record the DI-chain constructor-parameter gap), so adding another injected engine to
  the refactoring engines' constructors carries a known cost.
- **Keep thin forwarding methods in `SymbolNavigationEngine` after the move.** Considered under
  Migration mechanics; not recommended there, kept as the fallback.

## Open questions

- **Name.** `SyntaxTargetResolver` is a working name. Alternatives: `TargetResolver` (shorter, but the
  semantic bridge would then look like it belongs), `DeclarationResolver`, `SyntaxCandidateResolver`.
  Needs a user decision before the first `MoveMember`.
- **Static class vs instance.** "Likely static" assumes the precondition check passes for every
  member. If `ResolveBySnippetOrThrow` or any hint builder needs injected state, the answer changes.
- **Where do `DescribeNameOnlyCandidates` and the semantic formatters
  (`DescribeCandidateLocations`, `DescribeNearMissCandidatesAsync`) end up?** Not placed by the brief;
  needs a read of their inputs. Step 2 may change the answer.
- **Do step 1 and step 2 ship as one change or two?** Recommended two (step 1 reviewable as "no
  behaviour change"), but not decided.
- **Hint-wording tests.** Which existing tests assert on hint text is unknown; not yet searched.
- **Does `MoveMember` rewrite call sites across projects (Engines.Advanced, test projects) reliably?**
  Unverified; the first trial member answers it.
- **Does `AdvancedStructuralEngine` taking a dependency on the new static class cross any layering
  rule?** Expected fine (`Engines.Advanced` builds on `Engines.Basic`), but confirm with
  `build.ps1 -Flavor Solution`.

## Cost / risk

- **Touches:** about 91 call sites in two engines, one in `AdvancedStructuralEngine`, internal callers
  in `SymbolNavigationEngine`, and every test that names a moved member or constructs the engine to
  reach it. Mostly mechanical via `MoveMember`.
- **Step 1 risk is low in behaviour, moderate in tooling:** a pure move cannot change behaviour if
  the precondition holds, but a large cross-file `MoveMember` is a stress test of the tool, and any
  tool failure is a blocking finding (CLAUDE.md), which may halt the work mid-way.
- **Step 2 risk:** agent-visible error text changes. Small models rely on these messages to recover,
  so wording regressions are a mission-relevant risk, hence the pre-written wording tests.
- **Makes harder:** nothing structural. Code that today calls `_symbolNavigationEngine.ResolveCandidates`
  must call the new class; stale memory/doc references to the old home need refreshing.
- **Merge friction:** `MemberRefactoringEngine.cs` (4,779 lines) and `BasicRefactoringEngine.cs` are
  hot files; do the move when no concurrent session is editing them (see [[project_concurrent_sessions]]).
- **Not covered by this proposal and not reduced by it:** `MemberRefactoringEngine` stays a 4,779-line
  class. Splitting it along its seams (constructor parameters, enum members, member insert/sort,
  method signatures) is a separate effort, related only. Note the memory-recorded Bug 1/Bug 2 from the
  reorg closeout (constructor/method-signature call-site cascade) sits in that territory.

## Follow-ups (not done here)

- Mark `docs/current/proposal_universal_symbol_resolver.md` resolved for its resolver scope with a
  pointer to this proposal, and refresh its stale paths (`RoslynSentinel.Basic/...`,
  `RefactoringEngine.cs:NNNN`) to the post-reorg locations. This doc deliberately does not edit it.
- When approved or built, update the `docs/current/TODO.md` entry "Move syntax-side target resolution
  out of `SymbolNavigationEngine`, then unify hint formatters" (currently "not started"); when both
  steps land, move it to `docs/current/CLOSED.md`.

## Related

- `docs/current/proposal_universal_symbol_resolver.md` -- predecessor; defines `ResolveCandidates`,
  the "never throws on zero matches" rule and the a-la-carte disambiguation rule this carries over.
- `docs/current/TODO.md` -- entry "Move syntax-side target resolution out of `SymbolNavigationEngine`,
  then unify hint formatters".
- `docs/current/design_read_chokepoint.md` -- obsolete-then-sweep migration precedent, used here only
  as the fallback if a tool-driven outright move proves unreliable.
- `docs/current/proposal_compilation_cache.md` -- companion to the predecessor's semantic path; not
  affected, since the semantic layer stays in place.
- [[project_engine_reorg_final_closeout_bugs_1_2_3]] and
  [[project_engine_reorg_group6_solution_structure_partial]] -- the reorg (commit `72a327e` and
  neighbours) that produced the 91-call coupling and the DI-chain constructor-parameter gap.
- [[project_concurrent_sessions]] -- scheduling caution for hot files.
