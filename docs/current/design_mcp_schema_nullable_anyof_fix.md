---
name: design-mcp-schema-nullable-anyof-fix
description: "Findings on how to make RoslynSentinel's nullable-string tool parameters emit anyOf instead of a type:[X,null] array in their JSON Schema, for cross-MCP-client portability. Deferred — not implemented."
metadata:
  type: design
  status: deferred
  discoveredDuring: investigation of blocking_error_methodsignature_add_rejects_required_trailing_cancellationtoken.md (see docs/obsolete/blockers/)
---

## Background

While investigating [[project_methodsignature_null_default_bug]] (closed as an upstream Claude
Code client bug, [anthropics/claude-code#81911](https://github.com/anthropics/claude-code/issues/81911)),
MCP Inspector's schema-portability linter flagged 6 parameters on `MethodSignature` alone
(`paramName`, `paramType`, `defaultValue`, `contextSnippet`, `lineBefore`, `lineAfter` — and by
extension, every nullable-string parameter on every RoslynSentinel MCP tool) for this warning:

> `type` is an array (`["string","null"]`). The array form is legal JSON Schema, but several MCP
> clients read `type` as a single string and either reject the tool or drop the constraint. Fix:
> split it into `anyOf` branches, each with a single `type` — `{"anyOf": [{"type": "string"},
> {"type": "null"}]}`.

This warning turned out **not** to be the cause of the null-default bug (MCP Inspector itself
successfully round-tripped the exact `type:["string","null"]` schema that triggers the bug via
Claude Code) — but it's a real, separate portability issue worth fixing on its own merits. This
doc captures the research into how to fix it, done via a background research agent, so the work
isn't lost even though implementation is deferred as "complicated, tackle separately" per user
direction on 2026-09-04.

## The finding: no built-in flag exists; requires bypassing `WithTools<T>()`

Researched against the exact pinned versions in this repo: `ModelContextProtocol.AspNetCore`
2.2.0 (pulling in `ModelContextProtocol` and `ModelContextProtocol.Core` 2.2.0 transitively) and
`Microsoft.Extensions.AI.Abstractions` 10.8.3, via decompiled source.

**No configuration flag produces `anyOf` automatically.** `Microsoft.Extensions.AI.
AIJsonSchemaCreateOptions` (a `sealed record`) exposes:
- `TransformSchemaNode` — a `Func<AIJsonSchemaCreateContext, JsonNode, JsonNode>?` per-node
  callback invoked bottom-up during schema generation.
- `IncludeParameter`, `ParameterDescriptionProvider` — filtering/description hooks, unrelated.
- `TransformOptions` (`AIJsonSchemaTransformOptions`) — a **post-generation** pass with named
  flags: `ConvertBooleanSchemas`, `DisallowAdditionalProperties`, `RequireAllProperties`,
  `UseNullableKeyword`, `MoveDefaultKeywordToDescription`. `UseNullableKeyword` converts
  `type:[X,"null"]` to OpenAPI-3.0-style `"nullable": true` — **not** JSON Schema `anyOf`, and
  doesn't satisfy the Inspector warning either.
- `IncludeSchemaKeyword` — unrelated.

So the only way to get `anyOf` output is the `TransformSchemaNode` callback, rewriting the schema
tree by hand.

**The harder problem: `WithTools<T>()` can't carry this option at all.** Decompiling
`Microsoft.Extensions.DependencyInjection.McpServerBuilderExtensions` (lives in the
`ModelContextProtocol` 2.2.0 meta-package) shows every `WithTools` overload
(`WithTools<TToolType>()`, `WithTools(builder, target, ...)`,
`WithTools(builder, IEnumerable<McpServerTool>)`, `WithTools(builder, IEnumerable<Type>, ...)`,
`WithToolsFromAssembly(...)`) only accepts a `JsonSerializerOptions?` — none of them expose a way
to pass `AIJsonSchemaCreateOptions` through. Internally each builds its own
`McpServerToolCreateOptions { Services = services, SerializerOptions = serializerOptions }` per
`[McpServerTool]`-attributed method, leaving `SchemaCreateOptions` unset with no way to override it
from the call site RoslynSentinel currently uses
(`mcpBuilder.WithTools<SentinelRefactoringTools>()` etc., in
`RoslynSentinel.Server.Basic\ServiceRegistrationExtensionsBasic.cs` and the equivalent Advanced
file).

The hook does exist one layer down: `ModelContextProtocol.Server.McpServerToolCreateOptions` (in
`ModelContextProtocol.Core`) has `public AIJsonSchemaCreateOptions? SchemaCreateOptions { get; set; }`,
and `AIFunctionMcpServerTool.CreateAIFunctionFactoryOptions` maps it straight through
(`JsonSchemaCreateOptions = options?.SchemaCreateOptions`) into the `AIFunctionFactoryOptions`
passed to `AIFunctionFactory.Create(method, target, factoryOptions)`. It's just unreachable via the
convenience `WithTools<T>()` API RoslynSentinel currently uses everywhere.

## Two implementation paths, both requiring touching every tool-registration call site

**Path A — manual registration, bypassing `WithTools<T>()` entirely.** Replace each
`mcpBuilder.WithTools<SomeToolClass>()` call with hand-rolled reflection that mirrors what
`WithTools<T>()` does internally, but sets `SchemaCreateOptions`:

```csharp
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

public static class SchemaFixups
{
    public static readonly AIJsonSchemaCreateOptions AnyOfNullableOptions = new()
    {
        TransformSchemaNode = (AIJsonSchemaCreateContext ctx, JsonNode schema) =>
        {
            if (schema is JsonObject obj
                && obj.TryGetPropertyValue("type", out var typeNode)
                && typeNode is JsonArray typeArray
                && typeArray.Count == 2
                && typeArray.Any(t => (string?)t == "null"))
            {
                var nonNullType = typeArray.First(t => (string?)t != "null");
                obj.Remove("type");
                obj["anyOf"] = new JsonArray(
                    new JsonObject { ["type"] = nonNullType!.DeepClone() },
                    new JsonObject { ["type"] = "null" });
            }
            return schema;
        }
    };

    public static IMcpServerBuilder WithToolsAnyOfNullable<TToolType>(this IMcpServerBuilder builder)
    {
        foreach (var method in typeof(TToolType).GetMethods(
                     BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is null) continue;

            builder.Services.AddSingleton<McpServerTool>(services =>
            {
                var options = new McpServerToolCreateOptions
                {
                    Services = services,
                    SchemaCreateOptions = AnyOfNullableOptions
                };
                return method.IsStatic
                    ? McpServerTool.Create(method, target: null, options)
                    : McpServerTool.Create(
                        method,
                        createTargetFunc: r => ActivatorUtilities.CreateInstance(
                            ((MessageContext)r).Services, typeof(TToolType)),
                        options);
            });
        }
        return builder;
    }
}
```
Every `mcpBuilder.WithTools<X>()` call site in `ServiceRegistrationExtensionsBasic.cs` /
`ServiceRegistrationExtensionsAdvanced.cs` would change to `mcpBuilder.WithToolsAnyOfNullable<X>()`.
This re-derives name/description/attribute wiring that `WithTools<T>()` currently handles
(`AIFunctionMcpServerTool.DeriveOptions` internally) — risk of subtly diverging from the SDK's own
attribute-processing behavior (e.g. `[Description]`, `[McpServerTool(Name=...)]`,
`[Produces]`/custom attributes RoslynSentinel relies on) unless carefully mirrored.

**Path B — post-hoc schema decorator.** Keep every `WithTools<T>()` call as-is; instead register a
DI decorator over `McpServerTool` (or a startup pass over the built tool list) that walks each
registered tool's `ProtocolTool.InputSchema` (parse the `JsonElement` into a mutable `JsonNode`,
apply the same `type:[X,"null"]` → `anyOf` rewrite recursively, replace `InputSchema`). Cheaper to
retrofit (one shared pass, zero changes to existing registration call sites) at the cost of a
second full JSON walk per tool at startup and being one step further from the SDK's own generation
pipeline (transforms output rather than steering generation).

**Recommendation when this is picked back up:** Path A is cleaner (fixes at the source, uses the
SDK's own intended extension point) but touches ~9+ registration call sites across both Basic and
Advanced server projects and needs careful side-by-side testing against `WithTools<T>()`'s current
behavior (tool names, descriptions, `[Produces]`/`[Consumes]`/custom attribute handling) to avoid
regressions. Path B is lower-risk to introduce incrementally (wrap, verify schemas improve, keep
existing registration code untouched) and easier to revert if it causes issues, at the cost of
being a slightly hackier fix. Do this as its own scoped task, not bundled with anything else — it
touches server startup/registration code shared by every tool, so a mistake here has a large blast
radius (could break tool discovery entirely, as nearly happened once already this investigation
with an unrelated theory about `RequestContext<CallToolRequestParams>` breaking registration — see
[[feedback_verify_before_theorizing_on_tool_errors]]).

## Key files / versions for whoever picks this up
- `RoslynSentinel.Server.Basic\ServiceRegistrationExtensionsBasic.cs` and
  `RoslynSentinel.Server.Advanced\ServiceRegistrationExtensionsAdvanced.cs` — the `WithTools<X>()`
  call sites (9+ in Advanced alone).
- NuGet packages pinned: `ModelContextProtocol.AspNetCore` 2.2.0, `Microsoft.Extensions.AI.
  Abstractions` 10.8.3. Any implementation should re-verify these API shapes haven't changed if the
  package versions have moved on by the time this is picked up.
- `C:\Users\Administrator\.nuget\packages\modelcontextprotocol\2.2.0\lib\net10.0\
  ModelContextProtocol.dll` — where `WithTools<T>()` actually lives (the meta-package, pulled in
  transitively — not `ModelContextProtocol.Core`, which is where `McpServerTool.Create(...)` and
  `McpServerToolCreateOptions` live instead).
