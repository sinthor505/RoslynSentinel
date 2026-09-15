# Finding: an ambiguous plan instruction made a model oscillate 12+ times, then silently pick the wrong reading — and a log-analysis subagent flattened that into a false "correctly identified" summary

**Status:** confirmed, root-caused to source. Code gap left behind (`ReplaceSnippetBatch` still
strict-only) and the plan-doc wording are both open follow-ups, not fixed by this doc. The
log-analyst gap (item 6 below) has already been fixed.

## What happened

`docs/current/plans/plan_replacesnippet_whitespace_tolerant_match.md` asked a model to switch
`ReplaceSnippet` from `ContextHelper.FindExactSnippetPosition` to
`ContextHelper.FindSnippetPositionWithLength`, then in step 2:

> "Do the same for `ReplaceSnippetBatch` if it has its own separate call to
> `ContextHelper.FindExactSnippetPosition` — search the file for all occurrences of
> `FindExactSnippetPosition` and update every call site that removes/replaces text using
> `match.Length`. Do NOT change any call site that only *locates* a position without removing
> text."

`ReplaceSnippetBatch` (`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs:887`) calls
`FindExactSnippetPosition` once per edit and stores the result in a tuple,
`(int index, ContextHelper.SnippetMatch match, string newContent)` (line 882). That single
`SnippetMatch` is read again later — for overlap detection at lines 905-918 (`r.match`,
`prev.match`, `curr.match`) and for the actual splice at line 931
(`spliced.Remove(match.Start, match.Length).Insert(...)`, via a deconstructed
`foreach (var (_, match, newContent) in byStart.OrderByDescending(...))`). There is one match
value per edit, computed once at line 887, threaded through a tuple into both the overlap check
and the removal — not two independent computations.

The instruction's "only locates... without removing" test has two readings for this call site,
and they disagree:

- **Syntactic** ("does `.Length` appear on this call's own source line?") — line 887 fails this
  test; no `.Length` there.
- **Data-flow** ("is the returned value's field eventually consumed by a removal, even through an
  intermediate container?") — line 887 passes this test; `match.Length` from this exact value is
  used to remove text at line 931.

The plan author intended the data-flow reading (per the plan's own framing: "update every call
site that removes/replaces text using `match.Length`" — line 887's `match` *is* used that way,
just not on the same line) but wrote text that a careful reader can equally validly parse as the
syntactic one.

## The model's behaviour: correct tracing, then arbitrary tie-break

Transcript: `lmstudio_logs/plan_replacesnippet_whitespace_tolerant_match.md - 2026-09-15 09.11.md`
(model: qwen3.5-9b-coder). The model repeatedly re-derived the *correct* data-flow fact and then
talked itself back to the syntactic reading, at least 12 times across the transcript:

- Line 241: "So line 887 is just locating for overlap detection. It doesn't remove text directly.
  The actual removal happens later at line 934 using `match.Length` from the match object
  collected at line 887."
- Line 247: "So the call site at line 887 does participate in a removal chain... this one DOES
  ultimately lead to a Remove using `match.Length` from it. So I think both should be changed."
- Line 340: "It's not just locating without removing — it's actively used for removal later."
- Line 429/441/488/538/581 (five separate re-derivations of the same "only locates... I should
  NOT change line 887" conclusion, each re-quoting the identical plan sentence as justification).
- Final decision at line 589: "Do NOT change line 887 (ReplaceSnippetBatch overlap detection) —
  it only locates positions for overlap checking; doesn't remove at the call site."

The model never resolved *which test the plan meant* — it kept re-running both tests, getting
different answers, and eventually anchored on whichever one it re-derived last. Build and
`RunTest` both passed afterward (see below), so nothing downstream caught the inconsistency: it
now shipped `ReplaceSnippet` whitespace-tolerant and `ReplaceSnippetBatch` still strict-only, the
exact split the plan's own step 2 said to avoid.

## Contributing factor: a nearby comment about a different mechanism, in the same corruption vocabulary

The overlap-detection block the model was staring at has this comment directly above it
(`SentinelWorkspaceTools.cs:901-904`):

```
// Reject overlapping matches before splicing anything — silent last-writer-wins here is
// the same corruption class as the closed ReplaceSnippet silent-splice-corruption finding
// on adjacent lines (wrong match length previously corrupted a neighboring line with no
// error at all).
```

This comment is about the overlap-check logic, not about which matcher function is called. But it
sits a few lines below the `FindExactSnippetPosition` call at line 887, and reuses the exact
corruption-incident vocabulary ("wrong match length... corrupted a neighboring line") that the
plan's own Background section uses to describe the *matcher-choice* bug. A skim reads this as "the
fix for that incident already lives here" — it doesn't; it addresses a different mechanism
entirely (rejecting overlapping edits vs. which matcher computes the match).

## Contributing factor: one name, three scopes, one continuously-threaded value

`match` is used as: a try-scoped local at its point of computation (line 887), a tuple element
name (line 882, `ContextHelper.SnippetMatch match`), and a deconstructed loop variable in two
separate `foreach` loops (`r.match`/`prev.match`/`curr.match` via LINQ, and
`foreach (var (_, match, newContent) in ...)` at line 929). All three refer to one value computed
once per edit — but the repeated bare name reads like three independent bindings unless the reader
already knows the tuple is carrying it through. This slowed a human reviewer down for the same
reason it fed the model's confusion: nothing in the naming signals "this is one value threaded
through a container," so each new appearance of `match` invites re-asking "where did *this one*
come from."

## Verification gave a false sense of completeness

The model ran `Build` (0 errors) and `RunTest` (all passing), including a new test it added,
`FindSnippetPositionWithLength_AdjacentSimilarLines_NoSpliceFragmentOnReplace`
(`RoslynSentinel.Tests/ContextHelperTests.cs`), mirroring the existing
`FindExactSnippetPosition_AdjacentSimilarLines_NoSpliceFragmentOnReplace` (~line 326). Both tests
call `ContextHelper` methods directly. Neither before nor after this change did any test exercise
`ReplaceSnippetBatch`'s own splice behavior for the adjacent-similar-lines corruption class its own
code comment names. The test suite could not have caught the inconsistency regardless of which
reading the model picked — "all green" here proves the tests' own (incomplete) coverage, not that
the task was done correctly. This was initially treated as sufficient proof of correctness by the
model, by a supervising subagent reviewing the transcript, and initially by the assistant in this
session, before a human pushed back.

The plan's own verification section made this worse by including a non-operational step:

> "Manually sanity-check: call `ReplaceSnippet` against a real file using an `oldContent` value
> that has slightly different indentation than the file's actual on-disk text... and confirm it
> now succeeds..."

No file, no fixture, no concrete input/expected output — the model was left to invent both the
input and the standard for judging the result, for a check that produces no artifact a reviewer
can later confirm was actually run correctly.

**This omission is worse than a generic missing fixture: the exact scenario was already known and
already reproduced.** The plan's own Background section names the precise failure mode
(`project_replacesnippet_silent_splice_corruption_adjacent_lines`), and
`ContextHelperTests.cs`'s existing `FindExactSnippetPosition_AdjacentSimilarLines_NoSpliceFragmentOnReplace`
test *is* a working, exact input/output pair for it — the same `BuildResult`-record source, the
same `oldContent`/`newContent` strings, the same adjacent-line corruption assertions. There was
nothing left to invent. The plan should have handed that literal fixture to the model as the
required input for the `ReplaceSnippetBatch` case (as the rewritten plan's step 3b now does),
instead of pointing at it once for context and then asking for a free-form "manually sanity-check...
with slightly different indentation" step that reproduces nothing in particular. Writing "go verify
this" when a ready-made repro already exists in the same file is a strictly avoidable gap, not an
unavoidable planning cost.

**This is a role-boundary failure, not just a shared bad habit.** In this workflow, test adequacy
is not the implementer's job to judge at all — it is the planner's job to *define* (what hazard
must a test exercise, what code path must it touch) and the reviewer's job to *confirm* (does the
test the implementer wrote or ran actually cover that hazard). The implementer's job stops at
"get a green build and pass the tests it was given or asked to write." An implementer that gets
green on an inadequately-specified test suite has correctly done its job — the defect is upstream,
in whoever specified or accepted that suite as sufficient. Absent a failing test, the implementer's
code is *defacto* correct **from the implementer's vantage point**; treating that same absence of
red as proof of correctness from the *planner's* or *reviewer's* vantage point is the mistake this
incident exhibits at every level: the model didn't audit its own test's coverage (correctly not its
job), and neither the plan nor the reviewing subagent supplied or checked for that coverage either
(incorrectly not doing their job). Assigning coverage adequacy to the wrong role is what let a gap
survive an all-green run undetected.

## Log-analysis gap (already fixed)

A `model-eval-log-analyst` pass over this transcript reported that the model "correctly identified
[line 887] only locates positions for overlap detection... so per the plan's instruction it was
left alone" — flattening 12+ explicit reversals into a single confident, incorrect
characterization. The subagent's brief at the time asked only for outcome-shaped facts (pass/fail,
errors, deviations), with nothing prompting it to notice reasoning friction on an otherwise-green
run. `.claude/agents/model-eval-log-analyst.md` has since been updated (see its "Friction is not
gated on failure" and "No visible failure is not 'nothing to report' for test/verification steps"
sections) to require reporting reversals/oscillation, dual-reading instructions, and verification
adequacy independent of pass/fail. Not re-proposed here; already done.

## Durable lessons

**A. An instruction that admits two non-equivalent readings will eventually get the wrong one
applied, even by a model reasoning carefully — this is an environment defect, not a model
failure**, per `CLAUDE.md`'s failure doctrine and root-cause discipline. There is no way to choose
correctly between two valid readings using the text alone; getting the "right" one on any given run
is luck, not comprehension. When writing a call-site-selection instruction, write it so only one
test is applicable — e.g., "trace where the returned value's fields are eventually consumed, even
through an intermediate collection/tuple/loop variable — do not judge by whether `.Length` appears
on the same source line as the call itself." Naming the *wrong* test explicitly, not just the right
one, closes off the reading you don't want.

**B. A model executing a plan under genuine ambiguity should surface the ambiguity, not silently
tie-break it.** When an instruction admits two plausible, non-equivalent readings that change the
resulting code, silently picking one after internal deliberation (even correct deliberation) hides
the decision from review. Plans and prompts given to models should carry a standing instruction
along the lines of: "If a step admits more than one reasonable interpretation, stop and report the
ambiguity, which interpretation you chose, and why — as a comment, a note in your change
description, or a message to the caller — rather than silently picking one." This turns an
arbitrary, invisible tie-break into something a reviewer can catch before it ships.

**C. Passing tests prove only the tests' own coverage, never correctness of the change — and
confirming that coverage is the planner's/reviewer's job, not the implementer's.** "Build
succeeded, tests passed" is not sufficient verification on its own; someone must positively
confirm the tests exercise the specific hazard the task is about. A green run against inadequate
coverage is indistinguishable from a green run against a correct change, which makes it worse than
a red run — a red run at least tells you something is wrong. Assign this by role, explicitly:
  - **Planner:** defines what hazard/behavior a new or modified test must exercise (not just "add
    a sibling test") — including naming the specific code path, not merely a function that happens
    to share a helper.
  - **Reviewer:** confirms, before or after the fact, that the tests actually cover what the plan
    asked for — checking coverage is what a review *is*, not an optional extra pass.
  - **Implementer:** gets a green build and passing tests against whatever it was given or told to
    write. In the absence of a failing test, the implementer's code is correctly treated as done
    *from the implementer's seat* — it is not the implementer's job to also second-guess whether
    the test suite it was handed (or asked to extend "in the same style as an existing test") is
    sufficient. That is a different skill (design/review), not a missing diligence step.

  Collapsing these three into "someone should have checked" is exactly how this gap shipped
  unnoticed: the model correctly did its (implementer) job, and no one was doing the planner/
  reviewer job of confirming the resulting suite actually covered `ReplaceSnippetBatch`.

**D. A "manually verify" step with no concrete fixture or input is not real verification and
should not appear in a plan given to a model.** If a workflow's checking step is meant to be
consumed by anything other than a human eyeballing a terminal in real time, it must produce a
pass/fail signal on its own — name an existing fixture/file and exact input/expected output, or
specify exact literal strings, or drop the step in favor of an automated test. "Try it and see"
leaves the model to invent both the input and the standard for judging the result.

**E. Comment/name proximity can prime the wrong reading even when the comment is factually correct
about something else.** A safety comment describing one mechanism (overlap rejection), placed near
but not about a different mechanism (matcher choice) it happens to share vocabulary with, reads to
a skimming model as covering both. When two related-but-distinct safety mechanisms sit near each
other in source, state explicitly in the comment which one it does and does not cover.

**F. Reusing one bare name across nested scopes that thread a single value through a
tuple/record/loop is a readability hazard independent of correctness.** Both the model and a human
reviewer were slowed by `match` appearing as a try-scoped local, a tuple field, and two separate
deconstructed loop variables, all holding one value computed once. When a value is captured into a
tuple/record for later use across a loop or method boundary, give the tuple field (and any later
deconstructed bindings) a name distinct from the originating local, and/or add a one-line comment
— "one value threaded through, not independently computed at each read site" — when this pattern
recurs.

**G. A cheap first-pass log-analysis step must surface friction even on a fully passing run.** This
is what let A-F go unnoticed until a human pushed back twice — the analysis brief only asked for
outcome-shaped facts, so 12+ reversals on one decision produced no flag because nothing failed.
Already fixed in `.claude/agents/model-eval-log-analyst.md` (see above); do not re-propose.

## Open follow-ups (not designed here)

- `ReplaceSnippetBatch` (`SentinelWorkspaceTools.cs:887`) still calls `FindExactSnippetPosition`
  and is strict-only, inconsistent with `ReplaceSnippet`'s whitespace-tolerant behavior. Needs its
  own implementation task.
- `plan_replacesnippet_whitespace_tolerant_match.md`'s step 2 wording needs the rewrite described
  in lesson A before it (or a similar future plan) is handed to a model again.

## Reference

- Plan: `docs/current/plans/plan_replacesnippet_whitespace_tolerant_match.md`
- Transcript: `lmstudio_logs/plan_replacesnippet_whitespace_tolerant_match.md - 2026-09-15 09.11.md`
  (lines 241, 247, 340, 429, 441, 488, 498, 538, 581, 589 for the oscillation)
- Source: `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs:698` (`ReplaceSnippet`'s changed
  call), `:860-935` (`ReplaceSnippetBatch`), `:882` (tuple declaration), `:887`
  (`FindExactSnippetPosition` call), `:901-904` (overlap-check comment), `:905-918`
  (overlap detection), `:931` (splice)
- Tests: `RoslynSentinel.Tests/ContextHelperTests.cs` —
  `FindExactSnippetPosition_AdjacentSimilarLines_NoSpliceFragmentOnReplace` (~line 326),
  `FindSnippetPositionWithLength_AdjacentSimilarLines_NoSpliceFragmentOnReplace` (added this run)
- Already-fixed related gap: `.claude/agents/model-eval-log-analyst.md` ("Friction is not gated on
  failure" / "No visible failure is not 'nothing to report'" sections)
- Related, distinct incident: `project_replacesnippet_silent_splice_corruption_adjacent_lines`
  (memory) — the original matcher-length corruption bug that motivated the strict-only matcher in
  the first place; this finding is about the *plan instruction* that governed migrating off it, not
  a recurrence of the corruption itself.
