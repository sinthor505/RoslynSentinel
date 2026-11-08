# `SubAgent` fails with a bare "failed unexpectedly (InvalidOperationException)" and leaves no findable run log

**Status:** OPEN 2026-10-12. Cause of the 2026-10-12 failures found with the debugger (the server's LM Studio host, 192.168.1.112 per the user, had no model loaded; I had checked 192.168.1.113). The open defect is that the tool error hides this: it names only the exception type and points at a run folder that was not found on disk.

## Cause found with the debugger (2026-10-12, run 20261012-043859-819-47e0acea)

Caught in Visual Studio at `RoslynSentinel.Common/LmStudioAgentClient.cs:174` (`CompleteOnceAsync`):

```text
System.InvalidOperationException: LM Studio request failed with BadRequest: {
  "error": { "message": "No models loaded. Please load a model in the developer page or use the 'lms load' command.",
             "type": "invalid_request_error", "param": "model", "code": null } }
```

The SubAgent base address comes from process-wide `LlmOptions.BaseUrl` (`SubAgentModelRun.cs:79`), set at server launch from `--llm-base-url`, else `ROSLYNSENTINEL_LLM_BASE_URL`, else `http://localhost:1234/v1` (`LlmOptions.cs:80-82`). `SubAgent` has no host parameter. This session's check of loaded models used 192.168.1.113, not the host the server was configured for, so it proved nothing about the server's host. The fix for the experiment is to launch the server with `--llm-base-url http://192.168.1.113:1234/v1`.

## What was being attempted

Experiment requested by the user: hand one file of error-codes relabel slice 1 (four `CannotEdit` -> `AlreadyInTargetState` branches in `MemberRefactoringEngine.cs`) to a local Qwen3.5 model through the `SubAgent` MCP tool. The server was restarted in `SubAgent` mode first (the tool was gated out in `claude-lean`). Call:

```text
SubAgent(
  model: "qwen3.5-9b-the-defiant-fable-uncensored-heretic-neo-imatrix-max-mtp",
  maxTokensPerTurn: 8192, turnCap: 30, wallClockCapMinutes: 20,
  prompt: "Task: relabel four ... branches in ONE file: RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs ...")
```

The model id is the only model `http://192.168.1.113:1234/api/v1/models` reported as loaded (`loaded_instances` non-empty, ctx 262144). The host address 192.168.1.113 was taken from memory and confirmed reachable by that call from this machine. No model turn is known to have run.

## The exact symptom

```text
{"isError":true,"errorData":{"errorCode":"Exception","message":"SubAgent failed unexpectedly (InvalidOperationException). Run id 20261012-043355-681-8a209822; see the server log and the run's agent.log for detail."}}
```

## Source trace

Not traced. Ruled out / observed:

- The tool is registered and callable (the call reached `SubAgent` and produced its own error text), so this is not the "gated tool" case.
- A recursive search of the repo, `%LOCALAPPDATA%` and `%TEMP%` for a folder or file matching `*8a209822*` and for a directory named `20261012-043355-681-8a209822` found nothing, so the "run's agent.log" the message points to was not located. Where the run artifacts are written is unknown to this writeup (hypothesis: a path under the server's own working directory or a worktree that was already deleted).
- Not checked: the server log, `SubAgentModelRun.cs`, whether the failure happened while building the private server or while contacting LM Studio, whether `ROSLYNSENTINEL_LLM_*` env vars are set in the server process (none are set in this shell).

## What is and is not confirmed

Confirmed: the error text above; the tool is active in the current server; LM Studio on 192.168.1.113 lists exactly one loaded model.

Not confirmed: the cause, the stage that threw, and whether the model name or host reached the tool correctly.

## Why this matters

The error names the exception type but not its message, and points at logs that are not at the stated location. A caller cannot tell a bad model name, an unreachable LM Studio, a worktree/private-build failure or a missing env var apart, so it cannot choose a recovery. Per the failure doctrine, the tool boundary should return the inner exception's message (sanitised) and an absolute path to the run folder.

## Suggested direction (not implemented)

1. Read `RoslynSentinel.Tools.Advanced/SubAgentModelRun.cs` and the `SubAgent` catch that produces "failed unexpectedly" to find which `InvalidOperationException` can be thrown and where the run folder is created.
2. Include the sanitised inner message and the absolute run-folder path in the `ResultError`.
3. Re-run the same call after the cause is known, and paste the verbatim inner exception into this doc.
