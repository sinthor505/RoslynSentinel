---
name: project_cs0122_fix_confirmed_2run_batch
description: "2-run PlanImplementVerify batch after the CS0122 lookup-helper fix (commit d4397b3): 1/2 pass; CS0122 hint confirmed working exactly as designed, but a distinct CS0138 (using-a-type-as-namespace) gap surfaced as the new bottleneck"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-01T09:00:28.571Z
---

## Setup

Per the user's request, `errorTools.Count` threshold raised from `<= 1` to `<= 2` in
`AssertFixApplied` (commit `996a43e`), and a fresh 2-repeat `PlanImplementVerify` batch run against
`.113` to check whether [[project_cs0122_lookup_helper_proposal]] (implemented outside this session
as commit `d4397b3`) improved on [[project_planimplementverify_5run_result_2]]'s 0/5.

## Result: 1/2 pass

**Run 2 — PASS.** Turn 2: model tried `ReplaceBlockFormatted(...)` unqualified → CS0103 (1 error).
Turn 3: correctly qualified as `BlockEditHelpers.ReplaceBlockFormatted(...)` → succeeded. Ordinary
"forgot to qualify" case, exactly what the existing CS0103 hint is built for. No CS0122 even fired
in this run — the method's accessibility already matched what the caller needed.

**Run 1 — FAIL** (still failed the raised `errorTools.Count <= 2` threshold — 3 errors occurred).
Sequence (transcript `20260901-084810-345`):
- Turn 2: unqualified call → CS0103, same as run 2.
- Turn 4: model tried `using BlockEditHelpers;`, treating the type as if it were a namespace →
  **new** error, CS0138 (`A 'using namespace' directive can only be applied to namespaces;
  'BlockEditHelpers' is a type not a namespace. Consider a 'using static' directive instead`) plus
  a repeated CS0103.
- Turn 5: model switched to `using static` → hit **CS0122**, with the new guidance text live and
  correctly shaped: `'BlockEditHelpers.ReplaceBlockFormatted(...)' is currently private. It must be
  changed to a level accessible from BlockConverter (...). Note: accessibility is set per-member,
  not inherited from the containing type's accessibility — raising BlockEditHelpers's own
  accessibility does not change ReplaceBlockFormatted's.`
- Turn 6: model reasoning: "The `using static` approach doesn't work because `ReplaceBlockFormatted`
  is still `private`. I need to change it to `internal`..." — read and acted on the new guidance
  correctly, on the very next turn. `ApplyDiff` succeeded.
- Turns 7-8: build succeeds, done.

## What this confirms

**The CS0122 fix works exactly as designed.** No repeat of the original run-1/run-2 member-vs-
container confusion loop from [[project_planimplementverify_5run_result_2]] (previously a 3-attempt
flail; now resolved in one shot the moment CS0122 fired). Directly validates the design's core
hypothesis: stating current accessibility + the member-vs-container note is sufficient corrective
information.

**What it doesn't fix — a distinct, adjacent gap.** This run's extra failure happened *before* the
model ever reached the accessibility question: an earlier "how do I even bring this symbol into
scope" exploration. The CS0103 hint's existing text ("qualify the call as ClassName.{name}(...)")
apparently wasn't strong enough to stop the model from also trying `using BlockEditHelpers;` as a
namespace-style fix. That produces CS0138, a diagnostic ID `CompilerErrorLookupHelper` doesn't
handle at all yet — it falls through to the plain diagnostic text.

## Possible next step (not yet built, not confirmed with user)

Either add a CS0138 branch (mirroring the CS0103/CS0122 pattern: state plainly "X is a type, not a
namespace — use `using static X;` or qualify calls as `X.Member(...)`"), or strengthen the existing
CS0103 message to preempt the type-as-namespace mistake before it happens. Scope: this addresses
one run's one extra error, not a large class of failures — same evidence-scoping discipline as
[[project_cs0122_lookup_helper_proposal]]'s own assessment section.

## Drift check

Zero drift false positives in either run — consistent with [[project_planimplementverify_5run_result_2]]'s
confirmation that the drift-detection fix holds.
