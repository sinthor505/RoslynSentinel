# Plan — Symbol/Member Disambiguation Survey

## Title
Survey every MCP tool that resolves a target by name (not by stable coordinate) and categorize
how each one disambiguates — or fails to — when more than one candidate matches.

## Background
While reviewing `docs/plan-symbol-tool-hardening-v1.md`'s Task F (`RemoveMember` precheck), a
follow-up question surfaced: how do `ReplaceMember` and `SafeDeleteUnusedSymbol` behave when more
than one member shares the target name (overloads, same-named members in different nested types,
a field/method name collision, etc.)?

Answering that one case turned up a real, structural gap:

- **`ReplaceMemberAsync`** (`RoslynSentinel.Basic/RefactoringEngine.cs` ~line 1208) resolves purely
  by identifier text via a shared `GetMemberName(MemberDeclarationSyntax)` helper (~line 3540),
  which switches on syntax node type and returns only `m.Identifier.Text` — it does not look at
  parameter lists, containing type, or anything else. The lookup is
  `root.DescendantNodes().OfType<MemberDeclarationSyntax>().FirstOrDefault(m => GetMemberName(m) ==
  memberName && ...)` — a depth-first, document-order walk that silently takes whichever match
  comes first. Two overloads of a method, or two same-named members in sibling types, are
  structurally indistinguishable to this call: there is no error, no warning, and no way to target
  "the second one." If the caller's `newSource` has a different signature than the member actually
  matched, the wrong member gets replaced with no indication anything went sideways.
- **`SafeDeleteSymbolAsync`** (`RoslynSentinel.Basic/StructuralRefinementEngine.cs` ~line 72), by
  contrast, resolves by `line`/`column` coordinate — `root.FindNode(new TextSpan(position, 0))` —
  then asks the semantic model for the symbol at that exact node. It is structurally immune to the
  overload-collision problem: two `Add` overloads at different lines are unambiguous as long as the
  caller passes the coordinates of the one they mean.

This is a real, user-facing correctness gap for `ReplaceMember` specifically (and plausibly other
name-only-resolution tools), but the full scope is unknown — this plan is deliberately scoped to
finding out how big the problem is before deciding what to fix. **Do not attempt fixes in this
plan** — that is explicit scope for a follow-up plan (see Deliverable below).

## Assumptions
- This is a research/survey task, not an implementation task. No production code changes. The only
  file this plan produces is the survey report itself (see Deliverable).
- 119 `[McpServerTool]`-decorated methods exist across `RoslynSentinel.Server.Basic/*.cs` and
  `RoslynSentinel.Server.Advanced/*.cs` as of this plan's writing (confirmed via `grep -rc
  "\[McpServerTool" RoslynSentinel.Server.Basic/*.cs RoslynSentinel.Server.Advanced/*.cs`). Re-run
  that count at the start of the survey — it will have drifted.
- Not all 119 tools resolve a target by name — many take a `filepath` alone (whole-file operations),
  a `projectName`, or operate solution-wide with no single-target resolution at all. Triage which
  tools are even in-scope (Step 1) before categorizing them (Step 2) — don't force a disambiguation
  category onto a tool that doesn't do target resolution.
- Line/column references in this plan are approximate and will have drifted — re-locate with Grep,
  don't trust the numbers given here as ground truth.
- This survey should read code and write the report; it should not run the MCP tools live or modify
  `RoslynSentinel.Tests`.

## Approach
Three sequential steps, each producing a section of the same report:
1. Inventory every tool that resolves a target (symbol, member, type, node) by something other than
   a stable coordinate the caller already possesses opaquely (e.g. `changeId`, `scanId`).
2. For each inventoried tool, categorize its disambiguation method and, separately, its documented
   *and* actual behavior when the resolution query matches more than one candidate.
3. Cross-reference against `ReplaceMember`'s specific gap (name-only, first-match, silent) to find
   every other tool sharing that same shape, ranked by risk (mutating > read-only).

## Key Files
- `RoslynSentinel.Server.Basic/*.cs`, `RoslynSentinel.Server.Advanced/*.cs` — tool wrapper
  definitions (`[McpServerTool]` methods, their `[Description(...)]`, and parameter attributes like
  `[Consumes(DataTag.ContextSnippet)]`, `[Consumes(DataTag.StartLine)]`).
- `RoslynSentinel.Basic/*.cs` (engines: `RefactoringEngine.cs`, `StructuralRefinementEngine.cs`,
  `SymbolNavigationEngine.cs`, `DiscoveryEngine.cs`, etc.) — the actual resolution logic backing each
  tool. The tool wrapper's parameters are necessary but not sufficient signal: a tool can *accept* a
  `contextSnippet` parameter while its backing engine method ignores it in some code path, or accept
  no such parameter at all while still being effectively safe (e.g. it validates uniqueness and
  errors on multiple matches rather than silently picking one).
- `RoslynSentinel.Common/ContextHelper.cs` (if present — check) — shared snippet/line-based
  resolution helper used by several engines; worth understanding once, since multiple tools likely
  delegate to it.
- `docs/plan-symbol-tool-hardening-v1.md` — prior art; Tasks D/E of that plan already added
  `contextSnippet`/`lineBefore`/`lineAfter` disambiguation to `FindReferences`/`FindCallersAsync`/
  `FindImplementationsForMemberAsync`, which is the "good" pattern to measure other tools against.

## Risks & Open Questions
- **Scope calibration.** 119 tools is a lot to hand-review individually. If time-boxing, prioritize
  *mutating* tools (anything that writes to disk or stages a change) over read-only ones — a
  misresolved read is a wrong answer; a misresolved write is silent data loss/corruption. Note in
  the report which tools were reviewed in depth vs. skipped, and why, so the omission is visible
  rather than mistaken for "confirmed safe."
- **"Behavior when ambiguous" requires judgment, not just grep.** A tool might accept a
  disambiguating parameter (contextSnippet, line) but still not actually use it to *break ties* —
  e.g. it could use `contextSnippet` only to build a nicer error message while the actual resolution
  is still first-match. Read the backing engine method's logic, don't infer safety from the tool
  wrapper's parameter list alone.
- **Don't confuse "requires a coordinate" with "safe."** A tool that takes `line`/`column` is only
  as safe as its lookup being coordinate-exact (like `SafeDeleteSymbolAsync`'s `FindNode` at an exact
  `TextSpan`). A tool that takes `line` but then does a name-based re-scan near that line (rather
  than resolving the exact node) could still have ambiguity if multiple matches occur near the given
  line. Check the actual lookup, not just the parameter's presence.
- Out of scope for this plan (deliberately deferred to the follow-up):
  - Any code changes to add disambiguation to `ReplaceMember` or any other tool found to have the
    gap.
  - Deciding *how* to fix each gap (e.g. contextSnippet vs. required line/column vs. erroring on
    ambiguity vs. returning all matches). That's a design decision for the follow-up plan, informed
    by this survey's findings.
  - Re-litigating tools already covered by `plan-symbol-tool-hardening-v1.md` Tasks D/E
    (`FindReferences`, `QuerySymbolRelationships`) — confirm their current state matches what that
    plan implemented, but don't re-review them from scratch.

## Steps

### Step 1 — Inventory tools that resolve a target by name/position
Grep both server projects for `[McpServerTool` to get the current, accurate list (don't trust the
119 count above — it will have drifted). For each tool, read its parameter list and one-line
description, then bucket it into one of:
- **Name-resolving**: takes a symbol/member/type name (e.g. `memberName`, `symbolName`, `typeName`,
  `containerName`) and must find a corresponding declaration or reference site in the workspace.
- **Coordinate-resolving**: takes `filepath` + `line`/`column`, or `filepath` + `contextSnippet` (+
  optional `lineBefore`/`lineAfter`), to pin an exact syntax location.
- **Handle-resolving**: takes an opaque handle from a prior call (`docCommentId`+`projectName` from
  `LocateSymbol`, a `changeId`, a `scanId`) — these are likely already unambiguous by construction,
  but confirm the handle actually maps to exactly one symbol/change rather than being re-resolved by
  name under the hood.
- **Whole-file/whole-solution**: no single-target resolution at all (e.g. `SearchSolutionText`,
  `GetFileOutline`, `FormatDocument`) — out of scope for this survey, note and move on.

Produce a flat list: tool name, file, bucket. This is the raw input for Step 2 — only
name-resolving and coordinate-resolving tools need the deeper look; handle-resolving tools need a
quick confirmation pass; whole-file tools can be listed and dropped.

**Output for this step:** a table (tool name | file | bucket | one-line note) in the report.

### Step 2 — Categorize disambiguation method and ambiguous-match behavior
For every name-resolving and coordinate-resolving tool from Step 1, open its backing engine method
(follow the tool wrapper's call into `RoslynSentinel.Basic/*.cs`) and answer, concretely:

1. **Disambiguation method** — pick the most accurate label, adding new ones if these don't fit:
   - `none` — first-match by name only (the `ReplaceMember`/`GetMemberName` shape).
   - `line/column` — exact syntax node at a coordinate (the `SafeDeleteSymbolAsync` shape).
   - `contextSnippet` (+ optional `lineBefore`/`lineAfter`) — snippet text match, optionally narrowed
     by adjacent line text (the `FindReferences`/`FindCallersAsync` shape from the prior plan).
   - `docCommentId`/handle — resolved via a documentation-comment ID or other opaque handle from a
     prior call.
   - `containingType`/`containingNamespace`/`projectName` filter — narrows candidates by an
     additional scoping parameter, but may still return/act on multiple if the filter itself isn't
     unique.
   - `semantic uniqueness check` — the engine explicitly checks candidate count and errors/warns if
     >1 (rare — flag any tool that already does this, it's the pattern to point other fixes toward).
2. **Ambiguous-match behavior** — concretely, when 2+ candidates match the given resolution
   criteria, what actually happens? Categorize as one of:
   - **Silent first-match** — picks one candidate with no signal anything was ambiguous (the
     `ReplaceMember` defect).
   - **Silent first-match with a documented caveat** — same behavior, but the tool's
     `[Description(...)]` or a code comment says how ties are broken (e.g. "first declaration in
     document order") — still a defect, but at least not surprising if read carefully.
   - **Errors/refuses on ambiguity** — the engine detects >1 candidate and returns an error result
     instead of guessing.
   - **Returns all matches** — the engine returns a list/union rather than picking one (this may be
     correct-by-design for some tools, e.g. a solution-wide search).
   - **Not reachable in practice** — e.g. the tool operates on a scope where duplicates are
     structurally impossible (confirm this claim, don't just assert it).
3. **Evidence** — cite the exact file/line of the resolution logic backing the claim (e.g.
   `RefactoringEngine.cs:1223` — `FirstOrDefault(m => GetMemberName(m) == memberName)`). A category
   without a code citation is not usable by the follow-up plan.

**Output for this step:** one row per tool (tool name | disambiguation method | ambiguous-match
behavior | evidence citation | mutating or read-only) added to the same report, building on Step 1's
table.

### Step 3 — Cross-reference against ReplaceMember's specific gap
`ReplaceMember`'s defect shape is: **name-only resolution + silent first-match + mutating**. Using
Step 2's categorization, produce a ranked list of every other tool matching that same shape
(name-only or `none` disambiguation, silent-first-match behavior, and mutating), most
identically-shaped first. For each, note:
- What it mutates (member body, whole file, multiple files).
- Whether a caller has *any* other way to pin the exact target today (e.g. a `contextSnippet`
  parameter exists but isn't required, an adjacent tool could be used first to disambiguate).
- A rough severity note: could a misresolved match here plausibly corrupt or silently discard code
  the way the `ReplaceMember` scenario could, or is the blast radius smaller (e.g. a read-only report
  that's merely wrong)?

Also explicitly re-confirm `ReplaceMember` itself and `RemoveMemberAsync`'s current disambiguation
state (both use `GetMemberName`-based `FirstOrDefault` per the Background section — confirm this is
still accurate post the `plan-symbol-tool-hardening-v1.md` changes, since `RemoveMember` gained a
tool-level precheck in that plan but the *target resolution itself* was not changed).

**Output for this step:** a ranked table (tool name | mutating? | shape match strength | existing
disambiguation escape hatch (if any) | severity note), plus a short prose summary of the 3-5
highest-priority candidates for the follow-up remediation plan.

## Deliverable
Write the full report as `docs/tool-disambiguation-survey-v1.md` (not another `plan-*.md` — this is
a findings document, not a plan to execute). It should contain, in order: Step 1's inventory table,
Step 2's categorization table, Step 3's ranked cross-reference and summary. End with a short
"Suggested next step" paragraph recommending (but not writing) a follow-up remediation plan scoped to
the highest-severity findings from Step 3 — that follow-up plan is explicitly out of scope here.

## Verification
This is a research task — there is no build/test/commit cycle for code, since no production code
changes. Before finishing:
1. Confirm every categorization claim in Step 2/3 has a file/line citation that actually exists
   (re-Grep each one — don't trust line numbers written earlier in the same session, since editing
   the report itself doesn't shift code line numbers, but a long session might span a concurrent
   edit by someone else).
2. Confirm the tool count and inventory list in Step 1 reflects a fresh `grep -rc "\[McpServerTool"`
   run at the time of finishing, not just the count taken at the start.
3. Commit the report file only (`docs/tool-disambiguation-survey-v1.md`) with a message summarizing
   the top-line finding (e.g. how many tools share `ReplaceMember`'s exact defect shape).
