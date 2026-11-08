# RoslynSentinel Documentation Index

**Last Updated:** 2026-11-08

Filing rules, filename prefixes and the Status-line format live in [templates/README.md](./templates/README.md).
Copy the matching template before writing a doc.

## Layout of `docs/current/`

The root holds only three files:

| File | Purpose |
| --- | --- |
| [TODO.md](./TODO.md) | Open items only |
| [CLOSED.md](./CLOSED.md) | Record of fixed and resolved items (older months are in `archive/closed_*.md`) |
| [DOCUMENTATION_INDEX.md](./DOCUMENTATION_INDEX.md) | This index |

Every other doc lives in a genre folder. Each genre folder has a `closed/` subfolder for history (done,
superseded or abandoned); a doc moves there when its Status line reaches a closed state.

| Folder | Filename | Holds |
| --- | --- | --- |
| [blockers/](./blockers/) | `blocking_error_<slug>.md` | A tool or compiler failure that stopped work |
| [findings/](./findings/) | `finding_<slug>.md` | Discovered behaviour, often a defect; fix not decided |
| [proposals/](./proposals/) | `proposal_<slug>.md` | A change we intend to make but have not built |
| [issues/](./issues/) | `issue_<slug>.md` | A known open problem or unresolved question |
| [design/](./design/) | `design_<slug>.md` | The worked design of something built or being built |
| [plans/](./plans/) | `plan_<slug>.md` | Sequenced implementation steps |
| [ideas/](./ideas/) | `idea_<slug>.md` | Speculative, not committed to |
| [references/](./references/) | `reference_<slug>.md` | Living how-it-works and where-things-live lookups |
| [evals/](./evals/) | `eval_<slug>.md` | Model-eval analyses, run reports, PlanStepRunner step sets and run outputs |
| [templates/](./templates/) | - | One template per genre, plus the house-style README |
| [archive/](./archive/) | - | Frozen pre-taxonomy docs that fit no genre, and the monthly `closed_*.md` rollups |

Generated tool and project tables are in `docs/generated/`; the known-failing-test and build-warning
baselines are `docs/known-*.txt`.

## Where to start

- What is open: [TODO.md](./TODO.md); what is already fixed: [CLOSED.md](./CLOSED.md).
- How the server is put together: [references/reference_architecture_map.md](./references/reference_architecture_map.md).
- Working conventions (build, docs, text, scripts): [references/reference_working_conventions.md](./references/reference_working_conventions.md).
