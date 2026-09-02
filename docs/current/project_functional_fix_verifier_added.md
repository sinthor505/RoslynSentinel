---
name: project_functional_fix_verifier_added
description: "FunctionalFixVerifier added to ModelEval tests: rebuilds ContosoOrders.Core after the model's edit and reflection-invokes ConvertAbstractClassToInterface directly, asserting on the real return value instead of only text-scanning the edited source — closes the gap that let run 3's whole-file-comment-out pass as a false success"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-02T06:53:41.875Z
---

Motivated directly by [[project_planimplementverify_5run_result_postfix_verify]]'s run 3: a model
replaced `BlockConverter.cs` with every line commented out, which compiled with 0 errors and
happened to satisfy `AssertFixApplied`'s substring checks for the wrong reason. Text-scanning the
edited source can never rule this class of failure out — the user asked whether the harness could
instead invoke real code post-edit and check an actual return value.

**Approach chosen** (of two considered): reflection-based, not a new xunit test project. The
already-existing-but-empty `Samples/ContosoOrders/ContosoOrders.Tests` project (real xunit +
`Microsoft.NET.Test.Sdk`, already copied into every `TestSolutionFixture` instance, zero `.cs`
files today) would have required a NuGet restore step — `WholeFileRewriteReproducer.cs`'s own doc
comment states the fixture deliberately has "no NuGet packages and no restore step," so bringing
that back to reuse `RunTest` was rejected in favor of staying restore-free.

**What was built**: `RoslynSentinel.Tests.ModelEval/FunctionalFixVerifier.cs` —
`InvokeConvertAbstractClassToInterfaceAsync(coreProjectDirectory, fileText, className, ct)` shells
`dotnet build` against the fixture's `ContosoOrders.Core.csproj`, loads
`bin/Debug/net10.0/ContosoOrders.Core.dll` into a collectible `AssemblyLoadContext`, reflection-gets
`ContosoOrders.Core.FixtureHelpers.BlockConverter.ConvertAbstractClassToInterface(string, string)`,
and invokes it. Throws (`InvalidOperationException`/`FileNotFoundException`) with diagnostic detail
(captured build output, or which type/method was missing) on any failure — build failure, missing
type/method, or a runtime exception from the invoked method itself — rather than returning a
sentinel value, since every failure path here already means the fix is broken.

Wired into `WholeFileRewriteAgentTests.AssertFixApplied` (now `async Task`, taking a
`CancellationToken`) after the existing text checks: invokes with
`WholeFileRewriteReproducer.TargetAbstractClassFileContent`/`"Shape"`, asserts the real output
contains `public interface IShape`, does NOT contain `public abstract class Shape`, and retains
`double GetArea()`. `PlanImplementVerifyAgentTests` (which calls this shared method) updated to
await it. `ConsistencyCheck`'s per-run catch block broadened from `catch (AssertionException)` to
also catch `InvalidOperationException`/`IOException` so a functional-check failure counts as one
failed run instead of aborting the whole N-run loop.

`PlanThenExecuteAgentTests.cs` has its own separate, independent `AssertFixApplied` — deliberately
NOT touched, out of scope for this change.

Verified end-to-end outside the full harness (no LM Studio needed for this part): copied
`ContosoOrders.Core` to a scratch dir, hand-wrote a correctly-fixed `BlockConverter.cs`/
`BlockEditHelpers.cs` pair, confirmed `dotnet build` produces the expected DLL path and a
throwaway reflection-invoke script gets the correct `IShape` output — confirms the build-path,
type-resolution, and invoke logic all work as designed. Build to 0 errors, committed `a335324`.

**How to apply**: next model-eval batch run against `WholeFileRewriteAgentTests`/
`PlanImplementVerifyAgentTests` will for the first time exercise this — watch for whether it ever
fires (i.e. a run passes the text checks but fails the functional one) or whether run 3's failure
mode was rare enough that it doesn't recur. If a future fixture needs the same treatment, the
pattern (build the exact project directory, reflection-load, invoke, assert on the real value) is
reusable, though `FunctionalFixVerifier` itself is currently BlockConverter-specific, not generic.
