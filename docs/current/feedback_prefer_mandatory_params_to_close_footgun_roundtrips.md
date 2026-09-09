---
name: feedback-prefer-mandatory-params-to-close-footgun-roundtrips
description: "When a new tool param exists specifically to prevent a known model failure mode, default to making it mandatory rather than optional, unless a concrete usage pattern needs the fallback"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 66fd43e4-a0cd-4fb9-b185-975a1b905997
  modified: 2026-09-09T15:55:32.814Z
---

When designing a new MCP tool param whose whole purpose is closing a previously-observed model
failure mode (e.g. a round-trip the model kept forgetting to make), prefer making it REQUIRED
rather than optional-with-a-safe-default — even if the optional version is technically strictly
more flexible.

**Why:** during [[project_createfile_tool_design_sketch]], `typeKind`/`typeName` were first
implemented as optional (falls back to a bare-namespace file). The user pushed back: an optional
param just relocates the failure mode the tool exists to close — a model that forgets to set it
still ends up needing a second `Member(add)` round-trip to reach a populatable type, identical in
shape to the original bug (WriteFile unavailable → model stuck) just one tool later. Mandatory
closes the omission path entirely; a rarer case (a file needing a 2nd+ top-level type) is still
served by the pre-existing tool (`Member(add, containerName: null, ...)`), so nothing was actually
lost by removing the optional fallback.

**How to apply:** when sketching a tool param that exists to prevent a specific observed failure,
ask "does making this optional reopen the exact failure mode I'm trying to close?" before defaulting
to optional-for-flexibility. If yes, make it required and rely on an existing sibling tool to cover
the rarer alternate case, rather than adding a fallback branch to the new tool.
