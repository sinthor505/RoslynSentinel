# TODO — known gaps not yet fixed

Running list of confirmed-but-deferred issues found during tool development/grading. Each entry
should have enough detail to pick back up without re-discovering the root cause. Remove an entry
once it's actually fixed (and note the fix in SCENARIOS.md/commit history instead).

## CRITICAL: `FindReferences`/`FindCallersAsync`/`FindImplementationsForMemberAsync` silently return
## "zero results" instead of an error when `contextSnippet` fails to resolve

**Found:** 2026-08-19, during the `contextSnippet` tool-description audit (see SCENARIOS.md /
"Still open" and the ExtractMethodSafe/ReplaceMember/ExtractLocalVariable fixes from the same day).

**What:** `SymbolNavigationEngine.FindCallersAsync` (`RoslynSentinel.Basic/SymbolNavigationEngine.cs:1281`)
has two resolution paths depending on whether `filePath` is supplied: if it is, `symbolName` is
**silently ignored** and the target is resolved purely from `contextSnippet` via
`ContextHelper.FindSymbolAtSnippetAsync`. If that fails to resolve (`symbol == null`, line 1340), the
method returns `new List<CallerInfo>()` — an **empty list**, not an error. The `FindReferences` MCP
tool wrapper (`RoslynSentinel.Server.Basic/SentinelSymbolTools.cs:389`) then returns
`Success: true, Data: []` — indistinguishable from "confirmed zero callers." Same shape in
`FindImplementationsForMemberAsync`.

**Why this is CRITICAL, not just a wording issue:** `SafeDeleteUnusedSymbol`'s and `RemoveMember`'s
entire safety contract is "confirm zero usages, then delete." An agent that calls
`FindReferences(filepath: ..., symbolName: "Foo", contextSnippet: <typo or stale text>, kind: callers)`
and gets back `{"success": true, "data": []}` has every reason to believe the symbol is genuinely
unused — it looks identical to a real zero-callers answer. If it then proceeds to delete a symbol
that's actually heavily referenced, this silent failure is the direct cause, and nothing about the
tool's response signals that anything went wrong.

**Suggested fix:** when `filePath` is supplied but `contextSnippet` (or the fallback name-based lookup)
fails to resolve to a symbol, return a distinct, actionable error (matching the pattern already used by
`ResolveMemberByNameOrSnippet`'s `BuildMemberHint`) instead of an empty list — e.g. "symbolName '{name}'
could not be resolved in {filePath} via contextSnippet; a request for its usages was NOT performed, do
not treat this as a confirmed-zero-usages result." Separately, fix `filePath`-supplied calls to also
check `symbolName` (currently silently discarded on that path) so the intended target and the actually-
resolved one can be cross-checked, not just trusted.

**Not fixed as part of the 2026-08-19 `contextSnippet` wording audit** — that work covered parameter
naming/description clarity; this is a distinct correctness/safety bug in the resolution logic itself.

## `contextSnippet`'s line-level whitespace-tolerant fallback returns a line-START position, not a
## precise in-line position — fine for member/type disambiguation, wrong for expression-level tools

**Found:** 2026-08-19, while adding a regression test for `ExtractLocalVariableAsync`'s exact-match
whitespace tolerance (see SCENARIOS.md "Fixed" list for that same-day fix).

**What:** `ContextHelper.FindAllSnippetMatches`'s single-line collapsed-whitespace fallback
(`RoslynSentinel.Common/ContextHelper.cs:58-74`) resolves a match to `lines[i].Start` — the start of
the *entire source line* — whenever the snippet's whitespace-collapsed text is found anywhere within
that line. This is the right granularity for `ResolveMemberByNameOrSnippet`/`ResolveTypeByNameOrSnippet`
(the position only needs to fall inside the right *member's* span), but it is NOT precise enough for
`ExtractLocalVariableAsync`, which needs the position to land exactly at an `ExpressionSyntax`'s
`SpanStart` — e.g. a snippet `"a + b"` (spaced) against real source `"return a+b;"` (unspaced,
same line) resolves via this fallback to the position of `r` in `return`, not `a`, so
`ExtractLocalVariableAsync`'s exact-match check (`e.SpanStart == pos && ...`) never finds a candidate
and it falls through to the ambiguous nearest-enclosing-expression guess instead — for a case that
should have been an unambiguous exact match.

**Not fixed as part of the 2026-08-19 `ExtractLocalVariableAsync` whitespace-tolerance fix** — that fix
only addressed exact-vs-fallback ordering for snippets that already resolve to the correct position
(e.g. multi-line differences reached via the exact-ordinal/line-ending-tolerant path, which does
preserve real in-source position). A same-line, inter-token whitespace difference still doesn't reach
`ExtractLocalVariableAsync`'s own exact-match branch at all — it's mis-resolved one layer earlier, in
`ContextHelper` itself.

**Suggested fix:** either (a) add a position-precise variant of the whitespace-collapse fallback that
returns the actual sub-line offset where the normalized snippet starts (mapping back through the
whitespace-collapse transform to the real, pre-collapse character offset), for callers that need
expression-level precision, or (b) have `ExtractLocalVariableAsync` request/require this more precise
resolution explicitly rather than sharing the same line-start-granularity path every member/type
resolver uses.

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
