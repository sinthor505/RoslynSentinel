# Split SentinelWorkspaceTools / SentinelRefactoringTools for DI-level tool-set granularity

## Context

Model-eval research this session found that tool-schema size itself measurably degrades a local
LLM's tool selection and latency ([[project_granite42_8b_tool_schema_size_isolated]]: 2 tools ~6s
vs 48 tools ~88s on an identical trivial prompt), and a working theory
([[project_sequential_edit_habit_vs_compiler_checks_theory]]) that too many competing tool
names/descriptions dilutes attention toward training-familiar shell-verb tools
(ReadFile/WriteFile/ApplyDiff/Build) away from more precise domain tools
(RenameSymbol/ChangeSignature) even when the model would otherwise reach for them.

The prior granite minimal-toolset test achieved its 2-tool condition by hand-building fake stub
tool schemas sent directly to the LLM API — bypassing this codebase's real DI/MCP registration
entirely. There is currently no way to get a real, small, coherent tool subset through the actual
`AddRoslynSentinelToolsBasic` DI path; the finest existing granularity is "Workspace" (24 tools) or
"Refactor" (15 tools) as whole units, gated by a flat `HashSet<string> activeModes` in
`RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`. This plan splits both
god-classes into cohesive sub-classes so future model-eval tests can compose small, real,
DI-registered tool sets (e.g. ~10-14 tools) instead of relying on a hand-rolled request filter or
fake stub schemas.

## Facts confirmed by reading the actual files (via GetFileOutline + grep, not assumption)

- `SentinelWorkspaceTools.cs`: 3177 lines, 24 live `[McpServerTool]` methods. 13-arg constructor:
  IWorkspaceManager, ValidationEngine, DiffEngine, DiagnosticEngine, SolutionManagementEngine,
  StructuralRefinementEngine, DependencyEngine, ProjectConsistencyEngine, SentinelConfiguration,
  ILogger&lt;SentinelWorkspaceTools&gt;, BuildEngine, SymbolNavigationEngine, TestRunEngine.
- `SentinelRefactoringTools.cs`: 1328 lines, 15 live `[McpServerTool]` methods. 14-arg constructor;
  6 of those params (StandardRefactoringEngine, SemanticRefactoringLibrary,
  GranularRefactoringEngine, CodeStyleEngine, CodeFlowEngine, CodeGenerationEngine) are dead —
  stored in fields, never read by any live tool method.
- **Cross-class coupling**: `SentinelWorkspaceTools.ApplyDiff`/`ApplyUnifiedDiff` call
  `SentinelRefactoringTools.BuildDiffFromPreImages` (3 call sites), which is itself already a thin
  wrapper around a shared `ValidateAndApplyHelper` static. The split eliminates this coupling by
  having the new file-edit class call `ValidateAndApplyHelper.BuildDiffFromPreImages` directly.
- **`RoslynSentinel.Server.Advanced` is fully insulated**: it references these two classes only via
  `typeof(SentinelWorkspaceTools)`/`typeof(SentinelRefactoringTools)` in `ServerStdio.cs`'s
  `ActiveToolTypes` DI smoke-check array, and delegates registration entirely to
  `AddRoslynSentinelToolsBasic` from `ServiceRegistrationExtensionsAdvanced.cs`. Zero direct
  construction under `RoslynSentinel.Server.Advanced/`. As long as both class names, their DI
  singleton registration under "Workspace"/"Refactor", and their public constructor signatures are
  preserved, Advanced needs zero changes.
- **18 direct-construction call sites across 17 test files** (`RoslynSentinel.Tests.Battery`,
  `.Advanced`, `.Asyncify`, `.Basic`) construct `SentinelWorkspaceTools`/`SentinelRefactoringTools`
  via full positional-argument constructors — no named args, so parameter order/count must be
  preserved exactly for these to keep compiling untouched.
- 7 of 8 `RoslynSentinel.Tests.ModelEval` files declare an identical
  `HashSet<string> ActiveModes = {"Refactor", "Workspace"}`. `TranscriptReplayTests.cs` instead
  uses `{"Generation", "Refactoring", "Workspace"}` — "Refactoring" (not "Refactor") matches no
  mode check, a likely pre-existing typo/no-op; flagged here, not in scope to fix.
  `PlanImplementVerifyAgentTests.cs` already has an 11-tool `MinimalToolNames` HashSet used with
  `LlmOptions.MinimalToolSchema` — exactly the kind of hand-rolled response-filtering this refactor
  should make obsolete, and good validation that ~11-14 tools spanning both Workspace and Refactor
  is a realistic minimal-toolset shape.

## Decision 1 — Class names and method groupings

### SentinelWorkspaceTools → 5 new classes (`RoslynSentinel.Server.Basic/`)

- **`WorkspaceFileEditTools.cs`** (7 tools) — ApplyDiff, ApplyUnifiedDiff, WriteFile, DeleteFile,
  RetryFailedChanges, UndoLastApply, ReadFile.
  Deps: IWorkspaceManager, ValidationEngine, DiffEngine, SymbolNavigationEngine,
  `WorkspaceReadNavigationTools` (ReadFile's outline fallback — documented cross-tool-class
  dependency, injected as a singleton like everything else), ILogger.
- **`WorkspaceBuildTestTools.cs`** (3 tools) — GetDiagnostics, Build, RunTest.
  Deps: IWorkspaceManager, DiagnosticEngine, BuildEngine, TestRunEngine, ILogger.
- **`WorkspaceProjectManagementTools.cs`** (7 tools) — ListSolutionItems, ListWorkspaceSolutions,
  LoadSolution, CreateProject, SplitProjectByFolder, ListProjectFrameworkTargets,
  SafeDeleteUnusedSymbol. Deps: IWorkspaceManager, SolutionManagementEngine, DependencyEngine,
  ProjectConsistencyEngine, StructuralRefinementEngine, ILogger.
- **`WorkspaceReadNavigationTools.cs`** (6 tools) — GetMethodSource, GetFileOutline, ListAll,
  SearchSolutionText, GetOperationDetail, GetLargeResult. Deps: IWorkspaceManager, ILogger only —
  confirmed the lightest cluster, none of these six call any injected engine besides
  `_workspaceManager`.
- **`WorkspaceHealthMiscTools.cs`** (2 tools) — GetWorkspaceHealth, Features.
  Deps: IWorkspaceManager, SentinelConfiguration, BuildEngine, ILogger.

Private helpers and record types move with their primary consumer (e.g. `OutlineItem`,
`FileOutlineResult`, `TextSearchMatch` → `WorkspaceReadNavigationTools.cs`; `SolutionItemFile`,
`ProjectInfoEntry` → `WorkspaceProjectManagementTools.cs`). `WriteBlobForApplyAsync` (used by both
the file-edit and project-management clusters) becomes a new static
`OperationBlobHelper.WriteBlobForApplyAsync(ILogger, IWorkspaceManager, ...)`, matching the
existing `ValidateAndApplyHelper` static-helper convention already in this codebase.

### SentinelRefactoringTools → 3 new classes (`RoslynSentinel.Server.Basic/`)

- **`RefactoringSignatureTools.cs`** (4 tools) — RenameSymbol, MethodSignature,
  ChangeAccessibility, ConstructorParameter. Deps: RefactoringEngine, IWorkspaceManager,
  ValidationEngine, SymbolNavigationEngine, ILogger.
- **`RefactoringStructuralTools.cs`** (6 tools) — Member, ModifyEnum, ModifyAttribute,
  ModifyModifier, ModifyBaseType, SyncTypeAndFilename. Deps: RefactoringEngine,
  StructuralRefinementEngine, SymbolNavigationEngine, IWorkspaceManager, ValidationEngine, ILogger.
- **`RefactoringExtractionDocsTools.cs`** (5 tools) — UsingDirective, SummaryComment,
  ExtractLocalVariable, ExtractMethodSafe, GenerateMapping. Deps: RefactoringEngine,
  MsToolAugmentEngine, MappingEngine, IWorkspaceManager, ValidationEngine, ILogger.

The 6 dead engine params (StandardRefactoringEngine, SemanticRefactoringLibrary,
GranularRefactoringEngine, CodeStyleEngine, CodeFlowEngine, CodeGenerationEngine) are dropped from
all 3 new classes — call out explicitly in the commit message as intentional cleanup, not
oversight. `PreviewFileContent`/`RequireUpdatedText` (pure statics) move to a new
`RefactoringToolHelpers` static class.

Every new class keeps its own thin `ValidateAndApplyAsync`/`BuildDiffAsync` wrapper delegating to
the existing `ValidateAndApplyHelper` static (same 1-line-wrapper shape as today, just present in
more files).

## Decision 1-Amendment — Separate MCP surface from implementation (`*Tools` / `*Impl` pairs)

Model-eval and code-quality both point at the same underlying problem beyond raw tool count: today's
`[McpServerTool]` methods mix three different concerns in one method body — MCP attribute/schema
declaration, parameter validation, and actual engine-calling business logic. Some methods are
self-contained, some call one helper, some orchestrate several engines — there's no consistent
layering. Since Decision 7's split already touches every one of the 39 method bodies once (moving
them to new files), this is the cheapest point to also separate MCP surface from implementation —
retrofitting it after the split ships would mean touching all 39 methods a second time.

**Scope for this pass (mechanical shim, not a rewrite)**: each new class from Decision 1 becomes a
pair — a `*Tools` class (MCP surface) and a `*Impl` class (implementation) — not a deeper redesign.
`*Impl` methods keep today's method bodies **verbatim**, including `ToolResult<object>` return types
and direct `ResultError` construction. The `*Tools` wrapper method becomes a 1-line delegate.

```csharp
// WorkspaceBuildTestTools.cs — MCP surface, thin wrapper
[McpServerToolType]
public class WorkspaceBuildTestTools
{
    private readonly WorkspaceBuildTestImpl _impl;

    public WorkspaceBuildTestTools(IWorkspaceManager workspaceManager, DiagnosticEngine diagnosticEngine,
        BuildEngine buildEngine, TestRunEngine testRunEngine, ILogger logger)
    {
        _impl = new WorkspaceBuildTestImpl(workspaceManager, diagnosticEngine, buildEngine, testRunEngine, logger);
    }

    [McpServerTool(Name = "GetDiagnostics")]
    [Produces(DataTag.Report)]
    [Description("Gets compiler diagnostics. ...")]
    public Task<ToolResult<object>> GetDiagnostics(
        [Description(ToolParams.Reason)] string reason,
        [Consumes(DataTag.ProjectName, required: true)][Consumes(DataTag.SourceFilepath, required: false)] ToolScope scope = ToolScope.solution,
        string? scopeName = null, bool summarize = false,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        [ToolOptionAttribute(ToolOptionTag.TopN)] int topN = 20,
        BuildVerifyLevel verify = BuildVerifyLevel.noBuild,
        CancellationToken cancellationToken = default)
        => _impl.GetDiagnostics(reason, scope, scopeName, summarize, maxDetails, topN, verify, cancellationToken);
}

// WorkspaceBuildTestImpl.cs — implementation, today's method body verbatim
public class WorkspaceBuildTestImpl
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly DiagnosticEngine _diagnosticEngine;
    private readonly BuildEngine _buildEngine;
    private readonly TestRunEngine _testRunEngine;
    private readonly ILogger _logger;

    public WorkspaceBuildTestImpl(IWorkspaceManager workspaceManager, DiagnosticEngine diagnosticEngine,
        BuildEngine buildEngine, TestRunEngine testRunEngine, ILogger logger) { /* assign fields */ }

    public async Task<ToolResult<object>> GetDiagnostics(string reason, ToolScope scope, string? scopeName,
        bool summarize, int maxDetails, int topN, BuildVerifyLevel verify, CancellationToken cancellationToken)
    {
        // exact body moved verbatim from today's SentinelWorkspaceTools.GetDiagnostics
    }
}
```

Rules:
- `reason` stays a real parameter on both layers (matches current signatures).
- All `[McpServerTool]`/`[Description]`/`[Consumes]`/`[Produces]`/`[ToolOptionAttribute]` attributes
  live on the `*Tools` wrapper only — zero MCP attributes in `*Impl` classes, from day one.
- `*Impl` classes are plain classes, not `[McpServerToolType]` — not independently invocable as
  tools, only regular DI-constructed objects, which makes them directly unit-testable without going
  through MCP dispatch.
- One `*Impl` per `*Tools`, 1:1 (e.g. `WorkspaceFileEditTools`/`WorkspaceFileEditImpl`,
  `WorkspaceBuildTestTools`/`WorkspaceBuildTestImpl`) — not a separate consolidation of impls across
  tool classes.
- The `SentinelWorkspaceTools`/`SentinelRefactoringTools` legacy facades (Decision 3) delegate to the
  new `*Tools` wrappers as already planned — they don't need to know `*Impl` exists.

**Deferred (explicit non-goal of this pass)**: `*Impl` methods still return `ToolResult<object>` and
construct `ResultError` directly, so they aren't yet MCP-agnostic in their return types — only in
their attributes/entry points. A full domain-type boundary (impl methods return plain domain types
or throw; the `*Tools` wrapper catches exceptions and builds `ToolResult<object>`/`ResultError`)
is a much larger per-method rewrite of every error path, not a mechanical move. Do that later,
per-tool, opportunistically — do not block this plan on it.

**Effect on Decision 7's steps 2/3**: "pure move of tool methods" becomes "split into `*Tools` +
`*Impl` pairs per the shape above." Marginal extra work per method (one delegating line + duplicated
parameter list) during a pass that already touches every method once.

## Decision 1-Amendment-2 — Audit per-method engine usage before finalizing class groupings (open, not yet done)

Decision 2's constructor table groups methods by name-intuition ("project management") rather than by
which engines those methods actually call. A first-pass grep of `WorkspaceProjectManagementTools`'s
planned 7 methods shows each of its 4 non-`IWorkspaceManager` engines is used by only 1-2 methods,
never shared within the class:

- `DependencyEngine` → only `ListSolutionItems`/`ListWorkspaceSolutions`
- `StructuralRefinementEngine` → only `SafeDeleteUnusedSymbol`
- `SolutionManagementEngine` → only `CreateProject`/`SplitProjectByFolder`
- `ProjectConsistencyEngine` → only `ListProjectFrameworkTargets` (leaving anyway — see the
  ListAll-consolidation discussion)

So this class's constructor is wide not because its methods share dependencies, but because the
class bundles several single-engine methods that each happen to drag in one unique engine. This is
the same shape called out for the old 13/14-arg god-classes, just at a smaller scale, and it will
carry straight into the new `*Impl` classes from the amendment above unless addressed.

**Not resolved here** — needs the same fact-finding rigor Decision 1 already applied to the two
god-classes (grep every engine call site per method) applied to all 8 planned classes, not just
this one, before deciding whether to: (a) accept narrow multi-engine constructors as fine since
`*Impl` classes are no longer LLM-schema-visible and only affect testability, (b) regroup methods
across the planned classes by engine-sharing instead of by naming-intuition, or (c) split further
(e.g. one `*Impl` per engine) at the cost of more files/classes for arguably little readability gain
on top of (a). Do this audit as its own step, after Decision 1's file split lands and before or
alongside the `*Impl` extraction, so groupings aren't re-shuffled twice.

## Decision 2 — Constructor dependency distribution

| New class | Ctor deps |
|---|---|
| WorkspaceFileEditTools | IWorkspaceManager, ValidationEngine, DiffEngine, SymbolNavigationEngine, WorkspaceReadNavigationTools, ILogger (6) |
| WorkspaceBuildTestTools | IWorkspaceManager, DiagnosticEngine, BuildEngine, TestRunEngine, ILogger (5) |
| WorkspaceProjectManagementTools | IWorkspaceManager, SolutionManagementEngine, DependencyEngine, ProjectConsistencyEngine, StructuralRefinementEngine, ILogger (6) |
| WorkspaceReadNavigationTools | IWorkspaceManager, ILogger (2) |
| WorkspaceHealthMiscTools | IWorkspaceManager, SentinelConfiguration, BuildEngine, ILogger (4) |
| RefactoringSignatureTools | RefactoringEngine, IWorkspaceManager, ValidationEngine, SymbolNavigationEngine, ILogger (5) |
| RefactoringStructuralTools | RefactoringEngine, StructuralRefinementEngine, SymbolNavigationEngine, IWorkspaceManager, ValidationEngine, ILogger (6) |
| RefactoringExtractionDocsTools | RefactoringEngine, MsToolAugmentEngine, MappingEngine, IWorkspaceManager, ValidationEngine, ILogger (6) |

Down from 13/14 to 2-6 per class. Note: the LLM-facing tool-schema size is controlled by
registration granularity (Decision 4), not constructor size — the DI slimming here is a
testability/code-quality win layered on top, not itself the mechanism that shrinks what the model
sees.

## Decision 3 — Preserve the 18 test call sites: thin backward-compat facade

`SentinelWorkspaceTools` and `SentinelRefactoringTools` become **legacy facades**: same original
public constructor signature (exact types/order), same original `[McpServerTool]` method
signatures (same attributes/descriptions/params), bodies delegating to internally-constructed
instances of the new split classes.

**Why facade over updating 18 call sites**: zero test-file edits, zero risk of an 18-file edit
chasing compile errors one at a time, and Advanced needs zero changes (its `typeof()` smoke-check
and delegated registration both keep working against an unchanged type name/constructor). The
downside — the two files don't shrink to zero, just to pure-delegation shims — is bounded and
worth it; a future opportunistic migration off the facade is out of scope here. Add a "LEGACY
FACADE — add new tools to the split classes, not here" doc comment at the top of both files.

**Rejected alternative — drop the facade, update call sites directly.** Confirmed via grep: 17 real
test files (`RoslynSentinel.Tests.Battery`/`.Advanced`/`.Asyncify`/`.Basic`) construct
`SentinelWorkspaceTools`/`SentinelRefactoringTools` via full positional constructors, plus 4
`typeof(SentinelWorkspaceTools)`/`typeof(SentinelRefactoringTools)` reflection sites
(`RoslynSentinel.Server.Basic/ServerStdio.cs`, `RoslynSentinel.Server.Advanced/ServerStdio.cs`,
`SentinelConsoleMode.cs`, `RoslynSentinel.Tests.Advanced/DependencyInjectionTests.cs`). Going facade-
free would require:
1. Rewriting each of the 17 test files individually — not mechanical, since a test exercising
   methods from more than one new split class needs to construct multiple new instances and dispatch
   calls to the right one; each file needs to be read to determine which new class(es) it actually
   needs.
2. Updating all 4 `typeof()`/assembly-reflection sites to enumerate 8 new types (or switch to
   scanning for `[McpServerToolType]` across the assembly) instead of naming 2 fixed types.
3. Losing the incremental build-checkpoint structure in Decision 7 — splitting the class and
   updating its call sites become one atomic, all-or-nothing unit of work, since the old type
   disappears the moment the split happens (no green build in between).
4. Decision 8's "Advanced needs zero changes" claim would no longer hold — item 2 touches
   `RoslynSentinel.Server.Advanced/ServerStdio.cs` directly, turning it into "small, real, but still
   low-risk changes" instead of no changes.

Rejected: this cost (17 non-mechanical file rewrites, loss of incremental commit safety, a real
Advanced-side change) is much larger than the facade's cost (two permanently-thin delegation files
plus a cosmetic logger-category quirk). Revisit as an optional later cleanup once the split classes
have proven stable in production use — not as part of this plan.

```csharp
[McpServerToolType]
public class SentinelWorkspaceTools
{
    private readonly WorkspaceFileEditTools _fileEdit;
    private readonly WorkspaceBuildTestTools _buildTest;
    private readonly WorkspaceProjectManagementTools _projectMgmt;
    private readonly WorkspaceReadNavigationTools _readNav;
    private readonly WorkspaceHealthMiscTools _healthMisc;

    public SentinelWorkspaceTools(IWorkspaceManager workspaceManager, ValidationEngine validationEngine,
        DiffEngine diffEngine, DiagnosticEngine diagnosticEngine, SolutionManagementEngine solutionManagementEngine,
        StructuralRefinementEngine structuralRefinementEngine, DependencyEngine dependencyEngine,
        ProjectConsistencyEngine projectConsistencyEngine, SentinelConfiguration config,
        ILogger<SentinelWorkspaceTools> logger, BuildEngine buildEngine,
        SymbolNavigationEngine symbolNavigationEngine, TestRunEngine testRunEngine)
    {
        _readNav = new WorkspaceReadNavigationTools(workspaceManager, logger);
        _fileEdit = new WorkspaceFileEditTools(workspaceManager, validationEngine, diffEngine,
            symbolNavigationEngine, _readNav, logger);
        _buildTest = new WorkspaceBuildTestTools(workspaceManager, diagnosticEngine, buildEngine,
            testRunEngine, logger);
        _projectMgmt = new WorkspaceProjectManagementTools(workspaceManager, solutionManagementEngine,
            dependencyEngine, projectConsistencyEngine, structuralRefinementEngine, logger);
        _healthMisc = new WorkspaceHealthMiscTools(workspaceManager, config, buildEngine, logger);
    }

    [McpServerTool(Name = "ApplyDiff")]
    [Produces(DataTag.ChangeId)]
    [Description("...")]  // keep original attribute/description text verbatim
    public Task<ToolResult<object>> ApplyDiff(...) => _fileEdit.ApplyDiff(...);
    // ...one 1-3 line delegating method per original tool method (~24 for Workspace, ~15 for Refactoring)
}
```

**Logger typing**: new split classes take a plain (non-generic) `ILogger` parameter, not
`ILogger<T>`. The facade passes its own `ILogger<SentinelWorkspaceTools>` straight through (it
implements `ILogger`); when a split class is constructed directly by DI under a new fine-grained
mode string, the container supplies its own correctly-typed `ILogger<T>` — same parameter, both
paths work. Cosmetic-only tradeoff: facade-constructed instances log under the facade's category
name, not their own — acceptable since the facade exists for compatibility, not primary use.

`SentinelRefactoringTools`'s facade keeps all 14 original positional params, including the 6 dead
engines, purely so the 4 test call sites using the full 14-arg form keep compiling — it accepts but
doesn't forward them.

## Decision 4 — New ActiveModes mode-string scheme

**Naming: flat strings** (e.g. `WorkspaceFileIO`, `RefactorSignature`), matching the existing flat
style of "Workspace"/"Refactor"/"Modernize"/"Quality" rather than introducing a dotted convention.

Keep "Workspace"/"Refactor" as umbrella strings registering the facade classes (all 7 existing
ModelEval tests and all 17 direct-construction test files stay unaffected — identical behavior).
Add new flat mode strings registering the new split classes directly, with no facade involved, for
opt-in narrow tool sets. Exact shape for
`RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs` (replacing lines 103-113 and
128-136):

```csharp
if (activeModes.Contains("Workspace"))
{
    // Umbrella: full-surface legacy facade — unchanged behavior for existing callers.
    services.AddSingleton<SentinelWorkspaceTools>();
    mcpBuilder.WithTools<SentinelWorkspaceTools>();
    services.AddSingleton<DocumentationTools>();
    mcpBuilder.WithTools<DocumentationTools>();
    services.AddSingleton<SentinelSymbolTools>();
    mcpBuilder.WithTools<SentinelSymbolTools>();
    services.AddSingleton<GitTools>();
    mcpBuilder.WithTools<GitTools>();
}
// Fine-grained Workspace sub-modes — independently opt-in-able, none require "Workspace".
if (activeModes.Contains("WorkspaceFileIO"))
{
    services.AddSingleton<WorkspaceFileEditTools>();
    mcpBuilder.WithTools<WorkspaceFileEditTools>();
    services.TryAddSingleton<WorkspaceReadNavigationTools>();   // ReadFile's outline fallback dep
}
if (activeModes.Contains("WorkspaceBuildTest"))
{
    services.AddSingleton<WorkspaceBuildTestTools>();
    mcpBuilder.WithTools<WorkspaceBuildTestTools>();
}
if (activeModes.Contains("WorkspaceProjectManagement"))
{
    services.AddSingleton<WorkspaceProjectManagementTools>();
    mcpBuilder.WithTools<WorkspaceProjectManagementTools>();
}
if (activeModes.Contains("WorkspaceReadNav"))
{
    services.TryAddSingleton<WorkspaceReadNavigationTools>();
    mcpBuilder.WithTools<WorkspaceReadNavigationTools>();
}
if (activeModes.Contains("WorkspaceHealthMisc"))
{
    services.AddSingleton<WorkspaceHealthMiscTools>();
    mcpBuilder.WithTools<WorkspaceHealthMiscTools>();
}
...
if (activeModes.Contains("Refactor"))
{
    // Umbrella: full-surface legacy facade — unchanged behavior.
    services.AddSingleton<SentinelRefactoringTools>();
    mcpBuilder.WithTools<SentinelRefactoringTools>();
    services.AddSingleton<SentinelAugmentTools>();
    mcpBuilder.WithTools<SentinelAugmentTools>();
}
if (activeModes.Contains("RefactorSignature"))
{
    services.AddSingleton<RefactoringSignatureTools>();
    mcpBuilder.WithTools<RefactoringSignatureTools>();
}
if (activeModes.Contains("RefactorStructural"))
{
    services.AddSingleton<RefactoringStructuralTools>();
    mcpBuilder.WithTools<RefactoringStructuralTools>();
}
if (activeModes.Contains("RefactorExtractionDocs"))
{
    services.AddSingleton<RefactoringExtractionDocsTools>();
    mcpBuilder.WithTools<RefactoringExtractionDocsTools>();
}
```

`WorkspaceReadNavigationTools` can be pulled in as a dependency (via `WorkspaceFileIO`) or directly
(via `WorkspaceReadNav`) — use `TryAddSingleton` (from
`Microsoft.Extensions.DependencyInjection.Extensions`) so registering it from both blocks in the
same call doesn't double-register. Only call `mcpBuilder.WithTools<WorkspaceReadNavigationTools>()`
under `WorkspaceReadNav` specifically — `WorkspaceFileIO` alone should not expose
GetFileOutline/ListAll/etc. as separate MCP tools, only use the instance internally.

## Decision 5 — Shared ActiveModes constant (optional cleanup, do last)

New `RoslynSentinel.Tests.ModelEval/CommonActiveModes.cs`:

```csharp
namespace RoslynSentinel.Tests.ModelEval;

internal static class CommonActiveModes
{
    public static readonly HashSet<string> WorkspaceAndRefactor = new(StringComparer.OrdinalIgnoreCase)
    {
        "Refactor", "Workspace",
    };
}
```

Update the 7 files (`SizeThresholdAgentTests.cs`, `OrderPricingRefactorChainAgentTests.cs`,
`PlanImplementVerifyAgentTests.cs`, `WholeFileRewriteAgentTests.cs`, `PlanThenExecuteAgentTests.cs`,
`OrderPricingRefactorAgentTests.cs`, `PlanOnlyAgentTests.cs`) to reference
`CommonActiveModes.WorkspaceAndRefactor` instead of a locally duplicated literal. Do **not** touch
`TranscriptReplayTests.cs` (different set, includes the likely-dead "Refactoring" typo — out of
scope). Do this last, as its own commit, one file at a time with a build check — pure
de-duplication, zero behavior change, but still not batched blind.

## Decision 6 — Proving the design: example minimal toolset

Illustrative shape for a future `RoslynSentinel.Tests.ModelEval/GraniteMinimalToolsetTests.cs`
(not implemented as part of this plan unless requested separately):

```csharp
// WorkspaceFileIO      -> ReadFile, ApplyDiff, ApplyUnifiedDiff, WriteFile, DeleteFile,
//                          RetryFailedChanges, UndoLastApply (7 tools)
// WorkspaceBuildTest    -> GetDiagnostics, Build, RunTest (3 tools)
// RefactorSignature     -> RenameSymbol, MethodSignature, ChangeAccessibility,
//                           ConstructorParameter (4 tools)
// Total: 14 tools, composed via real DI/ActiveModes — no hand-rolled filter, no fake stub schemas.
private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase)
{
    "WorkspaceFileIO", "WorkspaceBuildTest", "RefactorSignature",
};
```

This directly answers the original motivating problem: a real ~14-tool DI-composed surface,
assembled from three `activeModes.Contains` checks already wired into
`AddRoslynSentinelToolsBasic` by Decision 4.

## Decision 7 — Ordered execution steps with build checkpoints

Each step is its own commit boundary; build to 0 errors before proceeding
(per [[feedback_build_before_commit]]):

1. **Extract shared static helpers first** (no behavior change, unblocks both splits):
   - New `RoslynSentinel.Server.Basic/OperationBlobHelper.cs` — static
     `WriteBlobForApplyAsync(ILogger, IWorkspaceManager, ...)`, logic moved verbatim from
     `SentinelWorkspaceTools.WriteBlobForApplyAsync`.
   - New `RoslynSentinel.Server.Basic/RefactoringToolHelpers.cs` — `PreviewFileContent`/
     `RequireUpdatedText` moved verbatim (already private static, no instance state).
   - Update call sites in both original (not-yet-split) files to call the new statics.
   - **Build checkpoint.**

1.5. **Audit per-method engine usage across all 8 planned classes** (Decision 1-Amendment-2), before
   locking in Decision 2's constructor table — grep every engine field access per method (as already
   done for `WorkspaceProjectManagementTools` above) and decide whether any groupings should change
   before the split happens, so classes aren't re-shuffled twice.

2. **Split `SentinelWorkspaceTools`**:
   - Create the 5 new `*Tools`/`*Impl` file pairs per Decision 1 + Decision 1-Amendment: method
     bodies move verbatim into `*Impl` classes; `*Tools` classes keep all
     `[McpServerTool]`/`[Description]`/`[Consumes]`/`[Produces]` attributes and become 1-line
     delegating wrappers to `*Impl`.
   - Rewrite `SentinelWorkspaceTools.cs` as the facade per Decision 3, delegating to the new `*Tools`
     wrappers (facade doesn't need to know `*Impl` exists).
   - Update the 3 `BuildDiffFromPreImages` call sites to call `ValidateAndApplyHelper` directly.
   - **Build checkpoint** — this is the first point all 17 direct-construction test files and
     Advanced's smoke-check get re-verified against the new facade constructor.

3. **Split `SentinelRefactoringTools`** (same `*Tools`/`*Impl` pattern): create the 3 new file pairs,
   drop the 6 dead engine deps from them (keep them accepted-but-unforwarded in the facade
   constructor), rewrite `SentinelRefactoringTools.cs` as facade. **Build checkpoint.**

4. **Update `ServiceRegistrationExtensionsBasic.cs`** per Decision 4's exact if-block structure.
   **Build checkpoint**, then run `dotnet test` on `RoslynSentinel.Tests.Battery`,
   `.Advanced`, `.Basic`, `.Asyncify` (the 4 projects with direct-construction call sites) to
   confirm facade delegation preserves runtime behavior, not just compile-time shape — a
   build-only check wouldn't catch a dropped-parameter delegation bug.

5. **Optional, own commit**: add `CommonActiveModes.cs`, update the 7 ModelEval files
   (Decision 5). Build checkpoint.

6. **Optional, only if requested separately**: add `GraniteMinimalToolsetTests.cs` (Decision 6).
   Requires a reachable LM Studio instance to actually run; compiles either way.

## Decision 8 — Risk to RoslynSentinel.Server.Advanced: LOW

Advanced touches these two classes only via `typeof()` in `ServerStdio.cs`'s `ActiveToolTypes`
DI smoke-check array, and via delegating to `AddRoslynSentinelToolsBasic` in
`ServiceRegistrationExtensionsAdvanced.cs` — confirmed zero direct construction anywhere under
`RoslynSentinel.Server.Advanced/`. Since the facade preserves exact type names, namespaces, DI
singleton registration under "Workspace"/"Refactor", and constructor signatures, Advanced needs
**no source changes** — a normal solution-wide build after step 2 is sufficient confirmation, no
separate Advanced-specific verification needed.

## Files to create

- `RoslynSentinel.Server.Basic/OperationBlobHelper.cs`, `RefactoringToolHelpers.cs`
- Per Decision 1-Amendment, each class below is a `*Tools`/`*Impl` file pair (e.g.
  `WorkspaceFileEditTools.cs` + `WorkspaceFileEditImpl.cs`) rather than a single file:
  - `WorkspaceFileEditTools.cs`/`Impl.cs`, `WorkspaceBuildTestTools.cs`/`Impl.cs`,
    `WorkspaceProjectManagementTools.cs`/`Impl.cs`, `WorkspaceReadNavigationTools.cs`/`Impl.cs`,
    `WorkspaceHealthMiscTools.cs`/`Impl.cs`
  - `RefactoringSignatureTools.cs`/`Impl.cs`, `RefactoringStructuralTools.cs`/`Impl.cs`,
    `RefactoringExtractionDocsTools.cs`/`Impl.cs`
- `RoslynSentinel.Tests.ModelEval/CommonActiveModes.cs` (optional, Decision 5)
- `RoslynSentinel.Tests.ModelEval/GraniteMinimalToolsetTests.cs` (optional, Decision 6 — only if
  requested as a follow-up)

## Files to modify

- `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` → becomes thin facade
- `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs` → becomes thin facade
- `RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs` → new mode-string if-blocks
- 7 files in `RoslynSentinel.Tests.ModelEval/` (optional, Decision 5)

## Files confirmed NOT needing changes

- Everything under `RoslynSentinel.Server.Advanced/`
- All 17 test files with direct `new SentinelWorkspaceTools(...)`/`new SentinelRefactoringTools(...)`
  construction (facade preserves compatibility)

## Verification

- `dotnet build RoslynSentinel.slnx -c Debug` → 0 errors after every step above.
- `dotnet test` on `RoslynSentinel.Tests.Battery`/`.Advanced`/`.Basic`/`.Asyncify` after step 4 to
  confirm facade delegation is behaviorally correct, not just compiling.
- Build to 0 errors, then commit per [[feedback_build_before_commit]] — one commit per numbered
  step above, not batched.
