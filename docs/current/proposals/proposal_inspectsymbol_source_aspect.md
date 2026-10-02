# One shared "member source" engine method, exposed through Member(view) and optionally InspectSymbol

**Status:** PARTLY BUILT 2026-10-01. Option B (modal `Member(view, memberName)`) and the signature prerequisite are built: signature fix 0e93f99, member source in the commit that follows it (`SymbolNavigationEngine.GetMemberSourceAsync`). Not built: B2 (separate `list` operation) and Option A (`InspectSymbol(aspect: source)`); both remain open. The file name predates the revision.
Implementation notes: an overloaded name with no `contextSnippet` returns `Ambiguous` rather than the first overload (unlike `replace`/`remove`, which take the first); ambiguity vs not-found for snippet failures is read from the `failureMode` that `ResolveBySnippetOrThrow` hands its hint builder, not parsed from prose; the bare `InvalidOperationException` in that method is still there (follow-up).

## Motivation
No MCP tool returns the declaration of a single property, field, event or enum member, so an agent
that needs "what is the default / initializer / accessor shape of X" falls back to `ReadFile` plus a
guessed line range. CLAUDE.md states the gap directly: `GetMethodSource` is "methods only - use
`ReadFile` for fields/properties/constants".

Evidence (2026-10-01, Step 2b run of `plan_error_codes_follow_cause.md`): an agent needed to know
whether `EngineResultBase.UpdatedText` defaults to null. It called `ReadFile` with `startLine: 1,
endLine: 45` on a 32-line file (about 0.5 KB, so cheap there; in a large file the same guess is
expensive). It said it did not weigh a cheaper tool and named a member-source mode as the thing that
would remove the line-range guesswork.

What the existing tools return for that same property, run live on 2026-10-01:

| Tool | Result for `EngineResultBase.UpdatedText` | Declaration text / initializer? |
| --- | --- | --- |
| `Search(mode: symbol, symbolKind: property)` | one hit: `signature`, `filePath`, `line: 17`, `contextSnippet: "public string? UpdatedText"` | No. One-line snippet |
| `Search(mode: property)` | 724 records, 156 KB spilled to a large-result file; `query` did not narrow by name | No |
| `InspectSymbol(aspect: info)` | `name`, `kind`, `fullSignature`, `containingType`, `containingNamespace`, `accessibility`, `definedInFile`, `definedAtLine`, `modifiers: []` | No |
| `Member(operation: view, containerName, memberName)` | all 7 members of the container with `name`, `kind`, `signature`, `startLine`, `endLine`; `memberName` ignored; `UpdatedText` signature shown as `"[JsonIgnore]"` (defect, see `findings/finding_member_view_signature_truncated_at_attribute_line.md`) | No, header only, and wrong for attributed members |
| `GetFileOutline` | file-wide symbol list with `startLine`/`endLine`, no signatures | No |
| `GetMethodSource` | methods only | n/a |

`InspectSymbol` was not callable in the `claude` mode until its DI registration used the right class
name (`SymbolNavigationTools`, not `SentinelSymbolTools`); the user corrected that 2026-10-01.

## Proposal
Decided: build **one engine method** that returns a single member's declaration as written
(attributes, initializer, accessor bodies, leading doc comment) plus `startLine`/`endLine`, in
`SymbolNavigationEngine` (`RoslynSentinel.Engines.Basic`) beside `GetContainerMembersAsync`. Both
exposure options below are thin delegates to it, so the choice does not change the engine work.

Open: where to expose it. Recommendation is **Option B**; Option A can be added later as a delegate
if wanted.

### Option B (recommended): `Member(operation: view)` honours `memberName`
- `view` with `memberName` set returns that member's source. `containerName` becomes optional in that
  case (it is already unnecessary for `replace`/`remove`, which resolve by `memberName` plus an
  optional `contextSnippet`).
- `view` without `memberName` keeps today's behaviour: the member list for `containerName`. This is
  additive: `memberName` is currently ignored on `view`
  (`RefactoringStructuralImpl.cs:388-397`), and the only tests found cover enum containers
  (`BatteryTwentyFourTests.cs:323`) and do not pass `memberName`.
- Why this home: `Member` is the read-then-write loop. A model that will call `replace` with
  `memberName` + `newMemberSource` already holds the name; `view(memberName)` returns the exact text
  it is about to replace, in the same parameter vocabulary, with no `contextSnippet` to compose.
  `InspectSymbol` instead requires `filepath` + `contextSnippet` to resolve the symbol, which is the
  fragile step for a weak model.
- Cost: the mode switches on whether `memberName` is set. That is a modal parameter, which the repo's
  own guidance prefers to avoid (see Option B2).
- Option B2, the explicit split the user suggested: keep `view` = member source for `memberName`, and
  add a new operation (working name `list`, or `GetMembersList`) for the container listing. This
  removes the modal behaviour but changes the meaning of existing `view` calls that pass no
  `memberName`, so it needs a deprecation step or a compatibility rule. The listing itself should NOT
  be re-implemented over `GetFileOutline`: the outline is file-wide and has no `signature` field,
  while the container listing exists to supply signature text for overload disambiguation (its doc
  comment says so). If a filtered outline is wanted, add a `container` filter to `GetFileOutline`
  instead and keep `Member`'s list as is.

### Option A: `InspectSymbol(aspect: source)`
- Add `source` to `InspectSymbolAspect` (`RoslynSentinel.Common/ToolEnums.cs:119`) and a branch in
  `SymbolNavigationImpl.InspectSymbol` (`RoslynSentinel.Tools.Basic`, beside the `info` and
  `blastRadius` branches at ~86 and ~109).
- Fits its contract ("inspects a symbol in depth"), and the chain `Search(symbol)` then
  `InspectSymbol(source)` works with no line numbers because a `Search` hit already returns
  `filePath` and `contextSnippet`.
- Weaker as the primary home: it needs `filepath` + `contextSnippet` where `Member(view)` needs only
  names, and the `info` aspect it sits beside returns a small summary, so `source` would be the one
  aspect with a large, variable-size payload.

### Shared behaviour (decided)
- Supported kinds: property, field, event, enum member, constant, indexer, constructor, method. For a
  method, return the full source and say in the description that `GetMethodSource` is preferred (it
  supports continuation). A whole type is out: return `TargetIneligible` pointing at `ReadFile` or
  `GetFileOutline`.
- Cap the payload (about 200 lines); past the cap return the truncated text plus `isComplete: false`
  and `endLine`, so a follow-up `ReadFile` is one precise call.
- Resolution failures use `NotFound`/`Ambiguous`, never `Exception` (per
  `plan_error_codes_follow_cause.md`).
- Return source verbatim, including non-ASCII characters that are part of the code.

### Prerequisite, independent of the choice
Fix the `Member(view)` signature defect first (attribute line reported as the signature). Option B
builds on that method and would otherwise ship a correct `source` next to a wrong `signature`. See
`findings/finding_member_view_signature_truncated_at_attribute_line.md`, recommendation 1.

## Alternatives considered, not pursued
- **Extend `GetMethodSource` to accept members.** Lost: the name says "method", its schema is
  method-shaped (`methodName`), and it carries continuation semantics that do not fit a one-line
  property. Renaming it would break every caller and prompt.
- **Add a `declaration` field to `InspectSymbol(info)`.** Lost: `info` is a small summary; adding
  source text makes every `info` call pay for text most callers do not want.
- **Have `Search(mode: symbol)` return the full declaration.** Lost: it can return many hits, so the
  payload multiplies; the large-result spill already shows how big that gets.
- **Build the member listing on `GetFileOutline`.** Lost: no `signature`, file-wide rather than
  container-scoped (see Option B2).
- **Do nothing, document `ReadFile` with a narrow range.** The status quo. It works when the line
  range comes from `Search` or `Member(view)`, but relies on the model computing a range.

## Open questions
- Option B or B2 (modal `view`, or a new `list` operation with a compatibility rule for old `view`
  calls)? Needs a decision.
- The `InspectSymbol(info)` description promises "attributes, and documentation", but the output for
  `UpdatedText` had neither. Hypothesis: omitted when empty; `SymbolNavigationEngine.GetSymbolInfoAsync`
  was not read. Verify before deciding whether Option A overlaps with `info`.
- Is `Member` active in every tool mode a weak model runs in, including any read-only mode? Not
  checked. A read-only lookup living on a mutating tool is a possible objection to Option B.
- `Member`'s schema already carries about 20 parameters; adding behaviour to `view` raises the
  per-listing token cost slightly (no new parameter is needed for Option B).
- `Search(mode: property|field|...)` listing modes ignoring `query` (observed 2026-10-01: 724
  records). Separate issue; not part of this proposal.

## Cost / risk
- Engine: one new method in `SymbolNavigationEngine.cs`; the file is ~3100 lines, so place it next to
  `GetContainerMembersAsync`.
- Option B touches `RefactoringStructuralImpl.cs` (the `view` branch), the `Member` tool descriptions
  and one CLAUDE.md row (the "methods only" note). Option A touches `ToolEnums.cs` (schema change for
  `aspect`; check the schema-emission tests) and `SymbolNavigationImpl.cs`.
- No new tool class, so no `ToolClassRegistry` or DI change for either option.
- B2 changes the contract of an existing operation: needs a compatibility rule and a check of
  PlanStepRunner prompts and model-eval fixtures that call `Member(view)`.

## Related
- `findings/finding_member_view_signature_truncated_at_attribute_line.md` (prerequisite defect).
- `plans/plan_error_codes_follow_cause.md` (error-code vocabulary; `Member(view)` and
  `InspectSymbol(info)` both miscode resolution failures as `Exception`).
- CLAUDE.md "Dog-fooding" table (the "methods only" row this would change).
- Journal 2026-10-01 (`.claude/journal/2026-10-01_cc715aa0.md`): `Search`, `ReadFile`,
  `InspectSymbol` entries.
