# Batch ReplaceSnippet: multiple edits per call

## Motivation

`ReplaceSnippet` (`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs:567-751`) takes exactly one
`oldContent`/`newContent` pair per call. A multi-edit change to a file — or a change spanning several
files — needs N separate calls, each going through the full write chokepoint
(`PersistentWorkspaceManager.ApplyProposedChangesAsync`, `RoslynSentinel.Common/
PersistentWorkspaceManager.cs:1227`) independently: N delta-compiles of every referencing project, N
validation passes, N writes, where one would do.

Worse than the redundant compiles is a real sequencing hazard: call 2 in a sequence of edits to the
same file must be derived from call 1's *output* text, because the model has no way to know what the
file looks like after call 1 lands until it reads that result back. Today's only escape hatch is
`validateOnApply=false`, which skips the compile-error gate on a single call — it does not reduce
call count and does nothing about the sequencing dependency, since each call is still anchored
against whatever the file currently is on disk.

## Proposed shape

Mirror `ApplyDiff`'s batching *pattern* — one `Dictionary<FilePathWrapper,string>` resolved and
applied in a single `ApplyProposedChangesAsync` call
(`RoslynSentinel.Server.Basic/SentinelWholeFileWriteTools.cs:334-336`, changesetFormat=files) — but
at edit granularity instead of whole-file-content granularity. This does **not** adopt diff-hunk/`@@`
syntax; that stays exclusively `ApplyUnifiedDiff`'s gated territory per
`docs/obsolete/design_applyunifieddiff_replace_snippet_v1.md`'s Resolved Decision 1, and this
proposal does not reopen that.

New optional array parameter on the existing `ReplaceSnippet` tool — not a new tool:

```csharp
public async Task<ToolResult<object>> ReplaceSnippet(
    ToolCallReason reason,
    ProposedChangeAction action,
    FilePathWrapper? filepath = null,          // required only when edits is omitted — existing single-edit path unchanged
    string? oldContent = null,
    string? newContent = null,
    string? lineBefore = null,
    string? lineAfter = null,
    SnippetEdit[]? edits = null,                // NEW — batch path
    bool validateOnApply = true,
    bool returnDiff = false,
    CancellationToken cancellationToken = default)

public record SnippetEdit(
    FilePathWrapper Filepath,
    string OldContent,
    string NewContent,
    string? LineBefore = null,
    string? LineAfter = null);
```

`edits` and the singular `oldContent`/`newContent`/`filepath` params are mutually exclusive — reject
if both or neither are supplied. This is the same either/or validation pattern `ApplyDiff` already
uses for its own two mutually exclusive parameter sets: `changesetFormat=files` requires `changes`
(`filepath`/`unifiedDiff` unused), `changesetFormat=diff` requires `filepath` and `unifiedDiff`
(`changes` unused), and neither is universally required beyond `changesetFormat`/`action`
(`RoslynSentinel.Server.Basic/SentinelWholeFileWriteTools.cs:288-291`, `CONDITIONAL-PARAM-REVIEW-
REQUIRED` comment, enforced at `:318-327` and `:444,453,462`). `ReplaceSnippet`'s batch path follows
the same discipline: a `CONDITIONAL-PARAM-REVIEW-REQUIRED` comment on the parameter block, and an
explicit `InvalidArgument` rejection — naming which of "both supplied" or "neither supplied" — before
either path is attempted.

## Execution model

1. Group `edits` by file.
2. Load each file's document text once per file — a single `document.GetTextAsync()` read, not one
   per edit, mirroring the existing single-edit path's one read at
   `SentinelWorkspaceTools.cs:672`.
3. Resolve every edit's `oldContent` match against that file's *original* text via
   `ContextHelper.FindExactSnippetPosition` — the same method the current single-edit path already
   calls at `SentinelWorkspaceTools.cs:678` — never against a prior edit's output within the same
   batch. This is what removes the sequencing hazard described above: every edit in a batch is
   anchored against one fixed snapshot, not a chain of intermediate results the model never sees.
4. Apply all matches for a file as non-overlapping splices in reverse offset order (highest `Start`
   first), so an earlier-in-file splice's shift never invalidates a later-in-file match's offset.
   `DiffEngine.ApplyDiffCore` (`RoslynSentinel.Common/DiffEngine.cs:89-`) does not do this — it
   applies hunks in forward document order while tracking a running `offset` correction
   (`DiffEngine.cs:109`, `int offset = 0; // Track how much the file has grown/shrunk`) — so
   reverse-order application is a new technique for this codebase, not a reused one, chosen because
   all of a batch's match positions are known up front (step 3) and applying highest-offset-first
   needs no running correction term at all.
5. If two edits in the same file have overlapping matched spans, reject the entire batch before any
   write — `ToolErrorCode.InvalidArgument`, naming both edit indices and their spans. Silent overlap
   resolution (last-writer-wins) must not happen: this is the same corruption class as the closed
   `ReplaceSnippet` silent-splice-corruption finding on adjacent lines (memory:
   `project_replacesnippet_silent_splice_corruption_adjacent_lines`, referenced in the existing
   `FindExactSnippetPosition` comment at `SentinelWorkspaceTools.cs:673-677`), where using the wrong
   match length corrupted an adjacent line without any error at all.
6. Fold every file's final content into one `Dictionary<FilePathWrapper,string>` and make exactly one
   `ApplyProposedChangesAsync` call (`PersistentWorkspaceManager.cs:1227`) for the whole batch — one
   delta-compile pass, one atomic write, one `UndoChangeId` covering the whole batch.

## Failure reporting

Per-edit failures (no match, ambiguous match, overlap) must name the specific array index and file,
not collapse into one vague aggregate message — e.g. `"edits[2] (Program.cs): no match for
oldContent"`. This follows the existing precedent already established in `ReplaceSnippet`'s own
size-limit error path: the comment above `MaxOldContentLines`
(`SentinelWorkspaceTools.cs:556-562`) records that run `20260910-013550-398` livelocked because a
collapsed, shared error message didn't tell the model which of four bounds it had actually hit, and
the fix — reporting each bound separately with its own actual-value-against-limit line
(`SentinelWorkspaceTools.cs:620-622`, `exceeded.Add($"oldContent is {oldContentLineCount} lines
(limit {MaxOldContentLines})")`) — was added specifically because the vague aggregate cost the model
its last 24 turns reshaping the same edit. Batch validation failures get the same per-item treatment,
not a regression back to a single collapsed message.

## Size caps

Apply the existing per-edit constants — `MaxOldContentLines` (60), `MaxOldContentChars` (2000),
`MaxNewContentLines` (60), `MaxNewContentChars` (2000), all defined at
`SentinelWorkspaceTools.cs:563-566` — to each array element individually; a batch element is held to
exactly the same bound a standalone call would be.

Add a new cap on `edits.Length` (proposed: 20). This keeps `ReplaceSnippet` a many-*small*-edits
tool, not a `WriteFile` substitute — the original (now-obsolete) design doc was deliberate about this
boundary ("this design assumes there is no legitimate case for using this tool to submit an edit
larger than roughly 10-20 lines / 200 characters — anything bigger already has a better-fit tool,"
`docs/obsolete/design_applyunifieddiff_replace_snippet_v1.md:33-37`), and batching edit *count* must
not become a backdoor around the per-edit size discipline that boundary protects.

## Explicitly out of scope

- No diff-hunk/`@@` syntax — stays on `ApplyUnifiedDiff`, gated, per the obsolete doc's Resolved
  Decision 1.
- No cross-file atomicity guarantees beyond what `ApplyProposedChangesAsync` already provides for its
  `Dictionary<FilePathWrapper,string>` input. A batch spanning multiple files gets exactly the
  guarantees today's `ApplyDiff` `changesetFormat=files` batch already gets from that same call —
  nothing new is being promised here.

## Related work: other single-target mutating tools worth batching

A survey of every `[McpServerTool]` in `RoslynSentinel.Server.Basic/*.cs` and
`RoslynSentinel.Server.Advanced/*.cs` that writes code via `ApplyProposedChangesAsync` (directly, or
through `ValidateAndApplyHelper.ValidateAndApplyAsync`) found several other tools with the same shape
that motivates this proposal: small, structurally identical, independent edits across sibling
targets, where today's cost is N redundant delta-compiles with no sequencing dependency between the
calls. Precedent for "already batched" already exists in this codebase — `ApplyDiff` (multi-file
dict), `BulkComment`, `MoveAllTypesToFiles`, the Asyncify family (`BridgeAsyncMethods`,
`UpliftCallers`, `PropagateCancellationToken`, etc.), `MoveMember` (multi-member), and `ExtractMembers`
(superclass mode) all take an array or dictionary today. The following are single-target only and are
the strongest candidates to batch next, ranked by how cleanly each reduces to a uniform
`{target, value}` tuple and how often the repeated-call pattern shows up in real mechanical
refactoring:

1. **`ModifyModifier`** — adding `async`/`static`/`virtual` to N sibling methods after a
   signature-convention sweep is a routine, high-frequency chained pattern; the exact shape that
   motivated this proposal, just for a modifier keyword instead of a snippet pair.
2. **`ChangeAccessibility`** — "tighten these 8 members to internal/private" is a common post-review
   cleanup; each call today pays a full delta-compile + validate + write for a one-keyword change.
3. **`ConstructorParameter`** — adding the same new DI dependency (e.g. a new `ILogger` or
   `IOptions<T>`) across several constructors when introducing a cross-cutting service is a frequent,
   mechanically identical operation.
4. **`ModifyAttribute`** — sweeping `[Obsolete]`, `[Authorize]`, or similar across several members is
   common and currently pays the full chokepoint cost per member.

`Member` and `UsingDirective` were also considered plausible but rank below these four: their
per-call payload (full member source, or a single namespace) is more likely to legitimately vary in
shape per target, making a shared batch schema less clean than the `{target, keyword}` shape the four
above all share.

Each is a separate proposal, not folded into this one's implementation — the execution-model risk
(anchoring, overlap detection, reverse-offset application) is specific to `ReplaceSnippet`'s
text-splice mechanism and doesn't transfer directly to tools that mutate via syntax-tree operations
rather than text spans. This section records the candidates so they aren't re-discovered from
scratch; each should get its own design pass over the target tool's actual current implementation
before being built.

## Status

Drafted, not yet implemented. Author: Claude (Sonnet 5), for Andrew Almond
(andrew.almond@clearbridge.ca).

This proposal supersedes Resolved Decision 2 in
`docs/obsolete/design_applyunifieddiff_replace_snippet_v1.md` ("Single edit location per call —
confirmed... revisit only if eval data shows models frequently want to batch trivial same-file edits
and repeated calls prove to be a real cost"). That trigger condition is what this proposal is
responding to — Andrew Almond requested this directly, on exactly those grounds: repeated
single-edit calls against the same file, and the per-call recompile/round-trip cost of forcing each
subsequent edit to wait on the previous call's output before it can be composed.
