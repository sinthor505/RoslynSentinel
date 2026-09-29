# MoveMember fold blocked: no MCP tool can retype an existing constructor parameter in place, so DI-chain-only call sites cannot be resolved

Status: OPEN (new, found 2026-09-28/29 during engine reorg group 6, "Solution/project structure
merge", `.claude/plans/enumerated-jumping-babbage.md` section 6).

**STOPPED SHORT (2026-09-29, follow-up trace session):** re-confirmed this doc's own conclusion by
reading both tools' current schemas directly (not from memory): `ConstructorParameter`'s `add`
operation supports `defaultValue`/`nullDefault` so old call sites keep compiling, but has no
`callSiteFixups`-style parameter (nothing analogous to `MoveMember`'s own `callSiteFixups`) to
supply a real per-call-site construction/receiver expression. `MethodSignature`'s `add` is
append-only with the same limitation. Both confirmations match this doc's existing findings
exactly - nothing new was discovered that shrinks the scope.

The fix this needs - `ConstructorParameter(add, callSiteFixups: {...})`, threading a new required
dependency through both the target class's constructor and every existing inline-construction call
site in one atomic operation - is real, additive engine work of the same size and shape as the
cross-project `MoveType` gap in the sibling doc
(`blocking_error_movetype_cannot_relocate_namespace_type_across_projects.md`, see that doc's own
"STOPPED SHORT" section): a new tool capability spanning constructor-signature rewriting plus a
solution-wide call-site rewrite pass, not a targeted bug fix. Per the standing escape hatch, this is
being left open rather than forced through as part of a 3-bug fix session. No code changed in this
pass.

## Symptom

`MoveMember` folding `DependencyEngine`'s 3 methods (`GetProjectDependenciesAsync`,
`FindUnusedReferencesAsync`, `CheckPackageInconsistencyAsync`) into `SolutionStructureEngine`
(the group-6 merge target, itself created earlier in this session by `RenameSymbol`-ing the
pre-existing `ProjectStructureEngine` -> `SolutionStructureEngine`, matching the established
groups 4/5 pattern) fails both as a `dryRun:true` preview and as a real write, with
`UnresolvedCallSites`, sub-status `NoCandidateIntroducible`, at exactly these 8 sites:

- `RoslynSentinel.Basic/WorkspaceProjectManagementImpl.cs:136` (inside `ListSolutionItems`,
  `kind == SolutionItemsKind.dependencies` branch) and `:176` (inside the same method's
  `kind == SolutionItemsKind.all` branch) - both call `_dependencyEngine.GetProjectDependenciesAsync(...)`.
- `RoslynSentinel.Tests.Advanced/IntegrationTwentyNineTests.cs:439,450`
- `RoslynSentinel.Tests.Battery/BatteryEightTests.cs:152,162,172`
- `RoslynSentinel.Tests.Battery/BatteryTwentyFiveTests.cs:490`

Each result includes a `suggestedFix` naming the correct remediation shape ("Add a field of type
`SolutionStructureEngine` to X, then retry MoveMember, or supply callSiteFixups") - the tool's
guidance itself is accurate; the problem is that following it is not actually possible with any
tool in the current surface, for the reasons below.

Note: `ScanTools.cs`, `IntelligenceTools.cs`, `CodemodTools.cs` (Server.Advanced) also call
`_dependencyEngine.*` but did NOT appear in the unresolved list - `MoveMember`'s auto-resolver
correctly found their existing `_projectStructureEngine` field (retyped to `SolutionStructureEngine`
automatically by the earlier `RenameSymbol` call, since a rename changes the field's declared type
reference too, not just identifier text matching the class name) as a valid receiver. So this is
not a blanket DependencyEngine-fold failure - it is specific to call sites reachable only through
the `WorkspaceProjectManagementImpl`/`WorkspaceTools` DI chain (impl file) and their direct
test-construction call sites (test files).

## Root cause (file:line, verified by reading source, not assumed)

`RoslynSentinel.Basic/WorkspaceProjectManagementImpl.cs` constructor:

```csharp
public WorkspaceProjectManagementImpl(IWorkspaceManager workspaceManager, SolutionManagementEngine solutionManagementEngine,
    DependencyEngine dependencyEngine, ProjectConsistencyEngine projectConsistencyEngine,
    StructuralRefinementEngine structuralRefinementEngine, ILogger logger)
```

No `SentinelConfiguration` field anywhere in this class (confirmed via `GetFileOutline`: fields are
`_workspaceManager`, `_solutionManagementEngine`, `_dependencyEngine`, `_projectConsistencyEngine`,
`_structuralRefinementEngine`, `_logger` only). `SolutionStructureEngine`'s constructor requires
`(IWorkspaceManager workspaceManager, SentinelConfiguration config)` - so no `callSiteFixups` value
of the form `new SolutionStructureEngine(_workspaceManager, ???)` can be written inline inside this
class; there is no `_config`/`SentinelConfiguration` in scope to supply as the second argument.

One level up, `RoslynSentinel.Server.Basic/WorkspaceTools.cs` constructor DOES receive
`SentinelConfiguration config` as a parameter (confirmed via `GetMethodSource`, used later in the
same constructor to build `_healthMisc = new WorkspaceHealthMiscTools(new WorkspaceHealthMiscImpl(workspaceManager, config, buildEngine, logger))`),
but `WorkspaceTools` constructs `WorkspaceProjectManagementImpl` inline one statement earlier
without passing `config` through:

```csharp
_projectManagement = new WorkspaceProjectManagementTools(new WorkspaceProjectManagementImpl(
    workspaceManager, solutionManagementEngine, dependencyEngine, projectConsistencyEngine,
    structuralRefinementEngine, logger));
```

So `SentinuelConfiguration` exists in the chain, just not threaded into the one class that actually
contains the unresolved call sites. The architecturally correct fix is either (a) add a
`SentinelConfiguration` parameter to `WorkspaceProjectManagementImpl`'s constructor and pass
`config` through from `WorkspaceTools`, or (b) construct `SolutionStructureEngine` once in
`WorkspaceTools` and pass the already-built instance down instead of raw engine references.

Both options require **adding a new constructor parameter to an existing, already-DI-wired class**
- exactly the operation with no tool support:

- `ConstructorParameter(add, ...)` was attempted (dryRun:true) on both `WorkspaceProjectManagementImpl`
  and `WorkspaceTools` directly (adding a new `solutionStructureEngine` param alongside the existing
  ones, as a parallel-add rather than a retype, to test if that path was viable) - both rejected
  with `ValidationFailed`:
  - On `WorkspaceProjectManagementImpl`: CS7036 at `WorkspaceTools.cs:68` (the inline `new
    WorkspaceProjectManagementImpl(...)` call site now missing the new required arg).
  - On `WorkspaceTools`: CS7036 across 16 test files that construct `WorkspaceTools` directly,
    e.g. `ApplyDiffSizeGuardTests.cs:44`, `RunTestTests.cs:23`, `ReplaceSnippetSizeGuardTests.cs:24`,
    `ReplaceSnippetBatchTests.cs:22`, `UndoLastApplyTests.cs:63`, `ReadFileTests.cs:46`,
    `MutatingToolRejectionMessageTests.cs:58`, `ListSolutionItemsAllTests.cs:24`,
    `ListProjectFrameworkTargetsTests.cs:37`, `GetOperationDetailTests.cs:42`,
    `GetMethodSourceTests.cs:44`, `CreateFileDeleteFileTests.cs:26`, `BatteryTwentyTests.cs:38`,
    `GetScanResultTests.cs:50`, `MigrationScanResultTests.cs:113`, `ComprehensiveToolTests.cs:112`.
- `ConstructorParameter` and `MethodSignature` schemas were both read directly (not assumed from
  memory) - confirmed neither tool has a "retype an existing parameter's declared type in place"
  operation. `ConstructorParameter` supports `add` (new param at the end, optional `defaultValue`/
  `nullDefault` so old call sites still compile), `remove` (last param only, only if unused
  elsewhere), `view`. `MethodSignature` supports `add` (append only), `remove` (last param only,
  and only if no call site uses named args for it), `view`. Neither supports changing an existing
  parameter's type, nor does either support *adding a required param with automatic cascading
  updates to every existing inline-construction call site* - `defaultValue`/`nullDefault` avoids the
  compile break but does not solve the actual problem (the field still wouldn't be populated at
  those 16 test call sites, so the fold's own call sites inside `WorkspaceProjectManagementImpl`
  would still have nothing valid to reference if a default/null value flows through).

This is not the same defect as any of the three already-documented `MoveMember` defects (duplicate
field on target collision, cross-nested-class false CS0120, wildcard `callSiteFixups` corruption) -
those all concern `MoveMember`'s own rewrite logic being wrong; this is a genuine capability gap in
the surrounding tool family (`ConstructorParameter`/`MethodSignature`) that `MoveMember` merely
surfaces when its call-site resolution runs out of options.

## What this blocks in group 6

- `DependencyEngine` -> `SolutionStructureEngine` fold: blocked (all 8 unresolved sites trace to
  this exact chain).
- `ProjectConsistencyEngine` -> `SolutionStructureEngine` fold: very likely blocked by the identical
  chain - `ProjectConsistencyEngine` is injected into the same `WorkspaceTools` ->
  `WorkspaceProjectManagementImpl` constructors alongside `DependencyEngine` (confirmed present in
  both constructors' parameter lists above). Not yet attempted as a real `MoveMember` call this
  session (per the coordinator's standing order: stop at the first new tool error, do not keep
  attempting further folds that look likely to hit the same wall) - flagging as "very likely
  blocked, not confirmed" rather than "confirmed blocked."
- `SolutionManagementEngine` -> `SolutionStructureEngine` fold: same likely-blocked status, same
  reasoning (also present in both constructors above). Not attempted.
- NOT blocked / can proceed independently: `BreakingChangeEngine` and `ArchitecturalEngine` folds
  (neither engine appears in `WorkspaceProjectManagementImpl`'s or `WorkspaceTools`'s constructor
  parameter lists, so they do not share this specific DI chain - though `ArchitecturalEngine` has
  its own distinct complication, being in `RoslynSentinel.Advanced` while the merge target is in
  `RoslynSentinel.Basic`, which is a separate, not-yet-investigated question). The
  `GetPublicApiSurfaceAsync` duplicate-deletion (`DiscoveryEngine` vs `BreakingChangeEngine`) is
  also independent of this blocker.

## What would fix this (environment-side, not attempted - out of this task's scope per standing order)

- A `ConstructorParameter(retype)` or `ConstructorParameter(add, cascadeToCallSites: true)` operation
  that can add a required parameter to an existing class AND automatically thread it through every
  inline-construction call site up the chain (constructing/obtaining the new dependency at each
  call site rather than just failing closed) - this is the direct analogue of what `MoveMember`
  already does for call-site rewriting, but for constructor-parameter changes instead of member
  moves.
- Failing that, a narrower `ConstructorParameter(add)` variant that accepts a `callSiteFixups`-style
  map (same shape as `MoveMember` already has) so the caller can supply, per call site, either a
  variable name already in scope or a construction expression - letting the 16 test files and the
  `WorkspaceTools` inline chain be fixed in the same call that adds the parameter, instead of
  requiring the two steps to succeed independently (which is impossible today, since neither step
  can succeed until the other already has).

## Self-inflicted bypass disclosed (not a blocker, per CLAUDE.md's recovery clause)

Used the raw `Grep` tool (not the MCP `Search` tool) once, on `WorkspaceProjectManagementImpl.cs`,
searching for `_dependencyEngine\.|_projectConsistencyEngine\.|_solutionManagementEngine\.`. It
returned only "Found 1 file" with no content. Recognized as a dogfooding-mandate violation
immediately; switched to the MCP `Search` tool (mode:text) for the identical query on the identical
file, which worked correctly (5 matches with line numbers/previews). No residual tool-side anomaly
resulted - all subsequent MCP calls behaved normally. Reported here per the recovery clause; no
blocker doc was written for this and the task was not stopped on account of it (this doc is for the
separate, genuine DI-chain/no-retype-tool gap above, not for the bypass).

## Related

- `.claude/plans/enumerated-jumping-babbage.md` - group 6 ("Solution/project structure merge") plan
  text.
- `docs/current/blockers/blocking_error_movemember_duplicate_field_on_target_collision.md`,
  `docs/current/blockers/blocking_error_movemember_batch_false_cs0120_cross_nested_class.md`,
  `docs/current/blockers/blocking_error_movemember_callsitefixups_wildcard_corrupts_source.md` -
  the three prior, distinct `MoveMember` defects; this is a fourth, different class of gap
  (constructor-parameter tooling, not `MoveMember`'s own rewrite logic).
- `RoslynSentinel.Basic/WorkspaceProjectManagementImpl.cs` (constructor, `ListSolutionItems:136,176`),
  `RoslynSentinel.Server.Basic/WorkspaceTools.cs` (constructor, `_projectManagement` construction
  line) - the two files at the root of the chain.
