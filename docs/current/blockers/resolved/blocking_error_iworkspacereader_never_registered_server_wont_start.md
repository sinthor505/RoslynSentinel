# Blocking error: `IWorkspaceReader` is never registered in the DI container,
# so any server build that includes a reader-migrated engine fails to start

**Status:** FIXED 2026-09-25 -- `RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`
now forwards `IWorkspaceReader` to the shared `PersistentWorkspaceManager` singleton, alongside the
existing `ISolutionProvider`/`IWorkspaceManager` forwards:

```csharp
services.AddSingleton<IWorkspaceReader>(sp => sp.GetRequiredService<PersistentWorkspaceManager>());
```

Verified: solution builds with 0 errors; Advanced/HTTP flavor started directly from the built DLL
(`--transport=http --mode=all`) stays up past `ValidateOnBuild`, logs "Application started", and
listens on port 5100 -- all 17 previously-failing engines (`SemanticSearchEngine`,
`StandardRefactoringEngine`, `CodeFlowEngine`, and the 14 other Advanced engines listed below) now
resolve. Originally discovered 2026-09-25 via a real manual server-start attempt (Advanced/HTTP
flavor), reported by the user with the full `AggregateException` from `WebApplicationBuilder.Build()`.

Filed per CLAUDE.md's blocking-error policy: the server does not start at all with the current
`master` HEAD, for any flavor that reaches `AddRoslynSentinelEnginesAdvanced`/
`AddRoslynSentinelEnginesBasic` with `ValidateOnBuild = true` (see `ServerStartupHelpers.cs`'s
`EnableValidateOnBuild`). This blocks the test-helper work in progress (`TestServiceProviderBuilder`,
`RoslynSentinel.Tests/TestServiceProviderBuilder.cs`) only incidentally -- the real impact is that
the live product does not boot.

## What was observed

User attempted a manual server start (Advanced/HTTP flavor, per `ServerHttp.cs:78`,
`WebApplicationBuilder.Build()`) and got:

```
System.AggregateException: Some services are not able to be constructed (...)
```

with 17 inner `InvalidOperationException`s, all of the shape:

```
Error while validating the service descriptor 'ServiceType: RoslynSentinel.Basic.SemanticSearchEngine
Lifetime: Singleton ImplementationType: RoslynSentinel.Basic.SemanticSearchEngine': Unable to resolve
service for type 'RoslynSentinel.Common.IWorkspaceReader' while attempting to activate
'RoslynSentinel.Basic.SemanticSearchEngine'.
```

One inner exception has different wording but the same root cause:

```
Error while validating the service descriptor '...RoslynSentinel.Advanced.CodeFlowEngine...':
No constructor for type 'RoslynSentinel.Advanced.CodeFlowEngine' can be instantiated using services
from the service container and default values.
```

Confirmed (`RoslynSentinel.Advanced/CodeFlowEngine.cs:14,20`) both of `CodeFlowEngine`'s constructor
overloads take `IWorkspaceReader` as their first parameter -- there is no overload DI could fall back
to, so every overload is rejected for the same reason as the other 16, just phrased differently by
the container ("no constructor" instead of naming the one unresolvable parameter, because here it's
true of all overloads simultaneously).

## Root cause (traced to source)

`grep -n "AddSingleton<IWorkspaceReader" RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs
RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs` -- zero matches in either
file. Compare the two interfaces that *are* forwarded
(`RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs:55-56`):

```csharp
services.AddSingleton<ISolutionProvider>(sp => sp.GetRequiredService<PersistentWorkspaceManager>());
services.AddSingleton<IWorkspaceManager>(sp => sp.GetRequiredService<PersistentWorkspaceManager>());
```

`PersistentWorkspaceManager` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:27`) already
implements `IWorkspaceReader` alongside the other 10 interfaces it implements -- the type itself is
ready. The forwarding registration for that specific interface was simply never added.

Meanwhile, 17 engine constructors already depend on `IWorkspaceReader` directly (confirmed via
`grep -rl "IWorkspaceReader \w+[),]" --include=*.cs`):

- `RoslynSentinel.Basic`: `StandardRefactoringEngine`, `SemanticSearchEngine`
- `RoslynSentinel.Advanced`: `CloneDetectionEngine`, `CodeFlowEngine`, `CodeHealingEngine`,
  `CommentingEngine`, `DocumentationEngine`, `HealthOrchestrationEngine`, `InstrumentationEngine`,
  `MappingEngine`, `MetricsEngine`, `ModernizationUpgradeEngine`, `ModernLoggingEngine`,
  `PathDrivenTestEngine`, `RefinementEngine`, `StackOverflowEngine`

This is consistent with the in-flight "sweep batch" migration visible in recent commits (`994ffba`,
`7dc48e8`, `900a7ce`, `e5ee5ee`, `8f2abbe` -- "migrate ... off `ISolutionProvider` chokepoint"): the
migration is moving engine constructors from `ISolutionProvider`/`IWorkspaceManager` onto
`IWorkspaceReader`, but the DI registration side of that migration has not landed yet. The three
`RoslynSentinel.Server.Advanced` processes still running on this machine are stale builds predating
this gap (per `feedback_stale_server_before_rebuild` / `feedback_mcp_connection_failure_is_usually_vscode_spawn_issue`
-- this is why MCP tool calls have been failing all session even though processes are alive).

## Why `ValidateOnBuild` is working as designed here

This is the exact scenario `ServerStartupHelpers.cs`'s `EnableValidateOnBuild`
(`ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }`) exists to catch: a
missing registration surfaced at container-build time, with every broken descriptor named explicitly
in one `AggregateException`, rather than as a scattered runtime failure the first time each engine is
actually resolved. The error message is already about as actionable as this class of message gets --
it names the exact unresolvable service type and the exact type trying to activate it, for all 17
sites in one shot. No error-message fix is needed here; the fix is the missing registration itself.

## Fix applied

Added, alongside the existing two forwarding lines in
`RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs` (line 57):

```csharp
services.AddSingleton<IWorkspaceReader>(sp => sp.GetRequiredService<PersistentWorkspaceManager>());
```

`PersistentWorkspaceManager` already implemented `IWorkspaceReader`, so no other change was needed
on the implementation side. This single line resolved all 17 inner exceptions, since they all shared
the same missing registration.

## Relation to other in-progress work

`RoslynSentinel.Tests/TestServiceProviderBuilder.cs` (new helper, added this session, not yet
committed) deliberately registers `IWorkspaceReader` against `FakeWorkspaceManager` and sets
`ValidateOnBuild = false` specifically so tests using it aren't blocked by this same gap -- see that
file's doc comment. That workaround is scoped to the test helper only and should not be treated as
the fix; the production registration still needs the one-line addition above.
