# The marshaller writes the echo and stamps itself, so EchoReserveChars can go

**Status:** PROPOSED 2026-11-07. Awaiting review; nothing built. Replaces the "merge the stamping filters" idea in
plan_wire_result_marshaller "Out of scope", which a survey on 2026-11-07 showed cannot remove the reserve by itself.

## Motivation

`ToolResultMarshaller` fits a response to `InlineBudgetChars - EchoReserveChars` (15360 - 2560 = 12800 chars,
`Common/ToolResultMarshaller.cs` ~:104; `LargeResultHelper.cs` ~:29). The reserve exists because the marshaller runs
innermost, before filters that add text to every response. Verified (explorer, source read, 2026-11-07):

| Filter (outermost first, `Server.Advanced/ServiceRegistrationExtensionsBasic.cs` ~:92-106) | Adds to the wire text |
| --- | --- |
| ToolCallEcho | `toolCall{toolCallId,name,arguments}` first, or wraps non-object text in `{toolCall,message}` (`ToolCallEcho.cs` ~:104-133); on first start only, `ReplaceSnippetLimits` |
| HaltStamp | `isSessionHalted`, `sessionHaltKind`, `sessionHaltReason`, `sessionHaltRecovery` |
| WorkspaceStaleStamp | `workspaceStale`, `workspaceStaleReason`, `workspaceStaleRecovery` |
| ToolError | nothing (copies body `isError:true` onto `CallToolResult.IsError`) |
| LargeResultOffload (backstop), WirePathRelativize, breakers | stub for unseen over-budget bodies; path shortening |

The reserve is a constant, not a measurement. The echo's `MaxArgumentsChars = 2048` bound applies only to object and array
arguments (`ToolCallEcho.cs` ~:51-60), so a call with several long string arguments can exceed 2560 (hypothesis: no such call
seen). Halt plus stale plus echo together may also exceed it (reason and recovery lengths not measured). A response whose
marshalled text just fits can then leave the process over 15360 chars; the backstop catches it and logs "Result marshaller
miss" (`ServiceRegistrationExtensionsBasic.cs` ~:580, no test pins it).

## Proposal

Make the marshaller the last writer of response text for the responses it handles, so the budget is exact.

1. **Compose the echo and stamps in the marshaller** from `ToolCallContext` (call id, tool name, arguments, halt and stale
   state), build the final object once, then check the budget against the real final size and page if needed.
2. **Keep the filters for responses the marshaller never sees** (refusals from ArgumentValidation, breaker exceptions, the
   exception catch-all, mode off / passthrough): they stamp exactly as today; the marshaller marks its output as already
   stamped (a never-serialized flag on `ToolCallContext`) so the filters do not stamp twice.
3. **Replace the constant**: `EchoReserveChars` becomes `[Obsolete]`; `GetLargeResult` page sizing (same subtraction at
   `WorkspaceReadNavigationImpl.cs` ~:1598, ~:1738) and test `GetLargeResultTests.T18_...FillsSizeBudgetWithShortLines` use
   the measured size of the same stamp composition, or a small fixed safety margin for the pieces added after the marshaller
   (the offload stub only).
4. **Ordering invariants to keep** (verified): echo outermost so it sees refusals and pre-normalization arguments
   (`ServiceRegistrationExtensionsBasic.cs` ~:1044-1062); telemetry reads `IsError` after ToolError synced it; the orientation
   breaker post-check runs outside the marshaller, so halt state read inside the marshaller can be stale by one filter - the
   outer filter must patch the stamp when the state changed after the marshaller ran.

Cheap, separate, optional slice with no output change: merge HaltStamp, WorkspaceStaleStamp and ToolError (adjacent at #5-7)
into one finalizer to save two or three JSON parse and reserialize passes per response. It does not touch the reserve.

## Alternatives considered, not pursued

- **Merge the stamping filters into one finalizer outside the marshaller.** Still adds unmeasured text after the budget
  check, so the reserve stays. Rejected as the answer to this problem; kept as the optional slice above.
- **Finalizer inside the offload filter, with the stub finalized too.** An overflow would become a `Raw`-style offload
  instead of a paged envelope - worse for the caller, and the `Raw` wrapper no longer exists.
- **Measure the echo and stamps at runtime and feed the number back to the marshaller.** The marshaller runs before them, so
  it would have to predict them; the prediction is the same guess as the constant.
- **Shrink the echo** (drop arguments from it). Does not remove the unmeasured part and changes what the model sees.

## Open questions

1. Should ArgumentValidation refusals get halt and stale stamps? Today they do not pass through the marshaller.
2. How is halt state read exactly once with the orientation breaker's post-check? (Needs a read of the breaker filter.)
3. Does `LargeResultOffloadFilterTests` still need the reserve for its stub-shape assertions?
4. Not checked: real echo and stamp sizes, `Server.Advanced` registrations beyond the Basic one, whether
   `ToolResultMarshaller*Tests` assume the reserve.

## Cost / risk

- Touches `ToolResultMarshaller`, `ToolCallContext`, `ToolCallEcho`, the two stamp filters, `GetLargeResult` sizing and
  five fixtures: `ToolCallEchoFilterTests` (34 tests, ~6 through the real pipeline), `HaltStampFilterTests` (7),
  `WorkspaceStaleStampFilterTests` (5), `LargeResultOffloadFilterTests`, `ToolTelemetryFilterOrderTests` (pins filter order).
- Wire shape must stay identical; every model sees the echo and stamps, so a regression is global.
- Needs a server restart and a live check on a response near the budget.
- Value: exact budget, no "Result marshaller miss" from an oversized echo; the cost is a cross-cutting change for a case that
  has not been seen in a real run. Recommend building it only if a miss is observed or the reserve starts costing pages.

## Related

- [plan_wire_result_marshaller.md](../plans/closed/plan_wire_result_marshaller.md) -- Out of scope item this replaces.
- [reference_tool_response_pipeline.md](../references/reference_tool_response_pipeline.md) -- filters and the reserve.
