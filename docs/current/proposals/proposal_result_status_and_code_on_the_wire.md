# Put the result status (and a single cause key) on the MCP wire

**Status:** PROPOSED 2026-10-24. Awaiting review; nothing built. Follow-on to the DONE `IResultStatus` work.

## Motivation
`IResultStatus` (Status, Code, Message, IsError) is implemented across the result types
(`plans/plan_shared_result_status_interface.md`, DONE 2026-10-11), but the envelope a client sees does
not carry it. In `RoslynSentinel.Common/SentinelCallToolResult.cs` the envelope's `Status`, `Code` and
`Message` are each `[JsonIgnore]` and documented "Not serialized" (lines ~205-231). On the wire a client
gets `isError`, and on failure `errorData.{errorCode,message,detail}`; on success only a free-text
`statusMessage`. Three consequences, each an environment defect (a weak model, a script or a hook has no
structured field to branch on):

1. **Empty-but-valid is indistinguishable from found.** `Search(text)` with no hit returns
   `isError:false`, `totalRecords:0` and a `statusMessage` ("No matches were found for ...",
   `WorkspaceReadNavigationImpl.cs` ~924-945). `FindReferences` builds `statusMessage` from
   `summary.ToSummaryMessage(...)` (`SymbolRelationshipImpl.cs:269, 287, 323`); the transcript scan shows
   its zero-hit text, and `RunTest` has a zero-test success (producer not located: hypothesis, no
   source read). The only way to tell "none" from "some" is keyword-matching text. Even the existing
   non-error `ResultStatus.AlreadyInState` cannot reach the wire: the envelope getter returns `Success`
   whenever `!IsError` (`SentinelCallToolResult.cs:~213-216`).
2. **One cause, several spellings.** "Not found" is `NotFound` (`ToolErrorCode`, lines 75-168) but also
   `FileNotFound`, `MethodNotFound`, `NoOperationBlobFound`, `GitRootNotFound`, `GitPathNotFound`,
   `GitTargetNotFound` and (historically) `NoMatches`; see the table below. All of the stray ones fall
   into the `_ => ResultStatus.Failed` arm of `ResultStatusExtensions.FromErrorCode`
   (`IResultStatus.cs:55-67`, verified: no arm for them). Grouping by cause needs a hand-kept regex
   (`Get-NotFoundRecoveryReport.ps1` `-CodePattern`).
3. **The Git tool contradicts the envelope.** `GitErrorCodes.ToResultStatus` (`GitImpl.cs` ~84-92) maps
   `GitPathNotFound`/`GitTargetNotFound` to `NotFound`, but `GitTool` puts the raw code into a plain
   `ResultError` (`GitTools.cs:170`, `GitRemoteTools.cs:137`), so the envelope's own `Status` (via
   `FromErrorCode`) says `Failed` for the same failure.

**Measured** with `scripts/Get-NotFoundRecoveryReport.ps1` over 1,351 Claude transcripts, deduped by
tool_use_id, re-run 2026-10-24: 778 not-found-family events including 337 `Search` zero-result successes
found only by keyword; with `-AllErrors` 1,315 failures (1,667 events including 352 zero-result
successes). Top wire codes: Search InvalidArgument 298, ReplaceSnippet Exception 132, Search NotFound 85 /
NoMatches 84, ReplaceSnippet NotFound 73, GetMethodSource MethodNotFound 22 (0% carry a next step),
GetFileOutline FileNotFound 15, FindReferences NotFound 15 / ZeroResult 5.

## Proposal

### 1. Serialize `status` at the top level of the envelope
- A string, the `ResultStatus` name in camelCase (`notFound`, `ambiguous`, `invalidInput`, ...), omitted
  when `Success` so a normal call costs no extra tokens (same pattern as `NextOffset`,
  `SentinelCallToolResult.cs:~282`, `WhenWritingNull`).
- `ResultStatus` has no `[JsonConverter]` (`IResultStatus.cs:8`); other enums do (`ToolEnums.cs`). Cheapest
  safe shape is a new `string?` wire member (name it distinctly from the existing computed `Status`,
  e.g. a `[JsonPropertyName("status")]` property) that returns null for `Success`.
- `SharedJsonOptions` sets no naming policy (`SharedJsonOptions.cs:19-27`), yet the wire is camelCase, so
  the effective options are applied elsewhere (hypothesis: the MCP SDK / call filter). Slice 1 must assert
  the real serialized bytes, not the typed object.
- Weigh also emitting `code` for non-error outcomes. Leaning no for slice 1: `code` is an error code and
  `errorData.errorCode` already carries it on failure; `status` alone answers "empty or found". Open.

### 2. Non-error status for empty-but-valid results
- Add `ResultStatus.Empty` (candidate names: `Empty`, `NoMatches`) and change the single error table
  `ResultStatusExtensions.IsErrorStatus` (`IResultStatus.cs:48-49`) to
  `status is not (Success or AlreadyInState or Empty)`.
- The envelope derives `Status` from `IsError` and `Code` only (`SentinelCallToolResult.cs:210-223`), so
  `Empty` needs a stored signal (e.g. an `init` flag or status override set by the tool). Design open.
- First emitters: `Search` text (the zero-result return, `WorkspaceReadNavigationImpl.cs` ~945);
  `FindReferences` for callers/implementations/hits (`SymbolRelationshipImpl.cs:269/287/323`); then
  `RunTest` zero tests and the other `Search` modes after the producers are read. `AlreadyInState`
  becomes visible the same way for free.
- The prose in `statusMessage` stays (it carries the recovery ladder); `status` is the machine key.

### 3. Consolidate the stray codes
Producers verified by search; "today" is `FromErrorCode`.

| Wire code | Producer (file:line) | Status today | Proposed |
| --- | --- | --- | --- |
| FileNotFound | `WorkspaceReadNavigationImpl.cs:83,89`; `WorkspaceFileEditImpl.cs:181,187,204` | Failed | `NotFound` + kind `file` |
| MethodNotFound | `WorkspaceReadNavigationImpl.cs:133` | Failed | `NotFound` + kind `method` |
| NoOperationBlobFound | `WorkspaceFileEditImpl.cs:79` | Failed | `NotFound` + kind `operationBlob` |
| NoReversibleItems | `WorkspaceFileEditImpl.cs:97` | Failed | `TargetIneligible` |
| SyntaxRootNotFound | `WorkspaceReadNavigationImpl.cs:120,217` | Failed | leave (internal state; maybe `Exception`) |
| GitRootNotFound | `GitTools.cs:86`, `GitRemoteTools.cs:85` | Failed | `NotFound` + kind `gitRoot` |
| InvalidArguments (plural) | `GitTools.cs` 81-144 (10 sites), `GitRemoteTools.cs` 80-115 (8) | Failed | `InvalidArgument` |
| 17 `Git*` codes (`GitError`, `GitNoChanges`, `GitPathNotFound`, ...) | `GitImpl.cs` `GitErrorCodes` (~51-96) via `GitTools.cs:170` | Failed | keep as `kind`; make `FromErrorCode` call `GitErrorCodes.ToResultStatus` |
| UnknownToolset, ToolsetsUnavailable | `ToolsetControlTools.cs:51,62` | Failed | `InvalidArgument` / `FeatureDisabled` (hypothesis: sites not read) |
| NoMatches | `ToolErrorCode` constant only, line 103; no producer in source | NotFound (mapped, `IResultStatus.cs:58`) | retire; see note |
| DocumentNotFound | not a string code anywhere; only `EditOutcome`/`EngineOutcome` members | n/a (those map to NotFound, wire code `NotFound`) | none |

Notes. (a) The brief listed `DocumentNotFound` as a wire code; no string-literal producer exists and it
does not appear in the 1,351 transcripts, so it is dropped. (b) `NoMatches` is marked legacy "no tool
returns it" (`SentinelCallToolResult.cs:~98-103`) yet 84 `Search NoMatches` events are in the transcripts;
a re-run restricted to `-Since 2026-10-23` (64 transcripts) shows none, so these are from builds that
predate the successful-empty change (hypothesis; the commit history was not checked). (c) The `kind`
(subject) field is the alternative to inventing a code per noun; whether it is a new `ResultError`
member or a prefix is open. (d) Relation to `plans/plan_error_codes_follow_cause.md`: that plan makes
existing codes follow the real cause (typed throws, `DocumentEditResult.ErrorCode`); it lists "renaming
or reorganising `ToolErrorCode`" and the numeric scheme and telemetry as out of scope
(`project_tool_error_code_taxonomy_proposal`). This proposal does not touch those; it only
folds the hand-typed literals above into the existing `NotFound`/`InvalidArgument` vocabulary and exposes
`status`. A numeric scheme could later key off `status` + `kind`.

### 4. Consumers move from text to fields
- `scripts/Get-NotFoundRecoveryReport.ps1`: group on `status` (and `errorData.errorCode`), drop the
  keyword regex that produces `Code = ZeroResult`.
- `.claude/hooks/journal-log-call.ps1:79-87` today regexes `"isError":true` and `"errorCode"`; it would
  also record `status`, so the call log separates "empty" from "found".
- Model-eval / PlanStepRunner logs (not covered by the script today, per the reference doc) can read the
  same field.

## Alternatives considered, not pursued
- **Status quo plus keyword matching of `statusMessage`.** What the report script does (337 `Search`
  zero results found only this way); brittle and tool-specific.
- **Use `totalRecords == 0` as the emptiness signal.** Already on the wire, but the reference doc notes
  some tools signal emptiness only through text or an empty `findings`, and summaries leave it null.
- **Add every stray code to `ToolErrorCode`.** Keeps each spelling but grows the vocabulary and still
  cannot group by cause; it was the shape that produced the fragmentation.
- **Emit `code` on every response.** A cost on every success for no gain over `status`.
- **Make zero results an error again** (the old `NoMatches`). Rejected earlier on purpose:
  an empty search is a valid answer (`reference_not_found_recovery_ladder.md`).

## Open questions
1. Wire member name: `status` vs another word (`outcome` is already used by `OperationOutcome` and read by
   `SubAgentEvalResult`, ~94-95 per the plan; not re-verified).
2. Emit `code` for non-error outcomes, or `status` only?
3. `kind` as a new `ResultError` field, a code suffix, or reuse `StructuredDetail`?
4. `Empty` representation on the envelope (stored flag vs per-tool status override) and which tools adopt
   it after Search/FindReferences/RunTest.
5. Are clients keying on `FileNotFound`/`MethodNotFound`? Hypothesis: models branch on message text, but
   prompts, eval assertions and `ModelAgentRunner` (below) were not searched for the literals.
6. Does the SDK-level serialization apply the enum converter, so a `string?` member is unnecessary?

## Cost / risk
- **Wire change** to every response that is not plain `Success`. Readers of the body: `ModelAgentRunner.cs:346`
  (errorCode in the failure signature) and `:473` (`isError`); `ServiceRegistrationExtensionsBasic.cs:479`
  (`AddToolErrorFilter`); `SubAgentChildServerLauncher.cs:445`; `SubAgentEvalResult`. Adding a field is
  additive for `TryGetProperty` readers; renaming codes in slice 3 is not.
- **Tokens:** success unchanged (omitted); errors gain ~5-8 tokens; `Empty` results gain the same.
- **Back-compat:** changing `FileNotFound` -> `NotFound` breaks anything keyed on the old string; keep the old
  spelling in `errorData.errorCode` for one release and put the new key in `status` first.
- **Coverage:** `ResultStatusTests` (e.g. `Envelope_Failure_MapsErrorCodeToStatus_AndIsError`, line 21)
  is named as a typed-envelope mapping test; none of the tests read here serialize the envelope, so a
  green suite would not prove the wire (hypothesis; the file was not read in full). The planner must
  specify a serialization test through the real filter path; the reviewer must confirm it, per role
  boundaries (an implementer's green build says nothing about the wire). Also gate on a model-eval or
  PlanStepRunner smoke run, as plan step 7 did (`plan_shared_result_status_interface.md`).
- Makes harder: a second status vocabulary question (`Empty` vs `AlreadyInState`) must stay explained.

## Suggested slices (migration-first, new member beside the old; not a plan)
1. Wire `status` for non-Success outcomes only, new member beside the `[JsonIgnore]` ones; serialization test.
2. Extend `FromErrorCode` (Git via `GitErrorCodes.ToResultStatus`, FileNotFound/MethodNotFound/
   NoOperationBlobFound/InvalidArguments -> their statuses); producers unchanged, so only `status` improves.
3. Producers emit `NotFound` + kind; old constants `[Obsolete("Use ToolErrorCode.NotFound")]`.
4. `ResultStatus.Empty` + `IsErrorStatus`; `Search` text first, then `FindReferences`, `RunTest`.
5. Consumers: report script, `journal-log-call.ps1`, `ModelAgentRunner` signature.

## Known offenders (verified 2026-10-24; fix candidates tracked separately)
Policy: `references/reference_not_found_recovery_ladder.md`.
- `GetMethodSource` MethodNotFound is one line, no lookup, no next call:
  `WorkspaceReadNavigationImpl.cs:133` ("Method or constructor '{methodName}' not found in '{path}'.").
  0% guided in the scan (22 events).
- File-not-found is built in three copies: `WorkspaceReadNavigationImpl.cs:70-92`,
  `WorkspaceFileEditImpl.cs:173-190` (not 188), and `DocumentLookup.Describe`
  (`RoslynSentinel.Common/DocumentLookup.cs:102-124`). They search loaded documents only
  (`FindClosestPaths`, `DocumentLookup.cs:269-282`; the first copy repeats it inline at 72-79), never disk;
  `Samples/ContosoOrders` exists on disk but is not in `RoslynSentinel.slnx`, so "does not exist anywhere
  in the solution" misleads. The first two emit code `FileNotFound`, the third `NotFound`. Disk is checked
  only in the no-solution path (`WorkspaceFileEditImpl.cs:~200-204`).
- `FindReferences` snippet failures come from `SymbolNavigationEngine.FindCallersAsync` (called at
  `SymbolRelationshipImpl.cs:258,294`): messages at `SymbolNavigationEngine.cs:1517, 1543, 1635, 1669`
  list candidate locations and name calls to try, but give no ready-to-run call with arguments.
- `Member` container-not-found looks only in the same file: `SyntaxTargetResolver.BuildContainerNotFoundMessage`
  (`SyntaxTargetResolver.cs:108-128`, takes one file's `root`).
- Inline `"// Document not found."` with no path or candidates: `MemberRefactoringEngine.cs:554, 932, 1174,
  1431, 4943` (all verified) and one more at `BasicRefactoringEngine.cs:1782`.

## Related
- `proposals/proposal_shared_result_status_interface.md`, `plans/plan_shared_result_status_interface.md`
  (the work this extends; its step 7 smoke gate is the model for the gate here).
- `plans/plan_error_codes_follow_cause.md`; memory `project_tool_error_code_taxonomy_proposal`.
- `references/reference_not_found_recovery_ladder.md`; `plans/closed/plan_search_text_scan_all_projects_and_recovery_hints.md`.
- `ideas/server_telemetry_idea.md` (would consume `status`); `scripts/Get-NotFoundRecoveryReport.ps1`.
