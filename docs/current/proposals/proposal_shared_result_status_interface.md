# A shared status interface for status-carrying result types, with IsError as the canonical flag

**Status:** PROPOSED 2026-10-02, REVISED 2026-10-04 after discussion with the user. Phase 1 implemented 2026-10-04 (uncommitted); phases 2-5 not started.

## Motivation
`finding_result_status_vocabularies_fragmented.md` shows 82 status-carrying types with independent
vocabularies and an MCP `isError` flag derived by re-parsing serialized JSON in four places. Concrete
consequences: the same `SolutionNotLoaded` condition gives `IsError = false` at
`ServiceRegistrationExtensionsBasic.cs:282` and `true` at `:335`; `ErrorCodeFor(EditOutcome)`
(`RefactoringToolHelpers.cs:33-38`) sends 10 of 13 outcomes to `Exception`; bodies keyed `"success"` are
invisible to every `"isSuccess"` parser.

The MCP protocol's own flag is `CallToolResult.isError` (optional boolean, default false). There is no
`isSuccess` in the spec. The repo's `isSuccess` is an internal invention that must be translated to the
protocol flag at every boundary. Goal: one canonical polarity, `IsError`, matching the protocol, so a
status is determined once and passed up unchanged instead of re-parsed or re-derived per layer.

## Proposal
(D) = decided by the user, (R) = my recommendation.

1. (D) `IResultStatus` in `RoslynSentinel.Common`, with no `IsSuccess` member anywhere:
   - `bool IsError { get; }`
   - `ResultStatus Status { get; }`
   - `string? Code { get; }` a `ToolErrorCode` string, null on success
   - `string? Message { get; }`
2. (D) Applies to status-carrying types only. Payload-only types and anonymous/untyped returns
   (`finding_anonymous_and_untyped_return_shapes.md`) are out of scope. Non-outcome enums
   (`AgentStopReason`, `TestOutcome`, `FindingSeverity`, ...) stay as they are.
3. (D) Intended use: a status is readable at any stage without re-parsing, and passes upward by plain
   assignment or type test:
   - wrapping: `w.IsError = s.IsError;` (and `Status`/`Code`/`Message` likewise when they carry over)
   - inspecting: `if (w.Data is IResultStatus { IsError: true }) ...`, or `T : IResultStatus` on a wrapper
     so no cast is needed.
   Concrete types keep their rich members (`Outcome`, `ErrorData`, `FailedFiles`, ...) for nuanced use.
4. (R) Canonical `ResultStatus`, the smallest set covering the concept matrix: `Success`,
   `AlreadyInState`, `NotFound`, `Ambiguous`, `InvalidInput`, `NotApplicable`, `FeatureDisabled`,
   `Cancelled`, `TimedOut`, `Failed`. An extension `ResultStatus.IsErrorStatus()` is the single place that
   says which are errors (`Success` and `AlreadyInState` are not). Names reuse existing spellings where one
   exists (`ItemOutcome.AlreadySatisfied`, `ToolErrorCode.NotFound`).
5. (R) Stored vs computed `IsError`: a type that has its own outcome/status member computes it
   (`IsError => Status.IsErrorStatus()`), so it cannot disagree with its rich data. Wrappers and types with
   no status of their own store it, so `w.IsError = s.IsError` works. A test per adopting type asserts
   `IsError` agrees with `Status`.
6. (R) The call filter reads the flag in one place: `result is IResultStatus { IsError: true }` sets
   `CallToolResult.IsError`. Agent-side consumers (`ModelAgentRunner`, `SubAgentChildServerLauncher`) read
   the MCP client's `IsError` and stop parsing JSON text. `SolutionNotLoaded` gets one polarity, `true`,
   per MCP convention for tool-execution failures, unless `:282` is deliberate (open question 3).
7. (R) The 26 boundary conversion sites go through one `Status -> ToolErrorCode` table instead of
   hand-written switches.

## Phases
Each phase is its own compile-green commit. Gate: build after each phase; if the blast radius is too large,
do not abandon the member. Remove it from the interface (comment out, with a note pointing here) and
re-enable it in a later phase. Fallback order, hardest first: `Message`, `Code`, `Status`/`ResultStatus`,
never `IsError`. An alternative fallback is default interface members (`Status` derived from `IsError`,
`Code`/`Message` null); check target frameworks and `LangVersion` first.

1. Define `IResultStatus`, `ResultStatus`, `IsErrorStatus()` in `Common`. Implement on
   `SentinelCallToolResult<TSuccess, TError>`. `SolutionNotLoaded` polarity fixed. DONE 2026-10-04, uncommitted.
   The envelope now STORES and SERIALIZES `isError` (default true, fail closed, same as the old `IsSuccess`
   default false); `Status`/`Code`/`Message` are computed and `[JsonIgnore]`d. `IsSuccess` survives only as a
   non-serialized source-compat alias (`get => !IsError`, `init` sets `IsError = !value`) so the ~1264 existing
   sites keep compiling; it is never on the wire and gets removed in phase 4/5. The call filters read the body's
   top-level `isError` and copy it onto `CallToolResult.IsError` (no inversion), the offload-pointer envelope
   carries `isError`, and the agent-side consumers and test fixtures read `isError`. The filter does NOT get the
   typed value (it only ever sees the serialized `CallToolResult`), and does not need to: the body carries the
   canonical flag.
2. `EngineResultBase` / `DocumentEditResult`: one `Status` mapping from `EditOutcome` replaces
   `ErrorCodeFor(EditOutcome)`. Coordinate with `plan_error_codes_follow_cause.md`.
3. `EngineResultWrapper<T>` and the bespoke `bool Success` records (`MsAugmentResult`,
   `ExtractMethodResult`, `OutParamConversionResult`, `ToolUpdateResult`, `GitResult`, `DocWriteResult`).
4. Remaining status-carrying types, by consumer layer: engines, tools, then agent-side consumers.
5. Agent-side consumers switch to the client's `IsError` (`ModelAgentRunner.cs:465`,
   `SubAgentChildServerLauncher.cs:419`). Then drop `isSuccess` from the serialized envelope (open
   question 2).

## Alternatives considered, not pursued
- **Migrate every layer to `EngineResultWrapper<T>` first** (session 64071aa6 direction). Lost as a first
  step: a rewrite of every engine return that does not by itself fix the wire derivation and would bake in
  the NoChange decision. Compatible later.
- **A common base class instead of an interface.** Lost: records already inherit from different bases and
  bespoke records are sealed or positional.
- **`IsSuccess` as the primary member with `IsError` derived** (earlier revision of this doc). Lost: keeps
  the repo on the opposite polarity from the protocol, so every boundary still translates.
- **Collapse all enums into one.** Lost: non-outcome enums are not results of one operation.

## Open questions
1. NoChange / AlreadyInState: option a (new `AlreadyInState`), b (keep `NoChange`, relabel the 4 class-4
   producers; my recommendation), or c (Success + `Changed = false`). Decides whether `AlreadyInState` is in
   `ResultStatus`. The 4 class-4 producers must be relabelled before or during phase 2.
2. RESOLVED 2026-10-04: `isError` is serialized in the envelope body and `isSuccess` is gone from the wire
   (done in phase 1; full suite 3065 passed, 0 failed, 109 skipped). Remaining: bodies keyed `"success"` that
   are not `SentinelCallToolResult` (`EngineResultWrapper`, `DocWriteResult`, ...) still use their own spelling
   until their phase.
3. RESOLVED 2026-10-04 (user): `SolutionNotLoaded` at `:282` returns `isError: true`. Done in phase 1.
4. DECIDED 2026-10-04 (user): a failed build returns `isError: true`, for consistency. NOT YET IMPLEMENTED
   (`WorkspaceBuildTestImpl.cs:139/:170-175`); `SubAgentEvalResult` depends on today's behavior (agent claim,
   unverified), so check it when making the change.
5. Is `Cancelled`/`TimedOut` wanted on the tool surface? Today both fall to `Exception`.
6. The agent's matrix left 51 cells UNREAD; re-read the consumer columns before phase 4.
7. RESOLVED 2026-10-04: the filter does not need the typed value; it reads the body's `isError` once and sets
   the protocol flag (option c). A typed hook is unnecessary.

## Cost / risk
- Touches `Common` (new types), `Server.Basic` (filter), `Engines.Basic` (mapping), and each adopting type.
  Phase 1 alone touches few files; full adoption is about 82 types.
- Risk: full four-member interface on day one may be a large blast radius; mitigated by the fallback above.
- Risk: dropping `isSuccess` from the envelope changes every tool's output (phase 5, gated by open question 2).
- Two vocabularies coexist until phase 4 completes, the cost of staying additive.

## Related
- `findings/finding_result_status_vocabularies_fragmented.md` (evidence).
- `findings/finding_anonymous_and_untyped_return_shapes.md` (out of scope here).
- `findings/finding_nochange_producer_classification.md` (the NoChange classes and producer counts).
- `plans/plan_error_codes_follow_cause.md` (steps 3-6 stay paused until the result-type decision).
- Memory: [[project_tool_error_code_taxonomy_proposal]], [[project_server_telemetry_idea]].
