# Reference: design review checklist for plans and proposals

**Status:** CURRENT 2026-11-24. Owner-set rules; verified against the skills that cite it.

## Purpose

The checks a design must pass before it becomes an APPROVED plan. Cited by the `plan-design` skill
(design review, test design, readiness gate) and by `implement-plan`'s review. Each item is a question
with a pass condition; a design that fails one either changes, gains an enabling step, or records an
owner-approved exception in the plan's `## Decision` log.

## How it works

### Shape and placement

1. **Named types, no tuples or anonymous types across boundaries.** A tuple or anonymous type used as
   a return, parameter, field or DTO property needs a named type, or an owner-approved exception in
   the Decision log. A local deconstruction or a LINQ projection that stays inside one method is fine.
   New or changed DTOs follow the `dto-conventions` skill (reuse first, `NEW-DTO:` justification).
   Pass: every boundary type the design adds is named, reused or approved.
2. **One definition per shared value.** Any value that appears in 2+ places, or that a test asserts
   on (error codes, hint text, limits, field names, env var names), has one definition (constant,
   enum, static member) that every user references. Pass: no value in the design is maintained by
   hand in two places.
3. **Reuse before writing.** Existing helpers, DTOs, constants and factories near the change are
   listed (grounding) and either used or rejected with a reason. A new shared helper goes in the
   lowest layer that *needs* it, not the lowest layer available. Pass: each new helper names what was
   checked and why it is placed where it is.
4. **Layering and size.** Layering is one-way (CLAUDE.md, Architecture). Tool classes stay thin: they
   validate, call an impl or engine, and map the result; the logic lives in the named impl or engine.
   Adding to a class over **1500 lines**, or adding new code to `Common`, needs a placement
   justification in the step or an extraction step first. Check for a plan IN PROGRESS that is
   splitting the target before adding to it. Pass: every new member has a named home that meets
   these rules.

### Change cost

5. **Blast radius now and next time.** Measure the radius of this change (callers of each changed
   member: `InspectSymbol(aspect: blastRadius)` or `Search(mode: references)`), and ask how many places
   the *next* change to this behaviour will touch. Wire-visible text (errors, hints, descriptions)
   comes from one source so a wording change is one edit and breaks no test. Hot-path changes
   (request pipeline, write chokepoint, per-call code) state their performance check. Pass: the next
   likely change to this behaviour touches one place.
6. **Structured information.** Results on the wire are structured (lists of DTOs with typed fields),
   never flattened or pre-serialized text (`reference_tool_response_pipeline.md`). In-process signals
   are typed too: a dedicated exception type or enum, never matching on message text. Pass: every
   piece of information a caller or test needs is a typed field.
7. **Simplicity.** State the simplest version of the design and what small change elsewhere would
   enable it. If that enabling refactor is worth doing, it becomes a text-neutral early step. Every
   option, parameter and config knob names a concrete user; a knob added to stop a known failure is
   mandatory unless a usage needs the fallback (CLAUDE.md). Pass: the plan records the simplest
   alternative and why the chosen design is not more complex than needed.

### Tests

8. **Key tests with pass criteria.** Each behaviour in the Decision log maps to at least one named
   test (class and method) with what it asserts and its pass criterion. Eval and live steps state
   their outcome classes (`reference_model_eval_procedure.md`, "Scoring design").
9. **Lightweight fixtures.** In-process fakes first (`FakeWorkspaceManager` in
   `RoslynSentinel.Tests/Fakes/`, not `PersistentWorkspaceManager`). A heavier fixture is justified
   in the step.
10. **No out-of-process work unless needed.** A test that runs `dotnet build`, `dotnet test`, git or
    another process is justified in the step (what it proves that an in-process test cannot).
11. **No text asserts.** Assert on structured fields (error code, enum, DTO property, count). This
    depends on items 2 and 6: a test can only avoid text if the output is structured and the value
    has one definition. A text assert is allowed only when the wording itself is under test, and then
    against the shared constant, never a literal.

The test table in a plan carries, per test: name, behaviour covered, fixture, asserts, pass
criterion, justification if heavy (items 9-10).

### Sequencing

12. **Parallel-ready slices.** Decided during design, not at write-up: the files each step touches,
    a contract skeleton first for new interdependent code, disjoint files for steps meant to pair,
    text-neutral steps before behaviour-changing ones (`plan-writer` skill, `templates/plan.md`).

## Gotchas

- Items 2, 6 and 11 are one rule seen from three sides; failing one usually means failing all three.
- Items 3 and 4 pull against each other: "reuse" drifts into dumping helpers into `Common`. Placement
  wins; a helper used by one engine stays in that engine.
- Item 5 is about the *next* change, not only this one; the measured caller count covers this one.
