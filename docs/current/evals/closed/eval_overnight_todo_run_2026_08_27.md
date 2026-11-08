---
name: project_overnight_todo_run_2026_08_27
description: Overnight autonomous session working TODO.md items 1/3/5/6/7/9/8(best-effort) — outcomes and two durable pitfalls found along the way
metadata:
  type: project
---

Ran an overnight autonomous session against `docs/current/TODO.md`, working a negotiated subset:
items 1, 3, 5, 6, 7, 9, then 8 as best-effort (item 4 dropped by user instruction). All items closed
or explicitly deferred with findings; commits per item as instructed. See TODO.md's own dated entries
for full technical detail on each — this memory only captures what a future session wouldn't get from
reading the code or the TODO text alone.

**Two durable pitfalls found, worth remembering independent of the specific fixes:**

1. **`FilePath` has an implicit `string → FilePath` conversion** (`RoslynSentinel.Common/FilePath.cs:97`).
   Any code that does `new Dictionary<FilePath, string> { { "error", message } }` as a way to smuggle
   an error out of a method that returns `Dictionary<FilePath, string>` will silently compile, and if
   that dict is ever handed to `ValidateAndApplyAsync`/`ApplyProposedChangesAsync` with `autoStage=true`,
   it will actually try to write a file named `error` to disk. Found this exact bug in
   `RoslynSentinel.Advanced/RefinementEngine.PullUpMemberAsync`. If grepping for more instances: search
   for `Dictionary<FilePath, string>` literals with a string key that isn't a real path (`"error"`,
   `"__error__"`, etc.) — `RefinementEngine.InlineMethodAsync` in the same file has the `"__error__"`
   variant and was *not* fixed this session (flagged in TODO.md, out of the negotiated scope).

2. **TODO.md's line-number/root-cause references drift and can point at the wrong code entirely**, not
   just the right code at a stale line number. The `PullUpMember` entry (item 9) was written against
   `StructuralRefinementEngine.PullUpMemberAsync` (`RoslynSentinel.Basic`) as "the" stub — but
   `SentinelAdvancedRefactoringTools` actually injects *two* similarly-named engines
   (`_structuralRefinementEngine` from Basic, unused/dead; `_refinementEngine` from
   `RoslynSentinel.Advanced`, real and actually called). The tool wrapper calls the real one. Always
   verify a TODO's claimed call chain (`_field.MethodAsync(...)`) against the actual tool wrapper source
   before trusting a written diagnosis, especially when two classes share a suspiciously similar name.

**Item 8 (large-result offload) finding:** most of the ~24-tool "still open" list in TODO.md was
already stale — several tools (`UsingDirective`, `SummaryComment`, `ModifyAttribute`,
`ConstructorParameter`) were already wired by earlier work not reflected in that list. Of what's left,
none are cheap wins: tools like `ChangeAccessibility`/`ModifyModifier`/`ModifyBaseType` have no new
content beyond what the caller already passed in verbatim (already commented as deliberate in the
code), and extract/introduce-style tools (`ExtractLocalVariable`, `Introduce`, `ExtractMembers`, etc.)
are blocked on their engine methods only returning whole-file `UpdatedText` with no separately-exposed
fragment field — wiring those needs an engine-API-extension pass, not tool-layer wiring. No code
changes were made for item 8; see TODO.md for the full per-tool audit.

Related: [[project_advanced_extends_basic]] (the "Advanced extends Basic" mental model is *mostly*
true but doesn't mean every Basic engine is what's actually wired — always check the DI field, not just
the assumption), [[feedback_use_roslyn_sentinel_tools_first]] (the MCP HTTP server was down for this
whole session — `vs_roslyn_sentinel_advanced_http` failed to connect — so all work this session used
direct file tools/Bash as the documented fallback, not the dogfooded MCP tools).
