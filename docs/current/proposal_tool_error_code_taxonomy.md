# Tool return/error code taxonomy for locating and counting tool responses

## Motivation

Andrew raised (2026-09-11/12): we need a way to easily locate and count tool responses/errors
across the server — right now that data only exists reconstructible after the fact by parsing
archived `agent.log`/`transcript.json` files (see `project_server_telemetry_idea` memory / the
"Feature idea: server-side telemetry/metrics for tool call counts and error rates" entry in
`docs/current/TODO.md`, lines ~274-310). That TODO entry already scopes the *counting* half: a
confirmed hook point in the MCP call-tool filter chain
(`RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`'s
`AddRoslynSentinelToolsBasic`, ~line 167-406), registered after the domain-failure→protocol-error
sync filter.

This proposal is about the other half: the *taxonomy* — what the code values themselves look like
— which the TODO entry doesn't cover.

## Current state

- `ToolErrorCode` (`RoslynSentinel.Common/ToolResult.cs:26`) is a flat `static class` of ~11
  string constants (`SolutionNotLoaded`, `InvalidArgument`, `NotFound`, `NoMatches`,
  `DiffApplyFailed`, `Exception`, etc.), consumed as `ResultError.ErrorCode`.
- Usage is inconsistent: many throw sites fall through to the generic `Exception` code
  (`ToolException.cs:179`, the catch-all mapper) rather than a specific one. This undermines any
  counting effort until tightened — "how many X failures" is meaningless if most X failures are
  bucketed under the generic catch-all.
- Two other axes already exist independently and are NOT part of `ToolErrorCode`: `FindingSeverity`
  (Info/Warning/...) on `Finding`, and `DirectiveKind` (Proceed/ReviewRequired) on `ToolResult<T>`.
- No axis for "which tool" or "which operation" exists on the error code itself — that identity
  currently only lives in the MCP request context (`context.Params?.Name`) and each tool's own
  operation enum parameter (e.g. `ProposedChangeAction`, `WriteFileOperation`).

## Option raised: packed numeric code

Andrew's sketch: 1-digit severity + 3-digit tool + 2-digit operation + 2-digit status (8 digits),
e.g. `41236587` = error + ModifyEnum + add + symbol_not_found.

**Concern with this shape:** `ResultError.ErrorCode` is read by the model, not a human dashboard —
that's the whole reason the existing codes are human-readable strings. An opaque 8-digit int means
nothing to the model without holding a decoder table in context, where
`"ModifyEnum.Add.SymbolNotFound"` means something on sight and greps/counts identically. There's
also a registry-churn cost: fixed-width tool/operation ordinals need a hand-maintained ID table
that shifts as tools/operations are added or reordered — and tool identity is already available
for free at the filter chokepoint (`context.Params?.Name`), so re-encoding it inside the error
code buys nothing for counting purposes.

## Recommended direction (not yet built)

1. **Audit first.** Find every throw site / error-return path that currently collapses to
   `ToolErrorCode.Exception` (the generic catch-all) instead of a specific code. This is the bulk
   of the work — touches most of the ~15 tool files listed by `grep -rl ToolErrorCode`.
2. Keep `ErrorCode` as a short human-readable string (current shape), tightened to one code per
   real failure mode rather than generic fallbacks. Leave `FindingSeverity`/`DirectiveKind` as
   separate axes — don't fold severity into the error code.
3. Build the metrics filter (per the existing TODO entry) keyed on the tuple
   `(toolName, operation, errorCode)` — three strings pulled straight from the request/response at
   the chokepoint, no numeric ID space to invent or maintain.
4. If a compact form is still wanted for some external consumer (e.g. a fixed-width log column),
   derive it mechanically from the string tuple at report time only — never hand-maintain a numeric
   registry as the source of truth.

## Grouping shape: why not per-tool code types

Considered: per-tool types extending `ToolErrorCode`, or an `IToolErrorCode` interface implemented
by per-tool error types, to avoid one class holding every code.

Not mechanically possible against the current shape — `ToolErrorCode` is a `static class` of
`const string` (uninheritable, no virtual/interface members), and `ResultError.ErrorCode`
(`ToolResult.cs:183-184`) is a bare `string`, so there is no declared type at the consumption point
for an interface to constrain. The values are also serialized across MCP as strings and asserted as
string literals in tests (282 `ToolErrorCode.` occurrences across 28 files), so any scheme changing
the wire value breaks assertions, and any scheme preserving it makes the hierarchy decorative.

Substantively: codes are the axis to *aggregate across* tools ("how many `NotFound` failures this
run, across all tools"), which per-tool code types fragment. Tool identity is already free at the
filter chokepoint, so `(toolName, errorCode)` gives per-tool grouping without duplicating the code
space. Recommended instead: keep codes shared and flat, and split the **guidance registry** per tool
— see [`proposal_first_person_turn_injection.md`](proposal_first_person_turn_injection.md).

## Consumer of this work

[`proposal_first_person_turn_injection.md`](proposal_first_person_turn_injection.md) depends on this
audit: it injects per-code remediation guidance into the agentic loop phrased as the model's own
first-person statement, which is only honest if a code means exactly one real failure mode. The
generic-`Exception` collapse at `ToolException.cs:167-180` is precisely the bucket that would force
guessing, so it must carry no guidance string until tightened.

## Open questions for the next session

- Exposure mechanism for counts: new read-only tool vs. extending `GetWorkspaceHealth` /
  `GetComprehensiveHealthReport` (same open question as the parked telemetry TODO).
- Do counts need to persist across server restarts, or is in-process-lifetime enough for v1?
- Scope of the audit: fix every generic-`Exception` fallback in one pass, or land it
  tool-by-tool as each is touched for other reasons (mirrors the parked
  `project_error_messages_centralize_parked` decision to not centralize speculatively)?
