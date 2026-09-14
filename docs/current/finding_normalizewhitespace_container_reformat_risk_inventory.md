# `NormalizeWholeSubtreeWhitespace` call-site risk inventory

## Status

Informational — not a blocker, not yet scheduled for remediation. Seeds the future
caller-fixup/breaking-change tool audit and the `RedirectCalls`/call-shape-rewrite proposal
(`docs/current/proposal_redirect_calls_tool.md`).

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
