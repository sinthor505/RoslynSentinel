# `Member(remove, skipPrecheck: true)`'s compile-validation refusal no longer includes the CS0535 diagnostic text in `.Message`

**Status: OPEN, not investigated.** Discovered via a routine full-solution `RunTest` during an
unrelated engine-reorganization cleanup session; root cause not traced to source. This doc records
the regression, the evidence for where it sits in the codebase's recent history, and what a future
session needs to check first - it does not claim a confirmed cause.

## What was being attempted

Groups 4/5 of `.claude/plans/enumerated-jumping-babbage.md` (engine-reorganization cleanup): removing
now-dead `_granularRefactoringEngine`, `_refinementEngine`, and `_advancedTypeEngine` private fields
and their constructor-based initializations from `RoslynSentinel.Tests.Battery/BatteryTwentyFourTests.cs`,
following the earlier deletion of `GranularRefactoringEngine.cs`, `RefinementEngine.cs`, and
`AdvancedTypeEngine.cs` (their source engines, folded elsewhere per the plan). This was a mechanical
dead-reference cleanup in an unrelated part of the same test file - not touching the test documented
here, its target code path, or anything in `RoslynSentinel.Advanced/AdvancedStructuralEngine.cs`'s
`Member` implementation.

After the cleanup edits, a routine full-solution `RunTest` was run to confirm no regressions before
committing. Baseline (recorded earlier this session, before this session's edits): 2508 passed, 2623
total, 16 failed. After this session's field-removal edits: 2510 passed, 2626 total, 17 failed - a
net delta of +1 failure (3 new tests appeared alongside the 1 new failure; likely from the affected
Battery/Integration test classes recompiling, not evidence either way about this specific defect).
The one new failure, isolated and re-run standalone (fails identically alone, ruling out test-order
flakiness), is documented below.

## What was NOT the cause - ruled out

This session's own edits are not implicated. Verified via `GetFileOutline`/`Search` before writing
this doc:

- The only changes made to `BatteryTwentyFourTests.cs` this session were deleting 3 dead private
  fields and their constructor-based initializations earlier in the file's test-fixture setup.
- The failing test's own body (lines 923-949, quoted in full below) was never touched, read, or
  written this session prior to `RunTest` surfacing the failure.
- The failing test does not reference `_granularRefactoringEngine`, `_refinementEngine`,
  `_advancedTypeEngine`, or any of `GranularRefactoringEngine`/`RefinementEngine`/`AdvancedTypeEngine`
  at all - it calls `_refactoringStructuralTools.Member(...)`, an unrelated tool-facade field.

This is a genuine pre-existing regression, surfaced (not caused) by this session's incidental
full-solution `RunTest`.

## The failing test (verbatim, `RoslynSentinel.Tests.Battery/BatteryTwentyFourTests.cs:923-949`)

```csharp
[Test]
public async Task RemoveMember_HasImplementationOnly_SkipPrecheckTrue_BypassesToolLevelCheck()
{
    // An interface member's implementation isn't caught by the engine's caller-only
    // SymbolFinder check, so the default (skipPrecheck: false) refusal here can only be coming
    // from the new tool-level precheck. With skipPrecheck: true that precheck is bypassed ->
    // removal still fails, but for a different reason (the general compile-validation safety
    // net catching the now-unimplemented interface member), demonstrating skipPrecheck actually
    // skips the precheck rather than the refusal being a fluke of some other gate.
    SetSource("""
    namespace TestProj;

    public interface IGreeter
    {
        string Greet();
    }

    public class Greeter : IGreeter
    {
        public string Greet() => "hello";
    }
    """, "Greeter.cs");
    var refused = await _refactoringStructuralTools.Member(reason: "test message", "Greeter.cs", MemberAction.remove, memberName: "Greet");
    Assert.That(refused.IsSuccess, Is.False, "An interface member's implementation must be caught by the default precheck.");
    Assert.That(refused.ErrorData!.Message, Does.Contain("implementation"), "Default refusal must come from the tool-level precheck, listing the implementation.");
    var result = await _refactoringStructuralTools.Member(reason: "test message", "Greeter.cs", MemberAction.remove, memberName: "Greet", skipPrecheck: true);
    Assert.That(result.IsSuccess, Is.False, "Removing an interface's sole implementation still breaks compilation - the separate compile-validation safety net catches it.");
    Assert.That(result.ErrorData!.Message, Does.Contain("does not implement interface member"), "With skipPrecheck: true, the refusal reason must shift from the precheck to compile validation, proving the precheck itself was actually skipped.");
}
```

## The exact failure (verbatim from `RunTest` output)

```
  With skipPrecheck: true, the refusal reason must shift from the precheck to compile validation, proving the precheck itself was actually skipped.
Assert.That(result.ErrorData!.Message, Does.Contain("does not implement interface member"))
  Expected: String containing "does not implement interface member"
  But was:  "Member: the change was valid and matched its target(s), but introduces new compiler errors - change not applied. Fix the issue(s) below and retry."
```

Only the second assertion fails. The first assertion (default `skipPrecheck: false` path, expecting
`.Message` to contain `"implementation"`) passes without incident - the tool-level precheck's own
refusal wording is unaffected. Only the compile-validation refusal's `.Message` content has changed
shape: it now reads as a generic wrapper sentence with no compiler-diagnostic text embedded, where it
previously (per the test's own intent and the fact that this exact assertion shipped alongside the
4th dispatch-table-drift fix, see "Related" below) contained the raw `CS0535` diagnostic text
(`"... does not implement interface member ..."`).

## Where it happened

- Test: `RoslynSentinel.Tests.Battery.BatteryTwentyFourTests.RemoveMember_HasImplementationOnly_SkipPrecheckTrue_BypassesToolLevelCheck`,
  `RoslynSentinel.Tests.Battery/BatteryTwentyFourTests.cs:923-949` (test body only - not touched this
  session; see "What was NOT the cause" above).
- Surfacing tool call: `_refactoringStructuralTools.Member(reason: ..., "Greeter.cs",
  MemberAction.remove, memberName: "Greet", skipPrecheck: true)`, second call in the test.
- Not yet traced: the actual `Member`/compile-validation implementation responsible for constructing
  `result.ErrorData.Message` in this path. Per this doc's scope (see header), the `Member` tool's
  source was deliberately not read this session - left for the next investigator.

## Root cause - NOT CONFIRMED, hypothesis only

This session did not read the `Member` tool implementation or `ResultError` construction, per
instruction to document the symptom only. The following is a plausible connection based on recent
git history visible at session start, offered as a starting hypothesis for the next investigator to
verify or refute against actual source - not a confirmed cause:

Recent commits (per `git log`, visible in this session's starting context) reworked how structured
error detail is carried on `ResultError`:

- `1be77cf` - "Updated ApplyUnifiedDiff to return structured content for error data"
- `a1c8372` - "Add typed offload for large ResultError.StructuredDetail"
- `abfa5db` - "Updated validation failure message to include the detailed errors as Structured
  Content"

It is plausible that one of these (most likely `abfa5db`, given its description explicitly mentions
moving "detailed errors" into "Structured Content") changed the compile-validation failure path used
by `Member(remove)` to move the specific CS#### diagnostic text out of `ResultError.Message` and into
a separate structured-detail field (e.g. `StructuredDetail`), while leaving `.Message` as a shorter
generic wrapper sentence - matching exactly what is observed here. If so, this specific test assertion
(`Does.Contain("does not implement interface member")` against `.Message`) was not updated to either
(a) read the new structured-detail field instead, or (b) accept the new wrapper wording.

This is a hypothesis, not a finding. It has not been verified against the actual `Member` tool
implementation, the actual `ResultError`/`StructuredDetail` construction, or the actual diff of any
of the three cited commits. The next investigator should:

1. Read the `Member(remove)` compile-validation failure branch (likely in
   `RoslynSentinel.Advanced/AdvancedStructuralEngine.cs` or wherever `ValidationFailed` /
   compile-validation results are constructed for this tool) to find where `.Message` is set for this
   exact refusal path.
2. Read the diff of `abfa5db` (and `1be77cf`/`a1c8372` if needed) to confirm whether it touched this
   exact code path, or a different one (e.g. only `ApplyUnifiedDiff`'s own error path, which would
   rule this hypothesis out entirely).
3. Confirm whether the CS0535 diagnostic text is still present anywhere in `result.ErrorData` (e.g.
   in a `StructuredDetail`/similar field) or has been dropped entirely - these have different fixes
   (update the test to read the new field, vs. a genuine information-loss regression in the tool).

## Also flag - possible relation to a known recurring pattern, not confirmed

This is the same test (`RemoveMember_HasImplementationOnly_SkipPrecheckTrue_BypassesToolLevelCheck`)
used as the regression guard for a previously-fixed, unrelated defect: the "Member(remove) dispatch-
table drift pattern" documented in memory (`project_member_remove_dispatch_table_drift_pattern.md`),
4th incident, fixed via an `excludeInterfaceMembers` parameter, commit `f4d2d24` (2026-09-25). That
incident's fix note explicitly records that this exact test was the one that caught an unsafe
standalone attempt at the interface-exclusion-filter fix, which is why this test exists at all.

Two possibilities, evidence does not yet distinguish them:

- **5th incident in the same family** - a new dispatch-table/lookup-path drift, coincidentally
  surfacing through the same regression test that caught the 4th incident.
- **Unrelated message-format regression** - the precheck/dispatch-table logic this test was written
  to guard is untouched (the first assertion, which depends on that same logic, still passes), and
  this is instead a downstream `.Message`-construction change in the separate compile-validation
  safety net, per the git-history hypothesis above.

The fact that the first assertion (precheck path) still passes is weak evidence toward the second
possibility (message-format regression) over the first (dispatch-table drift), since a dispatch-table
regression would be expected to also risk the precheck's own member-resolution logic. This is not
proof either way - flagged as-is, not resolved.

## What unblocks it

- Read the `Member(remove)` compile-validation failure branch's `.Message`-construction code to find
  the exact `file:line` responsible, per the numbered steps above.
- Read the actual diff of commits `abfa5db`, `a1c8372`, `1be77cf` to confirm or rule out the
  structured-detail-offload hypothesis.
- Once the cause is confirmed, either: (a) fix the tool to keep the compiler-diagnostic text in
  `.Message` for this refusal path, if the move to structured detail was unintentional for this case,
  or (b) update this test's second assertion to check the correct field/location if the move was
  intentional and the test simply wasn't migrated.
- Until fixed or explicitly written off, this failure blocks full confidence in `RunTest`-based
  full-solution regression verification: the pre-existing baseline for this repo was 16 failures
  (2508/2623 passed); it is now 17 (2510/2626 passed). Any future full-solution `RunTest` comparison
  should expect 17 as the current baseline failure count, with this specific test as the one new,
  named, reproduced-standalone addition - not a fresh unexplained number to re-investigate from
  scratch.

## Related

- `docs/current/blockers/resolved/` and memory `project_member_remove_dispatch_table_drift_pattern.md`
  - the 4th incident (`excludeInterfaceMembers`, commit `f4d2d24`) that this exact test was written to
    guard; see "Also flag" above for why this may or may not be a 5th incident in the same family.
- `.claude/plans/enumerated-jumping-babbage.md` - groups 4/5 engine-reorganization cleanup, the
  unrelated task in progress when this regression was discovered via routine `RunTest`.
- Commits `1be77cf`, `a1c8372`, `abfa5db` - the hypothesis-only candidate causes; not yet diffed or
  confirmed against this code path.
- `RoslynSentinel.Tests.Battery/BatteryTwentyFourTests.cs:923-949` - the failing test, verbatim above.
