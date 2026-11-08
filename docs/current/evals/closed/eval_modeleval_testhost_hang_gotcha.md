---
name: project_modeleval_testhost_hang_gotcha
description: "A model-eval testhost.exe can hang indefinitely (alive but stuck, not crashed) even when the LM Studio host and model are fully healthy; kill the stuck testhost.exe PID and retry rather than restarting VS Code"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-01T22:22:04.208Z
---

A `PlanImplementVerify` 20-repeat batch against `.113` appeared stuck on run 1 for ~2 hours (log
never advanced past "test execution started"). In a separate session, `RoslynSentinel.Tests.Battery`
showed the same symptom. This is a **different failure mode** from
[[project_modeleval_testhost_crash_gotcha]] — that one is a silent *crash* (testhost dies, `dotnet
test` exits with code 0, remaining repeats silently dropped). This one is a genuine *hang*: the
testhost process stays alive (confirmed via `tasklist`/`wmic process ... get CreationDate`, showing
it running continuously since start) but makes no forward progress at all.

**Diagnosis steps that isolated it, in order:**
1. `curl http://192.168.1.113:1234/v1/models` — confirmed the LM Studio host itself is up.
2. `curl .../v1/chat/completions` with a trivial prompt — confirmed the model is actively serving
   inference in under a second, not overloaded/hung server-side.
3. `tasklist /FI "IMAGENAME eq testhost.exe"` + `wmic process where "name='testhost.exe'" get
   ProcessId,CreationDate,CommandLine` — found the stuck testhost's exact PID and start time,
   confirmed it had been alive but idle far longer than any phase's 5-minute wall-clock cap could
   explain.
4. Ran a fresh, fast, single-phase test (`PlanOnly -Repeats 1`) in the same environment while the
   stuck process was still alive — it completed normally in under 3 minutes. This proved the host,
   model, and MCP tool plumbing were all fine, and the problem was specific to the one wedged
   process, not systemic.

**Fix:** `taskkill /F /PID <stuck-testhost-pid>` (and its parent `dotnet.exe` if still present) for
each stuck process, then simply re-run the batch. No VS Code restart needed — confirmed by the
`PlanOnly` sanity check succeeding while the stuck processes were still present, and by the retried
batch starting cleanly immediately after the kill.

**How to apply:** if a model-eval batch appears stalled (log hasn't advanced past test-execution-
start for longer than the sum of that test's per-phase wall-clock caps), don't assume a VS Code
restart is needed. First confirm the LM Studio host is actually responsive (steps 1-2 above), then
find and kill the specific stuck `testhost.exe` PID (steps 3-4), then retry. Reserve a full VS Code
restart for cases where killing the specific stuck process doesn't unblock a retry.
