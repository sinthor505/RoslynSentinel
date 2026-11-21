# INBOX - untriaged issues

Rules: (1) append one line the moment an issue is noticed, before continuing:
`- YYYY-MM-DD HH:mm <symptom> - <evidence: file:line, quoted error, run/turn id> - <code TODO location, or "no code spot">`;
(2) append only - never edit or reorder another line, so concurrent sessions do not collide; (3) an issue with
a code location also gets its inline `// TODO` (CLAUDE.md, Deferred issues) - the code stays the source of truth,
this line is the pointer; (4) do not judge size or cause when adding - triage does that; (5) `todo-review` and
`todo-punchlist` triage every line: move it to [TODO.md](./TODO.md) (with a doc if it needs design), fix it, or
close it with a reason in [CLOSED.md](./CLOSED.md), then delete the line here. Code TODO markers are listed in
[todo_markers.md](../generated/todo_markers.md) (regenerate with `pwsh -NoProfile -File scripts/Get-TodoMarkers.ps1`).

## Untriaged

- 2026-11-21 23:58 plan_document_changeset_type Step 6d-3 deferred: AppliedChangeSummary.ChangedContent cannot be retyped alone (positional record, CS8866) and retyping it with the From(...) changedContent parameters breaks 63 CS1503 callers in 9 files / 5 projects (Tools.Basic RefactoringStructuralImpl 11, RefactoringExtractionDocsImpl 7, RefactoringSignatureImpl 6, CoreRefactoringTools 2; Tools.Advanced AdvancedRefactoringTools 20; Tests.Basic AppliedChangeSummary*Tests ~12, ResultStatusTests:1043) - code TODO: RoslynSentinel.Common/AppliedChangeSummary.cs:23
