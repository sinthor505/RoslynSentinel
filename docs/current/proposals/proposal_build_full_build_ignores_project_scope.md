# Build: a fullBuild ignores scope: project and compiles every project, silently

**Status:** PROPOSED 2026-10-28. TOOL-GAP from the Tests.Support.* rollout; awaiting review. Whether to honor the scope or reject it is open.

## Motivation

The rollout repeatedly used `Build(level: fullBuild, scope: project, scopeName: <project>)` to restore and compile one
new project, and the plan records the gap as follow-up 6: "`Build(scope: project)` compiles every project"
([plan_tests_support_projects_factory_rollout.md](../plans/closed/plan_tests_support_projects_factory_rollout.md); calls named
in Step 3 slice A and Step 4 `Tests.Battery.Basic`). The call succeeded, so the caller had no signal that the
requested scope was dropped; the cost is silent: a whole-solution `dotnet build --no-incremental` instead of one project.

Verified in source:

- `WorkspaceBuildTestImpl.Build` (`RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs`, the `isFullBuild` branch) calls
  `_buildEngine.RunFullBuildAsync(cancellationToken, maxDetails, level: fullBuildLevel)` for `fullBuild` and
  `fullBuildThrowaway`, and `RunQuickBuildAsync(scope, scopeName, ...)` otherwise. `scope` and `scopeName` are not passed to the
  full build. The comment in `Build` states it: "Full builds ignore scope, project scope is a name."
- `BuildEngine.RunFullBuildAsync` (`RoslynSentinel.Engines.Basic/BuildEngine.cs:190`) has no scope parameter. Its doc comment
  reads "Runs `dotnet build` on the whole solution", and the process arguments are `build <solutionPath> --nologo -v quiet
  --no-incremental` (plus the artifacts path for the throwaway level).
- `RunQuickBuildAsync` does honor `scope: project` and `scope: file` (`BuildEngine.cs:42-90`): in-memory diagnostics for one
  project or file, and a `BuildNotRun` error naming `ListAll` when the scope resolves to nothing.
- The tool schema gives no warning: the `scope` parameter on `BuildTool` has no `[Description]`
  (`WorkspaceBuildTestTools.cs:35-46`), and `ToolParams.BuildDescription` / `ToolParams.BuildLevel` (`RoslynSentinel.Common/ToolParams.cs:8-12`)
  describe `level` and the output, not that scope applies only to `quickBuild`. A caller therefore reads `scope: project`
  as "build this project", which is also how the plan's author used it.
- Contrast: `RunTest` documents and honors project scope (`WorkspaceBuildTestTools.cs`, `RunTestTool` descriptions).

The side effect that helped here (a whole-solution build also restored the new project, which is what
[proposal_load_solution_hint_for_unrestored_project.md](proposal_load_solution_hint_for_unrestored_project.md) needed) is an accident:
a project-scoped `dotnet build` would restore that project too, and a model that sees the whole solution compile cannot tell the
difference.

Not measured: the wall-clock difference between a project-scoped and a whole-solution full build on this repo. The plan gives
no figure for `Build`; for `RunTest(scope: solution)` the tool description says a solution scope "still builds every test
project (about 1.5 minutes)". Measure before arguing the benefit.

## Proposal

Decided: the silent drop must go. Open: how.

**Option 1 (preferred, open):** honor the scope for full builds.
- `scope: project` + `scopeName`: `dotnet build <that project's .csproj>`; resolve the project path from the loaded solution,
  and return `BuildNotRun` with the `ListAll` hint when the name is unknown (same wording as the quick build).
- `scope: file` with a full build: `InvalidArgument` ("a full build compiles a project or the solution; pass scope: project
  or use level: quickBuild for one file").
- `BuildResult.ProjectsCompiled` must then list what actually built (the project and its references), not every project; how
  `RunFullBuildAsync` derives that list today was not traced (it starts from `currentSolution.Projects`, `BuildEngine.cs`
  `projectNames`). Unverified; check before promising.

**Option 2 (cheaper stop-gap):** keep whole-solution behavior but say so. When `level` is a full build and `scope != solution`,
add a warning to the result and to the tool description: "scope is ignored for fullBuild/fullBuildThrowaway; the whole
solution was built". Fixes the silence, not the cost.

Staging (migration-first): ship Option 2 first (one result field plus one sentence in `ToolParams.BuildLevel`), then Option 1
behind the same parameters. No new parameter is needed either way. Option 2 changes only text, so it needs no `[Obsolete]`
step; Option 1 changes behavior for callers passing `scope: project` with `fullBuild`, so check journal usage before flipping.

## How to verify

- Option 2: unit test that `Build(level: fullBuild, scope: project, scopeName: X)` returns the warning, and that the default
  call (scope solution) does not.
- Option 1: build a two-project fixture where project B has a deliberate compile error; `Build(fullBuild, scope: project,
  scopeName: A)` must succeed and `ProjectsCompiled` must not list B; the solution scope must still fail on B.
- Live: the Tests.Support.Basic scenario from the plan (new project, no restore): `Build(fullBuild, scope: project)` leaves
  `obj/project.assets.json` for that project only.
- Check `RunTest`'s own build path (it uses the same `BuildGate` and `BuildIsolation`) is unaffected.

## Alternatives considered, not pursued

- **Drop the `scope` parameters for full builds from the schema.** One schema, two meanings is the actual smell, but
  `scope` is shared with `quickBuild` and a split would add a parameter. Not pursued; the schema budget argues against it.
- **Make `fullBuild` always solution-wide and document it only.** The status quo plus text; weaker than Option 2 because a
  model reading the result still sees success and no hint.
- **Use `quickBuild` for a single project.** Fine for diagnostics, but it does not restore or run post-build steps, so it does
  not replace `fullBuild` for the restore case that started this.

## Open questions

- Option 1 vs Option 2 first; whether Option 1 is worth it given that a restored whole-solution build was the working
  recovery. Depends on the unmeasured build times.
- `ProjectsCompiled` for a project-scoped full build (see above).
- Server staleness: `Build(fullBuild)` is also the call that updates the shared `bin/` (CLAUDE.md "Verification"); a project-scoped
  one would update only that project's output. Check that `isServerBinaryStale` semantics still hold when the server project is
  not the one built.

## Cost / risk

- Touches `BuildEngine.RunFullBuildAsync` (new scope parameters, project-path lookup), `WorkspaceBuildTestImpl.Build`, and the
  `Build` descriptions in `ToolParams.cs` (schema budget applies). Tests for `BuildEngine` live in `Tests.Tools.Basic`
  (26 hits there for `BuildEngine|RunQuickBuildAsync` in a text search with test projects included).
- Risk: a caller relying on the accidental whole-solution restore from `scope: project` stops getting it. Option 2 first avoids this.
- A project-scoped build does not verify downstream projects, so it is not a substitute for the pre-commit whole-solution build
  required by CLAUDE.md ("Build (0 errors) before committing").

## Related

- [plan_tests_support_projects_factory_rollout.md](../plans/closed/plan_tests_support_projects_factory_rollout.md) (follow-up 6)
- [proposal_load_solution_hint_for_unrestored_project.md](proposal_load_solution_hint_for_unrestored_project.md)
- [plan_blocking_solution_load_and_build_result_shape.md](../plans/plan_blocking_solution_load_and_build_result_shape.md) (Build result shape)
