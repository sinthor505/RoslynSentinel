# Idea: commit trailers (Plan:/Closes:) to reconcile plan, proposal and finding docs with git history

**Status:** NOT DESIGNED 2026-10-18. Convention plus a script first; a later `ProjectDoc` extension.

## What happened
Plans, proposals and findings were left open long after the work shipped, so TODO.md and the doc
folders grew stale and needed a manual cleanup (tidy-guidance, 2026-10-17). Nothing links a doc to the
commits that implement it.

## The actual gap
A doc has no machine-readable link to the commits that advance or finish it, so "is this done?" needs a
human or an agent to read the code and the log.

## Possible fixes (not decided)
- **Convention (chosen for now).** The ID is the doc filename without its extension (for example
  `plan_semantic_find_replace`); no counter, so concurrent sessions cannot collide. Every commit that
  advances a doc carries `Plan: <id>`; the commit that finishes it carries `Closes: <id>`. No ID in code
  comments: they go stale and git blame already links code to commits.
- **Reconciliation script** (extend `scripts/Test-GuidanceHygiene.ps1`): parse trailers from `git log`.
  A doc whose Status is open but which has a `Closes:` commit is a closure candidate for a human or agent
  to confirm; a doc with only `Plan:` commits is in progress. Only `Closes:` counts as done: a mere
  mention is not evidence, and plans land in slices.
- **Later: `ProjectDoc` tool.** Let `ProjectDoc` list docs with their commit trailers, validate that a
  `Plan:`/`Closes:` value names an existing doc, and perform the closure (update Status, move to
  `closed/`, add the CLOSED.md line). Trade-off: more tool surface and schema tokens; defer until the
  script proves the convention.
- **Not chosen: Git-tool enforcement** (reject unknown IDs, warn on untrailed commits). Revisit after
  the convention has been in use.

## Related
- `scripts/Test-GuidanceHygiene.ps1`, `.claude/skills/tidy-guidance/SKILL.md`
- `docs/current/templates/README.md` (closure convention to add)
- `RoslynSentinel.Tools.Basic/DocumentationTools.cs` (`ProjectDoc`)
