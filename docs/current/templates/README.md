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
| eval | Model-eval analyses, run reports, PlanStepRunner step sets and run outputs, eval research | `evals/` | `eval_<slug>.md` (a step set is a folder `eval_<slug>/`) | [finding.md](./finding.md) |

Every genre folder has a `closed/` subfolder for history (done, superseded or abandoned). Create a folder
if it does not exist yet. `docs/current/archive/` holds only frozen pre-taxonomy docs that fit no genre
and the monthly `closed_*.md` rollups; do not add new docs there.

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
8. **Lifecycle:** when a doc reaches a closed state (a fixed blocker, an IMPLEMENTED plan or proposal,
   a FIXED or SUPERSEDED finding, a closed issue, a finished eval), set the Status line (with the commit
   hashes) and move it to `<genre>/closed/` (`blockers/closed/`, `plans/closed/`, `evals/closed/`, ...);
   a fixed blocker also gets a resolution note citing the commit hash. Fix relative links when it moves.
9. **`docs/current/` root** holds only `TODO.md`, `CLOSED.md` and `DOCUMENTATION_INDEX.md`. Every other
   doc lives in its genre folder under the filename rules above (no hyphenated or `-v1` names, no
   `specs/`, `obsolete/`, `research/` or `testing/` folders).
10. **Maintaining this folder:** if the house style changes, change the template first.
11. **Commit trailers:** the doc ID is the filename without its extension (`plan_semantic_find_replace`).
    A commit that advances a plan, proposal, finding or issue doc carries a `Plan: <id>` trailer; the
    commit that finishes it carries `Closes: <id>` (put both above `Co-Authored-By`). Only `Closes:` means
    done; plans land in slices, so `Plan:` alone means in progress. Do not put IDs in code comments. On
    closing, update the doc's Status line, move it to its folder's `closed/` and move its TODO.md entry to
    CLOSED.md. The report-only `TRAILER` check in `scripts/Test-GuidanceHygiene.ps1` (run with `pwsh`) and `ProjectDoc`
    (`action: trailers` lists docs with their `Plan:`/`Closes:` commits, closure candidates and unknown IDs;
    `validate` checks one ID; `close` with `name` = the ID and `content` = the new Status text sets the Status
    line, moves the doc to `closed/`, adds the CLOSED.md entry (moving its TODO.md line when one links to the
    doc) and fixes relative links) do this for you (see
    [idea_doc_id_commit_trailers_and_reconciliation.md](../ideas/closed/idea_doc_id_commit_trailers_and_reconciliation.md)).
