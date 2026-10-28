# Finding: Member(addMember) re-indents the continuation line of an expression-bodied method

**Status:** OPEN 2026-10-28. Reported once from the Tests.Support.* rollout; not reproduced, root cause not traced, fix not decided.

## Context

Found while adding factory methods to `Tests.Support.Basic/TestEngines.cs` with `Member(operation: addMember)` in
Step 3 slice B of
[plan_tests_support_projects_factory_rollout.md](../plans/closed/plan_tests_support_projects_factory_rollout.md). The plan's
note (follow-up 4 and the slice B bullet): "`Member(addMember)` flattens continuation-line indentation of expression-bodied
methods (fixed by one `ReplaceSnippet`; also re-indented slice A's `TestRunEngine`)".

The shape that triggers it is the house style used throughout that file: an expression-bodied method whose body sits on its own
continuation line, for example
(`RoslynSentinel.Tests.Support.Basic/TestEngines.cs:45-46`)

    public static PersistentWorkspaceManager PersistentWorkspaceManager(ILogger<IWorkspaceManager> logger) =>
        ActivatorUtilities.CreateInstance<PersistentWorkspaceManager>(CreateProvider(), logger);

## What is broken

Presumably (the plan does not say) the caller supplied `newMemberSource` with the body line indented one level deeper than the signature; the written file had that
line at a different indent (the plan says "flattens", i.e. the continuation lost its extra indent). One `ReplaceSnippet`
restored it, and a second pass re-indented the earlier `TestRunEngine` member, so the damage was not limited to the
newly added member's own text in that run.

Not captured: the exact before/after text, which call produced it, whether the target file had
LF or CRLF endings, or how many members were affected. The plan has no count. Anything below is therefore a
lead, not a measured result.

## Root cause

Not traced. What the code does (verified):

- `MemberRefactoringEngine.AddMemberAsync` (`RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs:2303-2365`) parses the
  caller's text with `SyntaxFactory.ParseMemberDeclaration(newMemberSource)` and passes it to
  `RoslynFormattingHelper.InsertMemberDeclarationAndFormatInsertionAsync`.
- That helper (`RoslynSentinel.Common/RoslynFormattingHelper.cs:279-334`) rebuilds the new member's leading trivia (blank-line
  separator plus the indent copied from a sibling), sets its trailing trivia to one end-of-line, annotates it with
  `Formatter.Annotation`, and calls `FormatAnnotatedSpansAsync` (line 363), which runs `Formatter.FormatAsync(document, Formatter.Annotation)`
  and then normalises changed-line endings.
- Hypothesis (unverified): Roslyn's formatter re-computes indentation for the annotated member, and for a wrapped
  `=>` expression body it applies its own continuation rule rather than keeping the offset the caller wrote. This would also
  explain why the doc comment on the helper claims "only the newly inserted member" is formatted yet slice A's `TestRunEngine`
  was re-indented: the formatter owns "the whitespace and line break immediately before an annotated node" (comment on
  `FormatAnnotatedSpansAsync`, lines ~340-343), but that is the line before the node, not another member's body, so a second
  cause (a second `addMember` or `ReplaceSnippet` call) cannot be excluded. Needs a reproduction.

## Why it matters

Silent formatting damage inside the one tool agents use for every member insertion, and it travels: a model that sees its
inserted body change shape has to spend a repair round, or leaves the drift for the next reader. It matches the open
formatting-noise items in `docs/current/TODO.md` (the `SyncInterfaceToImplementation` re-spacing entry and the whitespace
initiative follow-ups), so a shared fix may exist.

## Recommendation

Nothing decided. In order:

1. **Reproduce first.** Add a characterization test beside the existing `AddMember` format tests (look for `MemberInsertAfterEolTests` and
   the `*FormatCharacterizationTests` files; contents not read): insert the two-line expression-bodied method above into a class
   whose siblings use the same style, assert the continuation line keeps its indent, and also assert a sibling's body is unchanged.
   If it passes on current code, record the finding as not reproducible and close it; then look at the call that actually did it.
2. If it reproduces, compare `Formatter.FormatAsync` with and without the annotation on the arrow body to confirm the cause, then
   either (a) skip formatting for members that already parse with well-formed trivia (the helper already computes the indent
   explicitly), or (b) restore the caller's continuation offset after formatting.
3. Whatever the fix, extend the same test to `InsertMemberAfter`/`InsertMemberBefore`, which share the helper
   (`MemberRefactoringEngine.cs` ~1949-2020 fall back to `AddMemberAsync`).

## Out of scope

- Moving the factories to a different style. The style is a convention of the support projects, not a tool contract.
- The other formatting-noise items tracked elsewhere (the `Member` unrelated-content reformat note, `SyncInterfaceToImplementation` in TODO.md);
  link here if they turn out to share a cause.
