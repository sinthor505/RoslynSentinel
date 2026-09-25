# Finding: Member(addTopLevelType) / Member(addMember) write a tool-provenance comment directly
# into committed file content, with no way to suppress it

**Status:** confirmed by source inspection, not yet fixed. This is a "document but continue"
finding -- a session-scoped policy permitted routing around it via `ReplaceSnippet` cleanup rather
than halting the session.

## What was being attempted

Structural edits via the `Member` MCP tool during interface-widening/scaffolding work on
2026-09-24:

1. `Member(operation: "addTopLevelType")` adding a second top-level type to a newly created
   `RoslynSentinel.Common/IWorkspaceReader.cs` (a `ReadSource` enum alongside the
   `IWorkspaceReader` interface).
2. `Member(operation: "addMember")` adding `GetSolutionAsync` to
   `RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs`.
3. `Member(operation: "addMember")` adding `GetDocumentTextAsync` to the same file, in a separate
   call.

## The exact symptom

All three calls succeeded and returned normal `AppliedChangeSummary`-shaped results, but the
written file content in every case had an extra line spliced in that was never part of the
caller's `newMemberSource`/`newTypeSource` argument:

```
// Added by AddTopLevelType (expected - used for diagnostics)
```

for case 1 (inserted between the two top-level types), and

```
// Added by InsertMemberAfter (expected - used for diagnostics)
```

for cases 2 and 3 (inserted immediately before each newly added member).

All three occurrences required a follow-up `ReplaceSnippet` call to strip the injected line before
the file was left in a state fit to commit.

## Where this happens (traced to source, not inferred from the symptom)

`RoslynSentinel.Common/ContextHelper.cs:744-751`, extension method `WithAddedByComment<T>`:

```csharp
public static T WithAddedByComment<T>(this T member, string toolName) where T : MemberDeclarationSyntax
{
    var comment = SyntaxFactory.Comment($"// Added by {toolName} (expected - used for diagnostics)");
    var newLeadingTrivia = member.GetLeadingTrivia()
        .Insert(0, comment)
        .Insert(1, SyntaxFactory.CarriageReturnLineFeed);
    return (T)member.WithLeadingTrivia(newLeadingTrivia);
}
```

This attaches the comment as real leading trivia on the synthesized `MemberDeclarationSyntax`
node -- i.e. it becomes part of the syntax tree that gets formatted and written to disk, not a
side channel or response-only annotation. It is called unconditionally (no parameter to opt out)
from every add-a-new-member code path in `RoslynSentinel.Basic/RefactoringEngine.cs`:

- `RefactoringEngine.cs:1436` -- `newMember = newMember.WithAddedByComment("AddMember");` (backs
  `Member(operation: "addMember")`)
- `RefactoringEngine.cs:1502` -- `newType = newType.WithAddedByComment("AddTopLevelType");` (backs
  `Member(operation: "addTopLevelType")`)
- `RefactoringEngine.cs:2626` -- `WithAddedByComment("InsertMemberAfter")`
- `RefactoringEngine.cs:2721` -- `WithAddedByComment("InsertMemberBefore")`
- `RefactoringEngine.cs:4410` and `RefactoringEngine.cs:4436` -- `WithAddedByComment("AddConstructorParameter")`
  (backing field and synthesized constructor)
- Also called from `RoslynSentinel.Basic/MsToolAugmentEngine.cs:1122`, `:1287`, `:1784` for
  `ExtractConstantSafe`, `Generate(kind: generate_to_string_safe)`, and `ExtractMethodSafe`.

The doc comment directly above the helper (`ContextHelper.cs:738-743`) states the intent
explicitly: the comment is meant to be visible in the written file, "so the addition is easy to
spot in a diff or code review without cross-referencing which MCP tool call produced it." So this
is not a case of response-only metadata accidentally leaking into file content (the original
working hypothesis going into this investigation) -- landing in the file is the documented design.
The defect is narrower than that hypothesis, but still real:

1. **No opt-out.** `WithAddedByComment` takes no flag and every call site above invokes it
   unconditionally. A caller who does not want a permanent provenance comment in committed source
   (the common case observed this session, across 3 separate calls) has no parameter on `Member`,
   `MethodSignature`, `ConstructorParameter`, `ExtractConstantSafe`, `Generate`, or
   `ExtractMethodSafe` to suppress it. Confirmed by reading the full parameter lists of
   `RefactoringStructuralTools.cs`'s `Member` declaration and the tool descriptions cited in
   `docs/current/finding_member_add_traceability_comment_wording.md` -- no suppression parameter
   exists anywhere in the surface.
2. **No tool-facing documentation of the behavior at all.** Neither the `Member` tool's
   `[Description]` nor its per-parameter descriptions mention that a comment line will be spliced
   into the synthesized member/type's leading trivia. A caller has no way to know ahead of the call
   that its `newMemberSource`/`newTypeSource` argument will not be written verbatim, short of
   reading this comment back afterward (which is exactly what happened in all 3 cases this
   session -- each required a `ReadFile`/diff check that caught it, followed by manual
   `ReplaceSnippet` cleanup).

This is a distinct defect from the one already tracked in
`docs/current/finding_member_add_traceability_comment_wording.md`, which is about the comment's
*wording* being ambiguous once it's in the file (reads like member documentation, not tool
provenance). This finding is about the comment being unconditionally present with no suppression
path and no advance warning in the tool surface -- both docs point at the same
`WithAddedByComment` helper but describe non-overlapping gaps; fixing the wording alone (the other
finding's proposed fix) would not address the lack of an opt-out documented here.

## Impact

Every `addTopLevelType`/`addMember` call (and by extension `AddConstructorParameter`,
`ExtractConstantSafe`, `Generate(generate_to_string_safe)`, `ExtractMethodSafe`, and
`InsertMemberBefore`/`InsertMemberAfter`-backed operations) risks leaving a tool-generated comment
in committed source unless the caller happens to notice and manually cleans it up via a follow-up
edit tool call. This defeats part of the purpose of a structural-edit tool over a manual text
edit -- the caller still has to visually verify and hand-fix the written content, exactly the
manual-massaging step MCP structural tools exist to remove (CLAUDE.md's failure doctrine: an
environment gap that silently relocates work onto the caller rather than either eliminating the
hazard or making it visible before it lands).

## What unblocks it

Either of the following, from the repo owner:

1. Add an explicit boolean parameter (e.g. `addProvenanceComment`, defaulting to current
   behavior for backward compatibility, or defaulting to `false` if the intended default is
   changing) to every MCP tool surface that reaches `WithAddedByComment` --
   `Member`/`ConstructorParameter`/`ExtractConstantSafe`/`Generate`/`ExtractMethodSafe` at minimum
   -- threaded down to the `RefactoringEngine.cs` call sites listed above; or
2. If the comment is meant to always be present by design (per the existing `ContextHelper.cs:738-743`
   doc comment), add that fact to the `Member`/`ConstructorParameter`/etc. `[Description]` text so a
   caller can decide up front whether to immediately follow up with a `ReplaceSnippet` removal
   rather than discovering it only after reading the file back.

Either fix should also resolve `docs/current/finding_member_add_traceability_comment_wording.md`'s
wording complaint at the same time, since both point at the same call sites.

## Related

- `docs/current/finding_member_add_traceability_comment_wording.md` -- same helper, narrower
  complaint (wording only, not the missing opt-out documented here).
- `docs/current/ideas/tool_attribution_idea.md` -- the original design intent this behavior
  implements.
- `RoslynSentinel.Common/ContextHelper.cs:738-751` -- `WithAddedByComment<T>` definition and its
  documented intent.
- `RoslynSentinel.Basic/RefactoringEngine.cs:1436, 1502, 2626, 2721, 4410, 4436` -- call sites
  backing `Member(addMember)`, `Member(addTopLevelType)`, `InsertMemberAfter`/`InsertMemberBefore`,
  and `AddConstructorParameter`.
- `RoslynSentinel.Basic/MsToolAugmentEngine.cs:1122, 1287, 1784` -- call sites backing
  `ExtractConstantSafe`, `Generate(generate_to_string_safe)`, `ExtractMethodSafe`.
