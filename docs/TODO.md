# TODO — known gaps not yet fixed

Running list of confirmed-but-deferred issues found during tool development/grading. Each entry
should have enough detail to pick back up without re-discovering the root cause. Remove an entry
once it's actually fixed (and note the fix in SCENARIOS.md/commit history instead).

## Duplicate/dead `SafeDeleteSymbolAsync` on `RefactoringEngine`

**Found:** 2026-08-19, while adding a `symbolName`/`contextSnippet` fallback path to the
`SafeDeleteUnusedSymbol` MCP tool (which calls `StructuralRefinementEngine.SafeDeleteSymbolAsync`).

**What:** `RefactoringEngine.SafeDeleteSymbolAsync(FilePath filePath, string contextSnippet, string?
lineBefore, string? lineAfter, ...)` (`RoslynSentinel.Basic/RefactoringEngine.cs:1531`) is a second,
independent implementation of "delete a symbol if unused" that:
- Takes `contextSnippet` alone (no `symbolName` needed) to resolve the target.
- Returns `Dictionary<FilePath, string>`, not `DocumentEditResult` — a different shape from every
  other engine method in this codebase.
- Signals failure via a magic `"ERROR"` dictionary key/value instead of `EditOutcome`.
- Has genuinely more thorough safety checks than the wired-up path: detects potential
  reflection/dynamic usage by scanning for string literals matching the symbol's name across the
  whole solution, and does a belt-and-suspenders identifier-name rescan beyond `SymbolFinder`.
- Is gated behind `_config.IsFeatureEnabled("SafeDeleteUnusedSymbol")` — a feature flag that the
  actually-wired tool path does not check at all.

**Is it reachable?** No. It is **not called by the `SafeDeleteUnusedSymbol` MCP tool** (that tool
only calls `StructuralRefinementEngine.SafeDeleteSymbolAsync`, a different class). The only caller
in the entire codebase is one test: `RoslynSentinel.Tests.Advanced/DeepFunctionalVerificationTests.cs`
(the reflection-risk test). It is dead code from the tool surface's perspective.

**Why this matters:** the reflection-detection safety check is real and useful — arguably more
correct than `StructuralRefinementEngine`'s SymbolFinder-only reference check for the deletion
scenario this tool advertises ("delete unused symbol"). Right now an agent that deletes a symbol
still referenced only via `nameof(...)`-adjacent string literals or reflection would sail through
undetected on the tool that's actually wired up.

**Options for whoever picks this up:**
1. Port the reflection-literal-scan safety check into `StructuralRefinementEngine.SafeDeleteSymbolAsync`
   (both overloads — line/column and the new symbolName/contextSnippet one), then delete the
   `RefactoringEngine` copy and its now-orphaned test (or migrate the test to exercise the merged
   check on `StructuralRefinementEngine` instead).
2. At minimum, confirm whether `_config.IsFeatureEnabled("SafeDeleteUnusedSymbol")` should also gate
   the wired-up tool — right now the tool ignores that flag entirely, which may or may not be
   intentional.

Not fixed as part of the `symbolName`/`contextSnippet` fallback work (2026-08-19) — that work stayed
scoped to `StructuralRefinementEngine`, the class actually reachable from the MCP tool surface.
