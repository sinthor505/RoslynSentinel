# One coordinator session for the owner, pinned eval builds on a multi-host queue, and file claims for parallel sessions

**Status:** PROPOSED 2026-11-12. Revised after a review of six concurrent session transcripts. The
single-host queue (`scripts/roslynsentinel-evalqueue.ps1`, c4b18a78) is built and in use; everything
else here is unbuilt. Supersedes the 2026-11-11 draft (per-host queue plus ranks, intent board and an
on-demand arbiter).

## Motivation

The owner runs about six Claude sessions on one working tree and three LM Studio hosts. Three costs
remain after the peer protocol (`.claude/skills/peer-coordination/SKILL.md`) and the single-host queue:

1. **The owner juggles questions from several sessions.** They are the only one holding the whole
   picture, so an answer to one session can disturb another that is working on related files or runs.
2. **Edit scheduling conflicts** between sessions.
3. **Eval scheduling conflicts:** several sessions want the same test, the same test at different
   commits, or different tests. Hosts: 113 (fastest), 112 (slow), 85 (slowest), one generation each.

### Evidence: transcript review, 2026-11-12

Six main-session transcripts (about 21 MB; ids `eb1f4d05`, `47174bf0`, `05ca34e2`, `ce0dc57f`,
`7918919b`, `621faaba`) were catalogued by three read-only agents: about 60 episode rows, fewer
distinct episodes because many appear from both sides. Times below are UTC on 2026-11-12.

- **Peers settled about two thirds of episodes alone; the owner was involved in about ten.** No write
  was refused by a stale read, and there were no build-lock errors (`MSB3027`, `CS2012`) in any of the six.
- **Freezing the tree for an A/B comparison is the largest cost.** Eval jobs build from the shared
  working tree, so a session measuring a change holds files:
  - one hold covered 10 files for an estimated 1.5 to 2 h (from 23:03) and froze three sessions;
  - two holds overlapped on `WorkspaceFileEditImpl.cs` (23:04 and 23:11);
  - an earlier hold lasted 89 min (16:14 to 17:44);
  - a session that queued five runs with `-OnTreeChange RunAnyway` recorded that "the five didn't
    test identical code".
  The scenario-evals session avoided a freeze at 16:21 by building from its own worktree through the
  queue's `-RepoRoot` seam.
- **Holds arrive late or miss work in flight.** A commit landed 71 s before the hold that forbade it;
  the two owners then chose different workarounds (pin at an old ref against revert in a worktree).
- **The owner gates answers and gets asked about peer data.** In one session, 5 of 12 owner questions
  were driven by peer data, with waits from 33 s to 12 min. Peer replies wait 4 to 10 min behind an
  open owner question. One plan was given to two sessions, each holding different owner answers; the
  merge cost 23 min of held edits plus another owner question.
- **Eval host and model state.** A model unloaded from 113 mid-run lost 17 of 20 samples (about
  22:45). A 5 s probe made 112 look down, so all work went to 113. A peer's "my A/B has started" was
  false; the queue's `status` showed it. One job waited about 45 min in the queue.
- **Most traffic has no protocol verb.** About 60% of messages were free-form: ETA questions, "send me
  the hash of your next change", failure reports, "does my change disturb your baseline". Failure
  reports went to guessed owners; two of four were already fixed (4 and 22 min earlier) because the
  reporter tested an older worktree ref. An ownership question woke four sessions to find one owner.
  Only one session used the `PEER <VERB> from` prefix consistently; one never loaded the skill.

## Proposal

Three parts, one per owner goal. Decided: all owner questions go through the coordinator (part 1).

### 1. Coordinator session: the owner's single point of contact

One long-running session whose job is to track every session's work and to bring the owner every
question, with a recommendation that weighs all sessions. The owner talks only to it.

**Decides vs advises**
- The owner decides everything that involves priority, scope, design or model choice. The
  coordinator recommends.
- The coordinator applies fixed rules on its own: ordering of eval jobs, deduplication, routing a
  failure report to the owning session, telling a session a file it wants is claimed.

**Questions**
- Worker sessions never ask the owner directly. A question goes to the coordinator by `SendMessage`
  (first line `QUESTION from <session>: <one sentence>`, then context, options and the files or runs it
  affects). The worker continues with any work the answer does not affect.
- Enforcement: a `PreToolUse` hook refuses `AskUserQuestion` in worker sessions and names the
  coordinator. The coordinator session is exempt (identified by an environment variable or a marker
  file written at its start).
- The coordinator batches open questions for the owner. Each carries an impact line built from the
  board (part 2): which other sessions' claims, slices or eval jobs the answer touches, and its
  recommendation.
- Each answer is written to the decision log and sent to every affected session, not just the asker.
  A second session cannot get a different answer to the same question.
- If the owner types into a worker directly, that worker writes the decision to the decision log.

**State lives in files, not in the coordinator's context**
- Under a shared folder outside the repo (default `%LOCALAPPDATA%\RoslynSentinel\coord\`): `board\`
  (one file per session), `questions\` (one file per open question), `decisions.md` (append-only).
- The coordinator reads them when it needs them, so it stays thin, survives compaction, and a fresh
  coordinator can take over by reading them. Workers keep working while it is busy or restarting.

**Optional:** a Claude Code panel beside the chat that renders the board, open questions and the eval
queue (plugin; see the `plugin-authoring` skill).

### 2. Edit scheduling: pinned eval builds first, then claims

**2a. Pinned eval builds (removes measurement freezes).** Every eval job builds a specific commit in its
own worktree (part 3), never the shared tree. A session measuring a change commits it (or names a
commit) and is unaffected by later edits, so holds for A/B comparisons are no longer needed.

**2b. File claims on the board.**
- Each worker keeps its board file current: plan, current slice, files it will touch in the slice,
  eval jobs, open questions, ETA.
- Before a slice, a worker claims the slice's files (plans already list them per step). A clash with
  another claim is visible at once; the worker tells the coordinator, which applies the rules or asks
  the owner.
- Release is automatic on commit (the claimed files are committed) or explicit.
- Ownership questions are answered from the board, not by broadcast. Failure reports go to the
  claimant, after the reporter re-runs the failing test at HEAD.
- Commit notices become board updates; "send me the hash of your next change to X" becomes a watch on
  X recorded on the board, which the coordinator fires.

**2c. Server-enforced claims (only if 2b is ignored in practice).** The MCP write path refuses a write
to a file claimed by another session, naming the claimant. Each window's subagents use that window's
server, so this also stops subagents already mid-task, which a HOLD message does not reach today.

### 3. Eval scheduling: one queue across hosts 85, 112 and 113

Extends `scripts/roslynsentinel-evalqueue.ps1` from one queue per host to one global queue.

- **Job key:** commit, test, arguments, `ROSLYNSENTINEL_*` variables, model and host. Same key attaches
  to the existing job and its results (as today). Different commit is a different job.
- **Pinned builds:** at dispatch the worker makes (or reuses) a worktree at the job's commit under the
  queue root and builds it once into its own artifacts folder; every job at that commit reuses the
  build. Uncommitted changes are not testable through the queue; the session commits first (a
  throwaway commit is fine). This replaces `TreeChanged` and `-OnTreeChange RunAnyway`.
- **Host choice per job:** a named host, or a tier: `fast` (113), `any` (first free host whose model
  matches). The scheduler records duration per test and host and uses it for estimates.
- **Comparison groups:** all arms of an A/B carry one group id, run on the same host and model, and are
  interleaved (A, B, A, B), so host speed or a model change cannot bias the comparison.
- **Model check:** before each job the worker reads the host's loaded models
  (`/api/v1/models`, `loaded_instances`). It dispatches only to a host that has the job's model loaded,
  and never loads or unloads models. A job no host can serve becomes an owner question through the
  coordinator, instead of burning samples.
- **Host health:** each host pauses independently when it stops answering (probe with a longer timeout
  than 5 s); `any` jobs move to the other hosts.
- **Priority:** set by the coordinator from owner decisions (`-Priority` today). A running job is never
  interrupted.

### Order of work

1. Part 3 (multi-host queue, pinned builds, model check). Needs `-NoBuild` and an artifacts-path
   parameter on `scripts/roslynsentinel-modeleval.ps1`, or the queue calling `dotnet test` itself;
   agree it with whichever session is editing that script.
2. Part 2b (board and claims) with the board file format.
3. Part 1 (coordinator session, question routing, `AskUserQuestion` hook, decision log). Needs a
   CLAUDE.md "Peer sessions" rewrite and a `coordinator` skill or agent definition.
4. Part 2c, only if needed.

## Alternatives considered, not pursued

- **Standing coordinator that decides for the owner ("CEO").** Rejected again: priorities and scope
  are the owner's. The coordinator here tracks state and recommends; it decides only by fixed rules.
- **Questions stay with each session; only cross-session ones go to the coordinator** (2026-11-12
  sketch). Rejected by the owner: one point of contact matters more than the extra hop.
- **On-demand arbiter subagent and owner-set session ranks** (2026-11-11 draft, parts B and D). No
  transcript episode needed a judge between competing priorities; where the owner was involved, the
  decision was theirs to make. The coordinator covers the rest.
- **One worktree and branch per session.** Removes edit conflicts entirely, but adds merge work and a
  server per worktree, and the transcripts showed no concurrent edits of the same code. Pinned eval
  builds remove the conflict that did occur.
- **Lease file plus run registry inside `roslynsentinel-modeleval.ps1`** (first 2026-11-11 draft).
  Superseded by the queue.

## Open questions

- Does stopping a run on `cancel` or time-out also stop the generation in LM Studio? Not verified.
- Build time per pinned commit, and disk use of one worktree plus artifacts per commit: unmeasured;
  needs a retention rule (for example, keep the last N commits).
- How the coordinator is identified to the hook, and what workers do when it is offline (wait, or ask
  the owner directly as a fallback?).
- Board file format and how often workers update it; whether a hook writes part of it (for example,
  dirty files from `Git status` at each commit).
- Should `scripts/roslynsentinel-planstep.ps1` runs use the same queue? They share the hosts.
- Can the coordinator compute an answer's impact reliably from the board alone, or does it need each
  worker's plan file too?

## Cost / risk

- **The coordinator is on every question's critical path.** Mitigated by file state, restartability
  and workers continuing with independent work; still, a stalled coordinator delays every question.
- **Committing before an eval** changes how sessions work; throwaway commits may need squashing.
- **The hook blocking `AskUserQuestion`** could strand a worker if the coordinator is down; it needs
  a fallback.
- **Board drift:** a stale claim or entry misleads everyone. Automatic release on commit and a staleness
  age reduce it.
- **Guidance cost:** a CLAUDE.md rewrite (15 KB budget), skill changes, a new hook and agent definition.
- **Edits to `roslynsentinel-modeleval.ps1`** must be sequenced with other sessions editing it.

## Related

- `scripts/roslynsentinel-evalqueue.ps1`, `scripts/roslynsentinel-evalqueue.Tests.ps1` - the
  single-host queue this extends.
- [reference_model_eval_procedure.md](../references/reference_model_eval_procedure.md) - hosts and the
  queue's usage section.
- [peer-coordination skill](../../../.claude/skills/peer-coordination/SKILL.md) and CLAUDE.md "Peer sessions".
- [plan_targeted_tool_scenario_evals.md](../plans/closed/plan_targeted_tool_scenario_evals.md) - shorter
  eval runs.
- [reference_lmstudio_loaded_models_endpoint.md](../references/reference_lmstudio_loaded_models_endpoint.md) - model check.
- [proposal_cross_session_write_journal.md](./proposal_cross_session_write_journal.md) - adjacent.
