# SemanticFindReplace(replaceConstruction): carry object initializers and stop echoing skipped sites in full

**Status:** PROPOSED 2026-10-28. TOOL-GAP from the Tests.Support.* factory rollout; awaiting review, nothing decided on the initializer mechanism.

## Motivation

Two separate gaps in one operation, found by the same rollout (about 1,450 `new X(...)` sites moved onto factories in
[plan_tests_support_projects_factory_rollout.md](../plans/closed/plan_tests_support_projects_factory_rollout.md), follow-ups 1 and 2).

**1. Initializer sites are skipped, so 11 sites stayed `new`.** The Step 5e final sweep counted 11 rewritable-looking
sites that `replaceConstruction` cannot carry: 8 in `Tests.Server` and 3 in `Tests.Tools.Basic`, all of the shape
`new PersistentWorkspaceManager(logger) { SolutionPath = ... }`. The Step 4 `Tests.Server` pass was a no-op for that reason
(plan Progress, "Step 4 `Tests.Server` NO-OP": 8 sites, all skipped; the Support.Basic reference was added and reverted).
Sites: `LargeResultOffloadFilterTests:52,237`, `RelativePathInputAuditTests:51`, `StructuredContentDataTagTests:46`,
`ToolArgumentCaseNormalizationTests:66`, `ToolCallEchoFilterTests:60`, `ToolParameterAliasTests:59`,
`WirePathRelativizeFilterTests:54`; and `SearchModeTests:71`, `SearchSolutionTextIncludeTestsTests:47`,
`SearchSolutionTextRecoveryHintTests:52` in `Tests.Tools.Basic`. The result is that `Tests.Server` has 0 factory calls
(plan Step 5e count), so that project still builds a full production type by hand.

Verified in source: the skip is unconditional. `ConstructionReplacer.GetSkipReason`
(`RoslynSentinel.Engines.Basic/ConstructionReplacer.cs`, the `node.Initializer is not null` check, return at line 253) returns
"site has an object or collection initializer; the factory call would lose it, so the site was left unchanged". The
rewrite itself only replaces the span from the node start to the argument list
(`TextSpan.FromBounds(node.SpanStart, argumentStart)` in `PlanAsync`), so the initializer text would be kept verbatim after
the argument list if it were allowed through, which is exactly why it is refused: `Factory(args) { X = 1 }` is not valid C#.
The limitation is documented in the tool description (`SemanticFindReplaceTools.cs:47`), so a caller is told, but cannot
act on it.

**2. Every skipped site is echoed in full.** `PlanAsync` adds each skipped site as
`new SemanticReplaceSite(path, line, Unsupported, before, before, skipReason)`
(`ConstructionReplacer.cs`, the `skipReason is not null` branch), so `Before` and `After` are the same full creation
expression text, plus a per-site reason string. The tool returns `sites = outcome.Sites` unconditionally in preview, in
apply, and in the "nothing to rewrite" apply
(`RoslynSentinel.Tools.Basic/SemanticFindReplaceTools.cs`, the three `sites = outcome.Sites` returns after the
`rewritten`/`skipped` counts). The "argument shape does not match" reason also repeats the whole factory signature at every
site (`GetSkipReason`, last `return` before `replacementPrefix = prefix`). The plan recorded the cost: "`replaceConstruction`
echoes full before/after for every skipped site (a wrong-overload call costs thousands of tokens)" (Step 5b). The rollout
used one call per overload, so a call for the 1-argument factory lists every site of the 2-argument shape as skipped. For
a weak model this is the expensive failure: the useful signal is the `rewritten`/`skipped` counts and a handful of
examples, not N copies of identical text.

Not verified: whether the central large-result offload filter pages this payload on its own. The plan's "thousands of
tokens" observation suggests it does not help enough here.

## Proposal

Both parts are independent slices; neither blocks the other.

### Part 1: carry initializers

Two candidate mechanisms. **Which one (or both) is open.**

- **1a. Map initializer members onto same-named factory parameters.** For `new T(a) { P = v, Q = w }`, if the factory has
  parameters named `p` and `q` (case-insensitive, type-compatible), rewrite to `Factory(a, p: v, q: w)`. Every initializer
  member must map, otherwise the site stays skipped with a reason that names the unmapped member(s). The speculative
  binding probe in `GetSkipReason` (`GetSpeculativeSymbolInfo` on `prefix + argumentList`) must include the named
  arguments. Works with `init`-only properties because the factory does the assignment. For the 11 sites the factory would
  be `TestEngines.PersistentWorkspaceManager(ILogger<IWorkspaceManager> logger, string? solutionPath = null)`; the only
  existing factory today takes `logger` alone (`Tests.Support.Basic/TestEngines.cs:45`).
- **1b. Trailing `Action<T>` configure argument.** For a factory whose last parameter is `Action<T>`/`Action<TBase>`,
  rewrite `{ P = v }` into a lambda `x => { x.P = v; }`. Works for any initializer member and needs no per-property
  factory parameter, but cannot assign `init`-only members (CS8852), cannot express collection initializers (`Add`), and
  needs a lambda parameter name that does not clash with names in scope at the site.

Common requirement for either: the tool cannot see whether the factory body actually applies the parameter or invokes the
action. A factory that ignores it would silently drop the initializer, the very loss the current skip prevents. See Open
questions.

### Part 2: cap the echo of skipped sites

- Return `sites` for rewritten sites in full (they are the diff the caller needs to approve).
- For skipped sites, group by reason: one entry per distinct reason with `count`, up to 3 example sites (path:line, and the
  `Before` text once, since `After` is identical), and the full list stored for `GetLargeResult` (an id in the result,
  as `Build` does with `FullDiagnosticsResultId`).
- Shorten the shape-mismatch reason: name the factory once at the top level of the result, not per site.
- Keep `rewritten` and `skipped` counts as they are.

## Staging (migration-first)

Additive, no `[Obsolete]` step needed: a site that is skipped today and is not supported by the chosen mechanism stays
skipped with the same semantics. Suggested slices:

1. Part 2 first (response shape only). Add the grouped `skippedSummary` and the stored full list beside the existing `sites`
   array; once callers move, drop skipped entries from `sites`. This is a response-shape change, so check consumers
   (`Search` for `SemanticReplaceSite` and `UnsupportedReason`) before removing anything.
2. Part 1a (named-parameter mapping), because it is deterministic and `init`-safe.
3. Part 1b only if a real caller needs a shape 1a cannot express.

## How to verify

- Unit tests next to the existing `ConstructionReplacer` tests (hidden from `Search` unless `includeTests: true`): initializer fully
  mapped (rewritten, `after` text is `Factory(a, p: v)`), partly mapped (skipped, reason names the member), no matching
  parameter (skipped), `init`-only property (1a rewrites, 1b skips), collection initializer (skipped), factory that exists
  in two overloads.
- Live check on the real 11 sites: add the `solutionPath` overload to `Tests.Support.Basic`, run
  `SemanticFindReplace(replaceConstruction, mode: preview, projectFilter: RoslynSentinel.Tests.Server)`, expect
  `rewritten: 8`, then `Build` and `RunTest` on `Tests.Server`.
- Echo: run a deliberately wrong-overload preview over the `Tests.Tools.Basic` project and compare response size before and
  after (the plan has no byte count, only "thousands of tokens"; record one).

## Alternatives considered, not pursued

- **Do nothing; hand-edit the 11 sites.** Cheap once, but leaves `Tests.Server` as the only test project with zero
  factory calls and leaves the next factory rollout with the same dead end. Not a tool-surface answer (CLAUDE.md mission).
- **Rewrite `new T(a) { P = v }` into `Factory(a) with { P = v }`.** Only valid for records; the targets here are classes.
- **Let the tool emit `Factory(a) { P = v }` unchanged.** Not valid C#.
- **Drop the per-site echo entirely.** Loses the "why was this skipped" signal that made the 11 initializer sites
  diagnosable in the first place; grouping keeps it.

## Open questions

- 1a vs 1b vs both. The leaning is 1a first; no decision.
- Should the tool verify the factory honors the parameter (for 1a: the parameter is used in the factory body; for 1b: the
  action is invoked)? A cheap approximation is a reference check on the parameter symbol inside the factory body.
  Unverified whether that is worth the complexity versus documenting the factory contract in the tool description.
- Does a factory that gains an optional `solutionPath` parameter change what `Tests.Server` exercises (optional parameters
  resolve differently when the call omits them)? Probably not for these tests, but unchecked.
- Exact response shape for grouped skips (field names) - open.

## Cost / risk

- Touches `ConstructionReplacer.GetSkipReason`/`PlanAsync` (Engines.Basic), `SemanticReplaceTypes.cs`
  (`SemanticReplaceSite` or a new summary record), and the result projection in `SemanticFindReplaceTools.cs`. No new tool,
  so none of the registration steps in CLAUDE.md apply.
- Risk: silently dropping an initializer if a factory ignores its parameter (see Open questions). Part 2 changes the response
  shape of an existing tool, so any caller reading skipped entries from `sites` must be checked first.
- Makes harder: the tool description (`SemanticFindReplaceTools.cs:42-47`) gets longer, and the lean-schema budget applies
  to it.

## Related

- [plan_tests_support_projects_factory_rollout.md](../plans/closed/plan_tests_support_projects_factory_rollout.md) (follow-ups 1, 2; Steps 4, 5b, 5e)
- [plan_test_construction_factories_pilot.md](../plans/closed/plan_test_construction_factories_pilot.md) (first live use: 119 sites in one call, initializer skips noted)
- `docs/current/TODO.md` P1 entry on `replaceConstruction` and object initializers
