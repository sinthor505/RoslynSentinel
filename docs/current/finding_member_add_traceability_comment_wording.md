# Finding: Member(add)'s auto-inserted traceability comment reads as member documentation, not tool provenance

**Status:** confirmed tool defect, not yet fixed.

## What's broken

`RoslynSentinel.Common/ContextHelper.cs:512-519` (`WithAddedByComment`) inserts a comment above
every member synthesized via `Member(add)`, e.g.:

```csharp
// Added by AddMember (expected - used for diagnostics)
public static EngineResultWrapper<T> Failure(...) => new(...);
```

The phrase "(expected - used for diagnostics)" reads as if it's describing the *member's* purpose
("this method is expected to be used for diagnostics") rather than the comment's own intent
(tool-provenance tracking, per the tool-attribution idea this implements). On review or in a diff,
a reader has to infer this is auto-generated metadata, not real documentation the author wrote.

## How to reproduce

Call `Member(add)` to insert any new member. The generated source is prefixed with the
`WithAddedByComment` line above whatever `newMemberSource` was actually passed.

## Impact

Confirmed reproducible across at least two separate runs: server logs from 2026-09-08, and again
in PlanStepRunner run `20260910-083738-402` step `02-phase1-types-and-engine-fix` (the
`EngineResultWrapper<T>.Failure` helper added via `Member(add)`). Not model error — the model's
`newMemberSource` argument was clean single-line source in both cases; the confusing text is
entirely tool-injected.

## How to apply

When reviewing a diff that used `Member(add)`, don't attribute this comment's wording to the
model. If fixing, reword to unambiguous provenance language, e.g.
`// Added by {toolName} — inserted by an automated MCP tool call`, and consider making the comment
optional per call rather than always-on.
