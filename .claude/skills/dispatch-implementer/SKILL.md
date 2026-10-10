---
name: dispatch-implementer
description: Brief template and slice limits for dispatching the Haiku-tier implementer subagent. Load before the first implementer dispatch in a session.
---

# Dispatching implementer: the slice contract

`implementer` is Haiku-tier with a 200k context. It finishes small, exactly-specified slices and
stalls on anything with a wide blast radius. Cutting the work down is the **dispatcher's** job. A
`PreToolUse` hook (`enforce-dogfood.ps1`) refuses a dispatch that breaks the formal parts below.

**Brief template** - every field on its own line, label first:

```
Files: <=3 .cs files, full paths including the project
Symbols: exact types/members to change
Call sites: pre-measured list (file:line), or "none"
Acceptance: ONE check - a clean Build, or one named test/diagnostic
Out of scope: what not to touch (e.g. "do not commit", "items 5-7") - always include "edit nothing
  outside the named symbols/branches; if a test seems to need another change, reply RESCOPE:"
  and "no Write/Edit/shell writes on .cs files"
```

Dispatch with `model: "haiku"` pinned explicitly.

**Measure first, then slice.** Get the call-site list from `InspectSymbol(aspect: blastRadius)` (or
`FindReferences`) and paste it into the brief. `blastRadius` takes one symbol per call (resolve it with
`LocateSymbol`, then pass `filePath` + a verbatim `contextSnippet`) and returns `totalCallSites`,
`affectedProjectsCount` and the reference list; count distinct files yourself. Treat a non-empty
`error` field as a failed measurement even if `isError` is false - on a stale server binary it
otherwise reads as "zero blast radius".

**Slice limits** (tighten, don't loosen, on doubt):
- 3 files or fewer in `Files:`; about 10 distinct edits at most; one acceptance check.
- Interdependent parts land in one edit: interface plus implementations, or signature plus callers, via
  one `ReplaceSnippet` with `batchEdits` (definitions first) or a call-site-updating tool
  (`RenameSymbol`, `ChangeSignature`, `MethodSignature`). Name the tool call in the brief.
- Big migrations are staged so every step compiles: add the new type/overload beside the old, move
  callers a few files at a time, remove the old one in a final slice.
- Over the limits, or about 15+ call sites, or 3+ affected projects: split again, or send straight to
  `implementer-senior`. Also to the senior: new algorithms, public API shape changes, cross-project
  type relocation, or work where you cannot name the files up front.

The implementer re-measures before editing and replies `RESCOPE:` (with its numbers) if the real blast
radius exceeds the brief's; the dispatcher then re-slices.
