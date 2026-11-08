# ArchitecturalEngine fold blocked: no tool can relocate a namespace-level type declaration across projects

Status: RESOLVED via workaround (2026-09-29, commit 43465f8). Worked around by manually
relocating `CircularDependencyChain` to `RoslynSentinel.Common` (see "Resolution" section
below), NOT by a real fix to `MoveType`/`MoveMember`. The underlying gap this doc describes -
no tool can relocate a namespace-level type declaration's file AND project membership together
- is still open. Leaving this doc in place as the record of that gap; do not read "RESOLVED" as
"the tool now supports this."

**STOPPED SHORT (2026-09-29, follow-up trace session):** attempted to scope a real fix (a new
`MoveType` `destination: "otherProject"` value). Traced both the tool wrapper
(`RoslynSentinel.Server.Advanced/AdvancedRefactoringTools.cs:1539`, `MoveType`) and its `ownFile`
engine implementation (`RoslynSentinel.Basic/RefactoringEngine.cs:780`,
`MoveTypeToFileAsync`) to determine real implementation size before building. Findings:

- Good news: confirmed via `grep`-equivalent inspection of the 3 relevant `.csproj` files that
  this repo uses modern SDK-style implicit (glob-based) compilation - zero `<Compile Include>`
  items anywhere. This means physically relocating a `.cs` file into a different project's
  directory tree is sufficient for it to become part of that project's build; no `.csproj` XML
  editing is needed. That removes what looked like it might be the hardest part.
- `MoveTypeToFileAsync`'s type-splitting logic (`BuildSplitFileRoot`, namespace-node
  reconstruction, orphaned-region cleanup) is reusable as-is for a cross-project variant - the
  only change needed there is computing the new file's directory from a *different* project's
  root instead of `Path.GetDirectoryName(document.FilePath)`.
- The genuinely new work, not reducible to a small patch:
  1. A caller-side rewrite pass: every file across the whole solution that references the moved
     type needs its `using` directives checked/added if the namespace changes (which it normally
     would across a project boundary, e.g. `RoslynSentinel.Advanced` -> `RoslynSentinel.Basic`).
     `MoveMember` already has this class of machinery for member-move call sites
     (`callSiteFixups`), but nothing equivalent exists for `MoveType` today - this would need to
     be built new, not reused.
  2. A dependency-direction guard: the move must be rejected if it would place the type in a
     project that cannot legally be referenced by (or cannot legally reference) the type's
     current consumers, per the one-directional `Common <- Basic <- Advanced` graph. No existing
     tool validates this today; MoveType has never needed to because it never crosses project
     boundaries.
  3. Unconfirmed whether the write-path chokepoint (`ValidateAndApplyAsync` and the underlying
     `Solution.AddDocument`-based apply logic) supports creating a document that lands in a
     *different* project than the source document's project in a single atomic change - every
     existing caller of that chokepoint (including `MoveTypeToFileAsync` itself) only ever writes
     within one project. This would need to be verified and very possibly extended before any of
     the above could even be wired up.

This is genuinely new, multi-layer engine work (type-splitting + solution-wide using-directive
rewrite + a new dependency-direction validator + a possible write-chokepoint capability gap), not
a targeted bug fix - it does not fit this session's "minimal, targeted fix" mandate. Stopping here
per the standing escape hatch rather than forcing a large, under-scoped change. No code was
written or changed in this attempt; this is a trace/scoping pass only, left here as a concrete
starting point (file:line for both the wrapper and the reusable `ownFile` logic) for whoever
picks this up as its own task.

Found 2026-09-29 during engine reorg group 6, "Solution/project structure
merge", `.claude/plans/enumerated-jumping-babbage.md` section 6).

## Symptom

`MoveMember` folding `ArchitecturalEngine`'s methods (`FindCircularDependenciesAsync` and others)
from `RoslynSentinel.Advanced/ArchitecturalEngine.cs` into `SolutionStructureEngine`
(`RoslynSentinel.Basic/ProjectStructureEngine.cs`, this session's group-6 merge target) fails
`ValidationFailed` with 10x `CS0246` ("The type or namespace name 'CircularDependencyChain' could
not be found") - reproduced twice, once before including the nested `LayerViolation` record in
`memberNames` (which resolved cleanly once added - it is a genuine class member) and once after
(where `CircularDependencyChain` remained the sole unresolved type). Errors land both in the moved
method bodies themselves (`ProjectStructureEngine.cs:574,648,684`) and in call sites rewritten by
`callSiteFixups` in 5 test files (`BatterySixTests.cs:157,174`, `BatteryTwentyTwoTests.cs:252,428`,
`IntelligenceTests.cs:44,59`, `IntegrationTwentyNineTests.cs:613`).

Everything else about this fold worked cleanly first: the 25 originally-unresolved call sites (all
test-file direct constructions/fields of `ArchitecturalEngine`, none production DI-chain sites -
distinct from the separate DI-chain blocker in
`blocking_error_movemember_no_retype_tool_for_di_chain_call_sites.md`) were all resolved in one
pass via 7 `"FilePath:*"` `callSiteFixups` entries, each supplying
`"new SolutionStructureEngine(_workspaceManager, new SentinelConfiguration())"` - every one of those
test fixtures already has `_workspaceManager` in scope and the constructor shape matches exactly.
This is the ordinary, already-proven pattern from groups 4/5, working as expected.

## Root cause (file:line, verified by reading source, not assumed)

`RoslynSentinel.Advanced/ArchitecturalEngine.cs:7`: `public record CircularDependencyChain(...)` -
declared at namespace scope (`namespace RoslynSentinel.Advanced`), a sibling of the
`ArchitecturalEngine` class, not a member of it. Confirmed via `GetFileOutline`:
`{"kind":"record","name":"CircularDependencyChain","container":"RoslynSentinel.Advanced", ...}` -
the container is the namespace, not the class.

`FindCircularDependenciesAsync` (one of the methods being moved) returns
`List<CircularDependencyChain>`. Once the method moves to `SolutionStructureEngine` in
`RoslynSentinel.Basic`, its body and signature still reference `CircularDependencyChain`, but that
type only exists in `RoslynSentinel.Advanced` - and `RoslynSentinel.Basic` cannot reference
`RoslynSentinel.Advanced` (one-directional project dependency graph: `Common <- Basic <- Advanced`,
confirmed in `project_dependency_direction` memory and never violated anywhere in this codebase).
`MoveMember`'s own `memberNames` parameter only accepts class *members* ("Members to move", per its
schema) - `CircularDependencyChain` is not a member of `ArchitecturalEngine`, it is a namespace-level
type, so it can never be included in `memberNames` no matter how the call is shaped. Confirmed this
is not simply an omission: adding `LayerViolation` (an actual nested-record *member* of
`ArchitecturalEngine`, per `GetFileOutline`'s `"container":"ArchitecturalEngine"`) to `memberNames`
resolved its own CS0246 cleanly in the same retry, proving the tool works correctly for members -
the remaining failure is specifically because `CircularDependencyChain` is not eligible to be a
`MoveMember` argument at all.

Checked `MoveType` as a possible alternative (schema read directly, not assumed): its `destination`
parameter only accepts `ownFile` (moves a type already in a shared file out to its own file, same
project) or `outerScope` (nested type promoted to its containing namespace, same project). Neither
option relocates a type to a **different project**. There is no MCP tool whose purpose is "move a
namespace-level type declaration from project A to project B."

## What this blocks

`ArchitecturalEngine`'s fold into `SolutionStructureEngine` cannot complete via `MoveMember` alone.
The method-level and call-site-level parts of the fold are fully solved (proven in the two dry runs
above); only the `CircularDependencyChain` record's cross-project location is unresolved. This is a
narrower, more surgical gap than the DI-chain blocker: exactly one namespace-level type needs to move
from `RoslynSentinel.Advanced` to `RoslynSentinel.Basic` before the method fold can be retried.

Not yet checked (out of scope per the standing order - stop at the first new tool error, do not keep
investigating workarounds): whether `LayerViolation`'s per-member move is otherwise fully clean (no
reason to think it isn't, since it resolved with zero errors in the same dry run), or whether
`CircularDependencyChain` has any OTHER consumers inside `RoslynSentinel.Advanced` that would break
if it were manually relocated to `Basic` (a `Search(mode:text)` for `CircularDependencyChain` was
not run this session - left for whoever picks this fold back up).

## What would fix this (environment-side, not attempted - out of this task's scope per standing order)

- A tool (or a `MoveType` destination value) that relocates a namespace-level type declaration's
  file AND project membership together - e.g. `destination: "otherProject"` with a target project
  name/csproj path, handling the `.csproj`-level file-inclusion change a plain text move wouldn't.
- Failing that, `MoveMember`'s own error message could be more actionable here: it currently reports
  a bare `CS0246` without noting that the referenced type is namespace-level (not a class member)
  and therefore structurally ineligible for `memberNames` no matter what the caller tries next - a
  model retrying this blind could burn several turns adding plausible-looking member names before
  concluding (as this session did, via `GetFileOutline` cross-referencing container) that no
  `memberNames` value will ever fix it.

## Self-inflicted bypass: none this segment

All `.cs` reads and the `MoveMember`/`MoveType` calls in this segment went through RoslynSentinel's
own MCP tools (`GetFileOutline`, `GetMethodSource`, `MoveMember`, `ToolSearch` for `MoveType`'s
schema). No `Read`/`Edit`/`Write`/`Bash`/`PowerShell` was used against a `.cs` file or for a git
operation in this segment.

## Resolution (workaround, not a tool fix)

A follow-up coordinator-directed session (2026-09-29) completed the fold using a manual
two-project-hop relocation instead of a single tool call:

1. Created `RoslynSentinel.Common/CircularDependencyChain.cs` as the sole declaration, in
   `RoslynSentinel.Common` - upstream of both `RoslynSentinel.Basic` (the fold target) and
   `RoslynSentinel.Advanced` (the original owner). This sidesteps the one-directional
   `Common <- Basic <- Advanced` dependency graph entirely rather than needing a cross-project
   move in either direction.
2. Removed the old `RoslynSentinel.Advanced/ArchitecturalEngine.cs:7` declaration via
   `ReplaceSnippet` (not `Member(remove, skipPrecheck:true)` - see the sibling blocker doc below,
   which that removal attempt reproduced and is now also resolved-by-workaround the same way).
3. Retried `MoveMember` for `ArchitecturalEngine`'s 18 members into `SolutionStructureEngine` -
   succeeded cleanly once `CircularDependencyChain` no longer needed to cross a project boundary
   the moved methods' signatures depended on.

Commit: `43465f8`. Build 0 errors, RunTest 2509/2626 passed with zero new regressions (18
failures, all pre-existing/flaky, see commit message for the full breakdown).

This is a workaround specific to this one type. It does **not** generalize: it only worked
because `CircularDependencyChain` had no members or logic tying it to `Advanced` specifically,
and because a `Common`-level home was semantically valid for it. A type that genuinely belongs
at the `Advanced` layer, needed by a `Basic`-layer fold target, would still hit this exact wall.
The tool gap described above remains open.

## Related

- `.claude/plans/enumerated-jumping-babbage.md` - group 6 plan text, specifically the note "confirm
  ArchitecturalEngine's current state... since it may have grown since the original survey" (it had
  not grown - 950 lines, same shape, confirmed via fresh `GetFileOutline` this session).
- `docs/current/blockers/blocking_error_movemember_no_retype_tool_for_di_chain_call_sites.md` -
  sibling group-6 blocker, different root cause (DI-chain constructor-parameter gap, not a
  cross-project namespace-type gap); together these two blockers cover `DependencyEngine`/likely
  `ProjectConsistencyEngine`/`SolutionManagementEngine` (DI-chain) and `ArchitecturalEngine`
  (namespace-type-across-projects). Still open - out of this task's scope.
- `docs/current/blockers/blocking_error_member_remove_skipprecheck_targetnotfound_ambiguous_symbol.md` -
  the `Member(remove, skipPrecheck:true)` `TargetNotFound` defect hit and worked around (via
  `ReplaceSnippet`) while executing step 2 of the resolution above. Still open as its own tool
  defect; the workaround here does not fix it.
- `RoslynSentinel.Advanced/ArchitecturalEngine.cs` (now an empty shell - constructor + 2 unused
  fields; left in place rather than deleted because 8 test files still directly construct it as
  a local fixture; not fixed here, judged out of scope),
  `RoslynSentinel.Basic/ProjectStructureEngine.cs` (`SolutionStructureEngine`, the fold target),
  `RoslynSentinel.Common/CircularDependencyChain.cs` (new home of the record).
- `project_dependency_direction` memory - the one-directional project graph this blocker sits on.
