# Use C# 15 union types for tool success payloads that have several shapes

**Status:** PROPOSED 2026-11-26. Awaiting owner review; depends on a .NET 11 / C# 15 upgrade.

## Motivation

The goal is clearer source, not a richer wire contract. Structured content for the model does not
matter here (the server may move to plain-text results to address the typo/slip issue), and the tool
schema must not grow.

Prior work: `proposals/closed/proposal_structuredcontent_rollout.md` bundled two goals. Its goal (2),
advertising DTOs through `outputSchema`/`StructuredContent`, was trialled and dropped on 2026-09-22.
Structured content is meant for integrations that need a durable output schema. Here it added
return-type constraints and per-call token overhead, and it did not measurably improve model
performance. Its leftovers were removed on 2026-11-26. Its goal (1), real DTOs instead of
`SentinelCallToolResult<object>`'s erased payloads, is what this proposal continues. Unions cover the
case that goal could not reach without a compromise type: a tool whose success has several unrelated
shapes.

When a tool's success payload has more than one shape, the code has three workarounds today. Each of
them hides the set of possible payloads from the reader and the compiler:

- **`object` payloads.** 856 matches for `SentinelCallToolResult<object>` in non-test code, plus 126 in
  tests (text `Search`, 2026-11-26). Examples:
  - `RoslynSentinel.Tools.Advanced/IntelligenceTools.cs:145-190` (`GetCallGraphTool`) assigns three
    different payloads to `SuccessData` (forward, reverse, and a `DocumentEditResult` tree from
    `AntiPatternEngine.cs:2784`).
  - `RoslynSentinel.Engines.Basic/RefactoringToolHelpers.cs:66-190` (`RequireUpdatedText`,
    `GuardEngineResult`, `GuardNothingToDo`, `GuardEngineFailure`) returns a plain string message on
    some paths (`:152`, `:170`) and a result object on others.
  - `RoslynSentinel.Tools.Advanced/AsyncifyTools.cs:236` and `:277` return a summary on one path and
    a page on the other.
- **Compromise DTOs.** `RoslynSentinel.Tools.Basic/EnvelopePageResult.cs:11-52` holds both
  `JsonArray? Items` and `string? Text`, and its doc comment says exactly one is set.
- **Wrapper DTOs added only to unify types**, for example the `*ResultEnvelope` records in
  `RoslynSentinel.Tools.Basic/RefactoringStructuralTools.cs:46-93` and
  `RoslynSentinel.Common/MigrationEnvelope.cs:18-21`.

With `object`, a reader must trace every return path to learn what a tool can return. A downstream
`switch` cannot be checked for exhaustiveness. Adding a new shape breaks nothing at compile time.

## Proposal

### What C# 15 unions give

Measured on SDK 11.0.100 with a scratch console app (`c:\tmp\unionprobe`, outside the repo):

```csharp
public record FooDto(string Name, int Count);
public record BarDto(string[] Files);
public union Payload(FooDto, BarDto);   // existing, unrelated types; no wrapper, no base class
```

| Check | Result |
| --- | --- |
| `JsonSerializer.Serialize(payload)` with a `FooDto` inside | `{"name":"x","count":3}`: the case's own shape, no `Value` wrapper, no type tag |
| An envelope record with a `Payload` property | `{"isError":false,"successData":{"files":["a.cs"]}}` |
| `JsonSchemaExporter` | `anyOf` of the case schemas (not used by this proposal) |
| `JsonSerializer.Deserialize<Payload>` | throws `JsonException`: "JSON value type 'Object' is ambiguous for union type 'Payload' ... Specify a custom type classifier" |
| `p switch { FooDto f => ..., BarDto b => ..., null => ... }` | compiles; the compiler checks that every case is covered |

The probe used `JsonSerializerDefaults.Web` and `DefaultJsonTypeInfoResolver`, not the server's wire
options (open question 1).

A union is a generated struct. It is marked `[Union]`, implements
`System.Runtime.CompilerServices.IUnion` (`object? Value { get; }`) and has one constructor per case.
Value-type cases are boxed. `default(Payload)` holds a null `Value`.

### What the model sees, and where `UseStructuredContent` fits

The model sees each tool's **name, description and `inputSchema`**. `inputSchema` is built from the
method parameters, their types and `[Description]`s, then the `IToolSchemaPatch` pipeline
(`McpToolSchemaPatcher.cs:207-216`). The return type is not part of it.

The return type can reach `tools/list` only through `outputSchema`. Both tool-creation paths end in the
SDK's `AIFunctionMcpServerTool.Create`: `SentinelToolFactory.Create` calls `McpServerTool.Create(function, derived)`
(`SentinelToolFactory.cs:114`), and the marshaller-off path calls `McpServerTool.Create(method, ...)`
(`McpToolSchemaPatcher.cs:188-203`). In SDK v2.4.0 (local clone, tag `v2.4.0`,
`src/ModelContextProtocol.Core/Server/AIFunctionMcpServerTool.cs`, `CreateOutputSchema`):

1. `UseStructuredContent` is not true: no `outputSchema`, whatever `OutputSchemaType` says. The
   attribute doc says so too: "`UseStructuredContent` must also be set to true for this property to
   take effect."
2. `UseStructuredContent` is true and an explicit `OutputSchema` exists (our factory fills it from
   `OutputSchemaType`, `SentinelToolFactory.cs:228-233`): that schema is advertised, and the return
   type is ignored.
3. `UseStructuredContent` is true and there is no explicit schema: the schema of the declared return
   type is advertised. This is the only case where `T` leaks: `SentinelCallToolResult<object>` gives an
   empty `successData` schema, and a union gives an `anyOf` of every case.

No tool reaches case 2 or 3, and none is meant to: structured content was trialled and dropped (see
Motivation, prior work). No tool sets `UseStructuredContent = true` (`ToolResultMarshaller.cs:52-53`).
The trial's leftovers were removed on 2026-11-26. They were `OutputSchemaType` attributes on four tools
(inert, because `UseStructuredContent` was false), the schema-only envelope DTOs behind them, and six
tests skipped with "StructuredContent is currently not implemented". `SentinelToolFactory` keeps its
`OutputSchemaType` branch only for parity with the SDK code it copies.

`UseStructuredContent = false` has one other effect: the SDK appends a return description (from
`[return: Description(...)]`) to the tool description. The repo has no `[return: Description]`.

On the consumer side, AgentLoop never forwards `outputSchema` (no `OutputSchema|outputSchema` match in
`RoslynSentinel.AgentLoop`). Anthropic tool definitions carry only an input schema. Whether Claude Code
drops `outputSchema` is unverified.

**Consequence:** declaring a union, or any typed `T`, on a tool method adds zero schema tokens, and the
result payload is the same size, because a union serializes as its case. Nothing has to be cast to
`object` at the wire handoff to protect the schema. Only `UseStructuredContent = true` could make the
declared type visible, and that option is dropped.

### Where the union is visible at runtime: one `if`

Serialization needs no special case (probe above). What does need one is the code that reads
`SuccessData`'s runtime type. For a union, that code would find the union struct instead of the case
DTO:

| Site | What it does |
| --- | --- |
| `RoslynSentinel.Common/ToolResultMarshaller.cs:601-612` (`SuccessDataTypeOf`) | reflects `SuccessData` and calls `GetType()`; the result feeds `ResultPager.ResolveRecordsPath` (`ResultPager.cs:60-86`) and `SequencePager.ResolveSequence` (`SequencePager.cs:62`, `:110`) to find the `[ResultRecords]` list |
| `RoslynSentinel.Common/SentinelCallToolResult.cs:398` (`Findings`) | `SuccessData as AppliedChangeSummary` |

Add one non-serialized accessor on `SentinelCallToolResult<TSuccess, TError>` and point both sites at
it:

```csharp
// Unwraps a C# union to its active case so runtime-type readers see the case DTO.
[JsonIgnore]
public object? SuccessPayload => SuccessData is IUnion u ? u.Value : SuccessData;
```

This is the "if before the serializer" idea, moved to where it is needed: the reflection readers, not
the serializer. If open question 1 shows that the wire options do *not* serialize unions transparently,
the fallback is one `JsonConverterFactory` for `IUnion` in the wire options (`SentinelToolFactory.cs:73-83`)
that writes `Value` with its runtime type. That is still one chokepoint and needs no per-tool code.

Unaffected, from the investigation:

- AgentLoop reads `successData` through `JsonElement` (`RoslynSentinel.AgentLoop/SubAgentEvalResult.cs:95-230`).
- The plain/XML renderers work from the wire JSON text (`RoslynSentinel.AgentLoop/ToolResultRendering.cs:31-59`).
- The only typed envelope round trip in tests is `RoslynSentinel.Tests.Basic/ResultStatusTests.cs:92`
  (`SentinelCallToolResult<string>`).

The ambiguous-deserialize exception therefore reaches nothing today.

### Declared type on tool methods (owner decision)

This is a readability choice only. Neither option changes the schema or the payload size (section
above).

- **A. Keep `SentinelCallToolResult<object>` on tool methods.** Unions appear only in engine and helper
  return types and in locals. The tool assigns the union (or its `.Value`) to `SuccessData`. This has
  the least churn, but the tool signature still says `object`.
- **B. Declare `SentinelCallToolResult<CallGraphResult>` (a union).** The signature documents the
  payload set. Recommended.

### Conventions

- A union is a DTO of role `Result` (or `Item` when nested). It takes the `*Result` suffix. A new union
  needs the `NEW-DTO:` justification, and its `Evaluated:` line names the case types it unifies. Add
  this to the `dto-conventions` skill.
- Unions are for alternative success shapes only. Failure stays an `IOperationResult` status or a typed
  `ToolException` (dto-conventions section 4), and `ErrorData` keeps its own type. No `union
  Result(Ok, Error)`.
- Every `switch` over a union has a `null` arm instead of `_`. `default(TUnion)` throws
  `SwitchExpressionException` without one, and a `_` arm hides a newly added case.
- Two cases with near-identical JSON shapes cannot be told apart on the wire, because there is no type
  tag. Give each a distinguishing property, or do not union them.

### Prerequisites

1. **Upgrade to .NET 11 and C# 15.** Projects target `net10.0` (for example
   `RoslynSentinel.Common/RoslynSentinel.Common.csproj:4`). `global.json` pins `10.0.400`. SDK 11.0.100
   is installed on this host.
2. **Dog-fooding: RoslynSentinel must handle union declarations before its own source uses them.** The
   pinned Roslyn (`Microsoft.CodeAnalysis.*` 5.12.0, `RoslynSentinel.Common.csproj:22-23`) parses
   `union` as `SyntaxKind.UnionDeclaration`, a `TypeDeclarationSyntax` (probe in `c:\tmp\unionroslyn`).
   Engine code that matches `ClassDeclarationSyntax`/`RecordDeclarationSyntax` or a kind list would skip
   it. Needed: a `union` mode in `Search`, plus union support in `GetFileOutline`, `Member`, `ReadFile(memberName)`,
   `InspectSymbol` and the `.claude/hooks` type detection (`enforce-dto-justification.ps1`). Which of
   these already work is untested (open question 3). This is also a user-facing feature for any C# 15
   codebase.

### Slices

1. .NET 11 / C# 15 upgrade (TFMs, `global.json`, `LangVersion`), build and test baseline, known-warnings refresh.
2. Union support in the Roslyn tools and hooks listed above, with tests on a union fixture.
3. `SuccessPayload` accessor, both readers switched to it, and three pin tests:
   - a union `SuccessData` serializes as the case shape through the wire options;
   - the pager resolves `[ResultRecords]` on the case type;
   - `Findings` sees an `AppliedChangeSummary` inside a union.
4. Pilot: `GetCallGraphTool` (three shapes), with goldens unchanged.
5. Migrate further candidates in slices, beside the old code: the `RefactoringToolHelpers` guard
   helpers (string vs result), then the `AsyncifyTools` summary vs page paths.

`EnvelopePageResult` is not an early candidate. Its `[ResultRecords] Items` is resolved by the pager,
and a union of `JsonArray` and `string` would lose that attribute.

## Alternatives considered, not pursued

- **Keep `object`.** No compile-time list of payloads, no exhaustiveness checks, and readers must trace
  every return path. This is the status quo the proposal replaces.
- **Compromise DTO with a `Kind` field and nullable members.** It puts irrelevant nulls on the wire, and
  nothing stops a member being set for the wrong kind (`EnvelopePageResult` already relies on a doc
  comment for this).
- **A common base record or marker interface.** It forces unrelated DTOs from different layers (Engines,
  Git, Common) into one hierarchy, and the base must live in the lowest layer. STJ polymorphism adds a
  `$type` unless configured, and nothing ensures every derived type is handled.
- **A union library (OneOf or similar).** It adds a dependency, needs a custom converter to serialize
  transparently, and the language now provides unions natively.
- **Closed hierarchies / closed enums** (related C# proposals). Not committed to a release.

## Open questions

1. Do the wire options (a copy of `McpJsonUtilities.DefaultOptions`, `SentinelToolFactory.cs:73-83`)
   serialize a union as transparently as the probe did? The probe used Web defaults with
   `DefaultJsonTypeInfoResolver`. Check before slice 3; if not, use the `IUnion` converter fallback.
2. With `TSuccess` a union struct, `TSuccess? SuccessData` (`SentinelCallToolResult.cs:336-339`) is the
   struct itself, not `Nullable<T>`. Does a default union serialize as `null`, and does anything rely
   on `SuccessData is null`? Untested.
3. Which RoslynSentinel tools already handle `UnionDeclarationSyntax` (outline, member read/replace,
   search kinds, rename of a union and its cases)? Untested.
4. Option A or B for tool method signatures (owner).
5. Does the .NET 11 upgrade itself carry other costs (MSBuildWorkspace on SDK 11, ModelContextProtocol
   2.4.0 on `net11.0`, new analyzer warnings)? Scope it as its own slice.

## Cost / risk

- The upgrade (slice 1) is the largest and least predictable part. The union work itself touches one
  record and two readers in `Common`.
- Wire output is unchanged if open question 1 holds. The pin test in slice 3 guards it.
- Value-type cases box, which is irrelevant at tool-result rates.
- Adopting unions in this repo before slice 2 would break the tools on their own source. Slice 2 is a
  hard gate.
- Future code that deserializes typed results (tests, harness) must use `JsonElement`, the case type,
  or a registered classifier. Deserializing to the union throws when two cases are both JSON objects.

## Related

- `docs/current/proposals/proposal_dto_ownership_role_lineage.md` - DTO survey and phased plan
- `docs/current/findings/closed/finding_engine_result_shapes_primitives_and_wrapper_usage.md` - engine result shape decision
- `docs/current/references/reference_tool_response_pipeline.md` - marshaller, pagers, large-result offload
- `.claude/skills/dto-conventions/SKILL.md` - reuse-first, NEW-DTO justification, suffixes
- `docs/current/proposals/closed/proposal_structuredcontent_rollout.md` - the structured-content/outputSchema rollout; its DTO goal (1) is continued here, its structured-content goal (2) was dropped
- C# 15 unions: https://devblogs.microsoft.com/dotnet/csharp-15-union-types/ and
  https://learn.microsoft.com/dotnet/csharp/language-reference/builtin-types/union
