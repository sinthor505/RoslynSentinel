---
name: project_wholefile_commentout_guard_added
description: "ApplyDiff size guard now also catches whole-file comment-out (same line count, zero active code); commit 579ead4"
metadata: 
  node_type: memory
  type: project
  originSessionId: 6fbe517c-9f66-49d7-b653-7120706ada50
  modified: 2026-09-02T07:02:37.217Z
---

Fixed a gap in the ApplyDiff >50%-shrink guard: it only compared raw line
counts, so an agent commenting out every line of a file one-for-one (same
line count, zero working code, 0 compiler errors since comments always
compile) sailed through undetected. Found via
ModelTestingResults/113/Model_FixesWholeFileRewriteBug_PlanImplementVerify/20260902-062730-159 —
ApplyDiff replaced BlockConverter.cs with every line prefixed "//",
reported success, and Build reported 0 errors. The sibling verify/transcript.json
in that run shows the model's own separate review caught it correctly via
plain ReadFile + reasoning ("the file is essentially empty of actual code").

**Fix**: added `PercentActiveCodeLinesRemoved` next to the existing
`PercentLinesRemoved` in SentinelWorkspaceTools.cs — parses old/new .cs
content with Roslyn (`CSharpSyntaxTree.ParseText`), counts lines actually
covered by a syntax token (excludes comment/whitespace trivia so markers
inside string literals don't skew it), and rejects with
`ConfirmationRequired` if either raw-line-shrink OR active-code-line-shrink
exceeds the 50% `LargeShrinkRejectionThreshold`. Verified against the exact
real transcript payload before writing the test: raw lines 43→43 (old guard
sees 0%), active lines 30→0 (100% collapse, new guard fires).

**Why**: same-line-count sabotage/mistake is a distinct failure signature
from the fragment-submission case the original guard targets — needs its
own detection, not a tuning of the existing threshold.

**How to apply**: if a future model-eval run surfaces another "guard
missed it" case, check whether the new content still *parses* as
meaningfully different C# before assuming a threshold needs adjusting —
line-count-based heuristics have blind spots that content-aware (Roslyn)
checks close cheaply. Test added:
`ApplyDiff_FilesFormatCommentsOutWholeFile_RejectsWithConfirmationRequiredAsync`
in RoslynSentinel.Tests.Battery/ApplyDiffSizeGuardTests.cs.

See also [[project_planimplementverify_5run_result_postfix_verify]],
[[project_functional_fix_verifier_added]].
