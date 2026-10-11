# `LmStudioAgentClient` ModelEval fixtures fail in SetUp: DI cannot resolve the `string model` ctor parameter

**Status:** RESOLVED 2026-10-11 (see Resolution at the end). Original status: OPEN 2026-10-11. Root cause traced to source (constructor signature changed by commit 495d0123 without updating the ModelEval DI registrations); the verbatim runtime exception has NOT been captured and is given below as the expected message only.

## What was being attempted

During the IResultStatus Step 7 smoke run (2026-10-11), a real-model fixture in
`RoslynSentinel.Tests.ModelEval` was reviewed/run. The fixtures build a host, register the typed
client, and resolve it in `[SetUp]`:

```csharp
hostBuilder.Services.AddHttpClient<LmStudioAgentClient>(client => { ... });   // e.g. WholeFileRewriteAgentTests.cs:324
...
_agentClient = _host.Services.GetRequiredService<LmStudioAgentClient>();      // WholeFileRewriteAgentTests.cs:383
```

The failure is unrelated to the IResultStatus rollout; it was found incidentally. No run/step id (no
model run ever started, since the failure is before the first model call).

## The exact symptom

Not captured verbatim. The fixtures are `[Explicit]` and `ModelEvalAssemblySetUp.OneTimeSetUp`
(`RoslynSentinel.Tests.ModelEval/ModelEvalAssemblySetUp.cs:28-34`) calls `Assert.Ignore` when
`ROSLYNSENTINEL_LLM_MODEL` is unset, so on a machine without the env var the fixtures are skipped
and the SetUp is never reached. The expected failure when the env var IS set (hypothesis, from
standard Microsoft.Extensions.DependencyInjection / `ActivatorUtilities` behavior, not observed):

```text
System.InvalidOperationException: Unable to resolve service for type 'System.String'
while attempting to activate 'RoslynSentinel.Common.LmStudioAgentClient'.
```

A future session should run one fixture with `ROSLYNSENTINEL_LLM_MODEL` set and paste the real text
here (LM Studio need not be loaded for this: SetUp reaches `GetRequiredService` only after the
assembly reachability probe, so a reachable server or a stubbed probe is needed).

## Source trace

- `RoslynSentinel.Common/LmStudioAgentClient.cs:46` - the only public constructor is
  `LmStudioAgentClient(HttpClient httpClient, string model, ILogger<LmStudioAgentClient> logger)`.
  `HttpClient` is supplied by the typed-client machinery and `ILogger<>` by the host, but
  `string model` is a bare primitive that no registration supplies. The constructor also throws
  `ArgumentException` on a null/whitespace model (same file, lines 47-54), so even a registered
  empty string would fail.
- Introducing commit (per the reporter's `git log`; not re-verified by this writeup): 495d0123,
  2026-10-01, "SubAgent step 2: LmStudioAgentClient takes the model as an explicit constructor
  parameter". It made `Model` a required per-instance ctor arg (deliberately NOT read from the static
  `LlmOptions.Model`, per the ctor XML doc at `LmStudioAgentClient.cs:35-40`) and updated only:
  - `RoslynSentinel.Utilities.PlanStepRunner/Program.cs:95` (`new LmStudioAgentClient(httpClient, LlmOptions.Model!, ...)`)
  - `RoslynSentinel.Tests.ModelEval/RepeatedToolFailureBreakerTests.cs:197` (`new LmStudioAgentClient(httpClient, "breaker-test-model", ...)`)
  - `RoslynSentinel.Tools.Advanced/SubAgentModelRun.cs:86` (`new LmStudioAgentClient(httpClient, model, ...)`)
- Fixtures NOT updated (verified by `Search(mode: text, query: LmStudioAgentClient)`, 7 fixtures,
  all still using the typed-client registration with no model supplied):

  | Fixture file | `AddHttpClient<LmStudioAgentClient>` | `GetRequiredService<LmStudioAgentClient>` | Where |
  | --- | --- | --- | --- |
  | `OrderPricingRefactorAgentTests.cs` | :196 | :242 | `SetUp` |
  | `OrderPricingRefactorChainAgentTests.cs` | :161 | :207 | `SetUp` |
  | `PlanImplementVerifyAgentTests.cs` | :372 | :399 | `RunPhaseAsync` (not SetUp) |
  | `PlanOnlyAgentTests.cs` | :162 | :205 | `SetUp` |
  | `PlanThenExecuteAgentTests.cs` | :117 | :160 | `SetUp` |
  | `SizeThresholdAgentTests.cs` | :217 | :263 | `SetUp` |
  | `WholeFileRewriteAgentTests.cs` | :324 | :383 | `SetUp` |

  `TranscriptReplayTests.cs` and `RepeatedToolFailureBreakerTests.cs` do not use the typed-client
  registration and are not affected.
- Why it went unnoticed: all affected test methods are `[Explicit]` (e.g.
  `WholeFileRewriteAgentTests.cs:411`), and `ModelEvalAssemblySetUp.cs:28-34` skips the whole
  assembly via `Assert.Ignore` when `ROSLYNSENTINEL_LLM_MODEL` is unset. The compiler cannot catch
  it because the DI registration is reflection-resolved at runtime. The real-model suite has
  therefore been unable to run since 2026-10-01.
- Stale doc: the XML summary at `ModelEvalAssemblySetUp.cs:6-8` still says each fixture "builds a real
  `LmStudioAgentClient` and only checks that `LlmOptions.Model` is non-empty"; that description
  predates 495d0123.

## What is and is not confirmed

Confirmed (source read this session):
- Constructor signature and that `string model` is a required parameter with no default.
- The seven registration/resolve sites above, none supplying a model.
- Three other construction sites use `new` and pass a model explicitly.
- Fixtures are `[Explicit]`; assembly SetUpFixture ignores when the model env var is unset.

Not confirmed:
- The verbatim exception text and its exact stack (see symptom section).
- That 495d0123 is the introducing commit and its date (taken from the reporter's `git log`).
- Whether any additional ModelEval-adjacent project (e.g. scripts, other test assemblies) resolves
  the client through DI; the search covered the whole solution text and found only the files above.

## Why this matters

- The entire real-model evaluation suite (the project's primary measure of whether weak models can
  drive the tools) has been dead for 10 days with no signal: a compile-clean, reflection-only break
  hidden behind two silent-skip layers (`[Explicit]` plus env-var `Assert.Ignore`).
- A constructor change with 10+ call sites in three projects had no tool or test that enumerated DI
  registrations of the type; the change was "complete" by build and unit tests while seven consumers
  were broken.
- Seven copies of the same registration block mean any fix must be repeated seven times, and the next
  constructor change will break them the same way.

## Suggested direction (not implemented)

Hypotheses, to be validated by whoever implements:
1. Preferred: one shared helper (for example an extension method on `IServiceCollection` in
   `RoslynSentinel.Tests.ModelEval`) that registers the client with a factory, reading the model from
   `LlmOptions.Model`, and replace all seven `AddHttpClient<LmStudioAgentClient>(...)` blocks with it.
   Shape: `AddHttpClient(...)` named/typed config for BaseAddress and Timeout, then
   `AddTransient(sp => new LmStudioAgentClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(name), LlmOptions.Model!, sp.GetRequiredService<ILoggerFactory>().CreateLogger<LmStudioAgentClient>()))`.
   Alternatively register `AddHttpClient<LmStudioAgentClient>(...).AddTypedClient((http, sp) => new LmStudioAgentClient(http, LlmOptions.Model!, ...))`.
2. Add a guard test (not `[Explicit]`, no live server needed) that sets a dummy model, builds the same
   service collection the fixtures use via the shared helper, and asserts
   `GetRequiredService<LmStudioAgentClient>()` succeeds. This removes the dependence on a loaded model
   to detect the break class. Note the ctor kicks off `LogLoadedModelStatusAsync` (never throws), so
   resolution against an unreachable URL is safe.
3. Update the stale summary in `ModelEvalAssemblySetUp.cs`.
4. Before fixing, capture the real exception text and paste it into the symptom section.

Per project convention, when fixed: add the regression test, move this doc to
`docs/current/blockers/resolved/` with a resolution note citing the commit hash.

## Resolution (2026-10-11)

- **Root cause:** as traced above; commit 495d0123 added a required `string model` ctor parameter and the
  seven ModelEval fixtures kept the plain `AddHttpClient<LmStudioAgentClient>` registration.
- **Fix:** direction 1 (typed-client factory). New `RoslynSentinel.Tests.ModelEval/ModelEvalServiceCollectionExtensions.cs`
  adds `AddLmStudioAgentClient(Action<HttpClient>)`, which calls `AddHttpClient<LmStudioAgentClient>(configureClient)`
  then `.AddTypedClient((http, sp) => new LmStudioAgentClient(http, LlmOptions.Model!, ILogger<>))`. All seven fixtures
  now call it; `AddHttpClient<LmStudioAgentClient>` appears only inside the helper.
- **Regression test:** `LmStudioAgentClientRegistrationTests.AddLmStudioAgentClient_ResolvesClient_WithModelFromLlmOptions`
  (no live server, not skipped by the env-var check). It was not confirmed to fail against the old registration.
- **Verified:** full suite 3915 tests, 3889 passed, 0 failed, 26 skipped. A live smoke run of
  `Model_FixesWholeFileRewriteBug_MinimalGuidance` (host 113, qwen3.5-9b-coder) now passes SetUp and runs 20 turns
  (the 2026-10-11 20:19 run failed in SetUp, the 20:25 run reached the model).
- **Commit:** the commit that moves this file into `resolved/` (hash recorded in `plan_shared_result_status_interface.md`).
- **Not done:** the stale summary in `ModelEvalAssemblySetUp.cs:6-8` (direction 3) and capturing the verbatim exception text.
