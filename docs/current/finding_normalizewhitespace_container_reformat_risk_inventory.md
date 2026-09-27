# `NormalizeWholeSubtreeWhitespace` call-site risk inventory

## Status

Informational — not a blocker, not yet scheduled for remediation. Seeds the future
caller-fixup/breaking-change tool audit and the `RedirectCalls`/call-shape-rewrite proposal
(`docs/current/proposal_redirect_calls_tool.md`).

**Re-confirmed 2026-09-26:** the underlying risk inventory is still substantively accurate
(spot-checked 3 of ~20 files, 0 fixed) but this doc's file paths and helper name have gone stale --
see "Path corrections" below. Also overlaps `docs/current/TODO.md`'s "Advanced side ~63-64
occurrences" entry: same underlying `NormalizeWhitespace()`-pattern problem tracked at a broader
file-count level. Treat this doc's inventory as the working checklist for that TODO.md follow-up,
not a separate/redundant tracking artifact.

### Path corrections (2026-09-26)

- The helper is `RoslynSentinel.Common/RoslynFormattingHelper.cs` (not `FormattingHelper.cs` as
  written throughout the rest of this doc).
- Several "Risky" files have moved projects since this doc was written:
  `GranularRefactoringEngine.cs`, `MappingEngine.cs`, `CodeStyleEngine.cs` are now under
  `RoslynSentinel.Advanced/`, not `RoslynSentinel.Basic/`. `IDEStyleEngine.cs` is now at
  `RoslynSentinel.Server.Advanced/IDEStyleEngine.cs`. `MsToolAugmentEngine.cs` and
  `SyntaxUpgradeEngine.cs` are the only two still correctly under `RoslynSentinel.Basic/` as
  originally listed.
- Spot-checked and confirmed still risky/unfixed: `AdvancedStructuralEngine.cs` (15 unscoped sites
  on whole-tree variables), `SyntaxUpgradeEngine.cs` (4 sites, not named in TODO.md's "Basic side
  fully closed" entry -- apparently missed by that sweep), `MsToolAugmentEngine.cs` (2 sites, these
  match the 2 sites TODO.md's Basic-closure entry names as deliberately left unfixed --
  `Document`-less contexts with no annotation-scoped `Formatter.FormatAsync` overload available).

## Background

This session fixed a real bug: `AddMemberAsync`/`InsertMemberAfterAsync`/`InsertMemberBeforeAsync`
(`RoslynSentinel.Basic/RefactoringEngine.cs`) called `Formatter.FormatAsync` over an entire
container node (class/struct/interface/record) to format one newly-inserted member, which reformats
every untouched sibling member as a side effect — dropping blank lines and normalizing spacing that
had nothing to do with the edit. The fix, `FormattingHelper.InsertMemberFormattedAsync`
(`RoslynSentinel.Common/FormattingHelper.cs`), scopes the formatting annotation to just the
inserted member instead.

Separately, ~85 other call sites across the codebase called Roslyn's `.NormalizeWhitespace()`
extension method directly and inconsistently, with no shared chokepoint. All of them have now been
mechanically converted to route through a new pass-through wrapper,
`FormattingHelper.NormalizeWholeSubtreeWhitespace(node, indentation, eol, elasticTrivia)`
(`RoslynSentinel.Common/FormattingHelper.cs:238-272`) — a literal find-replace, no behavior change.
That conversion is complete; this doc is the follow-on triage it produced, not a description of the
conversion itself.

## Why this inventory exists

Centralizing the call sites behind one wrapper doesn't fix the container-wide-reformat risk at any
of them — it makes the risk visible and greppable (`grep NormalizeWholeSubtreeWhitespace`) instead
of scattered across 23 files under Roslyn's raw extension-method name. This doc records the triage
done at conversion time so that visibility isn't lost: which call sites share the exact risk shape
that was just fixed in the `Member`-insert path, and which don't.

## Classification

**Safe** — the node being normalized is a freshly-synthesized `SyntaxFactory.X(...)` construction
with no pre-existing siblings to clobber, or a `With`-mutated copy of a single existing node (not a
container/tree root):

- `RoslynSentinel.Basic/RefactoringEngine.cs` — lines ~476, 5257, 5288 (fresh
  `SyntaxFactory.MethodDeclaration(...)`/`PropertyDeclaration(...)` chains), ~818 (`ifaceCompUnit`,
  fresh `CompilationUnit()`), ~540 (`callStatement`, normalizes only for a display string, not a
  write), ~1624 (`newTarget`, a `With`-mutated copy of a single existing member, not a container).
- `RoslynSentinel.Basic/CodeGenerationEngine.cs` — lines ~961, ~986 (fresh
  `SyntaxFactory` constructions).

**Risky** — the node being normalized is an existing tree root or container
(`root`/`newRoot`/`updatedRoot`/a `ReplaceNode(...)` result) with untouched siblings that would be
reformatted as a side effect, the same shape as the just-fixed `Member`-insert bug. Not fixed in
this pass — flagged for future audit only:

- `RoslynSentinel.Advanced/AdvancedLogicEngine.cs` — all 5 in-scope sites
- `RoslynSentinel.Advanced/AdvancedStructuralEngine.cs` — all ~18 in-scope sites
- `RoslynSentinel.Advanced/AsyncOptimizationEngine.cs` — all ~12 in-scope sites
- `RoslynSentinel.Advanced/AdvancedRefactoringEngine.cs`
- `RoslynSentinel.Advanced/AdvancedTypeEngine.cs`
- `RoslynSentinel.Advanced/ApiIntegrationEngine.cs`
- `RoslynSentinel.Advanced/ArchitecturalEngine.cs`
- `RoslynSentinel.Advanced/AsyncBatchEngine.cs`
- `RoslynSentinel.Advanced/CodeHealingEngine.cs`
- `RoslynSentinel.Advanced/DocumentationEngine.cs`
- `RoslynSentinel.Advanced/LogicOptimizationEngine.cs`
- `RoslynSentinel.Advanced/ModernLoggingEngine.cs`
- `RoslynSentinel.Advanced/ModernizationEngine.cs`
- `RoslynSentinel.Advanced/ModernizationUpgradeEngine.cs`
- `RoslynSentinel.Advanced/RefinementEngine.cs`
- `RoslynSentinel.Basic/CodeStyleEngine.cs`
- `RoslynSentinel.Basic/GranularRefactoringEngine.cs`
- `RoslynSentinel.Basic/IDEStyleEngine.cs`
- `RoslynSentinel.Basic/MappingEngine.cs`
- `RoslynSentinel.Basic/MsToolAugmentEngine.cs`
- `RoslynSentinel.Basic/SyntaxUpgradeEngine.cs`

Each file above needs its own read-through to confirm the exact call sites and whether each one is
actually reachable with pre-existing siblings at the point of the call (this triage was a fast pass
during the mechanical conversion, not a full audit) — treat the list as a starting point, not a
verified defect list.

**Excluded (not call sites needing conversion, correctly left untouched)**:

- `RoslynSentinel.Common/ContentHasher.cs:14` — `member.NormalizeWhitespace().ToFullString()`, used
  for content hashing/comparison, not a write.
- `RoslynSentinel.Common/PersistentWorkspaceManager.cs:1406-1407` — normalizes parsed text purely to
  compare old vs. new content, not a write.

## What would unblock remediation

The same fix shape already applied to `Member`-insert (`InsertMemberFormattedAsync`: scope the
`Formatter.FormatAsync` annotation to the changed node only, not the whole container) would apply to
each "risky" site above, but each needs individual verification that a real sibling exists at that
point (not all `root.NormalizeWhitespace()` calls necessarily have untouched siblings — some may
occur on transforms that already reconstruct the whole tree intentionally). This is exactly the kind
of repetitive, mechanically-similar-but-not-identical fix that the caller-fixup/breaking-change tool
audit (parked, see project memory) and/or a future `RedirectCalls`-family tool could reduce the cost
of — but neither is being acted on here; this doc is the input for that future work, not a
remediation itself.
