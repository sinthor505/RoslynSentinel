# TODO — known gaps not yet fixed

Running list of confirmed-but-deferred issues found during tool development/grading. Each entry
should have enough detail to pick back up without re-discovering the root cause. Once an entry is
actually fixed, move it to [CLOSED.md](./CLOSED.md) rather than deleting it outright.

## `Git` tool missing branch/push/checkout/worktree/stash — forces a shell fallback — not started

Raised 2026-09-12 while wiring the dog-fooding enforcement hook
(`.claude/hooks/enforce-dogfood.ps1`).

`Git` currently implements `status`, `log`, `diff`, `stage`/`add`, `commit`, `revert`. The hook
therefore blocks only those six via shell and has to **let everything else through**, because
denying an operation with no MCP equivalent would strand the task with nowhere to go.

Missing, in rough priority order:

- **`branch`** (list / create / delete / show current) and **`checkout`**/`switch` — needed for any
  branch-per-change workflow, and CLAUDE.md's own convention is to branch before committing off the
  default branch.
- **`push`** / **`fetch`** / **`pull`** — remote operations; `push` at minimum, since it's the one
  step that currently always escapes the chokepoint.
- **`worktree`** (add / list / remove) — PlanStepRunner drives worktrees directly, so this is the
  gap with the most existing in-repo usage, and the one place where a wrong path silently produces
  the `Worktree/` diff trap that CLAUDE.md warns about.
- **`stash`** (push / pop / list) and **`tag`** — lower priority, occasional use.
- **`show`** for a single commit, and `diff` between two arbitrary refs (today `target` takes
  `working`/`staged`/a single hash).

Why it matters beyond convenience: each uncovered operation is a permanent, sanctioned hole in the
dog-fooding chokepoint, so those code paths never get exercised and never surface the bugs that
dog-fooding exists to find. It is also the reason the hook's deny list has to be an allow-list of
six strings rather than a blanket `git` match — expanding the tool lets the hook get stricter.

Note the existing open blocker `blockers/blocking_error_git_stage_ignores_untracked_files.md`
against the current `stage` implementation; worth fixing in the same pass.

## `docCommentId` parameter audit across all tools — not started

**Found:** 2026-09-06, see [docCommentId_description_gap.md](./docCommentId_description_gap.md). `RenameSymbol`'s description
doesn't say how to obtain `docCommentId` (via `LocateSymbol`); confirmed as the direct cause of a
model fabricating a placeholder ID and getting rejected before self-correcting.

**Scope (two parts):**
1. Audit every tool parameter named `docCommentId` (`grep -rn docCommentId` across `[Description]`
   attributes/tool method signatures) and add "obtain this via `LocateSymbol`" (or equivalent) to
   each description that's missing it.
2. While auditing, identify every tool that takes `docCommentId` *and* also has other mandatory
   parameters (e.g. `filePath`) — flag these separately, since requiring both a resolved symbol ID
   and a hand-supplied file path is a second place a model can supply mismatched/fabricated values
   (the file path could point somewhere the docCommentId doesn't actually live), and the
   description should make clear which one is authoritative for locating the target.

**Not started** — deferred to its own session per the original memory's plan.

## Future feature: `UsingDirective(operation: add, simplifyAllCallers: true)` — solution-wide simplification

**Found:** 2026-08-19, while reviewing whether `UsingDirective` needed a `simplifySingleFile`/
`simplifyAllCallers` split. `simplifySingleFile` already effectively exists as the current
`simplifyExisting` bool (add-only, runs `Simplifier.ReduceAsync` scoped to just the edited
document) — no new work needed there. `simplifyAllCallers` does not exist and would be new,
larger-scope work, not a boolean flag on the existing method.

**Not `FindReferences` + a loop.** The obvious-looking shortcut — call `FindReferences` to get a
file list, then loop `UsingDirective(simplifySingleFile)` over each — doesn't work: `FindReferences`
resolves references to one specific *symbol* (a method/type/member via `docCommentId`), but this
feature is scoped to a *namespace*, which can contain many independent symbols
(`ContosoOrders.Core.Discounts` might have `DiscountCalculator`, `TaxCalculator`, etc.). There's no
single symbol representing "the namespace" to feed `FindReferences`, running it once per symbol in
the namespace would still miss files that don't reference that particular symbol, and a
`FindReferences` hit doesn't distinguish "referenced via existing using directive" (nothing to
simplify) from "referenced via a fully-qualified name" (the actual target).

**What it would actually need to do (namespace-scoped solution sweep, not symbol-based):**
1. Enumerate every document in the solution — not a filtered subset, since there's no cheap way to
   know in advance which documents contain a fully-qualified reference into the target namespace.
2. Per document: get the semantic model and look for `QualifiedNameSyntax`/
   `MemberAccessExpressionSyntax` nodes whose resolved symbol's containing namespace matches the
   target (a syntax/semantic scan, not a reference lookup) — cheaply skip documents with no such
   node before doing anything else, since most of the solution won't reference the namespace at all.
3. For each document that does have matches: ensure the `using` directive is present (add if
   missing, matching `AddUsingDirectiveAsync`'s existing idempotency check), then run
   `Simplifier.ReduceAsync` scoped to that document — same mechanism `simplifyExisting` already uses
   per-file, just applied across every matching document instead of one.
4. Only report/return documents that actually changed.

**Why not built now:** this changes the tool's blast radius from "one file" to "the whole
solution" — every document touched needs its own using-directive-presence check (not just the one
file the caller named), its own simplify pass, and its own change entry in the result. That's a
meaningfully different feature (a bespoke semantic-model sweep across the whole solution) than the
current single-document flag, and deserves a deliberate design pass (e.g. should it also report
which files it touched? cap how many files it'll touch in one call? require a dry-run first?)
rather than being bolted on as a same-shaped bool.

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

## Mutating tools don't return the resulting content, forcing a separate `ReadFile` to see the outcome

**Found:** 2026-08-19/20, raised by Andrew while reviewing the `Build` tool implementation session.

**What:** per Andrew, mutating tools originally returned the entire new file content on every write,
which bloated agent context (especially on large files) — this was since changed so mutating tools
return only a `changeId`/success flag, not the resulting text. The consequence: an agent that wants
to confirm what its edit actually produced (e.g. see the replaced member's new text, confirm a
generated signature looks right) must make a *second* tool call (`ReadFile`/`GetMethodSource`) to see
it — which defeats the original goal of reducing tool-call count and context bloat, just shifts the
cost from "one bloated response" to "two calls, one of which re-fetches what was just written."
`docs/plan-symbol-tool-hardening-v1.md`'s own review guidance ("flag it if the agent never re-reads
the file/method afterward to confirm... actually look correct") implicitly assumes agents *should* be
re-reading after every write, which is exactly the extra round-trip this behavior forces.

## `RoslynSentinel.Advanced`'s NormalizeWhitespace occurrences never got a follow-up sweep — Basic side now fully closed 2026-08-27; Advanced still open

**Found:** 2026-08-24, while auditing `docs/plan-normalize-whitespace-full-sweep-v1.md` (now filed
`docs/obsolete/`) for the docs reorganization pass. That plan completed a sweep of `RoslynSentinel.Basic`
but explicitly scoped out `RoslynSentinel.Advanced`'s ~64 occurrences of the same whole-file
`NormalizeWhitespace()` pattern as deferred/out-of-scope, and no follow-up plan doc for the Advanced
side exists anywhere in `docs/`.

**Why this matters:** the Basic-side version of this bug caused real line-shift/re-indentation damage
(see the plan doc's own root-cause writeup) before being fixed. If the same call pattern is still
present ~64 times in `RoslynSentinel.Advanced` (not re-verified count-wise in this pass — worth a fresh
grep for `NormalizeWhitespace()` before scoping work), those call sites carry the same latent risk and
have simply not been hit by a repro yet.

**Suggested approach:** grep `RoslynSentinel.Advanced` for `.NormalizeWhitespace()` calls that
re-serialize a whole document (vs. a narrowly-scoped single-node call, which is fine), cross-check each
against the Basic-side fix's shape (targeted formatting vs. whole-tree re-indent), and either confirm
they're already narrow/safe or port the same fix pattern across.

**Correction 2026-08-27 — the Basic-side sweep this entry assumed was "done" had two live misses of
its own,** found while fixing the unrelated "`ConstructorParameter` collapses multi-line signatures"
bug (separate TODO entry). `RoslynSentinel.Basic/RefactoringEngine.cs`'s `AddConstructorParameterAsync`
and `RemoveConstructorParameterAsync` both did `root.ReplaceNode(classDecl, newClassNode)
.NormalizeWhitespace()` — a whole-tree reflow identical to the pattern this entry describes, not a
narrowly-scoped one. Fixed by switching both to the file's own established
`ReplaceNodeFormattedAsync` helper (annotates only the new/replaced node and calls
`Formatter.FormatAsync` scoped to that annotation — already used by `AddMemberAsync`/`AddPropertyAsync`
/`AddFieldAsync` and others in the same file). `SortMembersAsync` (same file, ~line 3458) has the
identical unscoped `.ReplaceNode(...).NormalizeWhitespace()` shape and was NOT fixed in this pass — out
of scope for the `ConstructorParameter` bug, flagged here since it's the same bug class found by the
same audit. **Implication for this entry:** "the Basic sweep is done, only Advanced is unswept" can no
longer be assumed — worth a fresh grep of `RoslynSentinel.Basic` too (not just `RoslynSentinel.Advanced`)
for any other `.ReplaceNode(...).NormalizeWhitespace()`/bare `.NormalizeWhitespace()` call before
scoping the Advanced-side follow-up work, since the "already fixed" premise just proved incomplete once.

**Basic side fully closed 2026-08-27:** a fresh `SearchSolutionText` sweep of all of
`RoslynSentinel.Basic` found 89 raw `.NormalizeWhitespace()` hits (not just the 1 `SortMembersAsync`
miss above), across 17 files. Every hit was individually classified: 55 were the unscoped
whole-root-reflow bug, 34 were legitimate uses (whole-document `SyntaxRewriter.Visit` passes where
full-file reformatting is correct-by-design; small standalone nodes normalized before insertion,
never a reflow of existing content; text built only for display/comparison, never written back; or
brand-new output with the original root untouched). Of the 55 confirmed bugs, 52 were fixed by
switching to `ReplaceNodeFormattedAsync`/`RemoveNodeFormattedAsync` (or a shared-`SyntaxAnnotation` +
`Formatter.FormatAsync` pass for compound/multi-edit methods) — full per-file breakdown in the
`RoslynSentinel.Basic/*.cs` diffs from this date. 3 were deliberately left unfixed, flagged for a
human/architecture decision rather than a mechanical swap:
- `RunMicroRefactoringAsync` (`GranularRefactoringEngine.cs`) — its 5 dispatched helpers return bare
  `SyntaxNode?` with no way to identify which sub-node changed; scoping the fix requires changing
  helper return contracts, not just wrapping the call site.
- `ExtractConstantSafeAsync` and `GenerateToStringSafeAsync` (`MsToolAugmentEngine.cs`) — both
  Document-less (parse from a raw string via `CSharpSyntaxTree.ParseText`/`File.ReadAllTextAsync`,
  no `Document` available for `Formatter.FormatAsync`'s annotation-scoped overload). Would need an
  `AdhocWorkspace`-based formatting variant; `FormatDocumentSafeAsync` in the same file was checked
  as a possible existing precedent and doesn't cover this case.

Verified via `dotnet build RoslynSentinel.slnx` (0 errors, only pre-existing test-project warnings)
and the full test suite across all 5 test projects (0 new failures; every name in
`docs/known-failing-tests.{Basic,Advanced}.txt` still fails/skips the same way, nothing new).
`git diff --ignore-all-space` confirms each file's actual change is exactly the expected
targeted-formatting swap — large raw `git diff` line counts in a few files (e.g. `AnalysisEngine.cs`)
are pure pre-existing LF/CRLF line-ending noise, not scope creep.

**Advanced side (~63-64 occurrences) remains explicitly out of scope** — not started this session
per direct user instruction to do Basic only. Suggested approach section above still applies when
that work is picked up; re-run `SearchSolutionText` fresh rather than trusting the old ~64 count,
since this session's Basic count (89, not the originally-assumed handful) shows raw grep-style
estimates for this pattern have been unreliable so far.

## Read-tool metadata envelope (`isComplete`/truncation flag) — `GetMethodSource`/`GetFileOutline` done 2026-08-27

**Found:** 2026-08-24, while auditing `docs/spec-read-tool-metadata-envelope-v1.md` (kept in
`docs/current/`) for the docs reorganization pass. Zero `isComplete`/`IsTruncated`-style fields exist
anywhere in the codebase — the spec's proposed metadata envelope for read tools (so a caller can tell
whether a returned excerpt is the whole thing or was cut short) has not been started.

**Implemented 2026-08-27** (spec's steps 1–3): added `ReadEnvelope`
(`RoslynSentinel.Common/ReadEnvelope.cs`) — `SchemaVersion`, `LineCount`/`ByteCount` (total file, not
slice), `IsComplete`, `ReturnedFromLine`/`ReturnedToLine`, `ContinuationOffset`, `OutlineAvailable` —
plus `ReadEnvelopeBuilder.Build`/`BuildForWholeFile` and configurable `ReadEnvelopeThresholds`
(`ReadWholeMaxLines`=800, `OutlineAvailableMinLines`=400, `MaxReturnedLines`=1200, matching the spec's
suggested defaults). Wired into:
- `GetMethodSource` — `MethodSourceResult` gained an `Envelope` property; the envelope describes the
  **containing file's** scope while `ReturnedFromLine`/`ReturnedToLine` are the method's own line span
  (per spec, so a caller gets file-level scope even when it only asked for one method).
- `GetFileOutline` — return shape changed from a bare `List<OutlineItem>` to a new `FileOutlineResult
  { Envelope, Symbols }` record (this is a breaking shape change to `Data`; the one existing test
  asserting the old bare-list shape was updated).

**Steps 4–5 resolved 2026-08-27, no code change:**
- Step 4 (configurable thresholds) — decided the existing `ReadEnvelopeThresholds` static settable
  properties (`ReadWholeMaxLines`/`OutlineAvailableMinLines`/`MaxReturnedLines`) already *are* the
  tuning surface; a `read-limits.json`/env-var loader was judged unnecessary since nothing else in the
  repo loads per-feature JSON config at startup, and this matches the existing precedent
  (`ScanResultHelper.ThresholdBytes` is likewise a hardcoded constant, not file/env-configurable).
- Step 5 (fold the contract into "the tools-review doc") — that doc is `docs/obsolete/tool_review_v1.md`
  (where `BatchResultSummary` is documented), already retired to `obsolete/`. No live successor exists
  to fold this into; `tool-terminology-refinement-reference-v1.md` is a different, naming-focused doc,
  not a shared-contract catalog. Decided this TODO.md entry plus `ReadEnvelope.cs`'s own doc comments
  are the durable record — no new doc created.

`Read` File was left unwired: it already has its own startLine/endLine slicing and inline
`filePath/startLine/endLine/totalLines/source` shape; folding it into `ReadEnvelope` would touch three
response shapes (whole-file, ranged, offloaded) and wasn't part of this pass's scope. `MethodNotFound`
also still returns a plain error rather than `MethodFound=false` + populated envelope, since
`GetMethodSource`'s result type has no `MethodFound` field today — a further contract change, not
attempted here.

Verified via `dotnet build RoslynSentinel.slnx` (0 errors) and the full test suite across all 5 test
projects (0 new failures): new `ReadEnvelopeBuilderTests` (8 tests, spec's "BuildEnvelope helper" list)
plus envelope assertions added to `GetMethodSourceTests` and the `GetFileOutline` enum test.

**Remaining work if picked up again:** wire `ReadFile` into the envelope, and decide `MethodNotFound`'s
contract (`MethodFound` field + populated envelope instead of a plain error). Steps 4–5 are closed, not
just deferred.

## Tool terminology/naming backlog — open, unactioned

**Found:** 2026-08-24, while auditing `docs/tool-terminology-refinement-reference-v1.md` (kept in
`docs/current/`) for the docs reorganization pass. That reference catalogs weak/ambiguous tool and
parameter names (`GetBreakerStatus`, `GetMigrationLedger`, `ClearExternalDrift` vs. its sibling
`ListExternalDiskChanges`'s inconsistent metaphor, the `Apply*Codemod`/`Generate`/`Introduce`/`Inline`
untyped-string-discriminator family) and recommends, in order: (1) convert small/stable discriminator
params to real enums; (2) standardize the "call `DescribeAdvancedToolOptions` first" wording across all
wide dispatchers; (3) only then revisit the specific rename table. None of the three steps has been
started as of this pass.

**Suggested approach:** see the reference doc itself for the full weak-term table and confusable-group
list; this entry exists so the backlog is discoverable from `TODO.md` without having to know the
reference doc exists.

## Model tool-choice gap: `ApplyDiff` used instead of `ChangeAccessibility` for `ReplaceBlockFormatted`, costing 67.5% of all ApplyDiff failures — open, unactioned

**Found:** 2026-09-05, via a full-dataset aggregation pass over `ModelTestingResults\113` (995
archived model-eval runs) using the new `Parse-AgentLog.ps1` script (see
`docs/current/reference_parse_agent_log_script.md`). Full writeup:
`docs/current/model_eval_replaceblockformatted_accessibility_cost_2026_09_05.md`.

**What:** across every model-eval test variant that asks the model to reuse
`BlockEditHelpers.ReplaceBlockFormatted` (all of `MinimalGuidance`, `MinimalGuidanceDisambiguated`,
`PlanThenExecute`, `PlanImplementVerify`), the model overwhelmingly edits accessibility via a raw
`ApplyDiff` hunk instead of calling the purpose-built `ChangeAccessibility` tool (already present
in every affected run's tool allowlist — availability isn't the gap). When it sequences the
accessibility change *after* adding the call site instead of before/atomically-with it, the
pre-apply Roslyn diagnostic pass or the post-apply compile guard correctly rejects the edit
(`CS0103`/`CS0122`), forcing a retry. This single sequencing pattern accounts for 382 of 566
(67.5%) of every `ApplyDiff` failure across the entire 995-run historical dataset — the largest
single failure/retry driver of anything currently measured, affecting 206/995 runs, worst in
`PlanImplementVerify` (126/655 of its runs).

**Why this matters:** both compile guards are working as designed (this is not a tool bug) — the
cost is entirely wasted turns and tool-error-budget consumption
(`RoslynSentinel.Tests.ModelEval`'s `AssertWithinBudget`), not unrecovered failures. It's also the
quantified scale-up of an already-recorded qualitative finding
(a hand-reviewed 13-run sample found `ChangeAccessibility` used 1/13 times vs. `ApplyDiff`'s ~8/13
for the identical operation) — this is
the first full-dataset confirmation that the pattern is both real and large, not sample noise.

**Suggested approach (three candidates, none implemented/tested yet, roughly cheapest first):**
1. Add an explicit sequencing hint to the affected fixture prompts ("check/change
   `ReplaceBlockFormatted`'s accessibility before wiring the new call site") and re-run a batch to
   see if it measurably shifts the tool-choice ratio or the CS0103/CS0122 counts down.
2. Strengthen `ChangeAccessibility`'s `[Description]` to more assertively claim the "make X
   internal/public" use case away from `ApplyDiff` — opposite direction from the
   `ModifyModifier`-accessibility-enum fix (that one narrowed a tool's enum to steer *away* from
   it; this would need to steer *toward* one), so the same mechanism may not transfer directly.
3. Enrich the `CS0103`/`CS0122` compile-guard error text to name `ChangeAccessibility` explicitly
   when the underlying cause is fixable by it, rather than leaving the model to infer the fix path
   from a raw Roslyn diagnostic (matches the spirit of the agent-friendly-error-messages
   principle already applied elsewhere in the tool layer).

Not yet decided which lever to pull or A/B tested — this entry exists so the (large, confirmed)
opportunity is discoverable from `TODO.md` rather than only living in the dated doc.

## Feature idea: server-side telemetry/metrics for tool call counts and error rates — open, unactioned

**Found:** 2026-09-05, raised by Andrew while reviewing the `ReplaceBlockFormatted` finding above,
which required an offline post-hoc `Parse-AgentLog.ps1` aggregation pass over archived transcripts
to get per-tool call/error-rate numbers.

**What:** the MCP server itself has no live counters for how often each tool is called, or its
success/error rate — this data currently only exists reconstructible after the fact from
`agent.log`/`transcript.json` files (model-eval only) or not at all (real interactive/production
sessions have no equivalent transcript to mine). A lightweight in-process metrics layer (e.g. a
`ToolCallCount`/`ToolErrorCount` counter per tool name, exposed via a new read-only tool or a
`/metrics`-style endpoint) would give live visibility into tool usage and failure rates across
*any* session — model-eval or a real agent — not just ones that happen to have a saved transcript
to parse afterward.

**Why this matters:** the `ReplaceBlockFormatted` finding above and a separate tool-choice-fidelity
review both needed a bespoke offline analysis pass (grep/parse a log, or hand-review transcripts)
to surface tool-choice and error-rate patterns that live counters would expose immediately and
continuously, for every session, not just archived model-eval batches with saved logs.

**Hook point confirmed 2026-09-05 (not yet implemented):** the MCP call-tool filter chain in
`RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`'s
`AddRoslynSentinelToolsBasic` (`mcpBuilder.WithRequestFilters(...)`, ~line 167-406) is the single
place every tool call already passes through regardless of server flavor (Advanced reuses Basic's
registration — see `project_advanced_extends_basic`), and already has 5 filters following the
exact shape a metrics filter would need: `filters.AddCallToolFilter(next => new
McpRequestHandler<CallToolRequestParams, CallToolResult>(async (context, ct) => { var result =
await next(context, ct); try { /* logic */ } catch (Exception ex) { Debug.WriteLine(...); }
return result; }))`, with `context.Params?.Name` for the tool name and `context.Server.Services?
.GetService<T>()` already the established way to resolve a shared singleton (used today for
`PersistentWorkspaceManager`/`ILogger<T>`).

**Placement matters**: a metrics filter should be registered *after* the "domain-failure →
protocol-error sync" filter (ends ~line 245) — that's the one that sets `result.IsError = true`
for tools that catch their own exceptions and return `Success=false` instead of throwing (see
`feedback_agent_friendly_error_messages`), so counting before it would undercount real failures
the same way raw exception-based `IsError` detection already does. It should also count the
orientation breaker's own pre-check short-circuit (~line 342-350, which returns before ever
calling `next()`) as a real failed invocation of whatever tool was blocked, not skip it.

**Suggested approach:** a `ConcurrentDictionary<string, ToolCallStats>` (call count, error count,
maybe total duration) singleton, registered in DI and updated inside the new filter exactly as
described above, plus a new read-only tool (or extend `GetWorkspaceHealth`/
`GetComprehensiveHealthReport`) to surface current counts. Whether counts should persist across
server restarts, reset per-session, or both is an open design question — not decided here.
See also `docs/current/reference_parse_agent_log_script.md` and
`docs/current/model_eval_replaceblockformatted_accessibility_cost_2026_09_05.md` for the concrete
analysis gap that prompted this idea.


---

## `SearchSolutionText`: run both modes, use `searchMode` to *rank* rather than to *select*

**Status:** proposal only. Deferred out of the run-398 defect sweep (A4) on 2026-09-10.

Today `searchMode` selects which single search runs. A pattern with regex metacharacters passed
under `searchMode: literal` is searched literally as requested — correct per the parameter, but
in practice it returns zero results, and run `20260910-013550-398` burned three turns (17, 18, 25)
on exactly that: the model rewrote the pattern each time rather than changing the mode.

A4 fixed the immediate footgun by making `searchMode` **required**, so an unstated mode can no
longer silently produce the wrong search (see
`feedback_prefer_mandatory_params_to_close_footgun_roundtrips`). That is a strictly smaller change
than the idea below, and does not preclude it.

**The proposal:** run *both* the literal and the regex search, return the union, and use
`searchMode` only to order the results — matches from the requested mode first. A caller who names
the wrong mode then gets the right answer ranked second instead of an empty result set.

**Why this was not bundled into the defect sweep:**

- It changes result *semantics*, not just a parameter's default — every caller's result shape and
  ordering contract shifts.
- Auto-promotion of literal→regex was already tried and deliberately reverted. `BatteryTwentyTests`
  still asserts the no-fallback behaviour explicitly ("explicit literal mode must actually search
  literally, not silently switch to regex"). This proposal is *not* that reverted design — it adds
  results rather than substituting a mode — but the history says this tool warrants its own
  evaluation run rather than a change riding along with unrelated fixes.
- Cost is unmeasured: two passes over every document instead of one, on a tool that already scans
  the whole solution in parallel.

**Open questions:** whether a regex that fails to compile should degrade to literal-only or error;
whether the union should be de-duplicated per (file, line) or per (file, line, column); whether
`maxResults` applies before or after ranking; and whether the response should label each match with
the mode that found it.
