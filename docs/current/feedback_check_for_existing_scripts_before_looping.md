---
name: feedback_check_for_existing_scripts_before_looping
description: "Before hand-rolling a shell loop for a repeated task (e.g. N model-eval runs), check the repo root for an existing front-door .ps1 script first"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-01T02:24:21.918Z
---

Before writing a bash/PowerShell loop to repeat a command N times, check the repo root for
an existing front-door script that already wraps it — e.g. `roslynsentinel-modeleval.ps1`
already had a `-Repeats` parameter with a proper `testhost.exe`-exit wait between iterations
when a session hand-rolled its own bash loop instead and used it for a 5-run batch.

**Why:** a naive loop calling `dotnet test --artifacts-path <dir>` repeatedly races the next
iteration's build against the previous run's still-exiting `testhost.exe` and fails with
`MSB3027 "locked by testhost"` — a failure mode the existing script was specifically written
to avoid (confirmed in practice 2026-08-31, see [[reference_model_eval_procedure]]). Re-deriving
env vars/`--artifacts-path`/filter syntax by hand also risks drifting from the documented
procedure (wrong host suffix, missing `-Clean` archival, etc.).

**How to apply:** `ls` the repo root (or check existing `reference_*`/`project_*` memories)
for a `.ps1` front-door before writing any repeated/looped shell invocation of a known
recurring task in this repo — model-eval runs, VS Code control (`roslynsentinel-vscode-control.ps1`),
or similar. If a script exists, use its own repeat/batch parameter rather than wrapping it
in an outer loop.
