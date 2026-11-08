# `Member` tool: `containerName` conditionally-required-but-schema-optional gap — FIXED 2026-09-06

Fixed by prepending a `"REQUIRED PARAMS BY OPERATION — ..."` line to `Member`'s `[Description]`
(option 3 in this doc). Option 2 (schema-level conditional-required) was ruled out on investigation:
`ConsumesAttribute`/`ExternalInputRequiredAttribute` (`RoslynSentinel.Common`) only support a flat
`bool required`, no "required when operation == X" construct, and `containerName` is genuinely
optional for remove/replace, so it can't be made unconditionally schema-required either.

The same audit was then run across the rest of `SentinelRefactoringTools.cs` and found — and
fixed — the identical gap in `UsingDirective`, `SummaryComment`, `ConstructorParameter`, and
`MethodSignature`. A broader grep found ~20 more instances of the same shape in `GitTools.cs`,
`SentinelWorkspaceTools.cs` (including `ApplyDiff`), `SentinelAdvancedRefactoringTools.cs`,
`SentinelCommentingTools.cs`, and `SentinelScanTools.cs` — tracked as a follow-up, not yet fixed:
see [issue_conditional_required_param_audit_followup.md](./issue_conditional_required_param_audit_followup.md).

## Symptom

2026-09-06 smoke test of `qwen/qwen3.6-35b-a3b` against `Model_AppliesThreeChainedRefactors`, run
independently on both config 112 and 113 (different LM Studio hosts, same fixture): **both runs**
called `Member` with `operation: add` and no `containerName`, got rejected
(`ToolErrorCode.InvalidArgument`, "Member: containerName is required for operation 'add'."), then
self-corrected on the very next call by supplying it. Both runs still passed overall — this is not
currently blocking anything — but the same schema-usage mistake appearing identically on two
independent runs of a strong model, rather than as an isolated fluke, suggests a discoverability
gap worth closing at the schema/description layer rather than dismissing as model error.

Found during a smoke test of `qwen/qwen3.6-35b-a3b`, run independently on two different LM Studio
hosts against the same fixture, described above.

## Root cause (confirmed by reading source)

`RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs`, the `Member` tool
([Member], lines 270-308):

- The parameter is declared:
  ```csharp
  [Consumes(DataTag.SymbolName, required: false)] string? containerName = null,
  ```
  i.e. **schema-optional** — nothing in the MCP tool schema itself signals that `containerName` is
  required for some operations. It's tagged generically as `DataTag.SymbolName`, the same tag used
  for `memberName`, `typedName`, etc. — no distinguishing signal that this one is conditionally
  required.
- The *actual* requirement is enforced only at runtime, deep in the method body:
  ```csharp
  // line 398-399
  if (string.IsNullOrEmpty(containerName))
      return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "Member: containerName is required for operation 'add'.") };
  ```
  (Same pattern at lines 315-316 for `operation: view`.)
- The one place this is documented ahead of time is prose, buried in the middle of a long
  multi-operation `[Description]` block (lines 272-282) that covers add/remove/replace/view in one
  wall of text: `"OPERATION add: containerName required. Two modes — pass newMemberSource..."`.
  It's stated, but it's the second clause of a five-operation description block, with no structural
  emphasis (no leading bullet, no "REQUIRED:" prefix, no schema-level enforcement) — easy for a
  model skimming tool descriptions under time/token pressure to miss, especially since `remove`/
  `replace` (described immediately after, at length) explicitly do NOT need `containerName` at all
  ("remove/replace resolve memberName directly ... regardless of container" — line 281), which
  arguably reinforces a "containerName is usually optional" impression right next to the one case
  where it isn't.

## Why this is worth fixing

This is the same shape of gap as the `WriteFile` raw-JSON rejection fix (commit 3a4c521) and the
`docCommentId` description gap tracked in `TODO.md`: a real requirement exists, the tool enforces it
correctly and recovers gracefully when violated, but the requirement isn't surfaced where a model
is most likely to look (the schema's `required` flag) — only in prose it has to read carefully.
Both observed occurrences recovered in exactly one extra turn with no thrashing, so this is a
low-severity, high-frequency papercut, not a correctness bug — but per a broader pattern seen across
model-eval batches, these cheap discoverability fixes have historically had an outsized effect on
reducing wasted turns/tool errors relative to their implementation cost.

## Options to investigate (not yet decided — for the follow-up session)

1. **Split into operation-specific overload-like tools or discriminated params** — likely too
   large a change given `Member` is intentionally a single consolidated tool (add/remove/replace/
   view) per its own design; probably not worth it just for this.
2. **Make the schema itself conditionally enforce it.** MCP tool schemas (JSON Schema under the
   hood) support `allOf`/`if`/`then` conditional-required constructs. Check whether the
   `[Consumes]`/`[ExternalInputRequired]` attribute framework in this codebase (see
   `RoslynSentinel.Common` or wherever `DataTag`/`Consumes` are defined) already has a mechanism
   for "required when operation == X" — if so, this is likely the cleanest fix.
3. **Cheapest fix, matches the `WriteFile`/`docCommentId` precedent:** leave the schema as-is, but
   restructure the `[Description]` text so the per-operation required/optional parameters are
   visually distinct (e.g. one line per operation, `containerName` called out as **REQUIRED** in
   bold/caps specifically for add/view, immediately adjacent to `operation`'s own description or at
   the very start of the description block rather than mid-paragraph) — same "reduce reliance on
   the model reading dense prose carefully" fix already applied elsewhere.
4. Check whether `MemberAction` (the `operation` enum) already carries per-value XML doc comments
   that could be extended to state their own required-params inline, which might surface more
   reliably to a model than a shared top-level description block.

## Where to look when starting the follow-up session

- `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs:270-308` (the tool itself, description
  and signature)
- `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs:315-316`, `:398-399` (the two runtime
  required-ness checks — `view` and `add`)
- Wherever `DataTag`, `Consumes`, `ExternalInputRequired` attributes are defined (likely
  `RoslynSentinel.Common/`) — to check for any existing conditional-required mechanism before
  inventing a new one
- `MemberAction` enum definition — check for existing per-value doc comments
- Consider auditing other multi-operation tools in the same file (`SentinelRefactoringTools.cs`)
  for the same "one param required only for some operation values" shape, since `Member` is
  unlikely to be the only one — a single fix pattern could apply to several tools in one pass.
