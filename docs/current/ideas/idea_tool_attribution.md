# Idea: mark tool-inserted code distinctly from agent-authored code

Status: **raised, not implemented**. Filed 2026-08-27, during 9B-model dog-fooding.

## Context

`Member(operation: add)` already injects a `// Added by InsertMemberAfter` comment above inserted
members, added in response to an earlier, unrelated testing session where reviewers flagged odd
member placement as if the agent had chosen it — when actually it was the tool's insertion
behavior (e.g. missing blank-line separation from the next member).

## The idea

Introduce an attribute similar to `[CompilerGeneratedAttribute]` — something like
`[RoslynSentinel(AddedByAgent/ModifiedByAgent/AddedByEngine/ModifiedByEngine)]` — to distinguish
code the RoslynSentinel tool itself inserted/reformatted mechanically from code an agent authored
deliberately. This would generalize the existing comment-based signal beyond member insertion, and
would be machine-checkable/greppable unlike a free-text comment.

## Open questions

Not yet scoped. If picked up, needs design decisions:
- Where the attribute would apply — methods only, or any member/statement-level insertion?
- Whether it's a real compiled attribute vs. a lint-only marker.
- Whether/how it gets stripped before a human treats the code as "final."

Revisit before starting any related work — check this is still wanted and hasn't been superseded
by a simpler fix (e.g. just improving `InsertMemberAfter`'s blank-line handling, which is the
concrete bug underlying the original complaint).
