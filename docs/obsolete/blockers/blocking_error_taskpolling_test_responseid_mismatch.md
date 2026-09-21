# Task-polling vs. synchronous-call tests fail on full-content equality because `responseId` is a fresh GUID per call

**Status:** FIXED 2026-09-20. `McpTasksHarnessTests.cs` and `McpTasksHarnessBulkCommentTests.cs`
each had their own copy of `SerializeContent`; both were replaced with calls to a new shared
`McpTasksHarnessTestSupport.SerializeContent` (`RoslynSentinel.Tests.Advanced/McpTasksHarnessTestSupport.cs`)
that parses each `TextContentBlock`'s embedded JSON, strips `responseId`, and re-serializes -
preserving the original content-block-array shape so `FindDataElement`'s other callers are
unaffected. This is option 1 from "What would resolve this" below, applied via the shared-helper
route from option 3's intent (one normalization point instead of two copies). All 9
`McpTasksHarness*` tests pass, including the two that were failing.

## What was being attempted

While fixing 6 originally-failing tests in `RoslynSentinel.Tests.Advanced` and
`RoslynSentinel.Tests.Battery` (enum-rejection outcome mismatch in
`RoslynSentinel.Basic/RefactoringEngine.cs`'s `InsertMemberAfterAsync`/`InsertMemberBeforeAsync`, and
a stale `"data"` vs `"successDetails"` JSON key in
`RoslynSentinel.Tests.Battery/StructuredContentDataTagTests.cs` left over from the envelope
field-promotion rename in commit `fcc4190`), the session ran a broader filtered test pass that
happened to include two tests outside that original scope:

- `RoslynSentinel.Tests.Advanced/McpTasksHarnessTests.cs`,
  `TaskCapableClient_PollingToCompletion_MatchesSynchronousResult` (method at lines 107-127)
- `RoslynSentinel.Tests.Advanced/McpTasksHarnessBulkCommentTests.cs`,
  `TaskCapableClient_PollingToCompletion_DryRunMatchesSynchronousResult` (method at lines 100-126,
  failing assertion at line 115-118)

Both tests call the same MCP tool twice -- once synchronously via `CallToolAsync`, once through the
task-polling path via `CallToolWithPollingAsync` (backed by `CallToolAsTaskAsync` + polling) -- with
identical arguments, then assert the two results are byte-for-byte identical JSON via
`SerializeContent(polledResult) == SerializeContent(syncResult)`.

## The exact error text

From `TaskCapableClient_PollingToCompletion_MatchesSynchronousResult`:

```
String lengths are both 7585. Strings differ at index 375.
Expected: "...\"responseId\":\"a6b5b9ae-29c4-4987-8671-26d93...\""
But was:  "...\"responseId\":\"5ae3d7f7-771e-4d47-9af4-5821c...\""
```

`TaskCapableClient_PollingToCompletion_DryRunMatchesSynchronousResult` fails the same way, at its own
`Assert.That(SerializeContent(polledResult), Is.EqualTo(SerializeContent(syncResult)), ...)` (line
115-118 of `McpTasksHarnessBulkCommentTests.cs`), with two different GUIDs in the same `responseId`
position.

## Where it happened

- Test assertions: `McpTasksHarnessTests.cs:124-126` and `McpTasksHarnessBulkCommentTests.cs:115-118`.
- `SerializeContent` helper (identical shape in both files):
  `McpTasksHarnessTests.cs:165-166` / `McpTasksHarnessBulkCommentTests.cs:237` --
  `JsonSerializer.Serialize(result.Content)`. This serializes the MCP `CallToolResult.Content`
  collection, i.e. the tool's text content block(s), which for every RoslynSentinel tool is the JSON
  envelope described below -- not just a curated subset of fields.
- Field origin: `RoslynSentinel.Common/SentinelCallToolResult.cs:106`:
  ```csharp
  public string ResponseId { get; init; } = Guid.NewGuid().ToString();
  ```
  with doc comment (lines 100-105) explicitly stating its purpose is "so a human or agent reviewing a
  transcript/log can locate the exact tool response being discussed" -- i.e. it is *designed* to be
  unique per response instance, not a value that should ever be expected to match across two separate
  tool invocations.

## Root cause

Traced to source, not a hypothesis: `SentinelCallToolResult<TSuccess, TError>.ResponseId`
(`SentinelCallToolResult.cs:106`) is generated fresh via `Guid.NewGuid()` on every envelope
construction. Both tests invoke the target tool (`Features` in one file, `BulkComment` in the other)
twice -- once through `CallToolAsync`, once through the task-polling path -- which are necessarily two
separate server-side tool invocations and therefore two separate envelope instances, each getting its
own `ResponseId`. The tests' `SerializeContent` helper serializes the full `CallToolResult.Content`
(the entire JSON envelope, including `responseId`) and the assertion requires that whole serialized
string to match character-for-character between the two invocations. Since `responseId` is
by-design non-reproducible across invocations, this assertion was never satisfiable once
`ResponseId` became part of the serialized envelope content -- independent of whether the
task-polling path and the synchronous path are otherwise semantically equivalent.

Commit `a7a6e99` ("Add TSuccess/TError envelope generic, pilot on GetMethodSource + 5 lookup tools")
and `fcc4190` ("Promote envelope fields to top level (Phase 1 of field-promotion plan)") are the most
recent commits touching the envelope shape and are plausibly when `responseId` moved to (or became
more visible at) the top level of the serialized content that `SerializeContent` captures; this has
not been bisected, so treat it as context, not a confirmed introduction point. What is confirmed via
`git status` at the start of this session: none of this session's edits (`RefactoringEngine.cs`,
`SentinelWorkspaceTools.cs`, `StructuredContentDataTagTests.cs`) touch `SentinelCallToolResult.cs`,
`McpTasksHarnessTests.cs`, `McpTasksHarnessBulkCommentTests.cs`, the task-polling client path, or
`ResponseId` generation. This is a pre-existing defect, not a regression introduced by the session
that found it -- it was previously unnoticed/unsurfaced because the narrower filtered test runs used
in that session's original scope did not happen to include these two test files; a broader run did.

## Why this is an environment defect, not a model/test-author defect (per CLAUDE.md failure doctrine)

Per CLAUDE.md's failure doctrine, this doc describes what the test harness's assertion design failed
to account for, not a claim that "the test author should have known better." The assertion helper
(`SerializeContent`) gives no way to opt out of comparing a field that the envelope's own doc comment
(`SentinelCallToolResult.cs:100-105`) states is intentionally unique per response. Nothing in the test
file, the helper, or the envelope's public surface flags `ResponseId` as volatile/excluded-from-
equality -- a caller writing a "do these two calls return equivalent content" assertion has no local
signal that one specific field will always differ. That is the gap: either the envelope needs a
documented "volatile fields" marker/attribute the test layer can consume, or the test-comparison
helper needs to default to excluding known-volatile fields, rather than requiring every test author to
independently rediscover this by hitting the failure.

## What would resolve this

One of (not attempting any of these here -- docs only):

1. Change `SerializeContent` (or introduce a variant) in both test files to normalize/strip
   `responseId` before comparison -- e.g. parse to `JsonNode`/`JsonDocument`, remove the
   `responseId` property (and recursively from any nested envelopes if tool results can nest them),
   then re-serialize or structurally compare.
2. Replace the raw-string equality assertion with a semantic/structural JSON comparison that ignores
   a named allowlist of volatile fields (`responseId` today; anything similarly non-deterministic
   added later -- e.g. timestamps -- would need the same treatment).
3. Add a marker on `SentinelCallToolResult<TSuccess, TError>.ResponseId` (attribute, naming
   convention, or a static `SentinelCallToolResult.VolatileFieldNames` list) that a shared test-only
   helper reads, so future envelope fields added with per-call-unique semantics are excluded from
   equality assertions by construction instead of requiring each test file to special-case them by
   hand after a failure.

Any of these should be applied to both `McpTasksHarnessTests.cs:165-166` and
`McpTasksHarnessBulkCommentTests.cs:237` (and any other test using the same `SerializeContent`
pattern -- worth a repo-wide check before considering this closed) since the helper is duplicated
verbatim in both files.

## What's been ruled out

- Not a task-polling correctness bug: the two failures are pure `responseId` GUID mismatches at a
  single JSON position (`index 375` in the reported diff); nothing else in the 7585-character
  serialized payload differs (`String lengths are both 7585`), meaning the task-polled result and the
  synchronous result are otherwise byte-identical.
- Not caused by this session's edits: confirmed via `git status` that none of the 3 files modified
  this session (`RefactoringEngine.cs`, `SentinelWorkspaceTools.cs`,
  `StructuredContentDataTagTests.cs`) touch the task-polling path, the `SerializeContent` helpers, or
  `SentinelCallToolResult.ResponseId`.

## Related

- `docs/current/proposal_envelope_field_promotion.md` -- the field-promotion work that most recently
  reshaped the envelope's top-level fields; worth checking for prior discussion of `ResponseId`'s
  visibility, though this doc's root cause does not depend on that proposal being the introduction
  point.
