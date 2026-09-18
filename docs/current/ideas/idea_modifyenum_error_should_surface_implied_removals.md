# Idea: `ModifyEnum` validation errors should surface which members the call implicitly removed

**Status:** not designed. Raised 2026-09-18 after burning 2 failed attempts + a wrongly-filed
blocker doc on a self-inflicted mistake.

## What happened

Called `ModifyEnum(enumName: "GitOperation", values: "show")` intending "add a `show` member",
per a mental model of "values = what to add". The tool's actual, correctly-documented contract
(see its `[Description]` and `RefactoringEngine.ModifyEnumAsync`'s doc comment) is "values = the
enum's complete final member list" - so this call meant "delete every other member, keep only
`show`". The resulting candidate enum (containing only `show`) correctly failed to compile against
`SentinelGitTools.cs`'s `Git()` dispatch switch, which still referenced all 13 other names -
producing 16 CS0117 diagnostics, one per now-deleted member's use site.

Two retries (different `lineAfter` anchor, then a full forced `LoadSolution` reload) produced the
byte-identical error both times, which looked like a stuck/stale-workspace tool bug rather than a
correct rejection of a genuinely destructive request - the error message gave no signal that the
requested change was "delete 13 members" rather than "add 1". Ended up writing and then deleting an
incorrect blocker doc before reading `ModifyEnumAsync`'s own doc comment, which states the contract
plainly and would have caught this on the first read.

## The actual gap

The error text list every downstream CS0117 by file:line, but never states the more useful, more
immediate fact: "this call removes members {list}, which are still referenced at {N} call sites."
That framing would have made the mistake obvious in one line instead of requiring 16 lines of
scattered CS0117s to be manually pattern-matched back to "oh, I deleted these."

## Possible environment fixes (not decided)

- Have `ModifyEnumAsync` (or the `ModifyEnum` tool wrapper) detect `removed.Count > 0` and prepend
  a summary line to any resulting validation failure: `"This call removes: {removed}. If you meant
  to ADD '{values}' without removing the rest, pass the full existing member list plus your
  addition."` - it already computes `removed` for the success-path `Message`; the same value is
  available on the failure path with no new computation.
- Alternatively (bigger change, not clearly better): split `ModifyEnum` into a set-based
  "replace entire list" (today's behavior, keep as-is for reorder/remove use cases) plus a smaller
  additive `AddEnumMember`/similar that only ever adds - closes the round-trip footgun entirely
  instead of just improving its error message. Trades one tool for a clearer name against one more
  tool to maintain; not evaluated against `feedback_prefer_mandatory_params_to_close_footgun_roundtrips`
  without more thought.

## Related

- Memory: `feedback_verify_before_theorizing_on_tool_errors` (this session's own miss - retried the
  identical failing call under two different staleness theories before reading the implementation).
- CLAUDE.md failure doctrine: the model (me, in this instance) was "plainly wrong" per the mental
  model mismatch, but the environment gap (error message doesn't name the implied removals) is the
  actionable half of the finding.
