# Queue model-eval runs through one runner script; settle other peer conflicts with ranks, not a standing arbiter agent

**Status:** PROPOSED 2026-11-11. Part A is built (`scripts/roslynsentinel-evalqueue.ps1`,
tests in `scripts/roslynsentinel-evalqueue.Tests.ps1`, one live run on host 112); parts B to D are not built.
Differences from the sketch below: the queue root is `%LOCALAPPDATA%\RoslynSentinel\evalqueue`; other
model-eval parameters travel in one `-EvalArgs` string checked at submit; the tree fingerprint covers
source files only; `ROSLYNSENTINEL_*` variables are recorded per job; `-OnTreeChange RunAnyway` exists.
Not done: the bypass warning in `roslynsentinel-modeleval.ps1`, queuing `roslynsentinel-planstep.ps1` runs,
and whether `cancel` stops the generation in LM Studio (still unverified).

## Motivation

The peer protocol removes the owner from file handoffs, but sessions still stop for a decision when
they compete for the eval host or have conflicting goals or ordering (owner report, 2026-11-11). No
single incident has been traced to a transcript; the frequency is unmeasured.

The main cost is model time on host 113, not file blocking:

- The host performs one generation at a time. A passing 10-chain run takes 362-546 s, a failing one
  reached 1040 s, and a batch of 10 is about 70 minutes
  ([plan_targeted_tool_scenario_evals.md](../plans/closed/plan_targeted_tool_scenario_evals.md), "Problem").
  That plan shortens individual runs (estimate 1 to 1.5 minutes per scenario sample); it does not
  decide whose run goes next.
- Every session is steered to the same host: "use `-HostAddress 113` (RTX 4060) by default and do not
  ask" ([reference_model_eval_procedure.md:245](../references/reference_model_eval_procedure.md)).
- Nothing shows a session that the host is busy or what is waiting. The only instruction is "check the
  host before each live run" (the plan above, "Execution rules"), which has no mechanism behind it.
- `scripts/roslynsentinel-modeleval.ps1` runs its repeats in one loop (`:472`), so a batch holds the
  host from first run to last and a short run cannot slot in between.
- The peer protocol has verbs only for files, commits and restarts; everything else is "advisory: ask
  your user" ([SKILL.md:44](../../../.claude/skills/peer-coordination/SKILL.md)).
- Host time is also lost to faults no session watches for: an outage of host 113 lost 2 runs of batch
  H3 (commit `7ffd4368`), and the testhost can hang or crash silently (memory
  `project_modeleval_testhost_gotchas`).

The owner's first idea was a standing "CEO" agent that every blocked session consults. This proposal
keeps the aim (the owner is not the router for routine conflicts) and changes the mechanism: the common
conflict is a scheduling problem and needs a queue, not a judge. See "Alternatives".

## Proposal

Build A first. B to D are worth building only if the owner is still being asked once A is in use.

| Conflict | Mechanism | Needs a model? |
| --- | --- | --- |
| Eval host time; same run wanted twice | A. Eval runner: one queue per host | No |
| Goal or ordering conflict | B. Owner-set priority rank and tie-break rules | No |
| Conflict found late | C. Intent board | No |
| Anything A to C do not settle | D. On-demand arbiter, bounded | Yes, per conflict |

### A. Eval runner

A new script, `scripts/roslynsentinel-evalqueue.ps1` (name open), is the only way a session starts a
live-model run. It wraps `scripts/roslynsentinel-modeleval.ps1` and does not change it, because the
scenario-eval plan edits that script in its steps 3d, 5a and 10.

**No daemon.** State is files under a queue folder outside the repo (one subfolder per host). `submit`
writes a job file and starts a detached worker if none is running; the worker runs jobs until the
queue is empty, then exits. `scripts/Start-ServerPrebuild.ps1` already works this way (single flight
under a lock file, stale-lock age, hand-off to a new worker).

**Commands**

| Command | Does |
| --- | --- |
| `submit` | Takes the model-eval arguments plus session name, purpose and priority. Returns the job id, the position in the queue and an estimated start time. |
| `status` | Lists the running job (session, purpose, elapsed, expected) and the queue in order. |
| `cancel <id>` | Removes a queued job, or stops a running one. A session cancels only its own jobs. |
| `wait <id>` | Blocks until the job ends, then prints its state and results folder. Run in the background, it wakes the session, so no peer message is needed. |

**Scheduling**

- One job is one run. A request for N repeats becomes N jobs, so a short run can go between two long
  ones instead of waiting for the whole batch.
- Order: priority first (part B rank; default equal), then submission time. A running job is never
  interrupted by a higher-priority one.
- Estimated start time comes from recorded durations of earlier jobs with the same test name.

**Same run wanted twice**

- A job's key is commit, working-tree state, host, model, test and arguments. A `submit` whose key
  matches a queued, running or finished job attaches to it and returns the same results folder.
  `-Force` queues a new run anyway (more samples are sometimes the point).

**Pinning what is tested (the main hazard)**

- `roslynsentinel-modeleval.ps1` builds from the working tree when the run starts, into one scratch
  folder per host (`:102`). A queued job would therefore test whatever the shared tree holds when its
  turn comes, including a peer's later edits.
- Sketch: `submit` records the commit and dirty flag; at dispatch the worker compares them with the
  tree and, on a difference, marks the job `TreeChanged` and does not run it. The submitter decides
  whether to resubmit. Building per job at submit time is the stronger fix and an open question.

**Faults the worker handles, because it is the single front door**

- Before each job it checks that the host answers; if not, it pauses the queue and leaves the jobs in
  place.
- Each job has a time limit; on expiry the worker stops the test process and marks the job `TimedOut`.
- A job whose process ends without results is marked `Crashed`, not `Finished`.

### B. Priority rank and tie-break rules

- The owner gives a session a rank when starting it ("priority 1"); unranked sessions share the lowest
  rank. The rank is the owner's intent captured once, which is what an arbiter agent would otherwise
  have to guess. For eval runs the rank is the job priority in part A and nothing more is needed.
- For conflicts over files or ordering, rules applied in order, added to the `peer-coordination` skill:
  1. The lower-ranked session yields.
  2. Equal rank: the session already holding the files keeps them.
  3. Neither holds: the shorter declared job goes first.
  4. Still tied: the session name that sorts first goes first.
- `IAM` replies carry the rank. A new standing authorization in CLAUDE.md "Peer sessions" makes an
  ordering reached by these rules binding without asking the owner; the session mentions it in its
  next message, as for the existing authorizations.

### C. Intent board

- Each main session writes one small file at start and when its scope shifts: task in one line, rank,
  files it expects to touch, eval runs it expects to need.
- A session reads the board before starting work, so an overlap is settled by the rules in B before
  either side has edits to lose.
- An entry whose session is `offline` in `ListAgents` is ignored.

### D. On-demand arbiter

- Used only when A to C leave two sessions stuck. One session spawns a fresh read-only subagent and
  gives it both sessions' positions verbatim, the intent board and the rules in B. It has no memory
  between conflicts, so it cannot go stale or become a single point of failure.
- It may decide: order, which session performs a shared operation, which session reviews the result.
- It may not decide: that a session abandons, reverts or deletes work; a change of scope; anything an
  owner instruction already covers. Those go to the owner as one question carrying both positions.
- Each decision is logged (sessions, question, decision, one sentence of reason) and reported to the
  owner, who can overrule it.

## Alternatives considered, not pursued

- **Standing "CEO" session that all sessions message.** Lost because:
  - it knows only what each session tells it, and each session argues its own case, while the real
    input (which goal matters more) is the owner's;
  - the common conflict (host time) is scheduling, and a model adds cost and variance there;
  - a long-lived session grows its context, compacts and can die, and then every blocked session waits;
  - it makes one peer's messages binding, a wider change of authority than a short list of rules.
- **Lease file plus run registry inside `roslynsentinel-modeleval.ps1`** (first draft of this proposal).
  It prevents collisions but leaves the host idle between sessions, shows no queue, cannot split a
  batch, and edits a script another plan is changing.
- **Always-on runner service or binary.** Needs starting, monitoring and a port or pipe; the file queue
  with a self-starting worker gives the same commands with nothing to keep alive. Revisit if the script
  outgrows PowerShell.
- **Queue as an MCP tool.** Adds schema tokens to every session, and a run must outlive the session
  and server that asked for it.
- **A worktree per session.** Removes file conflicts by isolation, but does nothing for host time. Not
  ruled out as a separate proposal; it would also solve the pinning hazard in part A.

## Open questions

- Does stopping the test process on `cancel` or time-out also stop the generation in LM Studio, or does
  the host stay busy until it finishes? Not verified.
- Cost of one job per run: `dotnet test` build and start overhead per invocation is not logged (noted
  in the scenario-eval plan). If it is large, group short scenario samples into one job.
- Pinning: is refusing a job on a changed tree enough, or should `submit` build into a per-job folder?
  The second needs an artifacts-path override the model-eval script does not expose today.
- Dedup key on a dirty shared tree: a diff hash treats any unrelated peer edit as a different run.
- Should host 112 take overflow for low-priority jobs? The reference doc says to use it only when the
  owner asks.
- Should `Build` and `RunTest` be queued the same way? That is a server change, and whether per-session
  servers already separate their build output has not been checked.
- How often do goal or ordering conflicts occur once A exists? Unmeasured; decides whether B to D are
  built.
- Who spawns the arbiter, given the spawner writes the brief? Sketch: a fixed brief template that
  carries both position messages unedited.

## Cost / risk

- A is one new script and a queue folder; it changes no existing file except guidance (the reference
  doc and the `modeleval-runner` agent must send runs through it).
- A session that calls `roslynsentinel-modeleval.ps1` directly bypasses the queue and collides with the
  running job. Guard: the worker sets an environment variable and the model-eval script warns when it
  is absent; that one-line edit must be sequenced with the scenario-eval plan.
- A stale worker lock after a crash blocks the queue, hence the stale-lock age.
- Dedup can skip a run that was wanted; `-Force` and a clear message are the guard.
- `TreeChanged` refusals could be frequent on a busy shared tree and waste queue time.
- B and D add text to the `peer-coordination` skill and one line to CLAUDE.md, which has a 15 KB
  budget, and widen what a session does on a peer's say-so. The limit is ordering and deduplication
  only; the permission-laundering rule in the skill stays as it is.

## Related

- [plan_targeted_tool_scenario_evals.md](../plans/closed/plan_targeted_tool_scenario_evals.md) - shorter runs;
  edits the model-eval script.
- [reference_model_eval_procedure.md](../references/reference_model_eval_procedure.md) - hosts, scratch
  build folders.
- [peer-coordination skill](../../../.claude/skills/peer-coordination/SKILL.md) and CLAUDE.md "Peer sessions".
- [proposal_cross_session_write_journal.md](./proposal_cross_session_write_journal.md) - adjacent:
  cross-session file writes.
- `scripts/Start-ServerPrebuild.ps1` - the detached single-flight worker pattern.
- Memories: `project_concurrent_sessions`, `project_modeleval_testhost_gotchas`.
