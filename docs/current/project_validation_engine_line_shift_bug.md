---
name: project-validation-engine-line-shift-bug
description: "ValidationEngine.ValidateChangesAsync's diagnostic-delta dedup keyed on line number, misclassifying pre-existing errors as newly introduced after a line-shifting edit — found via BulkComment solution-wide runs, fixed 2026-08-26"
metadata:
  node_type: memory
  type: project
  originSessionId: ed38ed5b-d0aa-4f27-828c-fcbbb5d0b086
  modified: 2026-08-26T02:32:39.391Z
---

**Fixed 2026-08-26** (found while building [[project_mcp_tasks_test_harness_plan]]'s BulkComment coverage). `ValidationEngine.ValidateChangesAsync` (`RoslynSentinel.Common\ValidationEngine.cs:178-182`) dedups baseline vs. candidate compiler errors with `DiagnosticKey`, which included `location.StartLinePosition.Line`. This is a delta comparison meant to return only *newly introduced* errors — but any edit that adds/removes lines *above* a pre-existing error shifts that error's line number, so the candidate's copy of the same pre-existing error no longer matched its baseline key and got reported as "new."

**Fix applied:** `DiagnosticKey` now returns `$"{d.Id}|{d.GetMessage()}|{location.Path}"` — dropped the trailing `|{location.StartLinePosition.Line}` segment. Verified: solution builds with 0 errors, and the 4 `McpTasksHarnessBulkCommentTests` (including the real, non-dryRun `ContosoOrders.Core` run) still pass. Full solution test run in progress to confirm no regressions elsewhere.

**Repro:** solution-wide `BulkComment` (non-dryRun) against a solution containing a project with pre-existing unresolved-reference errors (e.g. `ContosoOrders.Tests` referencing xUnit types that aren't actually restored) plus other, unrelated projects. Seeding/commenting inserts `[ContentHash]` attributes and doc comments earlier in files across the solution; even projects untouched by the edit aren't affected, but *any file in the same project* as a pre-existing error, once edited above that error's line, causes `ValidateChangesAsync` to report the (unchanged) baseline error as newly introduced — appearing in `BulkComment`'s result as `"apply validation failed (N diagnostic(s))"` for every member in that project, blocking all of them from ever being committed.

**Why this stayed out of scope for the harness fix:** the harness's job was to prove Tasks support (capability negotiation, response correctness, cancellation) against a tool that does real work — not to fix pre-existing `ValidationEngine` defects. The test fixture (`RoslynSentinel.Tests\TestSolutionFixture.cs`) legitimately has an unrestored xUnit project, which is enough to trigger this without any bug in `BulkComment` itself. Routing the real-work tests to `scope: "project", projectName: "ContosoOrders.Core"` (the one project with no pre-existing errors) sidesteps it entirely and matches realistic caller behavior.

**Likely fix, if picked up later:** key `DiagnosticKey` on something line-shift-resistant — e.g. `(Id, Message, Path)` without line number, or a span relative to a stable anchor (containing member/type name) instead of absolute line position. Any fix should be validated against a solution with a real pre-existing error in an untouched-but-shifted file, not just a synthetic single-file case.
