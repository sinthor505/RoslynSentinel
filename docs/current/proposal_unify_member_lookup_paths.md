# Unify Member's divergent declaration-kind dispatch tables (design notes)

## Motivation

Three separate `Member`-family incidents have now produced the identical symptom - `remove` (or a
sibling lookup) reports "not found" for a member that `view`/`GetFileOutline` both confirm exists -
from three different root causes:

1. `project_member_remove_precheck_false_negative_on_const_fields.md` (2026-09-12, memory only) -
   narrow repro against `const` fields specifically.
2. `blocking_error_member_remove_field_precheck_false_negative_unrecoverablebreakerlock.md` (fixed
   2026-09-19, `e120b68`) - broadened #1 to all fields. Root cause:
   `SymbolNavigationEngine.cs`'s `FindImplementationsForMemberAsync` dispatch switch only matched
   `MethodDeclarationSyntax`/`PropertyDeclarationSyntax`; `FieldDeclarationSyntax` fell through a
   `_ => false` default and threw.
3. `blocking_error_member_remove_false_not_found.md` (fixed 2026-09-20) - a property
   (`ServerBinaryPath`), genuinely unrelated cause: `RefactoringStructuralImpl.cs`'s `remove`
   branch discarded `DocumentEditResult.Outcome`/`.Message`, relabeling a legitimate
   `EditOutcome.CannotRemove` (blocked by two `<see cref>` doc-comment usages) as "not found."

Incident 3 was confirmed distinct from incident 2 during root-cause analysis - different file,
different method, different mechanism. But investigating it surfaced a structural fact worth
recording on its own: **there are at least three independently-maintained "which declaration kinds
does this method understand" switch statements in the `Member`-remove/view/list code path**, and
they already disagree with each other:

| Location | Kinds matched | Default behavior |
| --- | --- | --- |
| `RefactoringEngine.cs:5292-5314` (`GetMemberName`) | Method, Property, Class, Interface, Field, Constructor, Enum, Record, Struct | `null` (silently drops the member from name-filtered results - see the inline comment there noting this exact gap was hit once for enums) |
| `RefactoringEngine.cs:5570-5578` (`GetContainerMembersAsync`'s inline switch) | Method, Property, Field, Constructor, Event/EventField, Indexer | falls back to `m.Kind().ToString()` (an internal Roslyn enum name, not necessarily agent-friendly) |
| `SymbolNavigationEngine.cs:1634-1641` (`FindImplementationsForMemberAsync`, post-`e120b68`) | Method, Property, Field | `_ => false` (throws "not found declared" - this is the exact branch `e120b68` patched, and it's already narrower than the other two tables) |

Three tables, three different kind sets, three different fallback behaviors for the kind they don't
recognize. `e120b68` fixed one gap in one of the three; nothing stops the same shape of bug from
resurfacing in a table that still doesn't handle, say, `IndexerDeclarationSyntax` or
`EventDeclarationSyntax` in `FindImplementationsForMemberAsync`, or `EnumDeclarationSyntax` in
`GetContainerMembersAsync`'s inline switch. Each of the three incidents above was found by a human/
agent hitting a live repro, not by an audit - the recurrence pattern itself (same symptom, three
different causes, over 8 days) suggests these will keep drifting apart unless the dispatch logic is
shared once rather than kept in sync by hand across future incidents.

## Proposed fix

Extract one canonical `MemberDeclarationSyntax -> (name, kind-label)` resolver into
`RefactoringToolHelpers.cs` (already the home for cross-cutting `Member`-family helpers - see
`ErrorCodeFor`/`RequireUpdatedText`, both added for the same "stop hand-rolling the same switch
per call site" reason). Shape:

```csharp
public static (string? Name, string KindLabel) DescribeMemberDeclaration(MemberDeclarationSyntax member) => member switch
{
    MethodDeclarationSyntax m => (m.Identifier.Text, "method"),
    ConstructorDeclarationSyntax ctor => (ctor.Identifier.Text, "constructor"),
    PropertyDeclarationSyntax p => (p.Identifier.Text, "property"),
    FieldDeclarationSyntax f => (f.Declaration.Variables.FirstOrDefault()?.Identifier.Text, "field"),
    EventDeclarationSyntax or EventFieldDeclarationSyntax => (/* ... */, "event"),
    IndexerDeclarationSyntax => (/* ... */, "indexer"),
    EnumDeclarationSyntax e => (e.Identifier.Text, "enum"),
    RecordDeclarationSyntax r => (r.Identifier.Text, "record"),
    StructDeclarationSyntax s => (s.Identifier.Text, "struct"),
    ClassDeclarationSyntax c => (c.Identifier.Text, "class"),
    InterfaceDeclarationSyntax i => (i.Identifier.Text, "interface"),
    _ => (null, member.Kind().ToString())
};
```

...and a matching `GetDeclaredSymbolForMember(SemanticModel, MemberDeclarationSyntax, string name,
CancellationToken)` that encapsulates the field-specific `GetDeclaredSymbol`-returns-null-on-the-
declaration-node-itself fallback (the exact fix `e120b68` ported from `FindCallersAsync` into
`FindImplementationsForMemberAsync` - currently living correctly in two places and still absent
from a third).

Then migrate all three call sites (`GetMemberName`, `GetContainerMembersAsync`'s inline switch,
`FindImplementationsForMemberAsync`'s dispatch) to call the shared helper instead of maintaining
their own switch. A union of all kinds any of the three currently recognize becomes the new floor
for all three - no call site regresses, and every call site gains whatever kinds it was previously
missing (e.g. `GetContainerMembersAsync` gains enum/record/struct/class/interface members-of-a-
container support it never had; `FindImplementationsForMemberAsync` gains everything beyond
method/property/field).

## Assessment

Likely worth doing, moderate cost:

- Directly targets a confirmed, repeated structural cause (three drifted tables), not a speculative
  one - each table's exact line range and kind set is cited above from current source.
- Does not fix any *new* bug by itself - all three known incidents are already individually fixed
  or fixed by this session's work. The value is preventing a fourth recurrence of the same symptom
  shape from a fourth still-undiscovered gap (e.g. indexers or events hitting the same `_ => false`
  wall `e120b68` just patched for fields).
- Real cost: touches three methods across two files (`RefactoringEngine.cs`,
  `SymbolNavigationEngine.cs`), each with existing callers and existing tests - this is a
  refactor, not a one-line patch, and needs its own regression coverage (one test per declaration
  kind per call site that changes behavior, at minimum for the kinds each site is *gaining*).
- Does not address the OTHER root cause found this session (`RefactoringStructuralImpl.cs`'s
  `remove` branch discarding `Outcome`/`Message` - already fixed separately, see
  `blocking_error_member_remove_false_not_found.md`) - that was an outcome-reporting bug, not a
  declaration-kind dispatch bug, and is out of scope here even though it produced the same visible
  symptom text.

## Open items for implementation

- Confirm no call site relies on its *current* narrower kind set as an implicit filter (e.g. does
  anything depend on `GetContainerMembersAsync` NOT listing enum/record/struct/class/interface
  members-of-a-container? A quick `FindReferences` on each of the three methods before touching
  them would answer this cheaply.)
- `GetMemberName`'s existing comment (`RefactoringEngine.cs:5300-5304`) documents a real prior
  incident (enums silently dropped from name-filtered results because `null` was returned for
  them) - preserve that historical context in the shared helper's doc comment rather than losing it
  in the extraction.
- Decide the shared helper's `_ => (null, ...)` default behavior once, rather than inheriting
  three different current behaviors (silent `null`, `Kind().ToString()` fallback, or throw) -
  recommend: never throw from the pure dispatch helper itself: let each of the three callers decide
  how to handle an unresolved kind so `FindImplementationsForMemberAsync`'s existing throw-based
  error reporting isn't silently swallowed into a `null`, but a *fourth* fresh unhandled kind can no
  longer produce a raw exception surfaced as a misleading "not found" the way `e120b68`'s bug did.
- Add regression tests enumerating every `MemberDeclarationSyntax` subtype Roslyn defines (not just
  the ones currently hit by a repro) against all three call sites, so a future ninth declaration
  kind added to C# doesn't silently repeat this exact incident class a fourth time.

## Status

Design proposal only - not yet implemented. Raised directly by
`blocking_error_member_remove_false_not_found.md`'s investigation surfacing this table comparison;
not itself required to close that blocker (which had a distinct, already-applied one-line fix).
