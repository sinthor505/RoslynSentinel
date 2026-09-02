---
name: feedback_always_writeup_cs_error_designs
description: "Whenever a model-eval failure traces to a specific unhandled CS#### diagnostic, always write up a CompilerErrorLookupHelper design doc for it (like project_cs0122_lookup_helper_proposal / project_cs0138_lookup_helper_proposal), even for narrow single-run evidence"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-01T09:35:05.812Z
---

Always write a short design doc proposing a `CompilerErrorLookupHelper` branch whenever a
model-eval failure traces to a specific CS#### diagnostic ID the helper doesn't yet handle — don't
wait for a pattern across multiple runs or a large batch.

**Why:** these fixes are cheap. [[project_cs0122_lookup_helper_proposal]] and
[[project_cs0138_lookup_helper_proposal]] both follow the same small shape: regex-capture a symbol
or type name out of Roslyn's already-precise diagnostic text, optionally look it up via
`SymbolNavigationEngine.LocateSymbolAsync`, and turn a "here's what's wrong" diagnostic into a
"here's exactly what to change" imperative. CS0138 in particular needed no symbol lookup at all —
just reformatting Roslyn's own "consider using static" hint into an unambiguous two-option
instruction. The cost of writing the design doc is low (one existing pattern to extend), the
implementation cost is low (a few lines in one file, one regex, one branch), and the payoff is
concrete: the CS0122 doc was implemented in a separate session and confirmed live, fixing exactly
the failure mode it targeted with zero repeat occurrences afterward
([[project_cs0122_fix_confirmed_2run_batch]]).

**How to apply:** the moment a model-eval transcript trace turns up an unhandled CS#### diagnostic
as a contributing cause — even from a single run, even as one of several errors in that run, even
if it isn't the majority failure mode — write the design doc immediately using the established
shape (Motivation with transcript evidence, Proposed fix reusing the existing regex/lookup pattern,
Output shape example, Assessment scoped honestly to just the observed failure mode, Open items).
Don't wait for a batch-level pattern to justify it; the low cost means single-run evidence is
sufficient justification on its own.
