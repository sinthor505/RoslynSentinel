# TODO — known gaps not yet fixed

Running list of confirmed-but-deferred issues found during tool development/grading. Each entry
should have enough detail to pick back up without re-discovering the root cause. Remove an entry
once it's actually fixed (and note the fix in SCENARIOS.md/commit history instead).

## Future feature: `UsingDirective(operation: add, simplifyAllCallers: true)` — solution-wide simplification

**Found:** 2026-08-19, while reviewing whether `UsingDirective` needed a `simplifySingleFile`/
`simplifyAllCallers` split. `simplifySingleFile` already effectively exists as the current
`simplifyExisting` bool (add-only, runs `Simplifier.ReduceAsync` scoped to just the edited
document) — no new work needed there. `simplifyAllCallers` does not exist and would be new,
larger-scope work, not a boolean flag on the existing method.

**What it would need to do:** given a namespace being added to one file, find every other document
in the solution that references that namespace via a fully-qualified name, ensure each has (or
gets) the corresponding `using` directive, then run `Simplifier.ReduceAsync` per-document to shorten
the now-redundant fully-qualified references — mirroring what `simplifyExisting` already does for
a single file, but solution-wide.

**Why not built now:** this changes the tool's blast radius from "one file" to "the whole
solution" — every document touched needs its own using-directive-presence check (not just the
one file the caller named), its own simplify pass, and its own change entry in the result. That's
a meaningfully different feature (more like a bespoke `SymbolFinder`-driven sweep) than the current
single-document flag, and deserves a deliberate design pass (e.g. should it also report which files
it touched? cap how many files it'll touch in one call? require a dry-run first?) rather than being
bolted on as a same-shaped bool.

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

## `contextSnippet` wording audit across tool descriptions

**Found:** 2026-08-19, while fixing `ReplaceMember`'s single-candidate `contextSnippet` bug (see
SCENARIOS.md Scenario 4 / "Fixed" list).

**What:** every `contextSnippet`-accepting tool's `[Description]` calls it "a distinctive substring
from the target member" (or near-identical wording) without clarifying what "distinctive" actually
requires — that it still needs to match the file's real text (now tolerant of whitespace/indentation
differences, but not genuine content differences). Across the 7 recorded ContosoOrders agent runs,
real agents have passed, for the exact same kind of call: a full member body, a signature-only
one-liner, a comment-only fragment, and a from-memory reconstruction that introduced a genuine content
difference (see `ContextHelperTests.FindSnippetPosition_SafeDelete_AgentFabricatedInterpolation_StillFailsToMatch`).
Nothing in the current wording steers an agent toward the safest choice (shortest unique substring
that's still copied verbatim) or away from the riskiest one (reconstructing a whole member from
memory).

**Why this matters:** with the 2026-08-19 fix, `contextSnippet` is now genuinely optional for any
non-overloaded target — but agents don't know that from the description alone, and will likely keep
supplying one defensively "just in case," reintroducing exactly the kind of avoidable mismatch this
session fixed for `ReplaceMember` specifically, on some other tool or some other snippet shape not yet
seen in a live run.

**Suggested approach:** a single pass across every `[Description]` mentioning `contextSnippet` (grep
for `ToolParams.ContextSnippet` and inline duplicated wording — some tools use the shared constant,
others still inline their own text) to state consistently: (1) only needed when the name is ambiguous
(2+ same-named declarations); (2) prefer the shortest substring that's still unique — a signature line
is usually enough, a full body is rarely necessary and is more failure-prone to reproduce verbatim; (3)
copy it verbatim from a prior tool result rather than retyping from memory. Not done as part of the
`ReplaceMember` fix, which addressed the resolution *logic* but not the *wording* that leads callers to
over-supply a snippet in the first place.

## Deferred: `contextSnippet` deprecation tracking, and `NearMissList`'s 3-candidate cap

**Found:** 2026-08-19, closing out `docs/plan-tool-disambiguation-remediation-v1.md` Task I/J
(hint-strategy evaluation + raw-`ContextHelper` error-message enrichment).

**What (two related, deliberately-unresolved questions):**
1. Every tool touched by that plan keeps `contextSnippet` fully optional, silently first-matching
   by name when omitted — including when the name is genuinely ambiguous. Whether to eventually
   require `contextSnippet` (or `symbolName`+`contextSnippet`) once ambiguity is detected, or at
   least emit a non-fatal warning on a silent first-match against 2+ candidates, was explicitly
   raised as a Risks-section question in that plan and never decided — it's a product/reliability
   trade-off (breaking today's default-argument-free call shape vs. catching silent wrong-guesses
   proactively), not something to decide unilaterally while fixing the hint text.
2. The `NearMissList` hint strategy (now the sole implementation in `RefactoringEngine.BuildMemberHint`/
   `BuildTypeHint`) caps its candidate list at 3, with a "+N more" suffix beyond that. No fixture in
   the current test suite has more than 3 real same-named candidates, so this was left at the plan's
   originally-specified cap rather than speculatively widened or made configurable.

**Why not resolved now:** both are explicitly flagged in the plan doc's Task J addendum as
recommendations for the user to decide, not gaps this session's work left broken — the additive,
non-breaking behavior is working as designed today.

## `ConvertExpressionBodyAsync` has the same contextSnippet bug class as `ReplaceMember`, different code shape

**Found:** 2026-08-19, while fixing `ReplaceMember`'s `ResolveMemberByNameOrSnippet`/
`ResolveTypeByNameOrSnippet` single-candidate bug (see SCENARIOS.md Scenario 4 / "Fixed" list).

**What:** `RefactoringEngine.ConvertExpressionBodyAsync` (`RoslynSentinel.Basic/RefactoringEngine.cs`,
~line 1643) resolves its target with an `if (contextSnippet != null) { position-based } else {
name-based candidates }` branch — structurally different from `ResolveMemberByNameOrSnippet`'s
"compute name-based candidates first, only consult the snippet if 2+" shape. This means a supplied
`contextSnippet` bypasses name-based candidate computation entirely rather than being ignored when
unnecessary, so the same failure mode (a defensive/mismatched snippet blocking an otherwise-unambiguous
resolution) is still possible here, just via a different code path.

**Why not fixed alongside `ReplaceMember`:** the one-line "skip if `candidates.Count <= 1`" guard used
for the two shared helpers doesn't directly apply — this method would need restructuring to compute
name-based candidates unconditionally first, then decide whether to also honor a snippet-based
position, which is a larger, more careful change than the guards applied elsewhere. Worth checking
whether any other `RefactoringEngine`/`StructuralRefinementEngine` methods share this same
`if (contextSnippet != null) { position } else { name }` shape (not yet audited) before fixing, so all
affected methods get the same treatment in one pass rather than one at a time as each is found live.
