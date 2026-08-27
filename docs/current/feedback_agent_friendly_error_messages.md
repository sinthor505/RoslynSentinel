---
name: feedback-agent-friendly-error-messages
description: "All tool responses, especially errors, must never expose raw exceptions/internal details to the agent — log those server-side instead"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 8cc40adc-02cd-47df-a2c3-a5257d5998e5
  modified: 2026-08-27T17:25:42.639Z
---

Every MCP tool response — especially error messages — must be agent-friendly: a clean, self-contained
statement of what went wrong, with no raw exception text, stack traces, internal file paths, or
references to internal documentation (e.g. `docs/TODO.md` entries) that the agent can't open.
Full diagnostic detail belongs in the server-side log (`_logger.LogError`/`LogWarning`), not in the
string that flows into `ResultError`/`ToolResult.Error`.

**Why:** Caught live in [[project_overnight_todo_run_2026_08_27]]'s Git-timeout follow-up: a
`TimeoutException` thrown from `GitTools.RunGitAsync` initially had a `Message` containing the raw
git args, the exact timeout value, and a `see docs/TODO.md's '...' entry` cross-reference. Every
`GitTools` operation method (`StatusAsync`, `LogAsync`, etc.) catches `Exception ex` and returns
`Error = $"... failed: {ex.Message}"` straight to the calling agent — so all of that internal detail
would have leaked into the agent-visible result verbatim. The user caught this by inspection before
it shipped, not because a test failed.

**How to apply:** Whenever an exception's `Message` (or any hand-built error string) is going to flow
into a tool's returned `Error`/`ResultError`, keep that string minimal and self-contained — state the
failure and the outcome ("Git operation timed out after 30s and was cancelled."), nothing about *how*
the code detected it or *where* to read more internally. Route the full detail (args, values, doc
cross-references, stack traces) through the logger instead, at the point the error is caught/thrown.
This applies equally to newly-added tools ([[project_operation_blob_json_gotchas]] has more on
result-shape gotchas elsewhere in the codebase) and to any existing tool being touched for another
reason — if you notice a raw-exception leak while editing nearby code, fix it as part of that change.
