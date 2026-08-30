# Blocking error: `LoadSolution` reports success on a nonexistent/bogus solution path

## Status: blocking — root cause confirmed via LM Studio server logs; paused pending user go-ahead

## Revision note

An earlier version of this doc theorized a race between `[SetUp]`'s solution load and a
model-triggered reload, or `AddFileToSolution`'s fixture files not surviving a reload. Both
theories are **wrong** — ruled out by reading the actual LM Studio request/response log for the
run in question (`lmstudio_logs/192.168.1.112/2026-08-29.2.log`, lines ~266368-266419). The real
cause is simpler and more precise: the model passed a malformed path, and `LoadSolution` silently
reported success anyway. See "Confirmed root cause" below.

## What happened

During an autonomous model-eval sweep (model `mistralai/ministral-3-3b` on host 192.168.1.112),
the model's `LoadSolution` tool call was given a hallucinated/garbled `solutionPath`:

- **Correct path** (from the prompt): `C:\Users\Administrator\AppData\Local\Temp\2\RoslynSentinelTests_1612e1c2-630a-4a9f-9e66-c45515981e6f\ContosoOrders.Core\...`
- **Path the model actually sent**: `C:\Users\Administrator\AppData\Local\Temp\2\RoslynSentinelTests_1612e1c2-4a9f-9e66-c5515981e6f.sln`

The model dropped the `-630a-` segment of the GUID, dropped the `ContosoOrders.Core` subdirectory
entirely, and appended `.sln` directly onto the truncated temp-directory name — a small-model
(3B) path-reproduction error on a long UUID string, not a harness bug in itself.

The bug is what happened next: the tool call **returned `"success":true`**, with
`"data":"Solution loaded: C:\\...\\RoslynSentinelTests_1612e1c2-4a9f-9e66-c5515981e6f.sln"` —
echoing the bogus path back as if it had loaded successfully. The model had no way to know
anything was wrong. Every subsequent `ReadFile` call against the real fixture files then failed
with `FileNotFound`, `projectsLoaded=0`, and the model spent 15+ turns flailing — trying
alternate paths, guessing at `ListSolutionItems`, eventually fabricating and deleting a scratch
file — never recovering, because the tool result that would have told it "the path you gave me
doesn't exist" never arrived.

Full LLM-side transcript:
`C:\tmp\modeleval-ministral3b-112\model-eval\SizeThreshold\n0\20260830-025244-258\transcript.json`
Raw request/response confirming the exact argument and response:
`lmstudio_logs\192.168.1.112\2026-08-29.2.log` (search `RoslynSentinelTests_1612e1c2-4a9f`).

## Confirmed root cause (read from source, not inferred)

`PersistentWorkspaceManager.LoadSolutionAsync` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:290-354`):

```csharp
try
{
    CurrentSolution = await _workspace.OpenSolutionAsync(solutionPath, null, cancellationToken);
    ...
}
catch (Exception ex)
{
    ...
    _workspaceLoadErrors.Add($"Failed to open solution: {ex.Message}");
    // Even if solution fails to open, try to get current partial solution if any
    CurrentSolution = _workspace.CurrentSolution;
    if (CurrentSolution?.ProjectIds.Count == 0 && _workspaceLoadErrors.Count == 0)
    {
        _workspaceLoadErrors.Add($"Solution '{solutionPath}' opened but no projects were found. ...");
    }
}
```

When `OpenSolutionAsync` throws (e.g. the `.sln` file doesn't exist on disk — exactly what
happens with a garbled path), the exception is caught, a message is appended to the internal
`_workspaceLoadErrors` list, and the method **returns normally** with an empty
`CurrentSolution` from the freshly-created `MSBuildWorkspace`. Nothing in this method — and
apparently nothing in the `LoadSolution` MCP tool wrapper
(`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs:287`) — checks `_workspaceLoadErrors`
before building the `ToolResult<object>` response. The tool reports `success:true` and echoes
the (unvalidated) `solutionPath` back in `data`, regardless of whether the open actually
succeeded.

Contrast this with `GetWorkspaceHealth`'s own documented behavior (from its tool description,
seen in the same LM Studio log's tool schema dump): "`IsOperational=true` +
`HasLoadedSolution=false` means no solution loaded yet — not an error" — i.e. the codebase
already has a place that correctly distinguishes "nothing loaded" from "load succeeded."
`LoadSolution` itself doesn't make that distinction in its own return value.

## Why this matters beyond this one sweep

This isn't specific to small/weak models — any caller (human or model) that passes a slightly
wrong solution path gets a false-positive "Solution loaded" response with no indication anything
is wrong, and only discovers the problem several tool calls later via unrelated `FileNotFound`
errors that don't obviously point back to the real cause (a bad `LoadSolution` argument). A
larger/more careful model would likely just get the path right more often, not be immune to this
class of failure.

## Impact on the current sweep effort

- `mistralai/ministral-3-3b`'s sweep result is invalid as a measure of task-solving capability —
  it never got a working environment for the real task, and the failure mode (garbled long
  UUID-bearing path) is itself a mildly interesting small-model data point, but the 15+ turn
  flailing afterward reflects the missing error signal, not the model's reasoning.
- Any *other* run (any model, any sweep) where the transcript shows a `LoadSolution` call with an
  argument that doesn't exactly match the path given in the prompt should be treated as suspect —
  it may have silently "succeeded" into an empty solution. Worth grepping prior transcripts for
  `"Name": "LoadSolution"` calls and diff-checking the argument against the prompt's stated path
  before trusting a `converged=False` result as a genuine model weakness.

## Suggested next steps (not started — needs user go-ahead)

1. Fix `LoadSolutionAsync` (or the `LoadSolution` tool wrapper) to return failure — not success —
   when `_workspaceLoadErrors` is non-empty after the load attempt, or at minimum surface the
   accumulated error messages in the response so a caller (model or human) can see the load
   didn't actually work.
2. Consider whether `OpenSolutionAsync` throwing on a nonexistent path should be checked
   explicitly up front (`File.Exists(solutionPath)`) with a clear, immediate error, rather than
   relying on catching whatever exception MSBuild happens to throw.
3. Once fixed, a model given a bad path would get an honest, immediate error instead of 15 turns
   of confused flailing — this alone might materially change small-model pass rates on this
   harness, since "recover from a clear tool error" is a much easier task than "notice a
   false-positive success didn't actually work."
4. Re-run the ministral-3-3b sweep once this is fixed, since the current result measures the
   harness's error-reporting gap, not the model.
