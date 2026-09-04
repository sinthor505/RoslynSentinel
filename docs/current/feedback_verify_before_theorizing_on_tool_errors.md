---
name: feedback-verify-before-theorizing-on-tool-errors
description: When multiple unrelated MCP tools suddenly fail with the same error, check the literal error and actual current file/schema state before building a causal theory tied to whatever changed most recently
metadata:
  type: feedback
  originSessionId: b36b7e80-314b-4a64-a7a9-10327c472699
  modified: 2026-09-04T09:05:56.666Z
---

While investigating [[project_methodsignature_null_default_bug]], a stretch of `MethodSignature`/
`GetMethodSource`/`ReadFile` calls all failed with `"The arguments dictionary is missing a value
for the required parameter 'filepath'."`. Instead of first checking what `'filepath'` actually
referred to, this was immediately theorized as a new "server-wide argument-binding outage" caused
by the user's just-prior edit to `GitTools.cs` (uncommenting a `RequestContext<CallToolRequestParams>
requestParams` debug parameter) — a plausible-sounding story built from timing/proximity, written up
in the blocker doc as a confirmed new finding, before checking a single fact.

The user pushed back with one question ("why would `requestParams` in GitTools cause this when it's
uncommented in other tools without issue") that immediately falsified the theory once checked: the
`requestParams` line was commented out in every file, including `GitTools.cs`, the whole time — and
`GitTools.Git` doesn't even have a `filePath` parameter, so it couldn't have been the target of the
failing calls in the first place. The real cause, found in under a minute once actually looked for:
the tools' real parameter is lowercase `filepath`, and every failing call used `filePath` (capital
P) — a plain casing typo repeated three times, unrelated to any recent code change.

**Why this matters:** an error message naming a specific identifier (`'filepath'`) is a direct,
checkable clue — grep the source or re-fetch the tool's schema for that exact identifier before
inventing a mechanism. Recency bias (blaming the most recent change in the room) produced a
confident, wrong, and elaborately-justified theory that a 30-second schema check would have avoided
entirely, and it got written into a "documented finding" before verification.

**How to apply:** when several previously-reliable tool calls suddenly fail identically, first (a)
re-read the literal error text for a concrete noun (parameter name, file path, type name) and check
it against current source/schema directly, before (b) reaching for "what changed recently" as an
explanation. Only write a root-cause finding into a blocker doc after that check, not before.
