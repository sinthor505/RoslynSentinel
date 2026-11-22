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

- 2026-11-21 16:39 SemanticFindReplace replaceConstruction cannot retype `new Dictionary<FilePathWrapper, string>` -> `new DocumentChangeSet` (needs a non-generic factory; skips object/collection-initializer sites); plan_document_changeset_type Step 7 fell back to batched ReplaceSnippet over ~140 sites - evidence: Batch C orchestrator report, .claude/journal/implementer-ledger.md 7a-7g lines - no code spot
