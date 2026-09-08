---
name: lmstudio_dispatch_silence_hang_and_idle_timeout_fix
description: "Model_AppliesSevenChainedRefactors hung 3400s+; LM Studio server log showed the request received but never dispatched to an inference slot, connection stayed open with zero bytes; fixed via idle-read timeout + retry-once in LmStudioAgentClient"
metadata: 
  node_type: memory
  type: project
  originSessionId: db7ceb49-6e55-4860-a8b7-121511b9b250
  modified: 2026-09-08T16:43:30.167Z
---

`Model_AppliesSevenChainedRefactors` (RoslynSentinel.Tests.ModelEval, OrderPricingRefactorChainAgentTests)
hung for 3400s+ against a <900s expectation. Root-caused via LM Studio's own server log
(`lmstudio_logs\192.168.1.113\2026-09\2026-09-08.{10,11}.log`), not the .NET side.

## Root cause

Distinct from [[project_lmstudio_streaming_json_truncation_investigation]] (that one is mid-stream
JSON corruption from `<think>`-tag parsing on completed generations). This one: the 66th request in
a long multi-turn agent loop (`POST /v1/responses`, model `qwen3.5-9b-coder`) was logged as
**received** by LM Studio but the log shows **zero evidence it ever entered the inference
pipeline** — no "Streaming response...", no slot launch, no `print_timing`, no slot release — while
every one of the prior 65 requests in the same file got "Streaming response..." within 0-2s. No
ERROR/WARN/crash/OOM anywhere in either log file; the next hourly log rotation shows LM Studio
continuing to serve other requests normally afterward. The request appears to have been silently
dropped by LM Studio's router/queue rather than dispatched to a slot.

The client-side `.NET` stack sat blocked in `StreamReader.ReadLineAsyncInternal` →
`HttpConnection.ChunkedEncodingReadStream.ReadAsyncCore` — a completely ordinary live-read stack, no
evidence of a `PersistentWorkspaceManager`/`_solutionLock` deadlock (that was an initial hypothesis
from VS debugger data alone and was wrong — see [[feedback_verify_before_theorizing_on_tool_errors]]
adjacent lesson: always check server-side logs before theorizing about client-side locking).

**Debugger side-effect observed**: attaching VS and breaking the process was enough idle time for
LM Studio (or an intermediary) to eventually RST the long-idle connection; resuming surfaced it as
`IOException` (inner `SocketException`, "forcibly closed by the remote host") at the exact
`ReadLineAsync` call site. This did not cause the underlying hang — the connection was already stuck
open with nothing arriving; the debugger break just gave whatever idle-reap timeout exists time to
fire before the observation.

## Fix (2026-09-08, LmStudioAgentClient.cs / LlmOptions.cs)

1. `LlmOptions.StreamIdleTimeoutSeconds` (new `--llm-stream-idle-timeout-seconds` /
   `ROSLYNSENTINEL_LLM_STREAM_IDLE_TIMEOUT_SECONDS`, default 120s) — measures idle time **between**
   SSE lines, not total call duration, so a genuinely slow reasoning burst isn't penalized. Enforced
   inside `ReadServerSentEventsAsync` via a linked `CancellationTokenSource.CancelAfter` per read,
   throwing a new `StreamIdleTimeoutException`.
2. `CompleteAsync` now retries the whole request exactly once on `StreamIdleTimeoutException`,
   `InvalidOperationException` (covers non-2xx HTTP and `response.failed`/`error` SSE events), or
   `IOException` (covers the forcibly-reset-connection case). A second consecutive failure
   propagates — not retried again.
3. Deliberately NOT relying on `HttpClient.Timeout` for this: once headers are read for a streamed
   response (`HttpCompletionOption.ResponseHeadersRead`), `HttpClient.Timeout` no longer bounds body
   read time, which is exactly why the existing 600s+ per-test client timeout never fired here.

## How to apply

Any new hang report against `LmStudioAgentClient`-based tests: first check whether it now surfaces
as a `StreamIdleTimeoutException`/retried-`IOException` log line (fix working as intended, just a
slow/flaky LM Studio) before re-opening a deadlock investigation. If hangs persist past the 120s+
retry budget (~240s worst case), that's a genuinely new failure mode, not this one recurring.
