# Plan: echo tool-call inputs in every tool response (`toolCall` envelope)

Status: approved 2026-10-01, ready to implement.

## Problem

A tool response carries no record of the call that produced it: no tool name, no mode/operation,
no arguments. Reviewing a transcript, `agent.log`, or an offloaded large result means pairing each
response back to its call by position, which breaks down across long runs, retries, and offload
stubs. The only per-response identifier today is `SentinelCallToolResult.ResponseId`
(`RoslynSentinel.Common/SentinelCallToolResult.cs:120`), a `Guid.NewGuid()` property initializer
that is generated per record instance and is lost entirely on several paths (below).

## Decision: one outermost call-tool filter, no per-tool changes

Rejected alternative: thread a `toolCallId` through every tool method and set it on every
`SentinelCallToolResult`. It cannot cover responses that never reach a tool or never carry an
envelope, all in `RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`:

- argument-validation rejection (`AddArgumentValidationFilter`, plain text, ~line 728)
- orientation breaker refusal (~line 571) and unrecoverable breaker refusal (~line 645), plain text
- `SolutionNotLoadedException` and unexpected-exception catch (~lines 275-297), plain text
- the large-result offload filter replaces the whole body with an
  `{offloaded, resultId, sizeBytes, itemCount, isSuccess, statusMessage, message}` stub (~line 525),
  dropping `responseId`/`serverInfo` today
- tools whose result type is not `SentinelCallToolResult` (e.g. `ApplyChangesResult`)

It also touches every tool signature and has the forgotten-call-site failure mode.

Filter registration order: the first filter added in `WithRequestFilters` is the outermost (the
existing comment at ~line 517 confirms the IsError-sync filter, added earlier, wraps the offload
filter). A filter added **first** therefore post-processes **last** and sees the final content of
every path above.

## Response shape

Every response's first text content block gets a top-level `toolCall` property, inserted as the
**first** property of the JSON object:

```json
{
  "toolCall": {
    "toolCallId": "3f9a1c0be27d",
    "name": "ReplaceSnippet",
    "arguments": {
      "filePath": "c:\\...\\Foo.cs",
      "oldContent": "public void Bar()\r\n{\r\n    var x = 1;...[+412 chars, 18 lines]",
      "reason": "Replace Bar body with the new guard"
    }
  },
  "serverInfo": { ... },
  "isSuccess": true,
  ...
}
```

- `toolCallId`: first 12 hex chars of `Guid.NewGuid().ToString("N")` (token-thrifty; collisions are
  irrelevant at this scale).
- Text block whose text is a JSON **object**: insert `toolCall` at index 0 (`JsonObject.Insert`,
  available on net10.0). Leave every other property semantically unchanged.
- Text block whose text is anything else (plain text, JSON array, JSON primitive, unparseable):
  replace the text with `{"toolCall": {...}, "message": "<original text verbatim>"}`.
- Only the **first** `TextContentBlock` is stamped. No text block at all: leave the result as-is.
- Do **not** touch `StructuredContent`: it must conform to the tool's declared output schema.
- Do not change `IsError`.
- Cheap pre-check before parsing: only attempt `JsonNode.Parse` when the trimmed text starts with
  `{`; otherwise go straight to the wrap path.

## Argument echo and truncation

Snapshot `context.Params.Arguments` into a `JsonObject` **before** calling `next` - the validation
filter's `NormalizeParameterCase` mutates the dictionary in place, and the echo must show what the
model actually sent (this is the case most worth seeing on a validation rejection).

Truncation is by value shape, not a flat length - a flat 100-char cut chops real file paths, whose
filename is the useful part:

| Value | Rule |
| --- | --- |
| String with no newline (paths, names, `mode`, `reason`) | Keep up to 260 chars; beyond that, first 260 + `...[+N chars]` |
| String containing a newline (code, diffs) | First 100 chars + `...[+N chars, M lines]` (N = chars omitted, M = total line count) |
| Array | Echo the first 3 elements (recursively truncated), then append the string `...[+N more]` if longer |
| Object | Recurse into each property |
| Number / bool / null | Verbatim |
| Whole echo | If the serialized `arguments` object still exceeds 2048 chars, replace every object/array-valued top-level argument with `...[omitted, N chars]` |

Never split a surrogate pair at a cut point (back off one char if the last kept char is a high
surrogate). Markers are ASCII only (`...`, not an ellipsis glyph).

## Configuration

New static options class `RoslynSentinel.Common/ToolCallEchoOptions.cs`, following the
`LlmOptions` pattern (`RoslynSentinel.Common/LlmOptions.cs`): `public static bool Enabled { get;
private set; } = true;` plus `Configure(string[] args)` reading `--echo-tool-args` then
`ROSLYNSENTINEL_ECHO_TOOL_ARGS`; the values `false`/`0` (case-insensitive) disable it, anything
else (including absent) leaves it enabled. Call `ToolCallEchoOptions.Configure(args)` next to every
existing `LlmOptions.Configure(args)` call in server entry points (`Server.Advanced/ServerHttp.cs`,
`Server.Advanced/ServerStdio.cs`; check the Basic server entry points for an equivalent startup
block and add it there too if one exists). The filter reads `ToolCallEchoOptions.Enabled` at call
time and, when false, is a pure pass-through. Default **on**.

## Implementation steps

1. **Baseline first.** Before any edit, run `RunTest` on `RoslynSentinel.Tests.Battery.Basic` and
   `RoslynSentinel.Tests.Advanced` and record the failing set. The worktree contains another
   session's in-flight SubAgent work; compare before/after on the same tree.
2. **`ToolCallEchoOptions`** in Common (above).
3. **`ToolCallEcho`** static helper in `RoslynSentinel.Server.Basic` (alongside
   `ToolArgumentValidator`, which is the precedent for filter helpers living in Server.Basic):
   - `JsonObject CreateEcho(string toolCallId, string? toolName, IDictionary<string, JsonElement>? arguments)`
     - builds `{toolCallId, name, arguments}` with the truncation rules applied.
   - `void Stamp(CallToolResult result, JsonObject echo)` - applies the response-shape rules.
   - Keep the truncation constants as named `const`s at the top of the class.
4. **Register the filter** as the very first statement inside `mcpBuilder.WithRequestFilters(filters
   => { ... })`, before `AddArgumentValidationFilter(filters)`. Extract it to a private static
   `AddToolCallEchoFilter(IMcpRequestFilterBuilder filters)` mirroring `AddArgumentValidationFilter`,
   with a doc comment stating why it must be registered first. Wrap the stamping in try/catch ->
   `Debug.WriteLine` and return the unmodified result on failure (a diagnostic aid must never break a
   call), matching the other filters.
5. **Delete `ResponseId`** from `SentinelCallToolResult<TSuccess, TError>` (the filter's
   `toolCallId` replaces it). Update `RoslynSentinel.Tests.Advanced/McpTasksHarnessTestSupport.cs`
   `SerializeContent` to remove `toolCall.toolCallId` (not the whole `toolCall`) instead of
   `responseId`, and update its doc comment. `Search(mode: text, query: "responseId")` across C#
   to confirm no other reference remains. Leave historical mentions in `docs/current/CLOSED.md`.
6. **Tests** in `RoslynSentinel.Tests.Battery.Basic` (where `LargeResultOffloadFilterTests`,
   `OrientationBreakerFilterTests`, `ToolArgumentCaseNormalizationTests` already exercise the filter
   chain - copy their `SetUp` pattern), new file `ToolCallEchoFilterTests.cs`:
   - successful envelope response: `toolCall` is the first property, `name` matches, args echoed,
     rest of the body `JsonNode.DeepEquals` the un-stamped body
   - argument-validation rejection (unknown parameter): wrapped as `{toolCall, message}`, message
     verbatim, `IsError` still true, echo shows the **pre-normalization** argument name
   - case-normalized call (e.g. `filepath`): echo shows `filepath` as sent
   - offloaded large result: the offload stub carries `toolCall`
   - truncation unit tests on `CreateEcho`: long single-line path kept to 260, multi-line string
     cut to 100 with correct `+N chars, M lines`, array capped at 3 with `+N more`, 2048 overall cap,
     surrogate pair not split
   - options: `Configure` parsing for absent / `false` / `0` / `true`. If you test the disabled
     filter path, mark the fixture `[NonParallelizable]` and restore `Enabled` in teardown (static
     state).
7. **Fix fallout.** Existing tests that assert exact plain-text content of validation/breaker/
   SolutionNotLoaded responses, or exact JSON equality of a response body, will now see the wrapper
   or the extra property. Update the assertion to read the `message` field / ignore `toolCall` - do
   not weaken what the test is checking. List every test you changed in the report.
8. **Verify.** `Build` the solution (0 errors, no new warnings), then re-run the two baseline
   projects and compare: report only new failures. Then run the full suite once
   (`RunTest` on `RoslynSentinel.slnx`) and report totals.

## Open question to verify (do not route around)

Whether task-augmented (MCP Tasks) tool calls pass through the call-tool filter chain. After step 5,
`McpTasksHarnessTests.TaskCapableClient_PollingToCompletion_MatchesSynchronousResult` and
`McpTasksHarnessBulkCommentTests.TaskCapableClient_PollingToCompletion_DryRunMatchesSynchronousResult`
compare a synchronous result to its task-polled twin. If the polled result has no `toolCall` at all,
the task path bypasses the filters: stop, report it with the evidence, and do not add a second
stamping path - that is a separate design decision.

## Out of scope

- `ILogger` scope correlation by `toolCallId`.
- Echoing into `StructuredContent`.
- Reporting which argument names `NormalizeParameterCase` repaired.
