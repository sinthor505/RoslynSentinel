# ModelEval harness never calls `NearMissOptions.Configure` -- the four-arm live check would silently run as four control arms

**Status:** FIXED 2026-11-18. `NearMissOptions.Configure([])` added to `ModelEvalAssemblySetUp.OneTimeSetUp` (commit below). Root cause was a missing `Configure` call in the test harness; no model run was started before the fix.

## What was being attempted

Step 13 (live check) of `docs/current/plans/plan_server_slip_recovery.md`: run `ShippingKindStep` through
`scripts/roslynsentinel-evalqueue.ps1` on tiel (host 113) in four arms of the new `NearMissOptions`
(autocorrect off/on x name display show/hide). The options were to be passed as environment variables
`ROSLYNSENTINEL_NEAR_MISS_AUTOCORRECT=true|false` and `ROSLYNSENTINEL_NEAR_MISS_NAME_DISPLAY=show|hide`.

Not run: no model run was started and no queue job was submitted. `tiel-coder-35b-a3b` is loaded on
192.168.1.113 (`/api/v1/models` queried 2026-11-18) and the host queue is empty.

## The exact symptom

There is no error text. The failure is silent: the env vars are accepted by the shell and by evalqueue,
but the ModelEval testhost never reads them. Every arm would run with autocorrect off and name display
show, and would look like a valid result.

## Source trace

Committed HEAD fff004a0.

- `RoslynSentinel.Common/NearMissOptions.cs`: `AutoCorrect` and `NameDisplay` are backed by private statics
  (`_autoCorrect`, `_nameDisplay`) that only `Configure(string[] args)` sets; it reads the flag, then the env
  var. There is no lazy env-var fallback.
- Only callers of `NearMissOptions.Configure` in the whole solution (tests included):
  `RoslynSentinel.Server.Advanced/ServerStdio.cs:48` and `RoslynSentinel.Server.Advanced/ServerHttp.cs:36`
  (both in `Startup`).
- `RoslynSentinel.Tests.ModelEval/ModelEvalAssemblySetUp.cs` `OneTimeSetUp` (~lines 30-39) calls
  `LlmOptions`, `LiftRenamesOptions`, `NotFoundInventoryOptions`, `LoopLatchOptions` (line 36) and
  `ErrorPolicyOptions` `Configure`, under the comment "The in-process eval server never runs
  ServerStdio/ServerHttp, so read the experimental server flags' env vars here". It does not call
  `NearMissOptions.Configure`.
- `ShippingKindSteppedAgentTests.SetUp` (~lines 63-68) builds the in-process server with
  `services.AddMcpServer()` + `AddRoslynSentinelToolsAdvanced(...)`.
  `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs:82` (`AddRoslynSentinelToolsBasic`)
  calls `BuildCallToolFilterRegistrations`, which (~line 184) adds the NearMissRecord filter only if
  `NearMissRecorder.IsActive` (`AutoCorrect || NameDisplay == Hide`), evaluated at registration time.
- Result: with the options never configured the filter is absent, no `_meta` `nearMissSubstitutions`
  records are written, and the eval cannot observe or exercise the feature in any arm.

## What is and is not confirmed

Confirmed (by source search at HEAD): the missing call, the sole `Configure` callers, and the
registration-time `IsActive` gate.

Per `docs/current/references/reference_model_eval_procedure.md`, evalqueue records `ROSLYNSENTINEL_*`
variables from the submitting shell and applies them to the run, so the env-var route should work end to
end once the harness reads them. This part is from the doc and is not exercised here.

Not confirmed: behaviour of the four arms under a live model (never run).

## Why this matters

The four-arm live check is not runnable as-is. Running it anyway would produce four identical control arms
that look like a valid result, and the run log gives no sign of which arm was intended or in effect. The
harness silently ignores an experimental server flag that every other flag in the same `OneTimeSetUp`
honours. This is L2 harness friction, not a defect in the code under test.

## Suggested direction (not implemented)

- Add `NearMissOptions.Configure([])` next to the other `Configure` calls in
  `ModelEvalAssemblySetUp.OneTimeSetUp`, before any fixture `SetUp` registers tools.
- Optionally stamp the effective `NearMissOptions` values into the transcript header so a run log shows
  its arm.
- Then submit the four evalqueue jobs for Step 13.
