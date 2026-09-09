# Plan — Add a `reason` parameter to every MCP tool

## Title
Add an optional `reason` parameter to all ~108 `[McpServerTool]`-attributed methods across both
server flavors, using the new `MethodSignature` tool
(`RoslynSentinel.Server.Basic\SentinelRefactoringTools.cs`, tool name `MethodSignature`) to make
the edits instead of hand-editing each file.

## Background
Original ask was 7 tools (SearchSolutionText, ReadFile, ApplyDiff, Build, ListAll,
ChangeAccessibility, ModifyModifier) so an agent could state *why* it's calling a tool, for later
transcript review. User expanded scope to **all** MCP tools, since the change is mechanically
identical everywhere.

**Key finding from the original design session: this needs no logging infrastructure.**
`agent.log` transcripts (see `RoslynSentinel.Tests.ModelEval\AgentLoop\LmStudioAgentClient.cs` and
`ModelAgentRunner.cs`) already log full tool-call argument JSON verbatim — confirmed directly
against a real transcript:
```
ModelAgentRunner: Turn 1: calling ReadFile with args: {"filepath":"...BlockConverter.cs"}
```
So `reason` just needs to **exist as a parameter** — it will show up in transcripts automatically
once the model starts passing it. This is a pure schema/prompt change, not a plumbing change. The
parameter is never read in any method body.

While scoping this, the user asked whether `ChangeSignature` (existing tool, reorders parameters)
or `ConstructorParameter` (existing tool, adds DI constructor parameters) could do the mechanical
edit. Neither can:
- `ChangeSignature` only **reorders** existing parameters (`newParameterOrder: int[]`) — no
  add/remove capability.
- `ConstructorParameter` only targets constructors and always pairs a new parameter with a
  generated backing field + body assignment — wrong shape for a bare optional parameter on an
  arbitrary method.

This produced a **new tool, `MethodSignature`**, built and committed in the prior session
(commit `836a223`, branch `master`) specifically to fill this gap and to do the 108-tool rollout
with. It is already implemented, unit-tested (6/6 passing in
`RoslynSentinel.Tests.Battery\BatteryTwentyFourTests.cs`), and the VS Code server binaries
(`bin-vscode\Advanced` stdio + `bin-vscode\Advanced.Http` on port 5150) were rebuilt via
`.\build.ps1 -Flavor Advanced -Config Debug -Mode Build -Force` to pick it up. **A fresh chat
session is required** for the new tool to appear in the session's tool list (confirmed pattern —
see `feedback_new_tool_needs_fresh_session` in memory).

## What `MethodSignature` does
`[McpServerTool(Name = "MethodSignature")]` in `SentinelRefactoringTools.cs`, engine methods
`AddMethodParameterAsync` / `RemoveMethodParameterAsync` / `GetMethodParametersAsync` in
`RoslynSentinel.Basic\RefactoringEngine.cs`.

- **operation: add** — appends a parameter to the end of a method's parameter list.
  `paramName`, `paramType` required; `defaultValue` optional (e.g. `"null"`). Resolves the target
  method by `methodName` (+ `contextSnippet`/`lineBefore`/`lineAfter` for overload
  disambiguation, same convention as every other refactoring tool). This is exactly what adding
  `reason` needs: `paramName: "reason"`, `paramType: "string?"`, `defaultValue: "null"`.
- **operation: remove** — only the *last* parameter can be removed; refuses otherwise. Not needed
  for this task, but useful if a bad rollout needs undoing on a single tool without a full
  `UndoLastApply`.
- **operation: view** — lists a method's current parameters, no changes made. Useful as a
  before/after sanity check.

Signature (all params besides `filepath`/`operation`/`methodName` optional except as noted):
```
MethodSignature(filepath, operation: add|remove|view, methodName,
                 paramName?, paramType?, defaultValue?,
                 contextSnippet?, lineBefore?, lineAfter?,
                 autoStage=true, dryRun=false, returnDiff=false)
```

## Target shape of the change (identical for all 108 tools)
- New parameter, added via `MethodSignature(operation: add)`:
  - `paramName: "reason"`
  - `paramType: "string?"`
  - `defaultValue: "null"`
- Attribute it with `[Description(ToolParams.Reason)]` — **`MethodSignature` does not add
  attributes**, so this is a manual follow-up: after each add, either re-open the file and apply
  the `[Description(...)]` attribute with `ApplyDiff`, or (simpler) do the attribute placement as
  a single `ApplyDiff` pass per file that also fixes up parameter ordering (see Task 2 below) —
  don't leave the parameter attribute-less.
- Parameter placement: after all existing optional parameters, immediately before
  `CancellationToken cancellationToken` (the near-universal last parameter in this codebase). If a
  tool method has no trailing `CancellationToken` parameter, place `reason` last.
- The parameter is **never read** in the method body — purely descriptive.

### `ToolParams.Reason` constant
Add to `RoslynSentinel.Common\ToolParams.cs` (matches the existing pattern used for
`ContextSnippet`, `ReturnDiff`, etc.):
```csharp
public const string Reason =
    "Optional. A brief note on why you're calling this tool right now — helps when reviewing " +
    "agent transcripts later. Not validated or acted on.";
```

## Known wrinkle: `MethodSignature` appends, `[Description]` needs manual placement
`AddMethodParameterAsync` inserts the bare parameter (name, type, default) via
`ParameterList.AddParameters(newParam)` — it does not attach a `[Description(...)]` attribute
(the engine has no notion of C# attributes on parameters, only the `ParameterSyntax` itself). Two
ways to close this gap, pick one **before** starting the 108-tool batch:

1. **MethodSignature first, ApplyDiff second per file.** Call `MethodSignature(add)` for every
   method in a file (fast, no line-number tracking needed since it resolves by `methodName`), then
   do one `ApplyDiff` pass per file adding `[Description(ToolParams.Reason)]` in front of each new
   `string? reason = null` — a single search-and-replace-shaped diff per file since the inserted
   text is textually identical every time.
2. **ApplyDiff only, skip MethodSignature.** Since the attribute still needs a manual `ApplyDiff`
   pass either way, this option asks whether `MethodSignature` earns its keep here. It does,
   because `MethodSignature` handles the harder part correctly and safely: resolving the right
   method (including overloads), guarding against a duplicate parameter name, formatting via
   Roslyn's `Formatter.FormatAsync`, and running the full `ValidateAndApplyAsync` compile-check —
   an `ApplyDiff`-only pass would need to hand-locate each insertion point and re-implement that
   safety net itself, with no compiler-error feedback loop if a placement guess is wrong.

**Recommended: option 1.** Use `MethodSignature` to do the actual parameter insertion (gets
correctness + validation for free), then a lightweight `ApplyDiff`/`SearchSolutionText` pass per
file to prefix `[Description(ToolParams.Reason)]` onto each `string? reason = null` it just added.

## Execution plan

### Task 0 — Fresh session confirms `MethodSignature` is visible
Sanity-check the new tool is actually callable (call it once with `operation: view` against any
method) before starting the batch. If it's missing, the server rebuild from the prior session
didn't take — rerun `.\build.ps1 -Flavor Advanced -Config Debug -Mode Build -Force` and start
another fresh session.

### Task 1 — Add `ToolParams.Reason`
Edit `RoslynSentinel.Common\ToolParams.cs` directly (this one file, do by hand — not worth a tool
call). Build (0 errors).

### Task 2 — Enumerate every target method
Do **not** trust a hardcoded line-number list (this doc deliberately omits one — line numbers
drift). Instead, per file, use `SearchSolutionText(pattern: "\\[McpServerTool\\(Name = \"",
searchMode: regex, fileGlob: "<file>")` or `GetFileOutline` to get the live list of tool method
names in each of these 15 files:

| File | Tool count (at plan-writing time) |
|---|---|
| `RoslynSentinel.Server.Basic\SentinelWorkspaceTools.cs` | 25 |
| `RoslynSentinel.Server.Basic\SentinelRefactoringTools.cs` | 15 (includes the new `MethodSignature` itself — **skip it**, see below) |
| `RoslynSentinel.Server.Advanced\SentinelAsyncifyTools.cs` | 15 |
| `RoslynSentinel.Server.Advanced\SentinelAdvancedRefactoringTools.cs` | 13 |
| `RoslynSentinel.Server.Basic\SentinelSymbolTools.cs` | 7 |
| `RoslynSentinel.Server.Advanced\SentinelIntelligenceTools.cs` | 7 |
| `RoslynSentinel.Server.Advanced\SentinelScanTools.cs` | 6 |
| `RoslynSentinel.Server.Advanced\SentinelQualityTools.cs` | 5 |
| `RoslynSentinel.Server.Advanced\SentinelCodemodTools.cs` | 4 |
| `RoslynSentinel.Server.Advanced\SentinelGenerationTools.cs` | 4 |
| `RoslynSentinel.Server.Basic\SentinelAdminTools.cs` | 3 |
| `RoslynSentinel.Server.Advanced\SentinelCommentingTools.cs` | 1 |
| `RoslynSentinel.Server.Advanced\SentinelModernizationTools.cs` | 1 |
| `RoslynSentinel.Server.Basic\DocumentationTools.cs` | 1 |
| `RoslynSentinel.Server.Basic\GitTools.cs` | 1 |

Total 108 including `MethodSignature`; **107 targets** once it's excluded (it's brand new and
doesn't need a `reason` param added to itself — or does, at your discretion, since it'll get
called like any other tool; user's original request predates its existence, so confirm with the
user rather than assuming).

Decide with the user before Task 3: does `MethodSignature` itself also get a `reason` parameter?
(Recommended: yes, for consistency — nothing in its design argues for an exception.)

### Task 3 — Batch the edits, file by file
For each file above:
1. For each tool method in the file, call
   `MethodSignature(filepath, operation: add, methodName: "<ToolMethodName>", paramName: "reason",
   paramType: "string?", defaultValue: "null")`.
   - **Overloads / ambiguous names**: none of the 108 tool methods share a name with a sibling
     method in the same file (each is a distinct `[McpServerTool(Name = "...")]`-decorated public
     method) — `contextSnippet` should not be needed, but if `MethodSignature` reports ambiguity,
     supply it (a short fragment of the method's `[Description(...)]` text works well).
   - If `MethodSignature` reports a compile error post-insertion, stop and investigate before
     continuing that file — do not paper over with `validateOnApply: false`.
2. After all methods in the file are done, re-read the file (`ReadFile` or targeted
   `SearchSolutionText(pattern: "string\\? reason = null", searchMode: regex, fileGlob: "<file>")`)
   and use `ApplyDiff` to prefix `[Description(ToolParams.Reason)]` onto each new
   `string? reason = null,` occurrence. This is a mechanical, identical-text insertion per
   occurrence — safe to batch as one `ApplyDiff` diff-format call per file touching every
   occurrence at once, or several smaller calls if the file is large enough that one diff risks
   anchor drift.
3. `Build` (quickBuild is enough per-file; run one `fullBuild` at the very end of Task 3).

Recommended order: smallest files first (`GitTools.cs`, `DocumentationTools.cs`,
`SentinelCommentingTools.cs`, `SentinelModernizationTools.cs` — 1 tool each) to validate the
two-step MethodSignature+ApplyDiff pattern cheaply before committing to it across the 25-tool and
15-tool files.

### Task 4 — Full solution build + targeted test sweep
- `dotnet build RoslynSentinel.slnx` — 0 errors required.
- Grep the test suites (`RoslynSentinel.Tests.Battery`, `RoslynSentinel.Tests.Basic`,
  `RoslynSentinel.Tests.Advanced`, `RoslynSentinel.Tests.Asyncify`) for any test asserting an
  exact parameter count or schema shape against these 15 files' tool methods — an added optional
  trailing parameter shouldn't break a well-written call-site test, but a test using reflection
  over `MethodInfo.GetParameters().Length` would need updating. `grep -rn
  "GetParameters().Length\|ParameterCount"` across the test projects as a first pass.
- Run the full battery suite (`RoslynSentinel.Tests.Battery`) — every tool method in these files
  already has some coverage there; a broken signature would show up as a compile error before
  tests even run, but rerun anyway to catch any runtime surprise.

### Task 5 — Rebuild VS Code server binaries + commit
- `.\build.ps1 -Flavor Advanced -Config Debug -Mode Build -Force` (Advanced project-references
  Basic, so this covers both — see `project_advanced_extends_basic` in memory).
- Commit. Per [[feedback_build_before_commit]], build clean → commit immediately; per repo
  convention, one commit for the whole rollout is reasonable here (108 mechanically-identical
  edits, not 108 independent decisions) unless the fresh session's own judgment says otherwise
  partway through.

## Assumptions
- `MethodSignature` (commit `836a223`) is present in `master` and the VS Code Advanced binaries
  reflect it — confirmed at the end of the prior session, re-confirm in Task 0.
- No tool among the 108 already has a parameter literally named `reason` — spot-checked during
  the original design session (broad grep for "reason"/"Reason" across the repo found no MCP
  parameter collision), but `MethodSignature`'s own duplicate-name guard will refuse the add
  cleanly if this assumption is ever wrong for some file.
- Tool counts per file in the table above are current as of this plan's writing (`git log` HEAD at
  commit `836a223`) and will drift the moment Task 3 starts adding `reason` params — treat the
  table as a starting checklist, not a live source of truth; re-verify counts with
  `SearchSolutionText` if anything looks off mid-task.
- This is purely additive (new optional parameter, default `null`) — no existing tool behavior,
  return shape, or call site changes. Should be a zero-risk change from every existing caller's
  perspective.

## Related memory
- `project_tool_attribution_idea` — a previously-parked, different idea (an
  `[RoslynSentinel(AddedByAgent/...)]` attribute to mark tool-inserted code) — not the same thing
  as this `reason` parameter, don't conflate them.
- `feedback_new_tool_needs_fresh_session` — why Task 0 exists.
- `feedback_build_before_commit` — governs Task 5.
- `project_advanced_extends_basic` — why one rebuild (`-Flavor Advanced`) covers both server
  flavors.
