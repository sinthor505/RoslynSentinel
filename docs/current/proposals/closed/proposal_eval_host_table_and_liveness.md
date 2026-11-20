# One eval-host table, a host liveness probe, a per-machine queue key and a run index

**Status:** IMPLEMENTED 2026-11-20 by plans/closed/plan_eval_host_table_and_liveness.md (8ccccff0). Open questions settled in that plan's Decision section.

## Motivation

- **Hosts are hardcoded in at least eight places, with no shared source** (survey 2026-11-19):
  `scripts/roslynsentinel-modeleval.ps1:399-416` (`$knownHosts`, 112/113/85 on `:1234/v1`),
  `scripts/roslynsentinel-evalqueue.ps1:220-236` (`Resolve-EvalHost`, TODO at 221 to share the table),
  `scripts/roslynsentinel-errorvariants.ps1:107-122`, `scripts/roslynsentinel-interrogate.ps1:24-26`
  and `scripts/roslynsentinel-planstep.ps1:31-33` (112/113 only), `scripts/Probe-OvernightLogprobs.ps1:36-144`,
  and the two `ModelTestingResultsArchiver.cs` copies (ModelEval `KnownHostAliases` 115-120; the
  ErrorVariants `DeriveHostSuffix` uses `Contains()` and already behaves differently). Docs:
  `reference_model_eval_procedure.md:38-69` (host list dated 2026-08-30) and the standing ruling in
  `reference_autonomous_run_conventions.md:19-28` (queries only 112/113 `:1234/api/v1/models`).
- **Unsloth (llama-server, `http://<host>:8888/v1`) on 85, 112 and 113 is becoming the primary server**
  (owner, 2026-11-19: faster than LM Studio). Today it runs only as a raw URL with a sanitised suffix. It
  works: job `20261120-064449-001` (ShippingKindStepFinal, `unsloth/Qwen3.5-4B-MTP-GGUF` on 85:8888) finished
  with 6 of 6 cases passing, so `/v1/responses` is honoured there.
- **LM-Studio-only calls** degrade on llama-server: `GET /api/v1/models` (load-status warning
  `LmStudioAgentClient.cs:93-136`, `FetchLoadSettingsAsync` 501-525, `RunSettings.cs:154-245`),
  `GET /api/v0/models` `loaded_context_length` (437-470), the LM Studio #2559 usage check
  (`ModelAgentRunner.cs:63`), and the model load/unload ruling. llama-server's `/v1/models` lists
  available models, not loaded ones.
- **The queue is keyed by URL, not by machine.** `Get-HostDir` / `Invoke-WithQueueLock`
  (`evalqueue.ps1:238-259`) key a folder, mutex and worker on the URL-derived suffix, so `85`,
  `http___192_168_10_85_1234_v1` (the same endpoint twice) and `http___192_168_10_85_8888_v1` are three
  queues on one GPU and can run jobs at once. Finished-job timestamps show no overlap yet (112: 591 jobs,
  113: 663, 85: 116); nothing prevents it.
- **Host status takes many probes.** On 2026-11-19 every LM Studio `:1234/api/v1/models` timed out
  while the `:8888` servers answered; finding that out took one call per host x backend, which every
  session repeats.
- **Run ownership and commit are not indexed.** `evalqueue status` shows session and purpose for
  Building/Running/Queued jobs, but has no session or active-only filter, no notification when a job ends,
  `-Session` is unchecked free text (`evalqueue.ps1:79`, appended at 541-557), and nothing maps a run
  folder to its job, session and commit except the transcript stamp inside it.

## Proposal

### 1. One host table (decided: owner 2026-11-20)
`scripts/eval-hosts.json`, one entry per endpoint: `alias` (e.g. `85`, `85u`), `machine` (physical host
key, e.g. `85`), `baseUrl`, `backend` (`lmstudio` | `llama-server`), `suffix` (artifacts and results folder
name; keep today's suffixes so existing `ModelTestingResults` folders stay valid), optional `default`
flag per machine. Every script resolves `-HostAddress` through one shared helper that reads it; both C#
archivers read the same file (or a generated C# copy, decided in the plan) and the duplicate
`DeriveHostSuffix` collapses to one. LM-Studio-only calls run only when `backend` = `lmstudio`; the
standing ruling and the procedure doc point at the table.

### 2. Host liveness probe (decided: owner 2026-11-20, both front doors)
`scripts/Get-EvalHostStatus.ps1`: probes every table entry in parallel with a short timeout and prints
one row per endpoint: machine, alias, backend, reachable, loaded model(s) where the backend can tell
(LM Studio `loaded_instances`; llama-server: the plan picks `/props`, `/slots` or `/health`), and the
queue state for that machine (running/queued job count, owning session). `-Json` for agents.
`roslynsentinel-evalqueue.ps1 hosts` calls the same script, so there is one implementation.

### 3. Queue keyed by machine
`Resolve-EvalHost` maps any alias or URL to its table entry; the queue folder, lock and worker key on
`machine`, so one GPU has one queue whatever endpoint a job names. The job keeps its endpoint. Existing
folders migrate (or are drained, then retired).

### 4. Run index (decided: owner 2026-11-20, option 1)
The worker appends one row per job end to `ModelTestingResults\<suffix>\runs.csv`: job id, session,
purpose, test, model, endpoint, pinned commit, state, start/end, run folders. Group by commit, session or
model from there. Run folder names stay `yyyyMMdd-HHmmss-fff`.

### 5. Ownership visibility
`status -Session <name>` and `-ActiveOnly`; validate `-Session` at submit against live
`~/.claude/sessions/*.json` (pid alive; `scripts/Get-PeerSessions.ps1` already reads them) and warn on an
unknown name.

## Alternatives considered, not pursued

- **Commit in the run folder name (suffix or prefix).** Breaks name matchers:
  `Archive-ModelTestingResults.ps1:43` and `Parse-AgentLog.ps1:180` (exact patterns) for a suffix; with a
  prefix also every `^\d{8}-\d{6}` matcher (`Get-RunLeafDirs` in modeleval.ps1, `evalqueue.ps1:804`,
  `errorvariants.ps1:168`, the queue tests), an all-digit short SHA would match `^\d{8}`, and SHA order loses
  chronology. The name is also built in about 17 ModelEval test files. The index gives the grouping instead.
- **A `<sha8>` folder level under the test folder.** Recursive matchers tolerate it (SizeThreshold's `n60`
  level), but it needs the 17 name sites routed through a shared helper; revisit only if browsing folders
  still matters after the index exists.
- **Probing hosts ad hoc per session.** What happens today; the cost motivates item 2.

## Open questions

- Generated C# copy of the host table versus reading the JSON at test run time (the test projects run from
  the pinned checkout, so the file is present either way).
- llama-server loaded-model detection endpoint; whether the model id the queue sends must match a loaded
  model or triggers a swap (VRAM rule: one model per machine).
- Alias naming for the second backend on a machine (`85u`, `85-unsloth`), and which backend is the
  machine default.
- Whether LM Studio stays installed on each machine; if both serve one GPU, the per-machine queue key is
  required, not optional.
- `-Session` validation: warn or refuse.

## Cost / risk

Touches every eval front-door script, both archivers and their tests (`ModelTestingResultsArchiverTests.cs:10-22`
pins the 8888 suffix), `LmStudioAgentClient`/`RunSettings` (backend gating), the queue's folder layout
(migration while jobs may be queued: land between runs; pinned jobs are immune to commits since
plan_pinned_eval_checkouts), and the procedure and conventions docs. Keeping today's suffixes avoids
moving result folders.

## Related

- [plan_pinned_eval_checkouts.md](../../plans/closed/plan_pinned_eval_checkouts.md) (pinned checkouts, `ROSLYNSENTINEL_RESULTS_ROOT`)
- [reference_model_eval_procedure.md](../../references/reference_model_eval_procedure.md)
- [reference_autonomous_run_conventions.md](../../references/reference_autonomous_run_conventions.md)
- `reference_lmstudio_loaded_models_endpoint.md`
