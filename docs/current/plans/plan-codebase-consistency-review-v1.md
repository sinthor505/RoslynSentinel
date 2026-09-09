# Plan: Codebase Consistency & Duplication Review

**Purpose**: audit RoslynSentinel for internally-duplicated or inconsistent patterns — places
where the same problem is solved multiple different ways across the codebase with no clear rule
for which to use. This is distinct from `docs/current/roslyn-duplication-audit-v1.md`, which
checks hand-rolled logic against the *Roslyn API surface*; this review looks *inward*, comparing
RoslynSentinel's own code/tests against itself.

**Known seed example** (already flagged, not yet fixed — see the project's
`project_deferred_cleanup_todos` memory): four different ways exist to get a solution into a test,
with no documented rule for which to use:
- `AsyncifyTestHelper` (in-memory `AdhocWorkspace`) in `RoslynSentinel.Tests.Asyncify`
- A near-duplicate `TestSolutionBuilder`
- `FakeWorkspaceManager` (`RoslynSentinel.Tests.Fakes`) — mostly-unimplemented `IWorkspaceManager`
  fake, throws `NotImplementedException` on `ApplyProposedChangesAsync`/`LoadSolutionAsync`
- Real `PersistentWorkspaceManager` + a temp-directory copy of `Samples/ContosoOrders`
  (`RoslynSentinel.Tests/TestSolutionFixture.cs`) — the pattern `SentinelAsyncifyToolsTests` and the
  newer MCP-tasks harness tests actually use in practice

Use this as the calibration example for the kind of finding this review should surface: not "this
code is wrong," but "this problem is solved N different ways and should be solved one way."

## Scope

The solution has ~15 projects (`RoslynSentinel.Basic/Advanced/Common`, `Server.Basic/Advanced`,
5+ test projects, `Server.Basic.Http`/`Server.Advanced.Http`, samples). A full line-by-line pass
isn't practical in one session — scope this as a **survey pass** (breadth over depth), producing
a prioritized findings list rather than fixes. Treat it the same way
`roslyn-duplication-audit-v1.md` treated its "candidates not yet reviewed" tail: flag for later
depth rather than silently skip.

Suggested areas to check, roughly in priority order:

1. **Test fixture/helper patterns** (the seed example above, generalized). Grep for
   `IWorkspaceManager`-implementing or solution-loading helper types across all `*.Tests*`
   projects; group by what problem each solves (in-memory solution construction, temp-disk solution
   construction, workspace-manager faking) and flag near-duplicates.
2. **DI/service-registration patterns**. `ServiceRegistrationExtensionsBasic.cs` and any
   `Server.Advanced`-side equivalent — check for copy-pasted registration blocks vs. shared
   extension methods, and whether `Advanced` re-registers anything `Basic` already provides (see
   `project_advanced_extends_basic` memory: Advanced project-references Basic, so duplication here
   would be redundant, not just inconsistent).
3. **Diff/line-comparison logic**. The existing Roslyn audit already flagged (finding #1,
   RenameSymbol) that `RefactoringEngine.ComputeRenameHunks` duplicates line-diff logic already in
   `RoslynSentinel.Common.DiffEngine.CreateDiff`, deferred rather than fixed. Check for other
   hand-rolled diff/line-shift logic that should be routing through `DiffEngine`.
4. **Validation/apply chokepoints**. `project_write_path_chokepoint_unified` memory says all `.cs`
   writes now route through `ApplyProposedChangesAsync` and `ValidateAndApplyAsync` was deduped
   into `Common` — confirm no tool has since regressed by adding its own bespoke write/validate
   path instead of using the shared one.
5. **Error-mapping / result-shaping patterns**. Check whether `ToolResult`/error-to-success mapping
   (`ToolErrorMapper`, per `project_operation_blob_json_gotchas` memory) is applied consistently
   across tool families, or whether some tools hand-rolled their own try/catch-to-result shaping.
6. **JSON serialization helpers**. Look for repeated inline `JsonSerializer.Serialize`/custom
   converter logic across tool classes that could be a single shared helper — especially anything
   working around the `ItemRecordOutcome`-has-no-string-converter gotcha already documented.
7. **Constructor/DI wiring for engine classes**. Given how many `*Engine` classes exist
   (`AsyncifyTestHelper`'s notes list a couple dozen), check whether they're constructed via a
   consistent pattern (DI-resolved vs. `new`'d inline in tool methods vs. static) — inconsistency
   here tends to predict future testability pain, similar to the `IWorkspaceManager` segregation
   work already done.

## Suggested process for the fresh session/agent

1. Load context: point the agent at this file, `docs/current/roslyn-duplication-audit-v1.md` (so
   it doesn't re-tread that axis), and `docs/current/project_deferred_cleanup_todos.md` (the seed
   example).
2. Use `GetCodeInventory`/`GetSolutionMetrics`/`GetDiRegistrations` (RoslynSentinel's own MCP tools
   — dogfood them per the project's own "use RoslynSentinel tools first" convention) to get an
   inventory before grepping manually.
3. For each scope area above, produce a table row per finding: **Pattern**, **Locations** (files),
   **Divergence** (what's actually different between the copies — signature, behavior, staleness),
   **Suggested canonical version**, **Risk if unconsolidated** (grows worse over time vs. cosmetic).
4. Do **not** perform the consolidation in this pass — this is a survey/audit producing a
   prioritized backlog, mirroring how `roslyn-duplication-audit-v1.md` was run (findings tracked
   with `keep`/`todo`/`fixed`/`removed` actions, not all executed immediately).
5. Write findings to a new `docs/current/codebase-consistency-audit-v1.md` using the same
   table format as `roslyn-duplication-audit-v1.md` (# / Pattern / Locations / Finding / Action /
   Notes) so the two audits read consistently.
6. End with an explicit "candidates not yet reviewed" section for anything scoped out, same
   convention as the existing audit.

## Explicitly out of scope for this pass

- Actually consolidating the Asyncify test fixtures (that's the separate, already-scoped
  `project_deferred_cleanup_todos` session item #2 — this review's job is to confirm/expand that
  finding, not execute the fix).
- Re-auditing anything `roslyn-duplication-audit-v1.md` already covered (findings #1-14) unless
  new evidence contradicts a prior finding.
- Fixing anything found — file it as a finding with a suggested action, don't touch code.
