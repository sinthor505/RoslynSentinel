---
name: project_directive_error_messages_wiggle_room_theory
description: "Three tool error messages (ReadFile-family 'not found', orientation breaker tripped, CS0103 out-of-scope-member) rewritten from hedged/suggestive to direct 'You MUST' phrasing, based on run-2 of a PlanImplementVerify batch where the model retried the same wrong fix 5 times despite the message already naming the correct one"
metadata:
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
---

## What happened

Run 2 of a 5-run `PlanImplementVerify` batch against `.113` (post-[[project_dispose_waithandle_deadlock_found]]
fix, MTP disabled) failed on the mechanical `errorTools.Count <= 2` check — 6 failed tool calls,
not the "self-correction, 1-2 retries" pattern the check is meant to allow. The transcript
(`ModelTestingResults/113/.../20260902-052529-399/implement/agent.log`) showed why: at turn 4, a
CS0103 error already correctly named the fix —
`BlockEditHelpers.ReplaceBlockFormatted (BlockEditHelpers.ReplaceBlockFormatted(string, string, string))`
— but the model spent turns 5-14 trying `using ContosoOrders.Core.FixtureHelpers;` (redundant,
same namespace the file already declares), undoing it, re-adding it, and re-hitting the identical
CS0103 four more times before finally applying the qualified-name fix at turn 15.

The old message (`CompilerErrorLookupHelper.cs`'s CS0103 branch) read: *"exists elsewhere in the
solution but is not in scope here — most likely it needs to be called with its containing type as
a qualifier (a `using` directive does not import another class's static members...)"*. The user's
hypothesis: mentioning `using` at all — even to rule it out — may amplify the model's attachment
to that tool/approach rather than steering it away, because the model's own attention now includes
that token regardless of the surrounding negation.

Two other messages showed the same shape of problem on inspection, both hedged/suggestive rather
than directive:
- `ReadFile`/`GetMethodSource`/`GetFileOutline`'s "file not found" error gave no path forward — run
  5 of the same batch retried an identical wrong path 3 times before the model gave up on that
  angle (though that run's actual failure was an unrelated LM Studio streaming bug, not this).
- The orientation breaker's tripped message said allowlisted tools were "available... until one of
  them succeeds" rather than stating outright that `SearchSolutionText` is disabled.

## The fix

Three messages rewritten to direct, unhedged "You MUST" phrasing, avoiding naming any
tool/approach that isn't the intended fix:

1. **ReadFile-family not-found** (`SentinelWorkspaceTools.cs`, new `BuildFileNotFoundError` helper
   shared by `ReadFile`, `GetMethodSource`, `GetFileOutline`): searches the solution for Documents
   sharing the requested filename. If found, names the real path(s) directly and says "You MUST
   retry with the correct path." If no filename match anywhere, says "You MUST call
   ListSolutionItems(kind: all) next" instead of just reporting absence.
2. **Orientation breaker tripped** (`PersistentWorkspaceManager.cs`'s
   `IAutomaticCircuitBreaker.StateMessage()`): now opens with "SearchSolutionText is DISABLED..."
   instead of "Only X, Y, Z are available," and says "You MUST call ListAll(kind: all) or
   ListSolutionItems(kind: all) now" instead of "to find what you're looking for by browsing
   instead of guessing."
3. **CS0103 out-of-scope member** (`CompilerErrorLookupHelper.cs`'s `DescribeCs0103Async`): the
   branch matching run 2's exact scenario (candidate found, but not via a missing `using`) now
   reads "You MUST call it using the fully qualified name shown below (ContainingType.MemberName)
   — do not add a `using` directive, it will not fix this." The one deliberate exception to
   "never name the wrong approach": this message names `using` once, explicitly to rule it out,
   because leaving it unaddressed risked the model reaching for it anyway (as it just had, 5
   times) — judged worth the small re-amplification risk. The sibling branch (genuinely-missing
   namespace, where `using` *is* the correct fix) was left mentioning `using` since that's the
   intended action there, just tightened to "You MUST add one of the following."

## Verification

- `dotnet build RoslynSentinel.slnx -c Debug` → 0 errors.
- `RoslynSentinel.Tests.Battery` filtered to `ReadFileTests|GetMethodSourceTests|OrientationBreaker`:
  21/22 passed. The one failure (`ReadFile_LargerThanThreshold_OffloadsAndReturnsLargeResultInfoAsync`)
  confirmed pre-existing and unrelated by re-running against unmodified `master` via `git stash` —
  fails identically there.
- `OrientationBreakerFilterTests.cs`'s `Does.Contain("Orientation breaker")` assertion was checking
  the old message's own internal-jargon phrasing, not behavior — updated to
  `Does.Contain("SearchSolutionText is DISABLED")` to match the new wording; still passes.
- `RoslynSentinel.Tests`' `CompilerErrorLookupHelperTests`: 2/2 passed (neither asserts on the
  exact CS0103 branch text changed here, but nothing else regressed).

## How to apply — the wiggle-room theory itself is not yet confirmed

This fix ships on the strength of one transcript's evidence (run 2's 5x `using`-directive retry
loop) plus the user's independent read of the same pattern. It has NOT been validated with a
controlled before/after batch — the 3/5 pass rate from the batch that surfaced this (before these
message changes existed) can't be attributed to the new wording since the new wording wasn't live
yet. If a future `PlanImplementVerify` batch shows CS0103/orientation-breaker/file-not-found
retries dropping (or `errorTools.Count` failures on this specific pattern disappearing), that's
supporting evidence for "directive > hedged, and don't name the wrong tool." If retries persist
despite the new wording, the wiggle-room theory specifically (as opposed to "directive phrasing
helps generally") would be undermined — worth tracking as a distinct question from whether the
rewrite helped at all.
