
Grew out of investigating [member_replace_drops_leading_blank_line_and_verify_gap.md](./member_replace_drops_leading_blank_line_and_verify_gap.md):
`WholeFileRewriteAgentTests.cs:528-533` failed a run over a lost blank line even though the
model's actual fix was correct — a false-failure signature the user wants closed across all
ModelEval fixtures, not just this one. Confirmed via subagent research
(`WholeFileRewriteAgentTests.cs`, `OrderPricingRefactorAgentTests.cs`,
`OrderPricingRefactorChainAgentTests.cs`, `PlanImplementVerifyAgentTests.cs`) that:
- `FunctionalFixVerifier` (added commit a335324) already
  builds + reflection-invokes the **modified** method and checks real output — this part is
  correct today and should NOT change.
- The brittle part is the **"unrelated code unchanged"** checks: `WholeFileRewriteAgentTests.cs`
  uses exact-substring `Does.Contain` against a hardcoded oddly-spaced string (fails on any
  trivia change, including legitimate ones); `OrderPricingRefactorAgentTests.cs`/
  `Chain*.cs` use a looser `CollapseWhitespace` regex hack for the same purpose. No shared
  helper exists for this check — `SyntaxFactory.AreEquivalent` has zero usages anywhere in the
  repo (would be a first).
- No golden/baseline files exist for any fixture — expected content is reconstructed as C#
  string constants (`WholeFileRewriteReproducer`, `OrderPricingRefactorReproducer`).

**User's decided direction (2026-09-07), supersedes the AreEquivalent-only sketch below it**:
going forward, ModelEval fixtures should test both (1) modified code still functions correctly
and (2) unrelated code remains unmodified — via **real per-fixture test projects with
pre-existing tests**, run post-edit, rather than reflection-invoke or text-matching. This is more
realistic (mirrors how the tool would actually be used against a repo with its own test suite)
and sidesteps brittleness entirely for anything with test coverage.

**Architecture**:
- Each fixture target (`ContosoOrders.Core`, the OrderPricingRefactor ladder's calculator
  project) gets a sibling test project (e.g. `ContosoOrders.Core.Tests`) materialized into the
  scratch solution the same way `WholeFileRewriteReproducer`/`OrderPricingRefactorReproducer`
  materialize source files today (string constants written to disk, not physical golden files).
- Two test classes per fixture: `ModifiedMemberTests` (behavior contract of the method(s) the
  model is expected to change — replaces `FunctionalFixVerifier`'s manual reflection-invoke with
  a real xunit/NUnit test) and `UnrelatedMemberTests` (behavior of everything the model should
  NOT touch).
- **Move/rename-shaped fixtures** (the OrderPricingRefactor ladder, `MoveMember`/`RenameSymbol`
  targets generally): test against a stable "front door" method — an outer method, itself not
  touched by the fix, that calls through to whatever got moved/renamed/extracted — rather than
  the moved/renamed symbol directly. This makes the test refactor-proof (asserts the caller-
  visible behavior is preserved, indifferent to where the implementation now lives) without
  needing test-file updates every time a fixture's expected refactor changes symbol names. The
  front-door method must be a genuine third party the model isn't asked to touch — using the
  renamed/moved method itself as its own front door doesn't work.
- **Assertion flow** replacing e.g. `WholeFileRewriteAgentTests.cs:528-570`: (1) build still fails
  fast on compile errors, unchanged; (2) run the fixture's own test project (prefer direct
  `dotnet test` over routing through the agent's own `RunTest` MCP tool, so a bug in `RunTest`
  can't mask a real fixture failure — the agent already exercised `RunTest`/`Build` live during
  its own run, no need to re-prove that tool works via the assertion layer too); assert zero
  failures AND that total test count is unchanged (closes the loophole where a model "fixes" a
  failing test by deleting/`[Ignore]`-ing it instead of fixing the code — same failure class as the
  directive-error-message and reason-param-enforcement wiggle-room lessons from `TODO.md`'s
  closed-history); (3) `SyntaxFactory.AreEquivalent(node1, node2, topLevel: false)`
  (trivia-ignoring) as a **fallback only**, for any member that ends up with no dedicated test —
  defense in depth, not the primary signal once a test exists.
- `AgentToolErrorAssertions.AssertWithinBudget` (tool-error-count check) is orthogonal, stays
  unchanged.
- **Migration order**: `WholeFileRewriteAgentTests`/`PlanImplementVerifyAgentTests` (shared via
  `AssertFixApplied`) first — it's the fixture with tonight's actual false-failure evidence and
  is edit-in-place (no front-door indirection needed, `UnrelatedMethodBefore`/`After` are already
  stable leaf methods). `OrderPricingRefactorAgentTests`/`Chain*.cs` second, since those need the
  front-door pattern; their existing `CollapseWhitespace` checks get deleted once a passing
  front-door behavior test exists, since that's strictly better evidence.

**Also decided, separate from the test-suite redesign**: the tool-inserted attribution comment
(mentioned by the user, marks tool-vs-model-generated code — see
[ideas/tool_attribution_idea.md](./ideas/tool_attribution_idea.md), still unimplemented as of that
doc) should be worded into the ModelEval system prompt as expected/correct and not something to
remove during cleanup. And the "byte-for-byte unchanged" formatting-strictness in general should
relax: user's reasoning is that over-constraining prompts causes models to omit/overcorrect
(consistent with the directive-error-message wiggle-room lesson from `TODO.md`), formatting
differences don't affect
correctness and are addressable by any formatter, and scoring formatting nits as failures
produces the wrong conclusions about what these models are actually capable of. The per-fixture
test-suite redesign above is the concrete mechanism for this relaxation — once "unrelated code"
is tested by behavior rather than text, formatting drift stops being a scoring criterion at all.

**Status**: design only, not implemented. User is fixing the `Member(operation:replace)`
blank-line-drop bug in a separate session first; this redesign + the prompt wording changes are
tracked as follow-up work, likely also a separate implementation session.
