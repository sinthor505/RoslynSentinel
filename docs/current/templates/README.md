# Doc templates

Canonical house style for every doc under `docs/current/`. Copy the matching template, fill it in,
delete the `<!-- -->` guidance comments. **Do not read existing docs to learn the style** -- they
predate this folder and vary; the templates are the source of truth. Open an existing doc only for
its content (related work, prior attempts).

## Genre -> folder -> filename

| Genre | Use for | Folder | Filename | Template |
| --- | --- | --- | --- | --- |
| blocker | A tool or compiler failure that stopped work | `blockers/` | `blocking_error_<slug>.md` | [blocker.md](./blocker.md) |
| finding | Discovered behaviour (often a defect); fix not decided | `findings/` | `finding_<slug>.md` | [finding.md](./finding.md) |
| proposal | A change we intend to make but have not built | `proposals/` | `proposal_<slug>.md` | [proposal.md](./proposal.md) |
| issue | A known open problem or unresolved question | `issues/` | `issue_<slug>.md` | [issue.md](./issue.md) |
| design | The worked design of something built or being built | `design/` | `design_<slug>.md` | [design.md](./design.md) |
| plan | Sequenced implementation steps | `plans/` | `plan_<slug>.md` | [plan.md](./plan.md) |
| idea | Speculative, not committed to | `ideas/` | `idea_<slug>.md` | [idea.md](./idea.md) |
| reference | Living how-it-works / where-things-live lookup | `references/` | `reference_<slug>.md` | [reference.md](./reference.md) |

Create the folder if it does not exist yet.

## Rules

1. **Slug:** lowercase snake_case, names the subject or symptom (`member_strips_utf8_bom_on_write`).
   No version suffix (`_v1`, `-v2`). Revise the doc in place; git keeps the history.
2. **Status line:** the first line after the title is
   `**Status:** <STATE> <YYYY-MM-DD>. <one sentence>`. Typical states: OPEN, DECISION, APPROVED,
   IMPLEMENTED, FIXED, SUPERSEDED. Update it when the state changes; there is no Status section at
   the end.
3. **Dates** are absolute (`2026-10-01`), never "today" or "last week".
4. **Evidence:** cite `file:line`, quoted error text, or a run/step/turn id. Verify each claim
   against the code rather than repeating a brief. Label anything untraced as a hypothesis.
5. **Framing:** per the CLAUDE.md failure doctrine, a model tripping over a tool is an environment
   defect. State what environment change fixes it, never "the model should have known".
6. **ASCII-only punctuation** (CLAUDE.md): `-`/`--`, straight quotes, `<=`/`>=`.
7. **Too small for a file?** A one-line open item belongs in `docs/current/TODO.md`.
8. **Lifecycle:** a fixed blocker moves to `blockers/resolved/` with a resolution note citing the
   commit hash. A finished plan stays in `plans/` with status IMPLEMENTED and its commit hashes.
9. **Legacy docs** (anything at the `docs/current/` root, `specs/`, or hyphenated/`-v1` names)
   predate these rules. Leave them in place; do not copy their layout.
10. **Maintaining this folder:** if the house style changes, change the template first.
