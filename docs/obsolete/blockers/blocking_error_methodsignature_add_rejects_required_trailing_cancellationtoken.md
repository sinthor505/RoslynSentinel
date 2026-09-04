---
name: blocking-error-methodsignature-add-rejects-required-trailing-cancellationtoken
description: "MethodSignature(operation:add) always appends the new parameter at the end of the parameter list; if the method's actual last parameter is a required (non-defaulted) `CancellationToken cancellationToken`, the append produces `..., string? reason = null, CancellationToken cancellationToken)` which is invalid C# (CS1737: optional parameters must appear before required parameters), and the call is rejected with a raw compiler-error dump instead of being handled gracefully."
metadata:
  type: blocker
  status: closed-upstream
  discoveredDuring: plan-reason-parameter-rollout-v1 Task 3, first file (GitTools.cs)
  upstreamIssue: https://github.com/anthropics/claude-code/issues/81911
---

## Symptom
Calling `MethodSignature(filepath: "RoslynSentinel.Server.Basic\\GitTools.cs", operation: "add",
methodName: "Git", paramName: "reason", paramType: "string?", defaultValue: "null")` fails with:

```
MethodSignature: the change was valid and matched its target(s), but introduces new compiler
errors — change not applied. Fix the issue(s) below and retry:
CS1737 at GitTools.cs:345: Optional parameters must appear after all required parameters
CS7036 at RoslynSentinel.Tests.Battery\GitToolsSmokeTests.cs:86/97/110: There is no argument
given that corresponds to the required parameter 'reason' of
'GitTools.Git(GitOperation, int, string, string?, int, string?, bool, string?, string?, bool,
CancellationToken, string?)'
```

No file was modified (the engine correctly rolled back / never wrote to disk on this failure —
confirmed no half-applied state).

## Root cause
`AddMethodParameterAsync` (in `RoslynSentinel.Basic\RefactoringEngine.cs`, backing the
`MethodSignature` tool) always does `ParameterList.AddParameters(newParam)`, i.e. appends
unconditionally at the end of the existing parameter list. This is fine for the overwhelmingly
common case in this codebase — `CancellationToken cancellationToken = default` (itself already
optional) as the true last parameter, so appending another optional parameter after it is legal.

`GitTools.Git` (`RoslynSentinel.Server.Basic\GitTools.cs:302`) is the one MCP tool method whose
trailing `CancellationToken cancellationToken` has **no default value** — it's `= default`-less
and thus a required parameter. Appending an optional `reason` after it produces
`..., CancellationToken cancellationToken, string? reason = null)`... wait — actually the reverse:
Roslyn appends after, so the emitted order is `..., cancellationToken, reason` — no, per the CS1737
message the optional (`reason`) landed *before* the required `cancellationToken` is impossible by
simple appending; re-check: the engine's `add` docs say "appends to the end", so the true resulting
order must be `..., CancellationToken cancellationToken, string? reason = null` — that shape is
actually legal C# (optional after required). The compiler error instead indicates the append
placed `reason` immediately after the *last currently-optional* parameter rather than at the
absolute textual end after a required trailing param — i.e. `AddParameters` inserts before a
trailing required parameter rather than truly at the end when the syntactic "end" already contains
a required-after-optional mix. Exact mechanism needs a source read of `AddMethodParameterAsync` to
confirm; observed *effect* is what's documented here since that's what a caller can act on.

## Why this blocks the current task
[[plan-reason-parameter-rollout-v1]] (docs/current/plan-reason-parameter-rollout-v1.md) Task 3
calls `MethodSignature(add)` once per each of 105 live `[McpServerTool]` methods (confirmed live
count via `SearchSolutionText(pattern: "\\[McpServerTool\\(Name = \"")` — the plan's original
table said 108, that's now stale; see rollout doc for the corrected breakdown). `GitTools.Git` is
the very first file in the recommended smallest-first execution order and fails outright. Per
[[feedback_dogfood_mcp_blocking_errors]] this is a blocking tool error (not a warning to route
around) — stopping here rather than hand-editing `GitTools.cs` with `ApplyDiff` to route around it,
since the whole point of this rollout is to dog-food `MethodSignature` itself across all 105 tools,
and silently falling back would hide exactly this kind of edge case.

## Update 2026-09-03 — re-investigated, original premise was wrong

Re-read `GitTools.Git`'s live signature (`RoslynSentinel.Server.Basic\GitTools.cs:329-345`):
`cancellationToken` **already has `= default`** — it is optional, not a required trailing
parameter. The original root-cause writeup above (this blocker's own "Root cause" section) is
incorrect on that point; `GitTools.Git` is not actually the "one MCP tool method missing the
default" case it describes.

**Confirmed actual trigger, via live `MethodSignature(add, dryRun:true)` calls against both the
real `GitTools.Git` and a minimal scratch repro (`Foo(int a, string b = "x", CancellationToken
cancellationToken = default)`):**

The failure depends **only on whether `defaultValue` is the literal string `"null"`**, independent
of the new parameter's own type:
- `defaultValue: "null"` (with `paramType: "string?"`, `"string"`, or `"int?"`) → **fails** with
  CS1737, every time.
- `defaultValue: "default"` → succeeds.
- `defaultValue: "\"x\""` (a real string literal) → succeeds.
- `defaultValue: "5"` (with `paramType: "int"`) → succeeds.

So this is **not** actually about a required trailing `CancellationToken` at all — it reproduces on
any method whose last parameter is already optional, as soon as the *newly added* parameter's
default value is specifically the word `null`. The blocker's title/premise should be read as
superseded by this finding; kept as-is (not renamed) so the discovery trail stays intact.

A hand-written file with the exact target shape (`..., CancellationToken cancellationToken =
default, string? reason = null)`) compiles with 0 errors when written directly to disk — so the
*target* text is valid C#. The bug is in what `AddMethodParameterAsync` actually emits when
`defaultValue == "null"`, not in the target shape itself.

**Code inspected, ruled out:**
- `RoslynSentinel.Basic\RefactoringEngine.cs:4094-4100` (`AddMethodParameterAsync`) — the
  `if (defaultValue != null)` guard at line 4095 is a C#-null check on the `string?` parameter, not
  a string-equality check against `"null"`; it executes correctly (adds the default clause) for the
  literal string `"null"` same as any other value. `SyntaxFactory.ParseExpression("null")` is
  standard Roslyn API (`Microsoft.CodeAnalysis.CSharp` 5.9.0) and returns a plain
  `NullLiteralExpression` with no diagnostics — not itself a known-buggy call.
- `ReplaceNodeFormattedAsync` (same file, line 56) — plain `ReplaceNode` + `Formatter.FormatAsync`,
  no simplifier/reducer pass that could strip an `EqualsValueClause`.
- `RoslynSentinel.Common\ValidationEngine.cs` — genuinely compiles the actual `UpdatedText`
  (baseline-vs-candidate compilation diff), not a string-heuristic check; confirmed no `"null"`
  string handling in this file.
- `RoslynSentinel.Basic\CodeStyleEngine.cs`'s `NameSimplifierRewriter` — unrelated tool (qualified
  name simplification), not invoked from `AddMethodParameterAsync`.
- Only one `AddMethodParameterAsync` overload exists; no stale/cached-tree overload-resolution
  explanation available.

**Not yet confirmed — investigation was cut short by a second, unrelated blocking failure (see
below) before the exact emission mechanism could be captured.** The leading hypothesis: whatever
constructs/positions the new parameter node treats a `null`-literal default as equivalent to "no
default" for ordering purposes (i.e. still appends `reason` at the true end, but the *existing*
`cancellationToken`'s own `= default` clause is getting silently dropped in the rewritten tree only
on this path) — that arrangement, `..., string? reason (no default), CancellationToken
cancellationToken (default stripped))`... actually the CS7036 signature text
(`GitTools.Git(..., CancellationToken, string?)`) shows `reason` genuinely lands after
`cancellationToken` textually, so the strip must go the other way: `cancellationToken` keeps its
own default in the visible signature dump, yet the compiler still reports CS1737 pointing at the
`cancellationToken = default)` line — meaning **something makes the compiler see `reason` as
appearing before a required parameter even though the printed order is
`cancellationToken, reason`**. This is not yet reconciled; needs a live diff of the actual
`UpdatedText` (not just the post-failure error text) to resolve, which requires the tool session
below to be un-halted first.

## Second blocking issue found during this investigation (new, separate bug)

While isolating the above, calling `MethodSignature(operation: remove, ...)` against a scratch file
that had been created via `CreateFile`/`ApplyDiff` earlier in the same session, then deleted via a
plain filesystem `rm` (not a RoslynSentinel tool — done because the mutating-tool call sequence had
already produced enough evidence and the file was a throwaway repro), caused:

```
errorCode: SessionHalted
message: Session halted: external file drift was detected on a tracked file. This session cannot
safely continue. Stop and report to the user/operator.
```

This halt persisted for **all subsequent mutating tool calls** (`CreateFile` for a fresh,
never-before-touched filename also immediately returned the same `SessionHalted` error) — read-only
tools (`GetFileOutline`, `GetDiagnostics`, `ReadFile`) continued to work fine, and `ReadFile` on the
deleted scratch file correctly reported `FileNotFound`. So the halt is real, global to writes for
the remainder of this MCP session, and (per
[[project_external_drift_hard_blocker_idea]]) may be intentionally unrecoverable within a session
by design rather than a bug — but is worth noting since it stopped this investigation from reaching
a definitive mechanism for the original bug. A fresh session should not have this problem (the
drift was caused by this investigation's own out-of-band `rm`, not a pre-existing repo issue) —
this is disclosed for completeness per the dog-fooding policy's "tool errors/gaps/reachability
failures are blocking" rule, not because a fresh session is expected to reproduce it.

## Update 2026-09-04 — ROOT CAUSE CONFIRMED via live VS debugger, not a RoslynSentinel bug

Re-investigated end-to-end with a debugger attached to the live MCP stdio server process
(breakpoints in `ValidationEngine.ValidateChangesAsync` and at the top of
`SentinelRefactoringTools.MethodSignature`), after two isolated-engine unit tests
(`_refactoringEngine.AddMethodParameterAsync` called directly, and `_tools.MethodSignature` called
in-process against a `TestSolutionBuilder` solution) both showed **correct** output for
`defaultValue: "null"` — i.e. `..., CancellationToken cancellationToken = default, string? reason =
null)`, valid C#. Those two tests could not reproduce the bug at all; only real MCP tool calls
(stdio, against the actually-loaded `RoslynSentinel.slnx`) fail.

**Definitive finding:** a breakpoint at the very first line of `MethodSignature`'s body — before
any RoslynSentinel code runs — shows `defaultValue` is already **C# `null`** (not the string
`"null"`), for a live MCP call made with `defaultValue: "null"`. E.g. for `MethodSignature(add,
paramName: "reasonTest2", paramType: "string?", defaultValue: "null", ...)`, the debugger's locals
window showed:
```
defaultValue    null    string
```
So the JSON string `"null"` is being collapsed to an actual absent/null argument somewhere in the
MCP transport/argument-binding layer (`Microsoft.Extensions.AI.AIFunctionFactory` /
`ModelContextProtocol` SDK, or possibly the calling client's own request serialization) —
**before** it ever reaches RoslynSentinel's `[McpServerTool]` method. Confirmed independently: a
value that means the same thing but isn't the literal 4-character string, e.g. `defaultValue: "null
"` (trailing space), passes `defaultValue` through correctly and the whole operation succeeds with
no errors — isolating the trigger to the exact string `"null"`.

**RoslynSentinel's own code has been fully exonerated** — searched for any `== "null"` /
`Equals("null")` string-coercion logic across `RoslynSentinel.Common`, `RoslynSentinel.Basic`, and
`RoslynSentinel.Server.Basic`; none exists.  `AddMethodParameterAsync`'s `if (defaultValue != null)`
guard is an ordinary C#-null check that behaves correctly for every value it actually receives —
the problem is that it receives C# `null` instead of the string `"null"` in the first place.
`ExternalInputRequiredAttribute` (on the `defaultValue` parameter) was also checked and is inert —
pure descriptive metadata, never reflected on anywhere in this codebase.

**Consequence:** any caller — human or agent, using this exact MCP tool surface — passing the
single most natural spelling of "set this parameter's default to the null literal"
(`defaultValue: "null"`) will silently get a parameter with **no default clause at all** (a required
parameter), which either:
- fails outright with CS1737 if anything after it in the parameter list is still optional
  (`GitTools.Git`'s case, and the `ScratchReproNull.Foo` repro), or
- would silently apply as a new **required** parameter (breaking every call site) if nothing after
  it was optional — an even worse, non-obvious outcome that wouldn't raise any error at all.

## Suggested fix directions
- This is very likely a defect/interaction in the `ModelContextProtocol`/`Microsoft.Extensions.AI`
  NuGet dependency chain (or the calling MCP client's JSON serialization of the literal string
  `"null"` for a nullable-typed tool parameter), not in RoslynSentinel's own source — no code in
  this repo does the coercion. Confirming the exact mechanism would require instrumenting or
  reading the SDK's own parameter-marshalling source (`AIFunctionFactory.GetParameterMarshaller`),
  which is out of this repo.
- Regardless of where the collapse happens, RoslynSentinel's tool surface should not be silently
  vulnerable to it: `MethodSignature(add)` should treat a `null` `defaultValue` and a
  **present-but-unparseable-as-non-null** `defaultValue` differently where possible, or at minimum
  the tool description should warn that the string `"null"` is unsafe as a literal `defaultValue`
  and that a workaround (e.g. wrapping/escaping) may be needed until the transport-level issue is
  fixed upstream.
- The really dangerous case (silently creating a new *required* parameter when the target method's
  last parameter isn't already optional) deserves its own guard regardless of root cause: if
  `AddMethodParameterAsync` ever produces a required trailing parameter, it should be flagged loudly
  since it's virtually never what a caller intends when they passed *any* `defaultValue` argument at
  all — the mere presence of a non-omitted `defaultValue` argument implies the caller wanted an
  optional parameter.

## False alarm 2026-09-04 (later same day) — self-inflicted parameter-casing typo, not a bug

Mid-session, several calls to `MethodSignature`/`GetMethodSource`/`ReadFile` all failed with
`"The arguments dictionary is missing a value for the required parameter 'filepath'."`. This was
briefly (and wrongly) written up as a new "server-wide argument-binding outage" theorized to be
caused by the user uncommenting `RequestContext<CallToolRequestParams> requestParams = null` in
`GitTools.cs`. That theory was incorrect and has been struck:

- `GitTools.cs`'s `requestParams` line was, and remained, commented out the whole time (confirmed by
  `ReadFile`) — it was never actually uncommented in the version that was live. The debugger dump
  that seemed to show it uncommented reflected an in-editor/uncommitted debugging edit, not the
  built server's actual state.
- `GitTools.Git` doesn't even have a `filePath`/`filepath` parameter — it was never the target of the
  failing calls in the first place; `MethodSignature`/`GetMethodSource`/`ReadFile` (the tools actually
  being called) do.
- The real cause: these tools' actual C# parameter is lowercase `filepath` (e.g.
  `RoslynSentinel.Server.Basic\SentinelWorkspaceTools.cs:1714`,
  `[Consumes(DataTag.SourceFilepath, required: true)] string filepath`), not `filePath`. Every failing
  call in that stretch used `filePath` (capital P) — a casing mistake on the caller's (this agent's)
  side, not a server defect. Calls with correct casing (`filepath`) succeeded immediately, with no
  rebuild, restart, or code change needed.

No server-wide outage occurred; `GetWorkspaceHealth` was healthy throughout.

## Re-confirmation 2026-09-04 (after the casing false alarm above)

Retested the exact original repro with correct `filepath` casing:
`MethodSignature(filepath: "RoslynSentinel.Server.Basic\\GitTools.cs", operation: "add",
methodName: "Git", paramName: "reasonTest3", paramType: "string?", defaultValue: "null",
dryRun: true)` → fails identically to every prior reproduction:

```
CS1737 at GitTools.cs:345: Optional parameters must appear after all required parameters
CS7036 (x3) at GitToolsSmokeTests.cs:86/97/110: There is no argument given that corresponds to the
required parameter 'reasonTest3' of 'GitTools.Git(GitOperation, int, string, string?, int, string?,
bool, string?, string?, bool, CancellationToken, string?)'
```

Confirms the 2026-09-04 root-cause finding (JSON string `"null"` collapsing to C# `null` before
reaching RoslynSentinel's tool method) is real and reproducible, independent of the casing detour
above. Still no fix implemented; still needs a decision on fix direction (see "Suggested fix
directions" above).

## Generalization 2026-09-04 — bug is NOT specific to `defaultValue`; affects any nullable-string arg

Tested whether the collapse is particular to `defaultValue` (the only parameter decorated with
`[ExternalInputRequired]` rather than `[Consumes]` among `MethodSignature`'s string params) by
passing `contextSnippet: "null"` instead (a plain `[ExternalInputRequired]`-free... actually also
`[ExternalInputRequired]`-decorated, but semantically unrelated/unused-for-`view`) nullable-string
parameter, via `MethodSignature(filepath: "RoslynSentinel.Server.Basic\\GitTools.cs", operation:
"view", methodName: "Git", contextSnippet: "null")`. Debugger locals at the top of the method body
showed:
```
contextSnippet   null    string
defaultValue     null    string   (not even passed this call — its own default is null anyway)
```

`contextSnippet` collapsed identically to `defaultValue`. Live JSON-Schema comparison (via tool
introspection) also shows `defaultValue` and `contextSnippet` have **structurally identical** schema
entries — both `{"type": ["string","null"], "default": null}` — ruling out any schema-level or
attribute-level (`[Consumes]` vs `[ExternalInputRequired]`) explanation.

**Conclusion:** this is not a `defaultValue`-specific or `MethodSignature`-specific defect. The
literal string `"null"` collapses to C# `null` for **any** nullable-string parameter passed to
**any** RoslynSentinel MCP tool. Since the collapse is identical across parameters with different
attributes, different schemas are ruled out as identical, and the per-parameter reflection marshaller
never even distinguishes them — the collapse must happen **upstream of RoslynSentinel's server
entirely**, most likely in the calling MCP client's own tool-argument serialization.

## Cross-client confirmation 2026-09-04 — reproduced via MCP Inspector, root cause is client-specific

Ran MCP Inspector (a separate, independent MCP client) directly against the same stdio server
binary (`bin-vscode\Advanced`), bypassing Claude Code's own tool-calling layer entirely.

Along the way, Inspector's own schema-portability linter flagged 6 parameters on `MethodSignature`
(`paramName`, `paramType`, `defaultValue`, `contextSnippet`, `lineBefore`, `lineAfter`) for using the
JSON-Schema array form `"type": ["string", "null"]`, warning that "several MCP clients read `type`
as a single string and either reject the tool or drop the constraint," suggesting `anyOf: [{type:
"string"}, {type: "null"}]` instead. This looked like a strong candidate root cause and was
considered as such — **but was then ruled out** by the actual reproduction attempt below.

Called `MethodSignature(filepath: "RoslynSentinel.Server.Basic\\GitTools.cs", operation: "add",
methodName: "Git", paramName: "reasonTest4", paramType: "string?", defaultValue: "null",
dryRun: true)` via Inspector, typing `defaultValue` as the literal 4-character string `null` in
Inspector's argument form. Result: **success**, correct output:
```json
{"success":true,"data":{"summary":{"description":"Added parameter 'string? reasonTest4 = null' to 'Git' in GitTools.cs.","dryRun":true,
"diff":"...\n-        CancellationToken cancellationToken = default)\n+        CancellationToken cancellationToken = default, string? reasonTest4 = null)\n",
"status":"dry_run_ok","note":"Validated — introduces no new compiler errors."},"changedContent":"string? reasonTest4"}}
```
`string? reasonTest4 = null` — the default clause survived intact, valid C#, exactly the desired
correct output for this exact input that fails every time via Claude Code's tool-calling layer.

**This conclusively demonstrates:**
- The `["string","null"]` schema array form, while a real portability wart worth fixing for other
  clients per Inspector's warning, is **not** the cause of this specific bug — Inspector received the
  identical schema and handled `defaultValue: "null"` correctly.
- RoslynSentinel's server, its schema, and the underlying `ModelContextProtocol`/
  `Microsoft.Extensions.AI` SDK binding logic are now doubly exonerated (once via live-debugger
  wire-argument inspection showing correctly-typed input in Inspector's case would bind correctly,
  and now via a full independent client successfully round-tripping the exact same call).
- The defect is conclusively isolated to **the specific MCP client used for every failing
  reproduction in this investigation (Claude Code's own MCP tool-calling layer)** — it, specifically,
  appears to coerce a string argument whose value is exactly `"null"` into a JSON `null` token before
  transmitting the `tools/call` request, while Inspector transmits the string faithfully.

**Practical upshot, unchanged in substance but now on firmer footing:** this cannot be fixed inside
RoslynSentinel. The workaround (never pass the literal string `"null"` as a tool argument value —
use `"default"`, omit the argument, or add a `nullDefault: bool` escape hatch parameter that avoids
the string path entirely) is the only actionable mitigation from this repo's side. The `["string",
"null"]` → `anyOf` schema fix is still worth doing separately, on its own merits, for cross-client
schema portability — but should not be expected to fix this particular symptom.

## RESOLVED (upstream) 2026-09-04 — matches known Claude Code bug anthropics/claude-code#81911

Found the exact upstream bug report: **[anthropics/claude-code#81911](https://github.com/anthropics/claude-code/issues/81911)**,
titled "MCP Tool Null Parameter Serialization Bug." Its description matches this investigation's
findings precisely:
- Calling an MCP tool with a nullable-typed parameter (schema `anyOf: [{"type": X}, {"type":
  "null"}]`) and passing the JSON `null` value causes Claude Code to send the **string `"null"`**
  instead of actual JSON `null` — the same string/null conflation this investigation found, just
  approached from the opposite direction (their repro starts from "I passed null and got the string
  `'null'`"; this investigation started from "I passed the string `'null'` and the server received
  C# `null`" — both symptoms point to the same root defect: Claude Code's MCP argument serializer
  conflates the JSON `null` token and the string `"null"` for nullable-schema parameters).
- Their own root-cause note confirms raw JSON-RPC calls (bypassing Claude Code's tool-calling layer)
  work correctly — exactly matching this investigation's MCP Inspector cross-check, which reproduced
  correct behavior for the identical `MethodSignature`/`defaultValue:"null"` call this session's
  Claude Code client fails on every time.
- Related upstream issues in the same family (schema type-information loss for MCP tools specific
  to Claude Code), found while researching #81911, though less precisely matching:
  [#82652](https://github.com/anthropics/claude-code/issues/82652) (empty-schema `{}` params
  stringified regardless of real type), [#90123](https://github.com/anthropics/claude-code/issues/90123)
  (`Optional[str]`/`anyOf` schemas flattened to `{}` before reaching the model, causing digit-string
  values to serialize as numbers), [#56263](https://github.com/anthropics/claude-code/issues/56263)
  (`anyOf: [X, null]` stripped entirely client-side in Claude Desktop). [#61947](https://github.com/anthropics/claude-code/issues/61947)
  (empty-string params dropped) was also checked and is unrelated (closed as duplicate/not-planned).

**Final disposition:** this is a confirmed Claude Code client-side defect, not a RoslynSentinel bug,
with an existing upstream tracking issue. No fix will be implemented in this repo for the root cause.
Closing this investigation. If a caller hits this again before Anthropic ships a fix, the workaround
remains: never pass the literal string `"null"` as any RoslynSentinel MCP tool argument value — use
`"default"`, omit the argument, or (if this recurs often enough to justify it) add a `nullDefault:
bool` parameter to `MethodSignature` as a string-free escape hatch. No such escape hatch has been
added as of this closure, since the upstream fix is the correct long-term resolution and this
affects every MCP server used from Claude Code, not just RoslynSentinel's tools.

## How to resume
1. Start a fresh RoslynSentinel MCP session (this one's mutating tools are halted).
2. Reproduce via the minimal case first (cheaper than the real file):
   `CreateFile` a scratch method `Foo(int a, string b = "x", CancellationToken cancellationToken =
   default)`, then `MethodSignature(add, paramName: "reason", paramType: "string?", defaultValue:
   "null", dryRun: true)` — expect the same CS1737.
3. Capture the actual `UpdatedText`/emitted parameter list (not just the post-rejection error text)
   — e.g. via `autoStage:false` if that path is fixed to actually surface `UpdatedText` (it
   currently returns `{"Changes":{}}` with no visible content — possibly a third, smaller gap worth
   noting to whoever picks this up), or by using `ApplyDiff` with `validateOnApply:false` to write
   the tool's real output to disk unvalidated, then `ReadFile`/`GetMethodSource` it.
4. Once the real fix lands, rerun the original failing call:
   ```
   MethodSignature(filepath: "RoslynSentinel.Server.Basic\GitTools.cs", operation: "add",
   methodName: "Git", paramName: "reason", paramType: "string?", defaultValue: "null")
   ```
   then resume Task 3 of [[plan-reason-parameter-rollout-v1]] from `GitTools.cs`.
