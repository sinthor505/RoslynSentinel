# Plan: Metadata symbol lookup, gated decompiled source, and field access and type exposure queries

**Status:** DRAFT 2026-11-05. Step 1 (spike) is done and steps 2, 4 and 7 are adjusted for it. Nothing is built.

## Problem

Agents cannot read compiled dependencies, cannot ask who writes a field or property, and cannot ask which members expose a
type. Evidence and design: [proposal_ilspy_derived_capabilities.md](../proposals/proposal_ilspy_derived_capabilities.md).

## Decision

Four independent slices, in the order set by the proposal's Decisions section:

- Metadata lookup through Roslyn, with no new dependency.
- `reads`, `writes` and `refTaken` relationship queries.
- An `exposedBy` relationship query.
- A gated `GetExternalSource` tool on `ICSharpCode.Decompiler`, last because it alone adds a package.

The proposal lists the decompiler second. This plan moves it last so the three steps that serve weak models land first and
none of them waits on the package.

## Execution rules

- Implementation goes through subagents per CLAUDE.md. Load `dispatch-implementer` and measure blast radius before each brief.
- One `Build` or `RunTest` at a time. `Build` 0 errors before each commit.
- Each step is one commit with a `Plan: plan_ilspy_derived_capabilities` trailer; the last carries `Closes:` instead.
- Run the Slice-specific tests for each step, then compare the full run with the baseline of 0 failures.
- A new `[McpServerToolType]` class needs a server restart and a fresh chat session before it can be called live.
- Hints and descriptions name only tools the agent can currently call. No tool's text names `GetExternalSource`.

## Steps

### Step 1 - Spike the unverified items (DONE 2026-11-05)

Results are in the proposal's Spike results section. The project stays at `c:\tmp\ilspy_spike\`, outside the repo.

- Files: a throwaway console project under `c:\tmp\ilspy_spike\`, not in the solution. No repo changes.
- Change: measure and report, against this solution loaded with `MSBuildWorkspace` and Roslyn 5.9.0:
  1. `PortableExecutableReference.FilePath` for a framework type, a NuGet type and `System.Runtime`.
  2. Time for `SymbolFinder.FindDeclarationsAsync` on a miss and a hit, cold and warm, per project and across all projects.
  3. With package `ICSharpCode.Decompiler` 11.2.0.10050: map `T:System.Text.StringBuilder` and one member through
     ref pack, implementation DLL and type forward to `System.Private.CoreLib`, then decompile. Report the size and time for
     that and for `System.Linq.Enumerable`, cold and warm.
  4. Whether the file is left unlocked after decompiling a NuGet cache DLL.
  5. Whether `ReferenceLocation.SymbolUsageInfo` is public in Roslyn 5.9.
  6. Whether XML docs reach `GetDocumentationCommentXml` for a NuGet reference and a framework reference.
- Done when: every item has a measured answer, written into the proposal's Unverified list, and steps below are adjusted if
  any answer contradicts them. Whether `QuerySymbolRelationships.filePath` can pin a symbol is answered by reading source.

### Step 2 - Metadata symbol resolver

- Files: `RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs` (or a new `MetadataSymbolResolver` beside it), tests in
  `RoslynSentinel.Tests.Battery.Basic`.
- Change: resolver that tries source lookup, then `FindDeclarationsAsync` keeping non-source results, and accepts a doc comment
  ID. Results are deduped by doc comment ID and assembly identity. Used by nothing yet.
- Done when: tests pass for a source type, a framework type seen from several projects (one result, not `Ambiguous`), a NuGet
  type, a doc comment ID, and a name that exists nowhere.

### Step 3 - `GetTypeInfo` on metadata types

- Files: `RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs` (`GetTypeMembersDetailAsync`),
  `RoslynSentinel.Tools.Basic/SymbolNavigationImpl.cs`.
- Change: fall back to the resolver when source lookup finds nothing. `TypeMemberDetail.FilePath` and `Line` are null for
  metadata members. Reuses `TypeMemberDetail`; no new DTO.
- Done when: `GetTypeInfo` on `StringBuilder` lists its public members, and `Person` returns the same output as before.

### Step 4 - Metadata hits in zero-result messages

- Files: `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs` (`BuildMethodNotFoundErrorAsync`, `:130-169`), the
  `Search(mode: symbol)` zero-result path.
- Change: name the fully qualified symbol and assembly and give the next call. When `GetTypeInfo` is inactive, name the
  `McpToolsetControl` call first, following `WriteToolAdvice_UnderAllowList_NamesOnlyAllowedTools`. The fallback stops at the first
  project with a hit and has a 3 s budget, because the spike measured 2.4-8 s for the first query after a load and about 40 ms after
  that. Restricting to projects in scope would not help, because one project dominates. If the budget expires, the message says
  referenced assemblies were not checked.
- Done when: tests cover both states, active and inactive, and an expired budget. Measure a zero-result call after a source
  edit, which the spike did not cover, and tune the budget if it is wrong.

### Step 5 - `reads`, `writes` and `refTaken`

- Files: new classifier in `RoslynSentinel.Engines.Basic`, `RoslynSentinel.Common/ToolEnums.cs:150`,
  `RoslynSentinel.Tools.Basic/SymbolRelationshipImpl.cs:40-50`, tests.
- Change: per the proposal's Slice 3 table. Rows are `SearchResult`, with the class and context in `Detail`; no new DTO. Applies
  to fields and properties. An ambiguous name returns `Ambiguous`. Senior lane, because it is a new algorithm.
- Done when: one test per table row passes, including `possibleWrite`, `unclassified` and the initializer row.

### Step 6 - `exposedBy`

- Files: `RoslynSentinel.Engines.Basic/DiscoveryEngine.cs`, `RoslynSentinel.Common/ToolEnums.cs:150`,
  `RoslynSentinel.Tools.Basic/SymbolRelationshipImpl.cs`, tests.
- Change: per the proposal's Slice 4. Symbol comparison, recursing through generics, arrays, tuples and pointers. No new DTO.
- Done when: tests cover each member position, `Task<List<Foo>>`, explicit interface implementations, and a same-named type in
  another namespace that must not match.

### Step 7 - Gated `GetExternalSource`

- Files: `ICSharpCode.Decompiler` package reference in `RoslynSentinel.Engines.Advanced`; new engine there; new tool class in
  `RoslynSentinel.Tools.Advanced`; `RoslynSentinel.Common/ToolsetCatalog.cs` and the other four places its header lists;
  `Server.Advanced/ToolClassRegistry.cs`; the DI block; tests.
- Change: per the proposal's Slice 2 pipeline. May split into smaller commits: redirect and forward logic first, then the
  decompile and cache, then the tool and gating. Senior lane. Carries the `ExternalSourceResult` NEW-DTO block from the proposal.
  From the spike: type IDs always need a manual forward hop; open files with `PrefetchEntireImage` or the path constructor; set
  `ShowXmlDocumentation = false`; a whole `Enumerable` is about 466 KB before that setting, so the tool description points at
  member lookups first. Check whether reusing a loaded `PEFile` cuts the 1.3-3.5 s warm cost before designing the cache.
- Done when: tests from the proposal's Cost / risk list pass, `Build(level: fullBuild)` is clean, the tool is absent from the
  default tool list and appears after `McpToolsetControl(toolSet: externalSource, enabled: true)`, and a fresh session calls it
  live on `StringBuilder`.

## Out of scope

The proposal's Deferred list: literal search, Mermaid output, search syntax, the "what can I ask" hint, attribute-scan
registration, language-version gating, IL view and review-prompt rules.

## Risks and open decisions

- The reference-to-implementation mapping relies on SDK and NuGet folder layouts. It must surface failure as `referenceStubs` or
  `VersionMismatch`, never as silent wrong code.
- Step 5's classifier could make a `readonly` change look safe if it misses a write. Keep `possibleWrite` and `unclassified`
  visible in the output.
- The wire result marshaller plan is still open at Step 7 remainder, which also touches relationship results in `Search`. Re-check
  it before steps 5 and 6.
