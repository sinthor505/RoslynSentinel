# `Member(addMember)` reports `success: true` while silently dropping 3 of 4 field declarations from a multi-line `newMemberSource`

**Status:** OPEN as a distinct repro. Root cause is very likely the same defect already traced to
source in `docs/current/blockers/blocking_error_member_add_multi_type_silent_drop.md` (that doc's
case 1/2 dropped 1-of-2; this repro drops 3-of-4, from the same `ParseMemberDeclaration`
single-declaration-parse code path) - recorded separately per this repo's failure doctrine
("a tool failure is a blocking finding," each repro captured, not folded away) and because this
instance is `containerName`-scoped field-add on an *existing* class (`AddMemberAsync`), not the
top-level-type-add path, and involves 4 declarations rather than 2. Not yet independently re-verified
against source line numbers this session - see "Relationship to existing doc" below before treating
this as confirmed-separate.

## What was being attempted

During Decision 7 Step 2 of `docs/current/plans/plan_split_workspace_refactoring_tools_for_di.md`
(splitting `SentinelWorkspaceTools` into 4 new `*Tools`/`*Impl` pairs), a single
`Member(operation: "addMember", containerName: "SentinelWorkspaceTools", ...)` call was made against
`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`, with a `newMemberSource` string containing
four separate `private readonly` field declarations back-to-back, intended to land as four
independent sibling fields on the class:

```csharp
private readonly WorkspaceProjectManagementTools _projectManagement;
private readonly WorkspaceBuildTestTools _buildTest;
private readonly WorkspaceFileEditTools _fileEdit;
private readonly WorkspaceHealthMiscTools _healthMisc;
```

## The exact result reported

The call returned `success: true`. Its `changedContent` field echoed **all four** field declarations
verbatim, exactly as submitted - the response gave no indication that anything less than a
four-for-one write had occurred.

## What was actually on disk

The very next step - replacing the constructor body to reference `_buildTest`, `_fileEdit`, and
`_healthMisc` - failed immediately with `CS0103: The name '_buildTest' does not exist in the current
context` (and the same for the other two names). This was surprising given the immediately-prior
`success: true` response.

Verification via `GetFileOutline` followed by a direct `ReadFile` of the affected line range showed
only **one** field present on disk: `_projectManagement`. `_buildTest`, `_fileEdit`, and
`_healthMisc` were completely absent - no partial/truncated fragment, no stray token, nothing to
suggest they were ever parsed at all. The single surviving field was preceded by an auto-generated
comment, `// Added by InsertMemberAfter (expected - used for diagnostics)`, confirming the insertion
path taken.

**1 of 4 declarations landed; 3 of 4 were silently dropped**, while the tool's own response claimed
all 4 succeeded.

## Repro steps

1. Have an existing class with at least one field/member already declared (any container works; this
   repro used `SentinelWorkspaceTools`, an `[McpServerToolType]` class in
   `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`).
2. Call `Member(operation: "addMember", filepath: "<file>", containerName: "<ClassName>",
   newMemberSource: "<4 back-to-back `private readonly` field declarations, no wrapping type>",
   position: "after:<existing-field-name>")`.
3. Observe `success: true` and a `changedContent` echo containing all 4 declarations.
4. `ReadFile`/`GetFileOutline` the target file: only the **first** declaration in the source string is
   actually present.

## Why this is a blocking finding, not a footnote

Per `CLAUDE.md`'s failure doctrine, a "success" result that does not prove the write landed is
exactly the class of defect this project treats as a blocking finding rather than a model/usage
mistake. The `newMemberSource` passed was syntactically valid C# in full; the tool's own response
echoed it back as fully applied. Nothing about the call or its result gave any signal to distrust it
short of the next, unrelated tool call (a constructor edit) failing with `CS0103` - if that follow-up
call had not referenced the missing fields, or had come in a later turn after context moved on, this
would have shipped as a silently incomplete class with zero diagnostic trail.

## Workaround used

Split the single 4-declaration `newMemberSource` into 4 separate `Member(operation: "addMember", ...)`
calls, each adding exactly one field, each specifying `position: "after:<previous-field-name>"` to
chain them in order. All 4 individual calls succeeded correctly and were verified present via
`ReadFile` before the dependent constructor edit was attempted.

## Relationship to existing doc

`docs/current/blockers/blocking_error_member_add_multi_type_silent_drop.md` already traced this
general failure shape to source for two related call paths:

- `AddTopLevelTypeAsync` (`RefactoringEngine.cs:1292`, `containerName` omitted) - drops the 2nd of 2
  sibling top-level type declarations.
- `AddMemberAsync` (`RefactoringEngine.cs:1237`, `containerName` supplied) - drops the 2nd of 2
  sibling member declarations inside an existing container.

Both funnel `newMemberSource`/`newTypeSource` through `SyntaxFactory.ParseMemberDeclaration`, a
single-declaration Roslyn API that silently parses only the first declaration in a multi-declaration
string and discards the rest with no diagnostic - and separately, `ChangedContent` at
`SentinelRefactoringTools.cs:776`/`:815` is populated from the raw request parameter rather than the
actual applied result, so the response can never reveal the drop regardless.

This repro's call path (`containerName` supplied, i.e. `AddMemberAsync`) matches that doc's case 2
exactly, and the "1 of 4 kept, rest dropped" pattern is consistent with `ParseMemberDeclaration`
parsing only the first declaration and returning early - the same defect, at higher multiplicity (4
inputs instead of 2, so 3 dropped instead of 1). This was not independently re-confirmed against
`RefactoringEngine.cs`/`SentinelRefactoringTools.cs` line numbers this session (no source read was
done here; the existing doc's trace is being relied on rather than re-verified). If a maintainer
confirms the mechanism is identical, this doc can be folded into the existing one as an additional
repro/multiplicity data point rather than kept as a separate entry.

## What unblocks it

Same fix as the existing doc's "What unblocks it" section: after `ParseMemberDeclaration` returns a
non-null node, verify the parsed node's span reaches the end of the input string; if it doesn't,
reject with an explicit error naming the problem (e.g. "newMemberSource contains N declarations;
Member(addMember) only accepts one per call") instead of silently truncating. Separately, populate
`ChangedContent` from the actual post-write document/diff, not the echoed request parameter, so a
`success: true` response can never misreport a partial write as complete.

## Related

- `docs/current/blockers/blocking_error_member_add_multi_type_silent_drop.md` - same failure shape,
  root cause already traced to source; see "Relationship to existing doc" above.
- `docs/current/blockers/blocking_error_member_replace_strips_blank_line_between_adjacent_members.md` -
  different `Member` operation (`replace`, not `addMember`), different code path, same
  "reported success doesn't match disk state" family.
