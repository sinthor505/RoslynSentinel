# RunTest: run only the tests that reach a set of changed members

**Status:** PROPOSED 2026-10-09. Awaiting review; no code written. Timing split (build vs discovery vs execution) not yet measured.

## Motivation

During an implementer slice the agent edits one or two members and wants a fast "did I break the obvious
things" signal. Today `RunTest` offers only:

- `scope=solution`: runs every test project, one `dotnet test` process each, sequentially
  (`RoslynSentinel.Engines.Basic/TestRunEngine.cs` `RunAsync`, the `foreach (var (name, path) in targets)` loop).
  The tool description puts even a *filtered* solution run at about 1.5 minutes for 13 projects.
- `scope=project` + `filter`: fast, but the agent must already know which project and which
  `dotnet test --filter` expression cover its change. A weak model guesses, or skips the check and runs the
  full suite.

The suite is roughly 2,600 tests (memory `project_engine_reorg_final_closeout_bugs_1_2_3`: 2511 pass of 2627).
The full suite stays the gate at the end of a task (unchanged by this proposal); the gap is the interim check.

What a member-based run can and cannot save (read from `TestRunEngine.cs`):

- It saves test **execution** and, more importantly, whole test **projects**: a project with no relevant tests
  is not built, discovered or run at all.
- It does **not** save the build of the projects that are run. `RunOneProjectAsync` passes no `--no-build`
  (the only `dotnet test` option it adds beyond the defaults is `--filter`, line 315), so every call rebuilds.
- Hypothesis, unmeasured: build + discovery dominate a filtered run. If so, project pruning is the main win and
  per-test filtering is a minor one. See Open questions.

## Proposal

Add one optional parameter to `RunTest`:

```
testsForMembers: string[]?   // changed members; the server resolves the tests that reach them
```

`filter` is unchanged. `scope` is unchanged. Decided:

1. **Mutually exclusive with `filter`.** Passing both returns `InvalidInput` naming both parameters. v1 does not
   combine them.
2. **Orthogonal to `scope`.**
   - `scope=solution` (default): run only the test projects that contain a resolved test.
   - `scope=project` + `scopeName`: resolve across the solution but run only that project's share. Zero resolved
     tests in that project is reported as such, not as a pass.
3. **No new tool class.** Registration steps 2 and 3 in CLAUDE.md do not apply. Touch points:
   `WorkspaceBuildTestTools.RunTest`, `WorkspaceTools.RunTest` (a second declaration of the same tool name),
   `WorkspaceBuildTestImpl.RunTest`, `TestRunEngine.RunAsync`, plus their tests.

### Resolution (new class in `Engines.Basic`)

Layering is fine: `TestRunEngine` and `SymbolNavigationEngine` (which already implements `GetCallGraphAsync`)
are both in `Engines.Basic`. The resolver is a new engine class there; it must **not** reuse
`ControlFlowEngine.GetTestCoverageMapAsync` (`Engines.Advanced`, unreachable from Basic, and name/syntax
heuristic only: it matches test-method names containing the method name or any same-named invocation).

Algorithm, all against the Roslyn semantic model:

1. Resolve each entry to a symbol. Accepted forms: `Method`, `Type.Member`, `Namespace.Type.Member`. An
   ambiguous entry returns an error listing every candidate as fully qualified name + file, so the agent can
   re-call with the qualified form. An unresolvable entry returns an error, never a silent skip.
2. Walk callers transitively (reverse reachability) with a depth cap, stopping at test methods. Include:
   invocations, property/field reads and writes, object creation (constructors), method-group references, and
   callers of the interface/base member when the changed member is an implementation or override.
3. A test method is any method carrying an NUnit/xUnit/MSTest test attribute, identified by attribute *symbol*
   (namespace + name), not by text.
4. If a changed member is itself a test, include it. If it is test-project infrastructure (helper, fixture
   setup), the same walk finds the tests that use it.
5. Group resolved tests by project and by fixture class.

### Filter generation

Per project, an OR of `FullyQualifiedName~<Namespace.Class.Method>` terms. If a project's expression would exceed
a length cap (Windows command-line limit), degrade to fixture-class granularity for that project. `~` is a
superset match (`Foo` also matches `FooBar`); acceptable for a smoke run because it can only add tests.
`TestRunEngine.RunAsync` currently passes one `filter` to every project; it needs a per-project filter map.

### Response

Extend `TestRunResult` (the record in `TestRunEngine.cs`) with a member-run block, present only in this mode:

- `ResolvedTestCount`, `ProjectsRun`, `ProjectsSkipped`, and a capped list of resolved tests.
- `UnresolvedMembers` (should be empty on success).
- `Partial: true`, and the status message must say it is a smoke subset, not a substitute for the full run.
- **Zero resolved tests: run nothing** and return `RunCompleted=false` with a `Detail` such as "No tests reach
  <members>. Not a pass; run scope=project or the full suite." Evidence this needs care: for the existing
  filter path, `RunSucceeded` is `allRunCompleted && allExitZero && failedCount == 0`, which is **true** with
  zero tests (`RunAsync`, final `TestRunResult`), so a naive reuse would report a green result for an empty run.

### Parameter description (draft)

"Changed members (for example `Foo.Bar`); the server finds the tests that call or use them and runs only those,
in only the projects that contain them. A fast interim check - still run the full suite when the task is done.
Cannot be combined with `filter`."

## Alternatives considered, not pursued

| Alternative | Why it lost |
| --- | --- |
| Infer symbol-vs-filter intent from the `filter` string | Typos silently route to the wrong path and surface as an unrelated "unresolvable filter" error; a bare name can be a member, test class or project, so the meaning depends on workspace contents. Violates the failure doctrine (invalid call should be unrepresentable). |
| New enum value (`RunMemberTests`) on `scope` | `scope` is *where* to run, member resolution is *what* to run; they must combine (resolve, then restrict to one project). It would also need a second co-dependent parameter for the member list. |
| Reuse `GetTestCoverageMapAsync` | Wrong layer (Advanced), name/syntax heuristic, direct calls only: misses `Foo -> Baz -> test` and false-matches same-named methods. A "smoke subset" that silently skips affected tests gives false confidence. |
| Coverage-based test impact analysis (coverlet) | Needs an instrumented baseline run and goes stale on every edit. Far heavier than a static call-graph walk. |
| File/name convention (`Foo.cs` -> `FooTests.cs`) | The repo does not follow it consistently (for example tests named for the tool, in `Tests.Tools.Basic`, covering engine code). |
| Only add project auto-selection, no member resolution | Cheaper, but the agent still needs the right `--filter`; most of the "weak model guesses wrong" failure remains. May still be a worthwhile first slice, see Open questions. |

## Open questions

1. **Measure first.** Time one representative full run split into restore/build, discovery and execution per
   project. If execution is under about 30% of the total, expect the win to come almost entirely from skipping
   projects and from `--no-build` (below), not from per-test filtering. Unverified.
2. **`--no-build` after a successful `Build`.** `RunOneProjectAsync` always rebuilds. A separate, possibly larger
   lever: skip the rebuild when the caller just ran a clean `Build(fullBuild)` and no source changed since.
   Needs a staleness check; independent of this proposal and worth its own entry.
3. **Depth cap and fan-out.** What default depth, and what happens when a core utility reaches nearly every test
   (resolved set approaches the whole suite)? Candidate: if the resolved set exceeds a threshold, say so and
   suggest the full run rather than running 90% of it as a "smoke" test.
4. **Fixtures and base classes.** A changed `[SetUp]`, `[OneTimeSetUp]`, base-class member or `TestCaseSource`
   affects every test in the fixture and its derived fixtures without any call edge from the test body. Proposed:
   treat as fixture-wide; not yet designed.
5. **Reflection/DI-driven paths** (tool classes resolved from DI, `McpServerTool` dispatch, source-generated
   code) have no static call edge from tests. Static resolution under-reports these; the response must not claim
   completeness. Is the `Partial: true` wording enough?
6. **Member entry format** for weak models: is `Type.Member` plus the ambiguity-error loop enough, or should it
   accept `filePath` + `contextSnippet` like `FindReferences`?
7. **Staged vs committed source.** Resolution would read `ReadSource.Committed`; `dotnet test` runs on disk.
   These agree after a tool write; confirm for the staged-writes proposal.

## Cost / risk

- Moderate, one slice-sized feature: new resolver class (Engines.Basic), per-project filter plumbing in
  `TestRunEngine.RunAsync` / `RunOneProjectAsync`, a signature change across two tool declarations and the Impl
  (use `MethodSignature`/`ChangeSignature` so call sites follow), `TestRunResult` extension, tests. Probably 2-3
  implementer slices: (a) resolver + unit tests, (b) engine per-project filter + zero-test behaviour, (c) tool
  signature + description + tool tests.
- Rate limit: `RunTest` is limited to 10 calls (`CheckRateLimit("RunTest", 10)` in `WorkspaceBuildTestImpl`);
  smoke runs encourage more calls. Confirm the window still suits it.
- Risk: false confidence from an under-approximating static walk (open questions 4 and 5). Mitigation is in the
  response contract: `Partial`, the resolved list, and the explicit zero-result behaviour.
- `docs/generated/` and the emitted-schema golden tests may need regenerating (`scripts/Generate-ArchitectureMap.ps1`)
  if they capture tool parameters; unverified.

## Related

- Tool description for `RunTest` (`WorkspaceBuildTestTools.cs`): states the 1.5 minute solution-scope filter cost.
- `ControlFlowEngine.GetTestCoverageMapAsync` / `GetTestCoverageMap` tool (`QualityTools.cs`): existing heuristic,
  left untouched; could later be reimplemented on the new resolver.
- Memory `project_engine_reorg_final_closeout_bugs_1_2_3`: suite size and baseline.
- Memory `reference_known_failing_tests`: baseline to compare full runs against.
