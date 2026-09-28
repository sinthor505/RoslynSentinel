# `MoveMember`'s `"new"` call-site fixup keyword silently builds a zero-argument constructor call, ignoring the target class's actual constructor signature

**Status:** FIXED (2026-09-28). See "Resolution" directly below; the original incident writeup follows
unchanged for the record.

## Resolution

What changed:

- **`"new"` zero-arg contract enforced up front (proposal #3's pre-flight check).**
  `AdvancedStructuralEngine.MoveInstanceMembersAsync` now calls `EnsureNewFixupsAreConstructibleAsync`
  before building any change set. If any matched fixup value is `"new"` and the target type has no
  accessible (public/internal/protected internal) constructor callable with zero arguments (all
  parameters optional/params counts as callable), or the target is abstract/static, the call is
  refused with `InvalidArgument` and "No changes were made." The message names how many fixup keys
  used `"new"` and the first few keys, lists the target's actual constructor signatures (e.g.
  `AntiPatternEngine(IWorkspaceManager workspaceManager, SentinelConfiguration config = null)`), and
  says to pass a full receiver expression supplying ALL arguments, configuration included. A
  synthesized new target class (which gets an implicit parameterless ctor) is exempt. The
  `callSiteFixups` `[Description]` now states the zero-argument-only contract and that it is refused
  up front.
- **Compile-gate rejections surfaced properly (proposal #4 plus attribution).**
  - Validation rejections from `ValidateAndApplyHelper` now carry a dedicated
    `ToolErrorCode.ValidationFailed` instead of the generic `Exception` catch-all (the expected,
    recoverable "your change would not compile" outcome, distinct from `InvalidArgument`,
    `BuildFailed`, and real exceptions - per `proposal_tool_error_code_taxonomy.md`'s "one code per real
    failure mode").
  - `CompilerErrorLookupHelper` groups identical diagnostics (same id + message) into one line with a
    capped location list, so 86 identical CS7036s read as one cause, N sites.
  - When `MoveMember` was called with `callSiteFixups` and the compile gate rejects, the raw-error path
    prepends "N error(s) are on lines rewritten by your callSiteFixups value(s) '<value>' - the fixup
    expression itself is likely the cause". Fixup-rewritten nodes are tracked by syntax annotation so
    the attribution uses post-normalization line numbers. `MoveMemberResult.AppliedFixups` now reports
    each applied fixup (file, final line, key as passed, value).
- **Bulk fixup keys.** Keys may now be `"FilePath:Line"`, `"FilePath:*"` (every unresolved site in that
  file) or `"*"` (every unresolved site). Precedence: exact line > file wildcard > global wildcard.
  Only non-Valid rows are matched. File paths are matched case-insensitively after resolving relative
  paths against the solution root and `Path.GetFullPath`. Wildcard-matched rows count as resolved
  (no ledger entry). `"new"` via a wildcard goes through the same up-front check. Malformed keys and
  empty values are refused with `InvalidArgument` before anything runs. The 86-entry dictionary in
  this incident would now be a single `{"*": "<expr>"}`.
- **`NoCandidateIntroducible` rows carry a `SuggestedFix`** ("Add a field of type T to C (initialize it
  where the current receiver 'x' is initialized, with the same constructor arguments), then retry
  MoveMember - the call site will then auto-resolve."), included in the `UnresolvedCallSites` detail.
- Minor: the "no solution loaded" message mentions `.slnx`; a stale `SearchSolutionText` reference in a
  no-matches message now names `Search`.

Tests: `RoslynSentinel.Tests.Battery/PreviewInstanceMoveCallSitesTests.cs` (new-refusal with signature,
new-against-parameterless-target, global wildcard, exact > file > global precedence with a relative
upper-cased file key, SuggestedFix on NoCandidateIntroducible) and
`RoslynSentinel.Tests/CompilerErrorLookupHelperTests.cs` (identical-diagnostic grouping).

### Attempt 2's workaround is NOT a correct fix

`new AntiPatternEngine(_workspaceManager)` compiles, but each affected test class constructs its
engine with a test-specific `SentinelConfiguration`; the one-argument form silently falls back to the
default configuration, so the moved calls would run against a different config than the test set up.
"Compiles" is not "equivalent". Callers must either pass the full argument list (including the
configuration) or, better, introduce a field of the target type initialized alongside the existing
receiver (the new `SuggestedFix`) so the call sites auto-resolve.

### Proposal dispositions

1. **Rejected - constructor-argument inference for `"new"`.** Same silent-drop hazard as above: an
   inferred `new AntiPatternEngine(_workspaceManager)` would compile and quietly lose the
   `SentinelConfiguration`. It is also explicitly ruled out by
   `docs/current/proposal_movemember_instance_callsite_resolution.md` section 2 ("zero-argument
   constructor only ... rather than attempting to infer constructor arguments from the enclosing
   scope").
2. **Not done - removing the `"new"` shorthand.** It remains valid for genuinely parameterless targets
   and is now guarded up front, so the footgun is closed without removing the shorthand.
3. **Done** - pre-flight refusal naming the keys, the value, and the real constructor signatures.
4. **Done** - identical diagnostics grouped, plus fixup-line attribution and the `ValidationFailed` code.

### Unverified

- The claim below that CS7036 also appeared on non-key lines (`IntegrationThirtyFourTests.cs:302, 314,
  422`) was not re-verified during the fix; the explanation offered for it is a hypothesis.
- The fixup-line attribution note is applied only on the raw compile-gate fallback path; when
  unresolved rows remain, the `UnresolvedCallSites` responses take precedence.

---

## Original incident writeup

Root cause traced to source (see below). Working tree confirmed clean/untouched by
this investigation - both attempts below were `dryRun: true`, and `Git(status)` after the second
attempt shows only the pre-existing in-progress diff from earlier in this session (the two
`UsingDirective` additions to `AntiPatternEngine.cs`), no partial edits from either `MoveMember` call.

## What was being attempted

Continuing the engine-reorg plan's group 3 (dissolve `AnalysisEngine`), moving its remaining 30
members into `AntiPatternEngine`. An initial no-fixups dry run had already established 86 call
sites across 12 test files could not be auto-rewritten (`UnresolvedCallSites`,
`NoCandidateIntroducible` on each - none of the affected classes have a pre-existing
`_antiPatternEngine` field usable as a receiver, contrary to a stale prior-session memory note that
claimed most of them did).

**Attempt 1** - the exact call:

```
MoveMember(
  reason: "retry moving remaining AnalysisEngine detector methods into AntiPatternEngine now that usings are fixed",
  filepath: "RoslynSentinel.Advanced/AnalysisEngine.cs",
  className: "AnalysisEngine",
  targetClassName: "AntiPatternEngine",
  memberNames: ["GetTargetDocumentsAsync", "FindLargeTypesAsync", "FindLargeMethodsAsync",
    "FindDuplicateMethodsAsync", "FindInterfaceExtractionCandidatesAsync",
    "DetectLongParameterListsAsync", "DetectUnreachableCodeAsync", "HasCycle",
    "GenerateCallTreeAsync", "BuildCallTree", "GenerateEqualityOverridesAsync",
    "IsCollectionType", "GetElementType", "AnalyzeSemaphoreUsageAsync",
    "FindPossibleInfiniteLoopsAsync", "HasExitStatement", "DetectMismatchedAwaitAsync",
    "HasJustifyingComment", "IsTestFile", "CheckForEmptyCatchBlocksAsync",
    "FindLargeSwitchStatementsAsync", "CheckForRedundantCastAsync", "DetectReflectionUsageAsync",
    "FindPossibleDeadlocksAsync", "UnboundedCollectionTypes",
    "FindUnboundedStaticCollectionsAsync", "FindUnboundedRecursionAsync",
    "FindMisboundOverloadChainsAsync", "FindMissingGenericConstraintsAsync",
    "ComputeStructuralHash"],
  callSiteFixups: { <all 86 "FilePath:Line" keys, each value the literal string> "new" },
  dryRun: true
)
```

All 86 keys used the exact same value: the bare string `"new"`, chosen because the tool's own
documentation (per this session's task instructions, echoing the tool description) describes
`callSiteFixups` values as "a receiver expression or `'new'`" - a two-option contract that reads as
"either write a C# expression yourself, or say the word `new` and the tool will construct the target
type correctly for you."

**Result - verbatim (truncated only where noted):**

```
isSuccess: false
errorCode: "Exception"
message: "MoveMember: the change was valid and matched its target(s), but introduces new compiler
errors - change not applied. Fix the issue(s) below and retry:
CS7036 at C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tests.Integration\IntegrationThirtyFourTests.cs:302:
There is no argument given that corresponds to the required parameter 'workspaceManager' of
'AntiPatternEngine.AntiPatternEngine(IWorkspaceManager, SentinelConfiguration)'
CS7036 at ...BatteryThirteenTests.cs:32: There is no argument given that corresponds to the required
parameter 'workspaceManager' of 'AntiPatternEngine.AntiPatternEngine(IWorkspaceManager,
SentinelConfiguration)'
[... same CS7036, same message, repeated at every one of the 86 fixup sites plus several downstream
lines the fixup line's containing method also references (e.g. IntegrationThirtyFourTests.cs:302,
314, 422 - none of which were themselves fixup keys; these are other call sites in the same method
bodies whose enclosing construct also needed the now-differently-shaped local) ...]"
```

Every single line of this error is the identical `CS7036` message, differing only in file:line. Zero
disk changes were made (dry-run + the tool's own atomic-rollback contract).

## Root cause - traced to source, confirmed

`RoslynSentinel.Advanced/AdvancedStructuralEngine.cs`, inside `MoveInstanceMembersAsync`, in the
loop that rewrites resolved call sites:

```csharp
var newReceiver = receiverExpr == "new"
    ? (ExpressionSyntax)SyntaxFactory.ObjectCreationExpression(SyntaxFactory.IdentifierName(targetClassName)).WithArgumentList(SyntaxFactory.ArgumentList())
    : SyntaxFactory.ParseExpression(receiverExpr);
```

When `receiverExpr == "new"`, the tool builds `SyntaxFactory.ArgumentList()` - an explicitly **empty**
argument list - unconditionally. It never inspects `targetClassName`'s actual constructor via the
semantic model to know it requires an `IWorkspaceManager` (and optionally a `SentinelConfiguration`).
The literal keyword `"new"` therefore only ever produces `new AntiPatternEngine()`, which is a
parameterless-constructor call. Since `AntiPatternEngine`'s only constructor is
`AntiPatternEngine(IWorkspaceManager workspaceManager, SentinelConfiguration config = null)` (no
parameterless overload exists), this is guaranteed to fail with CS7036 for *any* target class that
does not happen to have a public parameterless constructor - which is essentially every engine class
in this codebase, since they are all constructor-injected. In other words, the `"new"` shorthand is
only correct for target types with a parameterless constructor, and nothing in the tool's contract,
schema, or error output says so.

**Attempt 2** confirmed the actual fix: supplying the full expression string
`"new AntiPatternEngine(_workspaceManager)"` as the fixup value (still an in-scope local, since
every affected test class has a `_workspaceManager` field) for all 86 keys succeeded on the
`SyntaxFactory.ParseExpression(receiverExpr)` branch - the dry run returned `isSuccess: true` with a
full `changedContent` preview showing every fixup site correctly rewritten, and no new compiler
errors. This is not filed as a fix here per the coordinator's explicit instruction to stop before
re-attempting the real (non-dry-run) move; it is reported so the next session does not have to
rediscover it. (Resolution note: this compiles but is NOT behavior-preserving - see "Attempt 2's
workaround is NOT a correct fix" above.)

## Did the error message enable recovery?

Partially, and only by inference, not by design. `CS7036`'s message text
("no argument given that corresponds to the required parameter `workspaceManager`") does name the
missing parameter and the constructor signature - so a careful reader can work out that `"new"`
produced a 0-arg call and that supplying `_workspaceManager` explicitly would fix it. But:

- The error is attributed to the **compiler**, not to `MoveMember` itself - nothing in the message
  says "your `callSiteFixups` value `'new'` was the cause" or "the `'new'` keyword does not carry
  constructor arguments." A caller has to already know how `"new"` is implemented (i.e., read this
  repo's source, as this investigation did) to connect the CS7036 back to the fixup value rather than
  to, say, a genuinely missing DI registration.
- 86 near-identical CS7036 lines were returned for a single root cause. There is no de-duplication or
  "these all share one cause" grouping - a model would plausibly spend many turns treating each line
  as a separate problem before recognizing the pattern.
- Recovery required a full retry with a completely different (longer, handwritten) value for every
  key - the tool does not offer, e.g., "did you mean `new AntiPatternEngine(_workspaceManager)`?" even
  though it has the target constructor's signature in hand at validation time (it's literally in the
  CS7036 text).

Turns to recovery in this session: 2 real `MoveMember` calls (both dry-run, so zero-risk, but still 2
full round trips through construction of an 86-entry fixup dictionary each time) plus a source read to
find the actual mechanism - not a "the model should have known" situation; the schema/description gave
no way to predict this without either already knowing the source or guessing-and-checking.

## Is this a genuine tool defect, or working-as-designed friction?

**This is a genuine tool defect in the `"new"` shorthand, not normal atomic-rollback friction.** The
atomic-rollback behavior itself (validate fully, reject with zero disk writes if the result would not
compile) is working exactly as intended and is not the complaint here - that part is good design and
is explicitly called out elsewhere as expected, safe behavior. The defect is narrower and specific:
the `"new"` convenience keyword's implementation silently assumes a parameterless target constructor
with no check, no schema constraint, and no error-message hint that this is what `"new"` means. That
assumption fails for effectively every constructor-injected class in this codebase.

## What would resolve this (concrete proposals, in preference order)

(Dispositions are recorded under "Proposal dispositions" in the Resolution section above.)

1. **Make `"new"` construct the target's actual required constructor call**, using the semantic model
   (already available at this point in `MoveInstanceMembersAsync`/`PreviewInstanceMoveCallSitesAsync`,
   which already resolves `targetClassName` to a symbol to search for in-scope candidates) to look up
   `targetClassName`'s constructor and synthesize arguments from same-named/same-typed fields or
   parameters in scope at the call site, the same way `PreviewInstanceMoveCallSitesAsync`'s existing
   `candidatesInScope` search already finds usable receivers for the non-fixup path. If a matching
   in-scope value can't be found for a required parameter, that is exactly the right moment to fail
   with a specific, actionable message (see #3) rather than silently emitting an empty argument list
   that is deferred to a downstream, less-specific CS7036.
2. **If (1) is impractical, remove the `"new"` shorthand from the schema/description entirely** and
   require callers to always supply a full expression (`"new AntiPatternEngine(_workspaceManager)"`).
   This is the "prefer mandatory params to close footgun round-trips" principle already applied
   elsewhere in this codebase (see memory `feedback_prefer_mandatory_params_to_close_footgun_roundtrips`) -
   an optional shorthand meant to save typing is instead relocating a failure into a generic compiler
   error two calls later.
3. **At minimum, if `"new"` keeps its current zero-arg behavior**, the tool's own pre-flight check
   (before returning the CS7036-from-compiler path) should recognize that `targetClassName` has no
   parameterless constructor and refuse with a `MoveMember`-specific error naming the fixup key,
   the literal `"new"` value that caused it, and the exact required-constructor expression to use
   instead (which it already knows, since it's what CS7036's own text contains) - turning an N-line
   generic compiler dump into one targeted, mechanically-generated suggestion per unique cause.
4. **De-duplicate repeated identical CS7036s in the result** regardless of which of the above is
   chosen, so a caller sees "1 root cause, 86 sites" instead of 86 undifferentiated lines - this is a
   general error-surfacing improvement independent of the `"new"` fix itself.

## Related

- `docs/current/blockers/resolved/blocking_error_constructorparameter_add_no_default_value.md` -
  same family of "the tool didn't know the target's constructor shape" gap, previously fixed for
  `ConstructorParameter(add)` by adding `defaultValue`/`nullDefault` params; this is the analogous gap
  in `MoveMember`'s fixup path.
- Memory `feedback_prefer_mandatory_params_to_close_footgun_roundtrips` - directly applicable: the
  `"new"` optional shorthand is exactly the kind of footgun round-trip that memory describes.
- `project_engine_reorg_group3_in_progress` memory doc - the group-3 task this friction was
  encountered while continuing; that memory's claim that only ~4-5 of 86 sites needed real fixups
  (the rest allegedly already using `_antiPatternEngine`) was independently found to be inaccurate
  this session (all 86 sites use `_analysisEngine`/`_engine`/local `engine`, none use
  `_antiPatternEngine`) - noted here for completeness, not itself part of this tool-defect report.
