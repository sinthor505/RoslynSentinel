---
name: listworkspacesolutions_driveroot_hang_fixed
description: "ListWorkspaceSolutions hung 32+ min / ~5GB RAM on workspacePath:\"/\"; fixed with drive-root rejection, real cancellation, 200k-file cap"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-05T04:42:07.203Z
---

`ListWorkspaceSolutions` had a genuine production bug: a model passing `workspacePath: "/"`
(a plausible "search from root" guess) passed `Directory.Exists` (which resolves `"/"` to the
current drive root, `C:\`, on Windows) and triggered an uncancellable, unbounded
`Directory.EnumerateFiles(@"C:\", "*.sln", SearchOption.AllDirectories)` — observed live during
granite-4.2-8b model-eval testing, hanging 32+ minutes while `testhost.exe` climbed toward ~5GB
RAM. `cancellationToken` was explicitly discarded (`_ = cancellationToken;`) in the original code,
so even the test's own cancellation couldn't interrupt it.

Root cause chain: `FilePath.NormalizeWirePath` doesn't special-case `"/"` or drive roots (only
trims quotes/whitespace, collapses doubled backslashes) → `Directory.Exists("/")` is `true` on
Windows → single blocking LINQ chain (`Concat`+`OrderBy`+`Select`+`ToList()`) with no cancellation
checks scans the entire drive.

**Fix** (`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`): (1) reject any `workspacePath`
that resolves to exactly a drive root via `Path.GetFullPath`+`Path.GetPathRoot` comparison,
returning a clear `InvalidArgument` instead of scanning; (2) replaced the LINQ chain with a manual
`foreach` calling `cancellationToken.ThrowIfCancellationRequested()` per file; (3) added a
`ListWorkspaceSolutionsMaxFilesWalked = 200_000` hard cap as a second line of defense for
legitimately-broad-but-not-drive-root paths. Verified via clean build + 2 new regression tests in
`BatteryTwentyTests.cs` (drive root and bare `"/"` both rejected).

Confirmed working live in a granite-4.2-8b re-run: the model still guesses `workspacePath:"/"` on
its first turn in every phase (see [[project_planimplementverify_promptcontext_solution_preloaded]]
for why), but now gets an instant clean `InvalidArgument` and self-corrects via `ListAll()` within
one turn, instead of hanging indefinitely.

**Why**: this was a real, previously-undiscovered bug independent of the model-eval sampling-param
investigation — worth fixing regardless of which model triggered it, since any agent could plausibly
guess `"/"` as a workspace root.

**How to apply**: if a future hang/memory-runaway is observed during model-eval or live MCP use with
a tool that takes a path parameter, check whether the path resolves to something unexpectedly broad
(drive root, UNC share root) before assuming it's a model reasoning failure — the tool layer may be
the actual bug. Also see [[feedback_attach_debugger_when_mcp_tools_cant_show_internal_state]] — this
bug was root-caused via the user's own VS debugger Parallel Stacks + call-stack screenshots, not
purely via log/transcript inspection.
