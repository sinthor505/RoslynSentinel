---
name: project-tool-attribution-idea
description: "Idea to mark tool-inserted code distinctly from agent-authored code, raised during 9B dog-food testing"
metadata: 
  node_type: memory
  type: project
  originSessionId: 8cc40adc-02cd-47df-a2c3-a5257d5998e5
  modified: 2026-08-28T06:30:57.076Z
---

User proposed (2026-08-27, during the 9B-model dog-fooding test series) introducing an attribute
similar to `[CompilerGeneratedAttribute]` — something like
`[RoslynSentinel(AddedByAgent/ModifiedByAgent/AddedByEngine/ModifiedByEngine)]` — to distinguish
code the RoslynSentinel *tool itself* inserted/reformatted mechanically from code an agent
authored deliberately.

**Why:** the `Member(operation: add)` tool already injects a `// Added by InsertMemberAfter`
comment above inserted members, added in response to an earlier, unrelated testing session where
reviewers flagged odd member placement as if the agent had chosen it — when actually it was the
tool's insertion behavior (e.g. missing blank-line separation from the next member). A structured
attribute would generalize that same signal (this was the tool, not agent judgment) beyond just
member insertion, and would be machine-checkable/greppable unlike a free-text comment.

**How to apply:** not yet implemented or scoped — this is a raised idea, not a committed plan. If
picked up, needs design decisions: where such an attribute would apply (methods only, or any
member/statement-level insertion?), whether it's a real compiled attribute vs. a lint-only marker,
and whether/how it gets stripped before a human treats the code as "final." Revisit before
starting any related work — check this is still wanted and hasn't been superseded by a simpler
fix (e.g. just improving `InsertMemberAfter`'s blank-line handling, which is the concrete bug
underlying the original complaint).
