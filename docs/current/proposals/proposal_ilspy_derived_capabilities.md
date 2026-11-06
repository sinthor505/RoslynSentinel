# Add metadata symbol lookup, decompiled source, and field access and type exposure queries, drawing on ILSpy

**Status:** PROPOSED 2026-11-05. Design decisions are settled (see Decisions); nothing is built. The spike has run (see Spike results).

## Decisions

Settled by the maintainer on 2026-11-05:

1. **The decompiler is gated.** `GetExternalSource` and everything that depends on the package stay off by default.
   Reading compiled dependencies is not the intended workload for weak models doing implementer-sized tasks.
2. **A type returns whole-type source.** The wire result marshaller handles size; there is no refusal round trip.
3. **Decompiled output omits XML docs.** `InspectSymbol` covers documentation.
4. **`reads`, `writes` and `refTaken` cover fields and properties.**

## Motivation

ILSpy paths below are relative to the local clone at `C:\Users\Administrator\source\repos\ILSpy`.

### Agents cannot see compiled dependencies

An agent asking about a NuGet or framework API has no tool that answers, so it guesses or leaves the tool
surface. That is an environment gap under the project mission, not a model failure.

- `Search(mode: symbol, query: StringBuilder, symbolKind: type)` returns `NotFound`. Name lookup uses
  `compilation.GetSymbolsWithName`, which sees only source declarations, then keeps `l.IsInSource` locations
  (`RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs:275`, `:309`).
- `GetTypeInfo` has the same limit: `GetTypeMembersDetailAsync` returns an empty list when that lookup finds
  nothing (`SymbolNavigationEngine.cs:711-723`).
- `GetMethodSource` requires a file. Its not-found helper already calls `SymbolFinder.FindDeclarationsAsync`,
  then drops every candidate with no source location
  (`RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs:130`, `:133`, `:139-143`). It then reports that no
  such method "is declared in any project of the loaded solution" (`:169`), with no pointer to the referenced
  assembly that may hold it.
- `InspectSymbol` can reach a metadata symbol from a source snippet, but reports only kind, signature,
  accessibility and summary, with no members or body (`SymbolNavigationEngine.cs:420-570`, from the review run).
- Maintainers already reach for `ilspycmd` by hand for this
  (`docs/current/proposal_changesignature_full_roslyn_service.md:286`).

### Nothing answers "who writes this field?"

- ILSpy offers Read By, Assigned By and Address Taken By (`ICSharpCode.ILSpyX/Analyzers/Builtin/FieldAccessAnalyzer.cs:38-75`).
- A text search of `RoslynSentinel.*` for `ValueUsageInfo|IsWrittenTo|SymbolUsageInfo` finds nothing.
- `TraceVariableLifetime` takes a file, a variable name and a line, and covers locals and parameters
  (`RoslynSentinel.Tools.Advanced/IntelligenceTools.cs:243-252`; the scope limit is from the review run).
- The region-based `WrittenInside` data-flow results do not answer a solution-wide question.

An agent making a field `readonly`, or inlining it, has to read every reference by hand.

### Nothing answers "which members expose this type?"

- ILSpy's Exposed By scans fields, properties, events and methods for a type in their signatures
  (`ICSharpCode.ILSpyX/Analyzers/Builtin/TypeExposedByAnalyzer.cs:47-109`).
- `QuerySymbolRelationships(methodsByReturnType)` covers method return types only, and matches by substring on
  the type's name and display string, not by symbol (`RoslynSentinel.Engines.Basic/DiscoveryEngine.cs:689`, `:707`).

## Proposal

Four independent slices, in this order. Slices 1, 3 and 4 add no dependency.

### Slice 1: find and outline metadata symbols (no dependency)

- Add one shared resolver in `Engines.Basic`: source lookup first, and only if that is empty,
  `SymbolFinder.FindDeclarationsAsync`, keeping non-source results. A doc comment ID goes through
  `DocumentationCommentId.GetFirstSymbolForDeclarationId`.
- The resolver dedupes by doc comment ID and assembly identity. `FindDeclarationsAsync` per project returned `StringBuilder`
  23 times on this solution, once per project, so without this every framework name would come back `Ambiguous`.
- `GetTypeInfo` uses the resolver, so a metadata type returns its members. `TypeMemberDetail` already has nullable
  `FilePath` and `Line` (`SymbolNavigationEngine.cs:80-92`), and the method already walks metadata base types.
  **No new DTO.**
- `Search(mode: symbol)` and the `GetMethodSource` not-found helper keep their source-only results, but a zero
  result now names the metadata hit: the fully qualified name, the assembly, and the next call as a
  `ToolCallHint`. It stays an error or an empty result, because the symbol has no source to return.
- The next call must be one the agent can make. `GetTypeInfo` is in the `projectAdmin` on-demand toolset in claude-lean
  (`RoslynSentinel.Common/ToolsetCatalog.cs:51-56`), so when it is inactive the hint names the
  `McpToolsetControl(toolSet: projectAdmin, enabled: true)` call first. `WriteToolAdvice_UnderAllowList_NamesOnlyAllowedTools`
  (`RoslynSentinel.Tests.Server/ClaudeLeanModeTests.cs:250`) is the existing pattern for advice that respects the allow list.
- The fallback on zero-result paths has a time budget, because the first symbol query after a load costs seconds (see Spike
  results). It stops at the first project with a hit. If the budget expires, the message says referenced assemblies were not
  checked, rather than implying the name exists nowhere.
- These messages never name `GetExternalSource`, so weak models are not steered toward it.

Roslyn symbols are the right source here, not the decompiler: they show the API surface of the project's target
framework, while decompiled code shows whichever runtime is installed.

### Slice 2: `GetExternalSource`, a decompiled view of a metadata symbol

Add a package reference to `ICSharpCode.Decompiler` in `Engines.Advanced`, with the tool class in `Tools.Advanced`, so
the dependency stays out of the Basic projects. The engine reuses the Slice 1 resolver from `Engines.Basic`. Pin the
package: the latest on nuget.org on 2026-11-05 is `11.2.0.10050`. It is MIT, targets `netstandard2.0`, and its runtime
dependencies are only `System.Collections.Immutable` and `System.Reflection.Metadata` 9.0.0
(`ICSharpCode.Decompiler/ICSharpCode.Decompiler.csproj:5,16,103-104`). It has no Roslyn dependency.

Gating:

- Add a new `externalSource` toolset holding only `GetExternalSource`, following the steps in
  `RoslynSentinel.Common/ToolsetCatalog.cs:7-11`, including `ToolClassRegistry.ClaudeLeanOnDemandToolClasses` for the new class.
- Outside claude-lean, make it opt-in through its own `--mode` value and part of `--mode=all`, as `SemanticFindReplace`
  is (`RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs:209`).
- Its engine is registered only inside the tool's `activeToolClasses` DI block, so a server that never enables it does
  not construct the engine.
- `SubAgentTools` is the precedent for an on-demand tool in `Tools.Advanced`: on a Basic server the name is reported as
  unavailable (`ToolsetCatalog.cs:63-64`).

Tool shape:

| Param | Notes |
| --- | --- |
| `reason` | As on every tool. |
| `symbol` (required) | `StringBuilder`, `System.Text.StringBuilder`, `StringBuilder.Append` or a doc comment ID. One param, so a model cannot pair the wrong two. |
| `projectName` (optional) | Whose references to resolve against. Needed only when projects see different assembly versions. |

- More than one match, such as overloads, returns an `Ambiguous` error listing each candidate's doc comment ID and
  signature, matching `GetMethodSource`. The first match is never returned.
- There is no `assemblyPath` param. Only assemblies already referenced by the loaded solution can be read.

Pipeline:

1. **Resolve** with the Slice 1 resolver, then take `OriginalDefinition` so that `List<int>` reaches `List<T>`.
2. **Locate:** `compilation.GetMetadataReference(symbol.ContainingAssembly)` as `PortableExecutableReference`, then
   `FilePath`.
3. **Redirect from reference to implementation.** This step is essential. Running
   `dotnet msbuild -getItem:ReferencePathWithRefAssemblies` on `RoslynSentinel.Common` shows framework references
   resolve to `packs\Microsoft.NETCore.App.Ref\10.0.12\ref\net10.0\`, while Roslyn itself resolves to
   `lib\net10.0\`. In the review run, `ilspycmd` on the ref-pack `System.Collections.dll` gave only `throw null;`
   stubs for `Stack<T>`, while the shared runtime DLL gave real bodies. ILSpy can detect a reference assembly
   (`Metadata/DotNetCorePathFinderExtensions.cs:215`) but does not map one to its implementation. Order:
   - `packs\<Name>.Ref\<ver>\ref\<tfm>\X.dll` to `shared\<Name>\<best>\X.dll`. Best is the exact version, then the
     highest with the same major.minor, then the highest overall (flagged as a mismatch). Skip preview runtimes unless
     the ref pack is itself a preview; this machine has `10.0.0-preview.*` beside `10.0.12`.
   - NuGet `<pkg>\<ver>\ref\<tfm>\X.dll` to `lib\<tfm>\X.dll`, using the nearest compatible TFM.
   - Otherwise, decompile the reference assembly and report `implementation: referenceStubs`, so the agent knows the
     bodies are absent rather than reading `throw null` as behaviour.
4. **Follow type forwards.** Implementation `System.Runtime.dll` forwards `StringBuilder` to `System.Private.CoreLib`.
   Measured: a `T:` ID returns an `ExportedType` handle from `System.Runtime` even with CoreLib in the module list, so types
   always need a manual hop. An `M:` ID returns Nil with `System.Runtime` alone, and a `MethodDefinition` in CoreLib once
   CoreLib is in the list. Resolve the target and retry, with a small hop limit. The decompiler
   must be built on the owning module, because only main-module types can be decompiled
   (`CSharp/CSharpDecompiler.cs:1407`, from the review run).
5. **Decompile** with one `CSharpDecompiler` per call (instances are not thread-safe, `:59`, from the review run),
   using `DecompileTypeAsString` (`:1432`) or `Decompile(IEnumerable<EntityHandle>)` (`:1448`). Pass the tool's
   cancellation token through `CancellationToken` (`:296`). That property and the module-list `FindEntity` used in step 4
   (`IdStringProvider.cs:826`) both exist at tag `v11.2` (checked with `git grep`).
   `ThrowOnAssemblyResolveErrors = false` and `ShowXmlDocumentation = false` (Decision 3; it defaults on).
   Open files with the `CSharpDecompiler(path, settings)` constructor or `new PEFile(path, PEStreamOptions.PrefetchEntireImage)`.
   Both left a NuGet cache DLL unlocked; plain `new PEFile(path)` holds a lock on it until disposed.
6. **Cache** in a bounded LRU keyed by implementation path, file timestamp and doc comment ID.
7. **Return** the result below. Size limits go through the normal wire result marshaller.

```
NEW-DTO: ExternalSourceResult (Result, Engines.Basic.Navigation)
Evaluated: MethodSourceResult - its Envelope describes a containing file, and decompiled output has none;
  TypeMemberDetail - a member row with no body; SearchResult - a location row with a required FilePath
Wire impact: new tool output field(s)
```

Fields: `Symbol`, `DocCommentId`, `AssemblyName`, `AssemblyVersion`, `Implementation`
(`runtime | package | referenceStubs`), `VersionMismatch`, `Source`, `Warnings` (from the decompiler's `.Errors`).

The result deliberately has **no file path and no line numbers**: there is nothing to edit, and returning a path would
invite an edit call or leak an install location. The tool description and the result both say the text is decompiled
and read-only, and may differ from the original source.

Registration needs the usual three parts: `[McpServerToolType]`, `ToolClassRegistry.cs` and the DI blocks.

Discovery: `McpServerStatus(toolListing: inactive)` and the toolset summary are how an agent finds the tool. No other
tool's message or description names it.

### Slice 3: `reads`, `writes` and `refTaken` relationship queries

Add these to `FindUsagesSearchKind` (`RoslynSentinel.Common/ToolEnums.cs:148-151`) and dispatch them in
`SymbolRelationshipImpl.RunRelationshipQueryAsync` (`RoslynSentinel.Tools.Basic/SymbolRelationshipImpl.cs:42-50`).
Include them in the `hideTestHits` set (`:40`). Applies to fields and properties; any other symbol kind gets an
`InvalidArgument` naming the kinds it supports. **No new DTO:** `SearchResult.Detail` carries the classification.

Do not port ILSpy's analyzer: it reads IL opcodes, so it answers for binaries rather than current source. Instead use
`SymbolFinder.FindReferencesAsync`, then classify each location through `SemanticModel.GetOperation`:

| Site | Class |
| --- | --- |
| Assignment target, object initializer, deconstruction target | write |
| Compound assignment, `??=`, `++`/`--` | readWrite (appears in both `reads` and `writes`) |
| `out` argument | write |
| `ref` argument | readWrite + refTaken |
| `in` argument, `ref` local, `ref` return, `&x` | refTaken (plus read where applicable) |
| Member write through a value-type field: `this.s.F = 1` writes `s` too | write on both |
| Non-readonly method call on a value-type field | possibleWrite |
| `nameof`, `cref` | neither; counted in the status message |
| No operation available | `unclassified`; never dropped silently |

- A field initializer is not a reference, so add it to `writes` as a declaration-site row.
- `Detail` names the context (`constructor`, `initializer`, `other`), because "written only in constructors" is the
  question that decides `readonly`.
- Field names like `_logger` occur in many classes, so the query must not merge them. `name` accepts `Type.Member` or a
  doc comment ID, and an ambiguous name returns `Ambiguous`.

### Slice 4: `exposedBy` relationship query

Add this to the same enum. Resolve the type to a symbol, then visit source members that are not private (explicit
interface implementations count): field types, property and indexer types, event types, method returns and parameters.
Recurse through generics, arrays, tuples and pointers, so `Task<List<Foo>>` matches `Foo`. Compare symbols with
`SymbolEqualityComparer`, not names. **No new DTO:** `SearchResult.Detail` holds the position (`returnType`,
`parameter:x`, `property`) and the accessibility. `methodsByReturnType` stays as it is.

### Deferred, not part of this proposal

Each of these is worth a separate decision later:

- Semantic literal search, where `0x10` finds `16` (`ICSharpCode.ILSpyX/Search/LiteralSearchStrategy.cs`).
- Mermaid class and association output (`ClassDiagrammerFactory` in `ICSharpCode.ILSpyCmd`).
- `+must -exclude =exact ~fuzzy` search syntax (`AbstractSearchStrategy.cs:119-223`).
- A "what can I ask about this symbol?" hint, modelled on `IAnalyzer.Show`.
- Attribute-scan tool registration, modelled on `ExportAnalyzerAttribute`, to remove the silently dead tool failure.
- Language-version gating of generated code, using the `DecompilerSettings` feature catalogue
  (`DecompilerSettings.cs:50-70`).
- IL disassembly as a second view of `GetExternalSource`.
- Review-prompt rules from ILSpy's `.github/CODE_REVIEW.md`.

## Alternatives considered, not pursued

- **`ICSharpCode.ILSpyX` as a dependency.** It is published on nuget.org and has no UI dependency, but its analyzers
  read IL from compiled binaries, so results lag source edits. They also use the decompiler's type system, not Roslyn's.
- **Shelling out to `ilspycmd`.** It needs a separately installed global tool and a process per call (0.8-1.7 s cold in
  the review run), and it loses in-process caching and structured errors.
- **Roslyn's metadata-as-source service.** It is not public API and `RoslynSentinel` does not reference Features. A byte
  scan of the Features 5.9.0 DLLs found no decompiler reference. The change-signature work already hit this internal-API
  wall (`docs/obsolete/blockers/blocking_error_changesignature_internal_roslyn_api.md`).
- **Extending `GetMethodSource`.** `filePath` is required and the result implies a file exists, so metadata would need a
  meaningless value in that param. It also covers only methods.
- **A new `Search` mode.** `Search` is a locator, and its input schema was only recently trimmed by about 46%.
- **Decompiling for the outline view.** It would show the installed runtime rather than the target framework API, and
  would make Slice 1 depend on the package.
- **Whole-project decompile to disk (`WholeProjectDecompiler`).** It creates files agents might edit, and needs a
  workspace load to be of any use.
- **SourceLink or symbol-server fetching of real source.** Higher fidelity, but needs network access and many packages
  do not support it. It could later become a first choice ahead of decompiling, without changing the tool shape.

## Open questions

Not measured:

- A warm decompile that reuses a loaded `PEFile` and resolver. The warm figures below open fresh objects each time.
- Whether files opened by the decompiler's assembly resolver for dependencies stay locked.
- What `CSharpDecompiler.Errors` contains in practice.
- How much of the symbol index cost returns after a source edit.
- Output sizes with `ShowXmlDocumentation = false`. The spike reports doc comments in the output, so sizes below may fall.

## Spike results

Measured 2026-11-05 with a throwaway project in `c:\tmp\ilspy_spike\`, loading this solution (23 projects) with `MSBuildWorkspace`
and Roslyn 5.9.0, on SDK 10.0.401 and runtime 10.0.12.

**Reference paths.** `MSBuildWorkspace` matches the `dotnet msbuild` query. `System.Runtime` has one path across all 23 projects,
under `packs\Microsoft.NETCore.App.Ref\10.0.12\ref\net10.0\`. `RoslynSentinel.Common` has 199 PE references: 167 under `packs\`,
32 under `.nuget`, 0 under `shared\`.

**`FindDeclarationsAsync` over all projects, sequential:**

| Case | Time |
| --- | --- |
| First miss after load, compilations already built | 2.4 s (`Common` 651 ms, median project 22 ms) |
| First miss after load, compilations not built | 2.4-2.9 s in the query, plus 7.0 s to build compilations, measured separately |
| First hit after load (`StringBuilder`), compilations not built | 5.0 s and 8.1 s |
| Second query | about 36 ms |
| Warm miss or metadata hit | 31-44 ms |
| Warm source hit | 68-78 ms |

The cost is the symbol index, on the first query. Restricting to projects in scope helps little, since one project dominates.

**Decompile, `ICSharpCode.Decompiler` 11.2.0.10050.** Cold is the first run in a process; warm opens fresh objects each run.

| Target | Size | Cold | Warm |
| --- | --- | --- | --- |
| `StringBuilder`, implementation via CoreLib | 134,727 B, 3,045 lines | 4.28 s | 1.34-1.58 s |
| `StringBuilder.Append(string)`, implementation | 558 B, 13 lines | 724 ms | about 700 ms |
| `Enumerable`, implementation | 466,089 B, 14,073 lines | 5.86 s | 1.85-3.49 s |
| `Enumerable.Where`, implementation | 1,607 B, 42 lines | 82 ms | 40-51 ms |
| `StringBuilder`, ref pack only | 86,243 B, 1,404 lines, 97 `throw null` | 1.49 s | 0.32-0.44 s |

The ref pack and implementation here share file version 10.0.1226.42308, and `IsReferenceAssembly()` tells them apart.

**Locks.** Tested on `Castle.Core.dll` from the NuGet cache. See Slice 2 step 5. `Microsoft.Build.Locator.dll` could not be used
because other processes hold it open with no decompiler involved, so a `FileShare.None` probe can fail for unrelated reasons.

**`ReferenceLocation.SymbolUsageInfo` is internal.** Reflection reports it non-public and using it gives `CS1061`, so the Slice 3
classifier stays as designed.

**XML docs** reach `GetDocumentationCommentXml` when an `.xml` sits beside the reference: framework yes, NuGet package-dependent.
`Microsoft.Build.Locator` ships none and returns empty, so `GetTypeInfo` must tolerate that.

**`QuerySymbolRelationships.filePath` cannot pin a symbol.** It only limits the documents scanned, for `attributeUsages` and
`objectCreations` (`RoslynSentinel.Tools.Basic/SymbolRelationshipImpl.cs:44-45`, `RoslynSentinel.Engines.Basic/DiscoveryEngine.cs:320-327`).
Slice 3 relies on `Type.Member` or a doc comment ID.

## Cost / risk

- **Slice 1:** low. Adds a fallback on paths that currently fail. The first query after a load costs seconds, so it needs the
  time budget above; after that it costs about 40 ms.
- **Slice 2:** medium. The redirect is the largest part, and it depends on SDK and NuGet layouts, so a wrong guess must
  surface as `referenceStubs` or `VersionMismatch`, never as silent wrong code. Decompiled output can differ from the
  original. The package offers no API stability promise that the review could find, so upgrades need a compile check. It
  adds about 2.6 MB. Parsing does not execute the assembly, and paths come only from the compilation's references.
- **Slice 3:** medium. The classifier has many edge cases, so it needs a test per table row, and a wrong `writes` answer
  could make a `readonly` change look safe. The `unclassified` and `possibleWrite` classes exist to prevent that.
- **Slice 4:** low.
- **Schema:** Slices 3 and 4 add only enum values, on a tool that is itself on demand in claude-lean. Slice 2 adds one
  gated tool, so it costs prompt tokens only when enabled, plus one new `ToolSetName` value in the `McpToolsetControl`
  schema. A new tool class needs a fresh chat session as well as a server restart.

Tests:

- A framework type reached through a forward, asserting real bodies rather than `throw null`.
- A NuGet `lib` assembly.
- A ref-only package, asserting `referenceStubs`.
- Overloads, asserting `Ambiguous`.
- A nested generic type.
- A decompile failure, asserting no raw exception in `ResultError`.
- A NuGet cache DLL left unlocked after a decompile.
- Output with no XML doc comments.

## Related

- `docs/current/plans/plan_wire_result_marshaller.md` - owns large-result handling for Slice 2.
- `docs/current/proposal_changesignature_full_roslyn_service.md` - prior manual `ilspycmd` use.
- `docs/obsolete/blockers/blocking_error_changesignature_internal_roslyn_api.md` - why internal Roslyn services were avoided.
- `docs/current/docCommentId_description_gap.md` - doc comment ID descriptions, relevant to the `symbol` param.
- `docs/current/proposals/proposal_dto_ownership_role_lineage.md` - DTO rules applied above.
