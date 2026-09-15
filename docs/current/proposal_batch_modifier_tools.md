# Batch ModifyModifier / ModifyAttribute / ModifyBaseType: multiple edits per call

Sequel to `docs/current/proposal_batch_replacesnippet.md` ("Related work" section). That proposal
surveyed every single-target mutating tool from tool *shape* alone and ranked four candidates:
`ModifyModifier`, `ChangeAccessibility`, `ConstructorParameter`, `ModifyAttribute`. This proposal is
the follow-up design pass over those candidates' **actual current implementations**, as that section
said each one should get before being built.

## Motivation

Same problem `ReplaceSnippet`'s batch solves, for a different family of edits: sweeping a single
keyword/attribute/base-type change across sibling members pays a full delta-compile + validate +
write cycle per member today. Concretely:

- Add `async` to 10 methods after a signature-convention sweep — 10 `ModifyModifier` calls.
- Add `[Obsolete]` to 6 members flagged in a deprecation pass — 6 `ModifyAttribute` calls.
- Add a new interface to 4 partial classes implementing a cross-cutting concern — 4 `ModifyBaseType`
  calls.

Each of these is exactly the "small, structurally identical, independent edits across sibling
targets" shape the ReplaceSnippet proposal used to justify batching — the same motivation, applied to
tools that mutate via a different mechanism.

## Why the ReplaceSnippet batch technique doesn't transfer

An Explore pass read the actual current implementations of all four originally-ranked candidates.
Finding: `ModifyModifier`, `ChangeAccessibility`, `ConstructorParameter`, and `ModifyAttribute` all
mutate via **syntax-tree `SyntaxNode.ReplaceNode`**, not text-offset splicing. `ReplaceSnippet`'s
batch design (`ContextHelper.FindExactSnippetPosition` anchor, reverse-offset splice, span-overlap
rejection — proposal_batch_replacesnippet.md's Execution model) is built entirely around text spans
and offsets within one file's raw string. There is no span to anchor, shift, or overlap-check here —
these tools never see the file as text; they resolve a `SyntaxNode`, replace it, and format.

Concretely (`RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs`), `ModifyModifier` (1296-1352),
`ModifyAttribute` (1214-1294), and `ModifyBaseType` (1353-1409) all follow the identical shape:
resolve a target via the engine, get back a `DocumentEditResult`, fold its `UpdatedText` into a
one-entry `Dictionary<FilePathWrapper,string>`, and call the shared `ValidateAndApplyAsync`
(`RoslynSentinel.Common/ValidateAndApplyHelper.cs:18`, which wraps the same
`ApplyProposedChangesAsync` chokepoint `ReplaceSnippet` uses). The engine layer
(`RoslynSentinel.Basic/RefactoringEngine.cs`) — e.g. `AddModifierAsync` (3188-3264), `AddAttributeAsync`
(2687-2790), `AddBaseTypeAsync` (2792- ) — each independently does its own `GetCurrentSolutionAsync`
→ find `Document` → `GetSyntaxRootAsync` → resolve one target node via `ResolveMemberByNameOrSnippet`
or `ResolveTypeByNameOrSnippet` → exactly one `FormattingHelper.ReplaceNodeFormattedAsync` call →
return the whole document's text as a string.

`ReplaceNodeFormattedAsync` (`RoslynSentinel.Common/FormattingHelper.cs:53`) takes a `root` and a
single `oldNode`/`newNode` pair, does `root.ReplaceNode(oldNode, annotatedNewNode)`, formats via
`Formatter.FormatAsync` against a tracking annotation so only the replaced node reformats (not the
whole file — deliberate, per the comment at `FormattingHelper.cs:36-52`; a past bug reformatted
unrelated siblings, see `docs/current/CLOSED.md` and `project_member_replace_drops_leading_blank_
line_and_verify_gap`), and returns the formatted document's full text. It is a per-call, per-node
operation. It does not chain multiple replacements against an evolving root, and none of the three
engine methods above call it more than once.

**Critical implication for batching.** If two edits in a batch target the same file, calling the
existing engine methods twice independently and taking the second call's `UpdatedText` is wrong: both
calls resolve their target against the same original, pre-batch `GetCurrentSolutionAsync` root (each
method fetches the solution and root itself — no in-batch state is shared between them), so the
second call's output would silently discard the first edit rather than compound it. Multi-edit,
same-file batching here needs a shared root that both edits mutate in sequence, which none of the
current engine methods do — this is new execution logic, not a reuse of `ReplaceNodeFormattedAsync`
as-is.

## Scope: three tools in, two candidates explicitly deferred

This proposal covers `ModifyModifier`, `ModifyAttribute`, and `ModifyBaseType` only.

The Explore pass also flagged `ModifyBaseType` (`SentinelRefactoringTools.cs:1353-1409`) as
mechanically identical to `ModifyModifier`/`ModifyAttribute` — same parameter shape, same
`AddRemoveAction`, same single-`ReplaceNode` engine call, same low complexity — even though it wasn't
in proposal_batch_replacesnippet.md's original four-candidate ranking (that ranking was done from
tool *descriptions*, not implementations, and missed it). It's folded in here because it shares
exactly the same batch mechanism as the other two; there is no separate design cost to including it.

`ChangeAccessibility` and `ConstructorParameter` are explicitly **out of scope** — see "Explicitly out
of scope" below for why.

## Proposed shape

Mirror how `ReplaceSnippet`'s batch was added: **one new optional array parameter per tool**, not a
new cross-tool multiplexer tool. Each of the three keeps its own existing singular-edit parameters
untouched, and gains an `edits` array of its own edit-type.

This was considered against the alternative of a single unified tool taking a heterogeneous
`{tool, target, ...}` operation list across all three, and rejected: each tool's per-edit payload
genuinely differs in shape — `ModifyModifier` needs `Modifier` + `AddRemoveAction`; `ModifyAttribute`
needs `ExistingAttribute` + `AttributeModifyAction` + a conditionally-required `NewAttribute` (already
flagged `CONDITIONAL-PARAM-REVIEW-REQUIRED` at `SentinelRefactoringTools.cs:1225`); `ModifyBaseType`
needs `BaseTypeName` + `AddRemoveAction`. A multiplexer payload would just reinvent three tools' worth
of conditional-param validation inside one shared schema for no benefit — `ReplaceSnippet` didn't
invent a cross-tool batch primitive either, it added one array param to itself.

New edit types in `RoslynSentinel.Common/BatchTypes.cs`, following the `SnippetEdit`/
`HandlerExtractTarget` convention already established there — plain mutable class, `FilePath` typed
`string` (not `FilePathWrapper`), per the same schema-emission constraint documented on
`HandlerExtractTarget.FilePath` (`BatchTypes.cs:214-217`): a `FilePathWrapper`-typed property on a
class used as a `List<T>` tool parameter makes `JsonSchemaExporter` emit an unrepresentable `true`
schema node, which LM Studio's grammar converter rejects (`docs/current/
project_filepath_schema_true_bug_and_detection_method.md`).

```csharp
/// <summary>One edit in a batch ModifyModifier call.</summary>
public class ModifierEdit
{
    public string FilePath { get; set; } = "";
    public string TargetName { get; set; } = "";
    public NonAccessibilityModifier Modifier { get; set; }
    public AddRemoveAction Action { get; set; }
    public string? ContextSnippet { get; set; }
    public string? LineBefore { get; set; }
    public string? LineAfter { get; set; }
}

/// <summary>One edit in a batch ModifyAttribute call.</summary>
public class AttributeEdit
{
    public string FilePath { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string ExistingAttribute { get; set; } = "";
    public AttributeModifyAction Action { get; set; }
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for Action=replace, unused for add/remove —
    // same rule as ModifyAttribute's own newAttribute parameter (SentinelRefactoringTools.cs:1225-1227).
    public string? NewAttribute { get; set; }
    public string? ContextSnippet { get; set; }
    public string? LineBefore { get; set; }
    public string? LineAfter { get; set; }
}

/// <summary>One edit in a batch ModifyBaseType call.</summary>
public class BaseTypeEdit
{
    public string FilePath { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string BaseTypeName { get; set; } = "";
    public AddRemoveAction Action { get; set; }
    public string? ContextSnippet { get; set; }
    public string? LineBefore { get; set; }
    public string? LineAfter { get; set; }
}
```

Each tool gains the array param, e.g.:

```csharp
public async Task<ToolResult<object>> ModifyModifier(
    ToolCallReason reason,
    FilePathWrapper? filepath = null,       // required only when edits is omitted — existing single-edit path unchanged
    string? targetName = null,
    NonAccessibilityModifier? modifier = null,
    AddRemoveAction? action = null,
    string? contextSnippet = null,
    string? lineBefore = null,
    string? lineAfter = null,
    ModifierEdit[]? edits = null,            // NEW — batch path
    bool autoStage = true,
    bool dryRun = false,
    bool returnDiff = false,
    CancellationToken cancellationToken = default)
```

`edits` and the singular per-tool params are mutually exclusive — same either/or validation
`ReplaceSnippet`'s batch uses (proposal_batch_replacesnippet.md's Proposed shape section): reject with
`ToolErrorCode.InvalidArgument` naming whether both or neither were supplied, via a
`CONDITIONAL-PARAM-REVIEW-REQUIRED` comment on the parameter block, before either path is attempted.
`ModifyAttribute`'s batch entry additionally inherits its own tool's existing
`action=replace ⇒ newAttribute required` rule, checked per-edit (see "Failure reporting" below).

## Execution model

The core difference from `ReplaceSnippet`'s batch: no text spans, no offsets, no reverse-order
splicing. The equivalent hazard and its fix are both node-identity operations instead.

1. Group `edits` by file (same as `ReplaceSnippet`'s batch, step 1).
2. For each file with N edits: fetch the `Document` and `SyntaxRoot` **once** — one
   `GetCurrentSolutionAsync` + one `GetSyntaxRootAsync`, not N — and resolve **all N targets** against
   that single original root up front, reusing each tool's existing resolver
   (`ResolveMemberByNameOrSnippet` / `ResolveTypeByNameOrSnippet`, `RefactoringEngine.cs:4743`,
   `:5045`) with the same ambiguity-exception handling (`InvalidOperationException` on an ambiguous
   name/snippet match) the singular path already has. Resolve-time failures are reported per-index,
   same convention as `ReplaceSnippet`'s batch (see "Failure reporting").
3. **Same-node collision check.** If two edits in the same file resolve to the same `SyntaxNode`
   instance (e.g. two `ModifierEdit`s both targeting the same method, or a `ModifierEdit` and an
   `AttributeEdit` — no, cross-tool mixing isn't possible per this design, but two same-tool edits
   both matching the same member by name/snippet is), reject the whole batch before any write,
   `ToolErrorCode.InvalidArgument`, naming both edit indices and the target name/file. This is the
   node-identity equivalent of `ReplaceSnippet`'s span-overlap rejection
   (proposal_batch_replacesnippet.md, Execution model step 5) — same "reject, never silently let the
   second stomp the first or silently merge" discipline, different detection mechanism (node
   reference equality against the shared original root, not offset-range intersection).
4. Once all N targets are resolved cleanly against the original root with no collisions, apply the N
   `ReplaceNode` operations in sequence using `SyntaxNode.TrackNodes` / `GetCurrentNode`: track all N
   target nodes up front on the original root, then for each edit in turn, call `GetCurrentNode` on
   the tracked node against the *current* root (which reflects every prior edit in this same batch),
   compute its replacement, and `ReplaceNode` to produce the next root. This is the standard Roslyn
   idiom for "keep referring to the same original node across a sequence of tree mutations that
   invalidate node identity after each step" — it is genuinely necessary here because
   `ReplaceNodeFormattedAsync`'s single-shot `root.ReplaceNode(oldNode, ...)` (`FormattingHelper.cs:64`)
   assumes `oldNode` is still part of the root it's called against, which stops being true after the
   first replacement in a multi-edit batch. `TrackNodes`/`GetCurrentNode` is not a new pattern
   invented for this proposal — it is already used in this codebase at
   `RoslynSentinel.Basic/MsToolAugmentEngine.cs:139-152` and
   `RoslynSentinel.Advanced/AdvancedRefactoringEngine.cs:248-261` for the same reason (sequencing
   multiple node replacements against one evolving root) — but it does not appear anywhere in
   `RefactoringEngine.cs` itself, where all three of these tools' engine methods live today; each of
   those methods currently does exactly one `ReplaceNode` per call and has never needed to chain.
5. Format **once** per file, after all N replacements are folded into the final root for that file —
   not once per edit. This is a strictly better property than `ReplaceSnippet`'s own batch design
   gets from its underlying mechanism: `ReplaceNodeFormattedAsync` formats on every single call today,
   so each of the three engine methods' current one-edit-at-a-time behavior means N sequential calls
   would pay N formatting passes, N-1 of them thrown away as soon as the next call re-reads the
   solution. Chaining replacements before formatting turns that into exactly one `Formatter.FormatAsync`
   pass per touched file, tracked against a single combined annotation set covering all N replaced
   nodes (mirroring the single-annotation tracking `ReplaceNodeFormattedAsync` already does for one
   node, extended to N).
6. Fold every file's final formatted text into one `Dictionary<FilePathWrapper,string>` — the same
   input shape `ValidateAndApplyAsync` already takes — and make exactly **one**
   `ValidateAndApplyAsync` / `ApplyProposedChangesAsync` call for the whole batch, across every file
   touched by any edit in the request. This is the same non-negotiable requirement
   proposal_batch_replacesnippet.md establishes for its own batch (Execution model step 6): validation
   happens only after every edit is folded together, never per-edit, because the point of batching is
   applying coordinated changes across a codebase in one shot — e.g. adding a new interface to N types
   and adding the member that satisfies it, in the same call, so the intermediate not-yet-implementing
   state never has to compile on its own between calls.

## Failure reporting

Same per-index, named-target convention as `ReplaceSnippet`'s batch
(proposal_batch_replacesnippet.md, Failure reporting section): a resolve failure, ambiguity, same-node
collision, or (for `ModifyAttribute`) a missing `NewAttribute` on an `Action=replace` edit must name
the specific array index, file, and target — e.g. `"edits[3] (Service.cs, target 'Run'): ambiguous
match, 2 candidates"` — never a collapsed aggregate message. `proposal_batch_replacesnippet.md` cites
a specific incident (run `20260910-013550-398`) where a shared, non-specific error cost 24 turns of
model time; the same failure mode applies equally here and gets the same fix.

## Size cap

Cap `edits.Length` per call. Propose reusing `ReplaceSnippet`'s batch cap of 20
(proposal_batch_replacesnippet.md, Size caps) as the starting value — these edits are cheaper per-item
than `ReplaceSnippet`'s text edits (no `MaxOldContentLines`/`MaxOldContentChars`-style payload-size
concern, since each edit is a keyword, attribute name, or type name, not an arbitrary text block), so
20 is not derived from a matching size constraint here, just reused as a reasonable starting ceiling
on edit *count* rather than re-deriving a new number without evidence either way would be better.

## Explicitly out of scope

- **`ChangeAccessibility`.** Same tool shape and same single-`ReplaceNode` engine mechanism as the
  three covered here — nothing in its architecture blocks following this exact design. It's held back
  specifically because bulk accessibility changes have a materially higher chance of breaking the
  build across a batch (tightening several members to `private`/`internal` in one shot is much more
  likely to produce a genuine compile break across referencing code than a modifier, attribute, or
  base-type addition), which is a UX/validation-messaging question — how a batch reports "3 of 8
  accessibility changes broke referencing code" — not an execution-model blocker. Worth its own short
  design pass once this one ships, not folded in here.
- **`ConstructorParameter`.** Genuinely harder, not just deferred for scheduling reasons: adding a
  parameter across N constructors requires a solution-wide `FindReferencesAsync` per target (to fix up
  every call site, not just the declaration) and has a same-constructor-merge hazard if two edits in a
  batch target the same constructor — a fundamentally different and larger execution model than
  "resolve N nodes against one root, chain-replace, format once." Needs its own design pass, not a
  variant of this one.
- **No cross-tool batch.** A single call cannot mix a `ModifierEdit` and an `AttributeEdit` in one
  array — each of the three tools gets its own `edits` array on itself, per the "Proposed shape"
  rejection of a unified multiplexer above.

## Status

Drafted, not yet implemented. Author: Claude (Sonnet 5), for Andrew Almond
(andrew.almond@clearbridge.ca).

This is a direct sequel to `docs/current/proposal_batch_replacesnippet.md`'s "Related work" section,
which flagged `ModifyModifier`, `ChangeAccessibility`, `ConstructorParameter`, and `ModifyAttribute` as
batch candidates from tool shape alone and explicitly deferred a design pass over each tool's actual
implementation. This proposal is that design pass for three of the four (`ModifyBaseType` added, not
originally ranked, because it shares the same mechanism), follows the already-shipped `ReplaceSnippet`
batch implementation as precedent, and leaves `ChangeAccessibility` and `ConstructorParameter` for
future proposals per "Explicitly out of scope" above.
