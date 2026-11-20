# Plan: one eval-host table, a host liveness probe, a per-machine queue key and a run index

**Status:** IN PROGRESS 2026-11-20. Steps 1-6 done (04fab1df, 986f1b29, 9737c57d, 1ea220f7, 68ed4226, af39ed7b).

## Problem

Implements [proposal_eval_host_table_and_liveness.md](../proposals/proposal_eval_host_table_and_liveness.md);
read its Motivation for the full case. Evidence below was re-verified against source on 2026-11-20
(after 83ca88dc / d46c368a changed `roslynsentinel-evalqueue.ps1`); where the proposal's anchor was off,
the corrected one is given. `evalqueue.ps1` anchors are HEAD-based as first read; another session's uncommitted
edits have since shifted the working-tree script (for example `Get-ExpectedSeconds` is at 346, was 333), so
re-verify every `evalqueue.ps1` anchor at hold point H3 (Decision 10) before editing.

- **Hosts are hardcoded in eight places, no shared source.**
  `scripts/roslynsentinel-modeleval.ps1:399-416` (`$knownHosts`, 112/113/85 on `:1234/v1`, suffix = alias);
  `scripts/roslynsentinel-errorvariants.ps1:106-123` (same table); `scripts/roslynsentinel-evalqueue.ps1:220-236`
  (`Resolve-EvalHost`, TODO at 221 to share the table); `scripts/roslynsentinel-interrogate.ps1:130-133`
  (112/113 only; the proposal's 24-26 is help text); `scripts/roslynsentinel-planstep.ps1:185-188` (112/113
  only; 31-33 is help text); `scripts/Probe-OvernightLogprobs.ps1:135` (`switch`, 85 special-cased) and `:144`
  (LM Studio `/api/v1/models`); `RoslynSentinel.Tests.ModelEval/ModelTestingResultsArchiver.cs:98-120`
  (`DeriveHostSuffix`, `KnownHostAliases`: exact URL match, `OrdinalIgnoreCase`, trailing `/` trimmed);
  `RoslynSentinel.Tests.ErrorVariants/ModelTestingResultsArchiver.cs:89-108` (`DeriveHostSuffix` uses
  `Contains()` on the IP, so `http://192.168.1.112:8888/v1` archives to folder `112` there but to
  `http___192_168_1_112_8888_v1` from ModelEval). That file's header (lines 5-9) says the copy is deliberate
  ("this project may not reference ModelEval ... does not belong in Common") and carries `// TODO unify`
  at line 9.
- **Unsloth (llama-server, `:8888`) is becoming the primary server and is only a raw URL today.** Live probe
  2026-11-20 of `GET http://<host>:8888/v1/models` on 192.168.10.85, 192.168.1.112 and 192.168.1.113 returns
  a router-style list; each entry has `id`, `loaded` (bool), `status.value` (`loaded` / `unloaded`) and, for
  the loaded one, `context_length` and `max_context_length`. Loaded ids observed: 85
  `Qwen3.5-4B-MTP-GGUF` (`context_length` 70000, max 262144), 112 `unsloth/Ornith-1.0-9B-GGUF` (70000),
  113 `Bonsai-27B-GGUF` (74752). `/props` answers; `/health` is 404. Ids are inconsistent about an org
  prefix, and the server accepts both forms: queue job files in
  `%LOCALAPPDATA%\RoslynSentinel\evalqueue\http___192_168_10_85_8888_v1\jobs\` show jobs with model
  `Qwen3.5-4B-MTP-GGUF` and `unsloth/Qwen3.5-4B-MTP-GGUF` (`20261120-064449-001`) both Finished, passed.
- **LM-Studio-only calls degrade on llama-server** (anchors verified): `LmStudioAgentClient.cs:93-136`
  (`LogLoadedModelStatusAsync`, `/api/v1/models`, reads `models[].key` and `loaded_instances`; started from the
  constructor at line 79); `:437-470` (`FetchContextLengthAsync`, `../api/v0/models` `loaded_context_length`;
  failure caches `-1`); `:501-525` (`FetchLoadSettingsAsync`, `../api/v1/models` ->
  `ModelLoadSettings.FromModelsJson`, `RunSettings.cs:155-245`); the #2559 usage-jump gate is at
  `ModelAgentRunner.cs:456` (`IsUsageJumpImplausible`, declared 67-68; the proposal's `:63` is its doc comment).
  Missed by the proposal: `ErrorVariantReplay.cs:393-436` (`CheckModelLoadedAsync` reports "not LM Studio's
  /api/v1/models" against llama-server) and `:643-649` (`ModelsUrl`). A duplicate `LmStudioClient.cs:135`
  (`/api/v0/models`) has 0 references from `Search(mode: references)`; left alone.
  Consequence of the `-1` fallback: `ModelAgentRunner.cs:317-319,481,493` skip the context warning and the
  context-exhausted check and feed `ReasoningReplayPolicy.FromEnvironment(contextLength)` a `-1` on every
  llama-server run today.
- **The queue is keyed by URL-derived suffix, not machine.** `Get-HostDir` (`evalqueue.ps1:238-242`) and
  `Invoke-WithQueueLock` (244-259, mutex name hashed from the folder) key on `HostInfo.Suffix`;
  `Get-JobPath` (269) and `Invoke-Wait` (707-708) use `job.suffix` as the folder name. The proposal's "same
  endpoint twice" is stale: the `:1234` URL form already resolves to alias `85` (line 229). The real
  defect is `:1234` and `:8888` on one GPU. Measured queue root 2026-11-20 (job files per folder):
  `112` 695, `113` 786, `85` 123, `http___192_168_10_85_1234_v1` 1, `http___192_168_10_85_8888_v1` 13,
  `http___192_168_1_112_8888_v1` 50, `http___192_168_1_113_8888_v1` 20, and two empty
  `192_168_1_112`, `192_168_1_113`. Machine folders `85`, `112`, `113` already exist.
  The worker is given one `$hostInfo` and probes only `<baseUrl>/models` (`Test-HostAnswers`, 787-793).
- **Run ownership is not indexed.** `-Session` is free text (`evalqueue.ps1:141`, appended at 551-553); no
  `status` filter; nothing maps a run folder to job, session and commit.

## Decision

Owner decisions of 2026-11-20 are entered in substance; entries 7-14 are the review round of the same day. The
plan-level choices after entry 14 are open to review. The coordinator's labels (D6-D13) are given for tracing.

1. **2026-11-20 (owner). Shared C# reader.** Both `ModelTestingResultsArchiver.cs` copies read
   `scripts/eval-hosts.json` at run time through one shared helper; the `Contains()`-based `DeriveHostSuffix`
   collapses into it. The file reaches each test output through a csproj `Content` link
   (`<Content Include="..\scripts\eval-hosts.json" Link="eval-hosts.json" CopyToOutputDirectory="PreserveNewest" />`)
   in both test projects, read from `AppContext.BaseDirectory`. Chosen over walking up to the repo root (the
   pattern `FindRepoRoot` uses for `RoslynSentinel.slnx`) because the link copies the table of the commit that
   was built, so a pinned queue checkout reads its own table, and it works under any `--artifacts-path`.
   Rejected: a generated C# copy (a second artifact to regenerate and commit; drifts). A missing file yields an
   empty table; an invalid file throws. Where the helper lives: entry 7.
2. **2026-11-20 (owner). llama-server detection.** Loaded-model detection uses `GET <baseUrl>/models`
   (`/v1/models`): `loaded` / `status.value == "loaded"` for "loaded", `context_length` where LM Studio's
   `loaded_context_length` is used today. `/props` and `/slots` are not used.
3. **2026-11-20 (owner). Model mismatch is refused.** A job whose `-Model` is not the loaded model on a
   llama-server entry is REFUSED by the worker preflight with "loaded: X, requested: Y". A code comment at the
   refusal site says the policy can be revisited if it causes friction (owner's words). Plan-level: "matches" =
   equal after lower-casing and keeping the text after the last `/` of each id (both forms are accepted by the
   server, see Problem). New terminal job state `Refused`. The exact refuse/run rule, including a host that
   reports no loaded state, is entry 9.
4. **2026-11-20 (owner). Bare alias = machine key.** `85` resolves to the entry with `default: true` for
   machine `85`. Resolved at SUBMIT; the job stores the concrete alias, endpoint, backend and suffix, so flipping
   the default never redirects queued jobs. The loader rejects a table with zero or more than one default per
   machine. Every entry has its own explicit alias (`85lms`, `85u`, `112lms`, ...); the bare machine key is
   never an entry alias. Results land in the resolved entry's suffix folder; today's suffixes are kept so
   existing `ModelTestingResults` folders stay valid (LM Studio entries `112`/`113`/`85`; llama-server entries
   keep the sanitised-URL folders `http___192_168_1_112_8888_v1`, `http___192_168_1_113_8888_v1`,
   `http___192_168_10_85_8888_v1`). Docs that say "85 = LM Studio :1234" are updated in Step 12. Machine 85 is
   192.168.10.85; 112 and 113 are 192.168.1.x. Which entry is the default: entry 12.
5. **2026-11-20 (owner). `-Session` is checked, not enforced.** Submit WARNS on a name that is not a live
   session (listing the live names) and never refuses.
6. **2026-11-20 (owner, via coordinator). Tests are merged into the slice they test** where the merged slice
   still fits `.claude/implementer-tiers.json` depth tier 1 (at most 3 files, 15 edits, 1 acceptance, 35 tool
   calls). No test step remains separate; Steps 11 and 13 note where a test dispatch may split off inside one
   worktree.
7. **2026-11-20 (owner, D6). `EvalHostTable` stays in `RoslynSentinel.Common` (Option A).** It reads
   `AppContext.BaseDirectory\eval-hosts.json` only; no file means an empty table and the `lmstudio` backend. The
   production callers `SubAgentModelRun.cs:103`, `PlanStepRunner/Program.cs:97` and
   `ServiceRegistrationExtensionsAdvanced.cs:31` (the last builds the `HttpClient` from `LlmOptions.BaseUrl`) are
   unchanged. Basis: `LmStudioAgentClient`, `LlmOptions`, `ModelAgentRunner` and `RunSettings` are already in
   Common and are the gating consumers (18 client constructor callers in 17 files); Common is referenced by both
   test projects, so no sibling reference is needed. Rejected: a linked source file in the test projects (the
   client would need a backend constructor argument at every llama-server caller; a missed site silently gates as
   LM Studio) and an environment variable (a direct `dotnet test` would gate wrongly).
8. **2026-11-20 (owner, D7). Queue-script steps are built and tested in a copy, then landed atomically.** Steps 11,
   13 and 14 are implemented and tested in a git worktree under `c:\tmp` (or a copy of `scripts\`), based on the
   HEAD at hold point H3 (which already holds Steps 2 and 5). The dispatcher lands each as ONE commit (cherry-pick
   of the worktree commit; shell `cherry-pick` is allowed) while the queue is idle. `evalqueue.ps1` in the shared
   tree is never edited in place mid-step. This replaces the earlier plan to `HOLD` the shared file: an in-place
   edit is live for every session at once (`Start-DetachedWorker` re-launches `$PSCommandPath`, `evalqueue.ps1:381`).
9. **2026-11-20 (owner, D8). The machine worker runs the first queued job whose endpoint answers.** A dead endpoint
   does not block another endpoint's job. One merged probe: a single `GET <baseUrl>/models` serves reachability and
   (llama-server) the loaded list; any failure of that request counts as "endpoint not answering": the job is
   skipped this pass, and when no queued job's endpoint answers the worker pauses (`WaitingForHost`, the 30-minute
   give-up as today). A failed request never produces a refusal and never `loaded: (none)`.
   - Plan-level rule for the refuse/run verdict (library function `Get-EvalModelLoadState`): the request succeeded
     and at least one listed entry reports a loaded state (`loaded` or `status.value` present). Then: an entry with
     state true matching the requested id -> run; otherwise -> REFUSE, `loaded: <ids of entries with state true, or
     "(none)">`, `requested: <model>`. If no listed entry reports a state (a non-router llama-server, or an empty
     list) the verdict is Unknown and the job RUNS (Risks 3). "(none)" therefore appears only when the host
     answered and said nothing is loaded.
10. **2026-11-20 (owner, D9). Hold points.**
    - **H1: Steps 1-7, 9, 10 and 12 land any time** (no queue file is touched; pinned jobs run their own checkout's
      scripts).
    - **H2, before Step 8:** ask session `roslynsentinel-b2` (`SendMessage`) to confirm there are no further
      comparison runs, or that reruns are pinned with `-Commit 83ca88dc`. Step 8 changes what a llama-server run
      records (context checks, replay policy), so an unpinned rerun would compare across the change.
    - **H3, before Steps 11, 13 and 14:** all queue folders idle (no job `Queued`/`Building`/`Running`, no live
      worker) AND the other session's uncommitted `scripts/roslynsentinel-evalqueue.ps1` and
      `scripts/roslynsentinel-evalqueue.Tests.ps1` edits are committed (`Git(operation: status)` shows neither
      modified). Dispatcher check, one PowerShell line, read-only (it reads job files and tries a non-creating
      write-open of `worker.lock`, because `status` repairs orphans by WRITING job files):
      ```powershell
      $r=Join-Path $env:LOCALAPPDATA 'RoslynSentinel\evalqueue'; $bad=@(); foreach($d in Get-ChildItem $r -Directory){ foreach($f in Get-ChildItem (Join-Path $d.FullName 'jobs') -Filter *.json -ErrorAction SilentlyContinue){ $j=Get-Content $f.FullName -Raw | ConvertFrom-Json; if($j.state -in 'Queued','Building','Running'){ $bad+="$($d.Name)/$($j.id) $($j.state)" } }; $l=Join-Path $d.FullName 'worker.lock'; if(Test-Path $l){ try{ [IO.File]::Open($l,'Open','Write','ReadWrite').Dispose() }catch [IO.IOException]{ $bad+="$($d.Name) worker alive" } } }; if($bad){ $bad; 'NOT IDLE' } else { 'IDLE' }
      ```
      Prints `IDLE` or the offending jobs and `NOT IDLE`. At H3 also re-verify the Step 11/13/14 anchors against the
      committed script (the other session's edits add queue-wide job ids and a `<host>/<id>` lookup).
    - **Sequencing with the open TODO "Retire the unpinned evalqueue path"** (`TODO.md:18`): never concurrent with
      Step 11, since both rewrite the dispatch lock. Recommended order: that TODO first, when its precondition holds
      (no job file without `pinnedCommit` in any folder; the H3 one-liner can be extended to check
      `$null -eq $j.pinnedCommit`), because it deletes the `Get-TreeFingerprint` dispatch branch Step 11 would
      otherwise keep. If unpinned job files remain, skip it and Step 11 keeps that branch verbatim.
11. **2026-11-20 (owner, D10). Existing URL-keyed queue folders: drain, then retire.** New jobs go only to the machine
    folder, and the switch happens only when every old folder is empty (H3). The six legacy folders
    (`http___192_168_10_85_1234_v1`, three `..._8888_v1`, empty `192_168_1_112`, `192_168_1_113`) stay as read-only
    history; `status`, `wait` and `Find-JobFile` still list and scan them (they enumerate every folder under the
    root, `evalqueue.ps1:295,651`). No file moves: every job's `logPath` is an absolute path inside its old folder.
12. **2026-11-20 (owner, D11). `default: true` is the Unsloth `:8888` entry on 85, 112 and 113.** The LM Studio
    entries keep explicit aliases (`85lms`, `112lms`, `113lms`). A bare `-HostAddress 85` therefore means llama-server
    from the moment a consumer reads the table (Steps 7, 9, 11); results of a bare-alias run land in the
    `http___..._8888_v1` folder. Effects and the window before Step 13: Risks 2 and 6.
13. **2026-11-20 (owner, D12). A pinned commit without `scripts/eval-hosts.json`:** the worker passes the entry's
    `baseUrl` as `-HostAddress` (the old script treats a URL verbatim). Because that script derives its results suffix
    by sanitising the URL, the job's `suffix` is set to the sanitised `baseUrl` for such a commit (so `Get-RunFolders`,
    `_scratchbuild_<suffix>` and `runs.csv` look where the old script writes); the job's `machine` stays the entry's.
    An unknown address (for example the queue tests' `stub`) is the same case: passed verbatim, machine = suffix.
14. **2026-11-20 (dispatcher defaults, owner away; D13).**
    - `EvalHostEntry` is renamed `EvalHost` (no new `*Entry` type, per the dto-conventions suffix rule). The new
      records carry NEW-DTO justifications (Step 1), evaluated against `ModelLoadSettings` and `LmStudioModelInfo`.
    - The Step 8 mutation check is run by the dispatcher, not the implementer.
    - No submit-time model-mismatch warning; the worker preflight suffices (out of scope).
    - "Whether LM Studio stays installed per machine" is CLOSED as moot: the queue key is the machine whatever
      backends exist, and entries can be removed from the table later without invalidating stored jobs.

Plan-level choices (open to review):

- **Backend is derived per client instance.** `LmStudioAgentClient` computes its backend from its own
  `HttpClient.BaseAddress` via `EvalHostTable.BackendFor`, not from the static `LlmOptions.BaseUrl` (the
  constructor doc comment at `LmStudioAgentClient.cs:59-67` explains why a process-wide static is wrong for
  per-call clients). No new environment variable.
- **One PowerShell library.** `scripts/roslynsentinel-evalhosts.ps1` is dot-sourced (the
  `roslynsentinel-evalcheckout.ps1` pattern, `evalqueue.ps1:162`) by every front door; machine key, not suffix,
  names the queue folder.
- **No contract-skeleton step.** The plan-writer rule applies when new types reference each other and later slices
  run in parallel on them. `EvalHostTable` and `LlamaServerModels` do not reference each other, the PowerShell
  library is one file, and every consumer starts after its implementation lands, so a skeleton would only add a
  throwaway write. The signatures consumers rely on are fixed in Steps 1 and 2. The closing sweep for leftover
  `NotImplementedException` / `TODO implement` still runs (Step 15).
- **Queue state for the probe comes from `evalqueue status -Json`.** One implementation of "what is queued";
  `Get-EvalHostStatus.ps1` calls it, `evalqueue hosts` calls `Get-EvalHostStatus.ps1`. Known effect: `status`
  repairs orphaned `Building`/`Running` jobs by writing them, so the probe inherits that side effect.
- **Session names are read from `~/.claude/sessions/*.json`** (`name`, `pid`), the same fields
  `Get-PeerSessions.ps1:43-46` reads, but directly: that script also scans every live session's transcript for a
  tab title (`:26-41`, `:47-51`), which a submit does not need.

## Execution rules

- **C# slices:** one `Build`/`RunTest` at a time, never two agents building the same project (or Common, which every
  test project builds) at once: they share `bin/obj`. Pairs below put at most one C# slice with a PowerShell or
  markdown slice. C# edits go through the MCP edit tools; `.ps1`, `.json`, `.csproj` and `.md` through Edit/Write.
- **Merged slices and "Done when".** A slice that owns its tests has one acceptance: the named test run exits green
  (a C# `RunTest` builds first, so 0 build errors is implied; a PowerShell test script parsing and running implies
  the parse check). The implementer reports; the dispatcher runs the named tests. A slice with no tests (Steps 7, 9)
  uses the parse check below.
- **PowerShell parse check** (Steps 7 and 9 only). Replace `<file>`:
  `pwsh -NoProfile -Command "$e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile('<file>',[ref]$null,[ref]$e); if ($e.Count) { $e | Out-String; exit 1 }"`
- **PowerShell tests** are plain assert scripts (house style of `scripts/roslynsentinel-evalqueue.Tests.ps1`:
  `pwsh -NoProfile -File <script>`, exit 0 and "All passed" = green).
- **Queue-script steps (11, 13, 14)** follow Decision 8 and H3: worktree from the HEAD that holds every earlier
  step, built and tested there, landed as one commit by the dispatcher on an idle queue. Each of the three starts
  from a fresh worktree off the then-HEAD (Step 13 needs Step 11's commit, Step 14 needs Step 13's). The existing
  `scripts/roslynsentinel-evalqueue.Tests.ps1` (in the version committed at H3, including the other session's
  edits) must stay green through all three: it uses host `stub` (not in the table, and its throwaway checkout has
  no `eval-hosts.json`), which Decision 13 keeps working, so it is the regression check for the verbatim fallback
  path only. It says nothing about table entries, machine folders, refusal or the index; those paths are covered
  only by the new test files written inside Steps 11, 13 and 14.
- **Commit trailers:** each slice commit carries `Plan: plan_eval_host_table_and_liveness`; the closing commit
  carries `Closes:` as well.
- **Failure framing:** a model that passes a mismatched `-Model` to a llama-server host, or sends `85` to the wrong
  server, is an environment gap (the host table and the preflight message are the fix), never "the model should
  have known".

## Steps

Sequence: text-neutral first (1-5), then behaviour-changing. Pairs are slices that may run concurrently (disjoint
files; at most one C# slice per pair). Every code slice owns its tests; each merged slice fits tier 1 (the per-step
line states files and edits against it).

Slot map (a slot is one dispatch window; hold points in Decision 10):

| Slot | Steps | Hold point |
| --- | --- | --- |
| S1 | 1 / 2 | H1 |
| S2 | 3 / 4 | H1 |
| S3 | 5 / 6 | H1 |
| S4 | 7 / 8 | H1 for 7; H2 before 8 (8 may start only after H2 clears; 7 may land first) |
| S5 | 9 / 10 | H1 |
| S6 | 11 / 12 | H3 before 11; 12 is H1 |
| S7 | 13 | H3 |
| S8 | 14 | H3 |
| S9 | 15 | H3 |

Step 12 (docs) is H1 and may move to any earlier slot; if it does, Step 11 runs alone in S6.

### Step 1 - C# helper and its tests
- Done: 04fab1df
- Files: `RoslynSentinel.Common/AgentLoop/EvalHostTable.cs` (new), `RoslynSentinel.Common/AgentLoop/LlamaServerModels.cs`
  (new), `RoslynSentinel.Tests.ModelEval/EvalHostTests.cs` (new, both fixtures `EvalHostTableTests` and
  `LlamaServerModelsTests`). 3 files, 3 `CreateFile` writes.
- Depends on: none. Pairs with: Step 2. Hold point: H1.
- Effect: text-neutral (no callers yet). Names checked 2026-11-20: no `EvalHost*` or `LlamaServer*` symbol exists.
- NEW-DTO (give these blocks in the brief, the report, the commit message and the `reason` of the `CreateFile` call;
  `EvalHostTable` and `LlamaServerModels` are static classes and need `DTO-EXEMPT: static helper`):
  ```
  NEW-DTO: EvalHost (Item, Common.AgentLoop)
  Evaluated: ModelLoadSettings - the load config of one loaded instance, built per run (BaseUrl, context length, flash attention, MTP, parallel); it has no alias, machine, backend, suffix or default flag and is not read from a table; LmStudioModelInfo - private nested class of LmStudioAgentClient (Id, LoadedContextLength), not visible outside it and LM Studio-shaped
  Wire impact: none

  NEW-DTO: LlamaServerModel (Item, Common.AgentLoop)
  Evaluated: LmStudioModelInfo - private nested class (Id plus loaded_context_length from /api/v0/models), not visible to a second file and carries no loaded flag; ModelLoadSettings - per-run load config of one instance, no model id and no loaded state
  Wire impact: none
  ```
- Change: namespace `RoslynSentinel.Common.AgentLoop`. Final signatures (Steps 3, 6, 8, 10 rely on them):
  ```csharp
  public sealed record EvalHost(string Alias, string Machine, string BaseUrl, string Backend, string Suffix, bool IsDefault);
  public static class EvalHostTable
  {
      public const string BackendLmStudio = "lmstudio";
      public const string BackendLlamaServer = "llama-server";
      public const string FileName = "eval-hosts.json";
      public static string DefaultPath { get; }                       // Path.Combine(AppContext.BaseDirectory, FileName)
      public static IReadOnlyList<EvalHost> Load(string? path = null);   // missing file -> empty; invalid -> InvalidOperationException
      public static EvalHost? FindByBaseUrl(IReadOnlyList<EvalHost> table, string baseUrl);
      public static string DeriveSuffix(string baseUrl);              // default table (loaded once per process)
      public static string DeriveSuffix(IReadOnlyList<EvalHost> table, string baseUrl);
      public static string BackendFor(string baseUrl);                // unknown URL or no table -> BackendLmStudio
      public static string BackendFor(IReadOnlyList<EvalHost> table, string baseUrl);
  }
  public sealed record LlamaServerModel(string Id, bool? Loaded, int? ContextLength);   // Loaded null = host reports no state
  public static class LlamaServerModels
  {
      public static IReadOnlyList<LlamaServerModel> Parse(string? modelsJson);   // never throws; invalid -> empty
      public static LlamaServerModel? FindLoaded(IReadOnlyList<LlamaServerModel> models, string requestedModel);   // Loaded == true and IdsMatch
      public static LlamaServerModel? FindListed(IReadOnlyList<LlamaServerModel> models, string requestedModel);   // IdsMatch, any state
      public static bool IdsMatch(string requested, string listedId);
  }
  ```
  - `Load`: `System.Text.Json`, case-insensitive names; rejects missing field, unknown `backend`, duplicate `alias`,
    an `alias` equal to any `machine` key, a `baseUrl` shared by two entries, and a machine with zero or more than
    one `default: true` entry (same rules and, where practical, wording as the PowerShell loader of Step 2); a
    missing file returns an empty list. `FindByBaseUrl`: trailing `/` trimmed, `OrdinalIgnoreCase`. `DeriveSuffix`:
    table suffix, else the existing sanitising rule (ASCII letters and digits only, others `_`, trimmed; empty ->
    `unknown-host`) copied from `ModelEval/ModelTestingResultsArchiver.cs:109-111`. `BackendFor`: entry backend,
    else `BackendLmStudio`. The default-table overloads load once per process (`Lazy`); an invalid shipped table
    propagates `InvalidOperationException`, a missing file does not.
  - `LlamaServerModels.Parse`: `data[]` -> `Id` (`id`), `Loaded` (`loaded` bool when present, else `status.value ==
    "loaded"` when `status` is present, else null), `ContextLength` (`context_length` int). `IdsMatch`:
    case-insensitive, text after the last `/` (same rule as `Test-EvalModelMatch`, Step 2).
  - Tests (NUnit 5, `await` async asserts), against a temp file or an inline table that mirrors the six shipped
    entries (the shipped file itself is checked in Step 3): `Load` validation cases (zero default, two defaults,
    alias equal to machine key, duplicate alias, shared baseUrl); `Load` of a missing path returns empty;
    `DeriveSuffix(table, url)` keeps every pin now in `ModelTestingResultsArchiverTests.cs:10-26` (exact `:1234`
    URLs -> `112`/`113`/`85`, including trailing `/` and `HTTP://...:1234/V1`; `:8888` URLs and
    `http://localhost:1234/v1` -> sanitised URL) and adds `http://192.168.10.85:8888/v1` ->
    `http___192_168_10_85_8888_v1`; `BackendFor`: `:8888` -> `llama-server`, `:1234` and unknown -> `lmstudio`, and
    an empty table -> `lmstudio` for every URL; `LlamaServerModels.Parse` of one trimmed live body holding the three
    loaded-id shapes quoted in Problem, a body whose entries have no `loaded`/`status` (Loaded null), invalid JSON
    -> empty; `FindLoaded` / `FindListed` with and without an org prefix; `IdsMatch` negative case.
- Done when: `RunTest` on classes `EvalHostTableTests` and `LlamaServerModelsTests` (project
  `RoslynSentinel.Tests.ModelEval`) passes (builds first).
- Lane: `implementer` (3 files, 3 writes, 1 acceptance).

### Step 2 - PowerShell host library, table data and tests
- Done: 986f1b29 (also `!scripts/eval-hosts.json` in .gitignore)
- Files: `scripts/eval-hosts.json` (new), `scripts/roslynsentinel-evalhosts.ps1` (new),
  `scripts/roslynsentinel-evalhosts.Tests.ps1` (new). 3 files, 3 writes.
- Depends on: none. Pairs with: Step 1 (different language, disjoint files). Hold point: H1.
- Effect: text-neutral (nothing reads them yet).
- Change:
  - `eval-hosts.json`: `{ "hosts": [ ... ] }`, one object per endpoint with `alias`, `machine`, `baseUrl`, `backend`
    (`lmstudio` | `llama-server`), `suffix`, `default` (bool), optional `note`. Content (defaults per Decision 12):
    `112u` machine 112 `http://192.168.1.112:8888/v1` llama-server suffix `http___192_168_1_112_8888_v1` default
    true; `112lms` machine 112 `http://192.168.1.112:1234/v1` lmstudio suffix `112` default false, note "GTX 1080";
    `113u` / `113lms` the same pattern on 192.168.1.113 (suffixes `http___192_168_1_113_8888_v1` and `113`, note
    "RTX 4060" on 113lms); `85u` machine 85 `http://192.168.10.85:8888/v1` suffix `http___192_168_10_85_8888_v1`
    default true; `85lms` `http://192.168.10.85:1234/v1` suffix `85` default false. Do not invent GPU notes for 85.
  - `roslynsentinel-evalhosts.ps1` (dot-sourced library, no top-level side effects), final signatures:
    `Get-EvalHostTablePath` (returns `Join-Path $PSScriptRoot 'eval-hosts.json'`);
    `Read-EvalHostTable([string]$Path = (Get-EvalHostTablePath))` returns entries with `Alias`, `Machine`, `BaseUrl`,
    `Backend`, `Suffix`, `IsDefault`, `Note`, throwing a plain-language error (facts and options, no policy words)
    for the same invalid-table cases as Step 1;
    `Resolve-EvalHostEntry([Parameter(Mandatory)][string]$Address, [object[]]$Table)` with this order: (1) entry alias
    exact, (2) bare machine key -> that machine's default entry, (3) base URL (trailing `/` trimmed, case-insensitive)
    equal to an entry's `baseUrl`, (4) otherwise the verbatim unknown form `Alias=$Address, Machine=<suffix>,
    BaseUrl=$Address, Backend='lmstudio', Suffix=($Address -replace '[^a-zA-Z0-9]','_').Trim('_'), Known=$false`; an
    empty suffix throws the message now at `evalqueue.ps1:234`, listing the table's aliases and machine keys instead
    of "(112, 113, 85)". Branch (4) must keep `stub` -> machine `stub` working: `roslynsentinel-evalqueue.Tests.ps1`
    relies on it;
    `Get-EvalHostModels([Parameter(Mandatory)]$Entry, [int]$TimeoutSec = 5, [switch]$ReachabilityOnly)` returns
    `[pscustomobject]@{ Reachable; Models; Error }`, `Models` holding `{ Id; Loaded; ContextLength }` (`Loaded` may be
    `$null`). llama-server: ONE `GET <baseUrl>/models` gives reachability and the list (`Id` = `data[].id`, `Loaded` =
    `loaded` when present, else `status.value -eq 'loaded'` when `status` is present, else `$null`, `ContextLength` =
    `context_length`). LM Studio: `GET <root>/api/v1/models` (root = `baseUrl` minus `/v1`), `Id` = `models[].key`,
    `Loaded` = `loaded_instances.Count -gt 0`, `ContextLength` = first loaded instance's `config.context_length`; it
    counts as reachable if `/v1/models` answers even when the native list fails (`Models = @()`, `Error` names the
    failed URL). `-ReachabilityOnly` issues one `GET <baseUrl>/models` for either backend and returns `Models = @()`.
    Never throws: timeout, HTTP error or unparseable body gives `Reachable = $false` and `Error` (the 2026-11-19 case:
    `:1234` timing out while `:8888` answers);
    `Test-EvalModelMatch([string]$Requested, [string]$LoadedId)`: lower-case both, text after the last `/`;
    `Get-EvalModelLoadState([object[]]$Models, [string]$Requested)` returns `[pscustomobject]@{ State; LoadedIds }`
    with the rule of Decision 9: `Loaded` (an entry with state true matches), `NotLoaded` (some entry reports a state
    and none loaded matches; `LoadedIds` = ids with state true, possibly empty) or `Unknown` (no entry reports a
    state, or the list is empty).
  - Tests (copy the `Assert-That` pattern of `roslynsentinel-evalqueue.Tests.ps1:18-22`), temp table files: valid
    table loads; zero default / two defaults / alias equal to a machine key / duplicate alias rejected;
    `Resolve-EvalHostEntry`: bare key -> the llama-server default entry, entry alias -> that entry,
    `http://192.168.10.85:1234/v1/` -> `85lms`, `http://192.168.10.85:8888/v1` -> `85u`, `stub` -> Known false and
    machine `stub`, `http://127.0.0.1:9/v1` -> suffix `http___127_0_0_1_9_v1`; `Test-EvalModelMatch` both prefix
    directions true, case-insensitive, different models false; `Get-EvalModelLoadState`: loaded match -> Loaded;
    entries with `loaded: false` only -> NotLoaded with empty `LoadedIds`; a different model loaded -> NotLoaded with
    its id; entries with no state fields -> Unknown; empty list -> Unknown; `Get-EvalHostModels` against loopback
    `System.Net.HttpListener` servers serving the 2026-11-20 llama-server shape, a body without `loaded`/`status`,
    and an LM Studio body with `loaded_instances`, plus a closed port (`Reachable` false, `Error` set) and a
    `-ReachabilityOnly` call; the real `scripts/eval-hosts.json` loads.
- Done when: `pwsh -NoProfile -File scripts\roslynsentinel-evalhosts.Tests.ps1` exits 0, "All passed".
- Lane: `implementer` (3 files, 3 writes, 1 acceptance).

### Step 3 - Link the table into both test projects, with a shipped-table test
- Done: 9737c57d
- Files: `RoslynSentinel.Tests.ModelEval/RoslynSentinel.Tests.ModelEval.csproj`,
  `RoslynSentinel.Tests.ErrorVariants/RoslynSentinel.Tests.ErrorVariants.csproj`,
  `RoslynSentinel.Tests.ModelEval/ShippedEvalHostTableTests.cs` (new). 3 files, 3 edits.
- Depends on: 1, 2. Pairs with: Step 4. Hold point: H1.
- Effect: text-neutral (a file appears in the output folders).
- Change: add the `Content` link of Decision 1 to each csproj, inside the existing `ItemGroup` that holds
  `error-policy.md` (ModelEval) or `TestData\**\*.json` (ErrorVariants). The test (class
  `ShippedEvalHostTableTests`) calls `EvalHostTable.Load()` (shipped file, via `DefaultPath`): 6 entries, exactly one
  default per machine and that default is the `llama-server` entry, every `:8888` entry is `llama-server`, every
  `:1234` entry is `lmstudio`, and the suffixes of the three LM Studio entries are `112`, `113`, `85` (pins today's
  folders). This is the only check that the link works for ModelEval; the ErrorVariants link is checked by `Test-Path`.
- Done when: `RunTest` on `ShippedEvalHostTableTests` passes, then `Test-Path` is true for `eval-hosts.json` in the
  ErrorVariants project's output folder (the dispatcher names the folder from the `Build` result); one verification
  pass.
- Lane: `implementer` (3 files, 3 edits, 1 acceptance).

### Step 4 - `Get-EvalHostStatus.ps1` and its tests
- Done: 1ea220f7 (per-row queue matches legacy suffix folders until Step 11; Step 14 reworks grouping)
- Files: `scripts/Get-EvalHostStatus.ps1` (new), `scripts/Get-EvalHostStatus.Tests.ps1` (new). 2 files, 2 writes.
- Depends on: 2. Pairs with: Step 3. Hold point: H1.
- Effect: text-neutral (new script; the queue does not call it yet).
- Change: `#requires -Version 7.0`; params `[string[]]$Machine`, `[int]$TimeoutSec = 5`, `[int]$ThrottleLimit = 8`,
  `[switch]$Json`, `[switch]$SkipQueue`, `[string]$QueueRoot`, `[string]$TablePath`. Dot-source
  `roslynsentinel-evalhosts.ps1`; probe every entry (or the `-Machine` subset) with `ForEach-Object -Parallel
  -ThrottleLimit $ThrottleLimit` calling `Get-EvalHostModels`. Inside `-Parallel` the dot-sourced functions are not
  visible: re-dot-source the library by path in the parallel block (path through `$using:`). Queue state (unless
  `-SkipQueue`): `& (Join-Path $PSScriptRoot 'roslynsentinel-evalqueue.ps1') status -Json -QueueRoot $QueueRoot`
  (in-process; the script ends in `exit`, so read `$LASTEXITCODE` and `ConvertFrom-Json` the captured output; note
  `status` repairs orphaned jobs by writing them), group snapshots by `machine` (Step 11 adds the field; until then
  derive it by resolving the snapshot's `host` leaf as a machine key or suffix through `Resolve-EvalHostEntry`), and
  per machine report `workerAlive`, the `Building`/`Running` job (id, test, model, session or sessions, purpose,
  endpoint alias) and the queued count with their sessions. One row per endpoint: `machine, alias, backend, baseUrl,
  default, reachable, loaded` (ids with state true; empty when the host reports no state), `contextLength`, `error`,
  `queue`. `-Json` prints the array; the default is a fixed-width table plus one line per machine for the queue.
  Exit code 0 whether or not hosts answer.
  Tests: a temp table whose entries point at loopback `HttpListener` servers (one llama-server shape, one LM Studio
  shape) and one closed port; with `-SkipQueue`: row count equals entry count, loaded id and context length from each
  shape, the closed port gives `reachable = false` within about `TimeoutSec`, `-Json` parses, `-Machine` filters. A
  queue case: a temp queue root holding one hand-written job file in state `Queued` (NOT `Running`: with no worker
  lock, `status` runs `Repair-OrphanedJobs` and rewrites a `Building`/`Running` job to `Crashed`, so a hand-written
  Running job would never show) under folder `m1` and a table entry with `machine: "m1"`: the row's queue state shows
  that job, its session and a queued count of 1 (this uses the pre-Step-11 `host`-leaf fallback, so it pins that
  path).
- Done when: `pwsh -NoProfile -File scripts\Get-EvalHostStatus.Tests.ps1` exits 0, "All passed".
- Lane: `implementer` (2 files, 2 writes, 1 acceptance).

### Step 5 - Run-index helper and its tests
- Done: 68ed4226
- Files: `scripts/roslynsentinel-evalruns.ps1` (new), `scripts/roslynsentinel-evalruns.Tests.ps1` (new). 2 files,
  2 writes. Written in full (no callers yet, so no skeleton).
- Depends on: none. Pairs with: Step 6. Hold point: H1.
- Effect: text-neutral.
- Change: dot-sourced library with `Add-EvalRunIndexRow([Parameter(Mandatory)]$Job, [Parameter(Mandatory)][string]$ResultsRoot)`.
  Appends one row to `<ResultsRoot>\<Job.suffix>\runs.csv` (creates it with a header) under a named mutex
  `Global\RoslynSentinelEvalRuns_<hash of the csv path>`; builds a `[pscustomobject]` and uses
  `Export-Csv -Append -NoTypeInformation -Encoding utf8` so commas and quotes in `purpose` are quoted correctly.
  Fixed columns: `jobId, machine, host, baseUrl, backend, test, model, sessions` (joined `+`), `purpose,
  pinnedCommit, state, passed, startedUtc, endedUtc, durationSeconds, reason, runFolders` (`<Test>\<leaf>` relative
  to the suffix folder, joined `;`). A job without the newer fields leaves them empty. Never throws into the worker:
  a failed append returns `$false` and the caller logs it. `Archive-ModelTestingResults.ps1:43-75` moves old run
  folders into `<test>\Archived\<leaf>`; storing the `<Test>\<leaf>` pair lets a reader find a folder in either
  place. The file is inert to the existing folder matchers (all are directory walkers:
  `Archive-ModelTestingResults.ps1:52`, `evalqueue.ps1:804`).
  Tests: temp results root; rows for a Finished and a Refused job; header written once, column order, `purpose`
  with a comma and a double quote round-trips through `Import-Csv`, `sessions` joined with `+`, `runFolders` joined
  with `;`, a job object lacking the newer fields yields empty cells, two concurrent appends (`Start-ThreadJob`)
  both land, a read-only target returns `$false` and does not throw.
- Done when: `pwsh -NoProfile -File scripts\roslynsentinel-evalruns.Tests.ps1` exits 0, "All passed".
- Lane: `implementer` (2 files, 2 writes, 1 acceptance).

### Step 6 - Collapse the two archivers onto the shared helper
- Done: af39ed7b (stale comment at EvalHostTable.cs:197 naming the removed DeriveHostSuffix: fix in Step 15 sweep)
- Files: `RoslynSentinel.Tests.ModelEval/ModelTestingResultsArchiver.cs`,
  `RoslynSentinel.Tests.ErrorVariants/ModelTestingResultsArchiver.cs`,
  `RoslynSentinel.Tests.ModelEval/ModelTestingResultsArchiverTests.cs`. 3 files, about 9 edits. The tests are the
  existing pins, retargeted in this slice.
- Depends on: 1, 3. Pairs with: Step 5. Hold point: H1.
- Effect: behaviour-changing for ErrorVariants archives only: a base URL that matches no `:1234` entry now archives
  to its table or sanitised-URL folder instead of the IP-keyed folder (`:8888` runs of the ErrorVariants replay move
  from `ModelTestingResults\112\` to `...\http___192_168_1_112_8888_v1\`; the ModelEval archiver's folders do not
  change). Already-archived ErrorVariants runs stay where they are.
- Change: both `ArchiveRunDirectory` bodies (line 35 in each) call `EvalHostTable.DeriveSuffix(
  RoslynSentinel.Common.LlmOptions.BaseUrl)`; delete both `DeriveHostSuffix` methods, `KnownHostAliases` (ModelEval
  `:115-120`) and the comments that describe them (ModelEval 96-98 area) and the ErrorVariants `// TODO unify ...`
  header lines 5-9 (replace with one line pointing at `EvalHostTable`). In `ModelTestingResultsArchiverTests.cs`
  retarget the two `[TestCase]` methods (calls at lines 17 and 25) to `EvalHostTable.DeriveSuffix(url)` (default
  table, so they now pin the shipped table through the Step 3 link), keep a case that fails if the 8888 suffix
  changes, and fix the class summary at line 4, whose `<see cref="ModelTestingResultsArchiver.DeriveHostSuffix"/>`
  would dangle (CS1574): point it at `EvalHostTable.DeriveSuffix(string)`. Blast radius checked: `DeriveHostSuffix`
  has 2 test callers and 2 archiver callers.
- Done when: `RunTest` on class `ModelTestingResultsArchiverTests` passes, then `Build` of project
  `RoslynSentinel.Tests.ErrorVariants` has 0 errors (it has no archiver test; the build proves its edit), and
  `Search(mode: symbol, query: "DeriveHostSuffix")` finds nothing and the build shows no CS1574; one verification
  pass.
- Lane: `implementer` (3 files, about 9 edits, 1 acceptance).

### Step 7 - Front doors with a host suffix: modeleval and errorvariants
- Files: `scripts/roslynsentinel-modeleval.ps1`, `scripts/roslynsentinel-errorvariants.ps1`. 2 files, about 8 edits.
  No test merged: neither script has a test seam for host resolution (the library is tested in Step 2; the scripts
  are exercised by Step 15's live runs).
- Depends on: 2. Pairs with: Step 8. Hold point: H1 (land any time; pinned jobs run their own checkout's copy).
- Effect: behaviour-changing. `-HostAddress 85` etc. resolve through the table and now mean the `default: true`
  entry, the llama-server `:8888` one (Decision 12), with results in `http___..._8888_v1`; `85lms`/`112lms`/`113lms`
  name LM Studio; a URL equal to a table `baseUrl` takes the table's suffix (for the three `:1234` URLs that is the
  short alias, so a direct URL run no longer lands in `http___192_168_10_85_1234_v1`-style folders). Until Step 13
  lands there is no refusal preflight for a mismatched `-Model` on a llama-server host: Risks 2.
- Change: replace `$knownHosts` and the if/else (`modeleval.ps1:399-416`, `errorvariants.ps1:106-123`) with
  `. (Join-Path $PSScriptRoot 'roslynsentinel-evalhosts.ps1')` and `Resolve-EvalHostEntry`; keep the `$baseUrl` and
  `$suffix` variables the rest of each script uses; keep the unknown-address warning, now listing table aliases.
  Update both `.PARAMETER HostAddress` help texts (`modeleval.ps1:111-115`; in `errorvariants.ps1` the one around
  line 24) to "an alias from scripts/eval-hosts.json, a machine key (its default endpoint) or a base URL".
  `modeleval.ps1:157` mentions `/v1/models` listing every model; leave it.
- Done when: the PowerShell parse check passes for both files and `Grep` for `knownHosts` over both finds nothing.
- Lane: `implementer` (2 files, about 8 edits, 1 acceptance).

### Step 8 - Gate the LM-Studio-only calls by backend: client and runner
- Files: `RoslynSentinel.Common/LmStudioAgentClient.cs`, `RoslynSentinel.Common/AgentLoop/ModelAgentRunner.cs`,
  `RoslynSentinel.Tests.ModelEval/LlamaServerBackendTests.cs` (new). 3 files, about 10 edits.
- Depends on: 1, 3. Pairs with: Step 7. Hold point: H2 (Decision 10) before this step starts.
- Effect: behaviour-changing for llama-server runs only (LM Studio and unknown-URL paths unchanged). On llama-server
  `transcript.ContextLength` is now the host's `context_length` instead of null, so the context warning and
  context-exhausted check (`ModelAgentRunner.cs:481,493`) and `ReasoningReplayPolicy` see a real window. llama-server
  runs before and after this step are not comparable on those; Risks 1 and 5.
- Change:
  - `LmStudioAgentClient`: add `public string Backend { get; }`, assigned in the constructor BEFORE line 79
    (`_loadedModelStatusCheck = LogLoadedModelStatusAsync();` starts the check on the same thread and reads the
    field) and after `_httpClient` is set: `Backend = EvalHostTable.BackendFor(httpClient.BaseAddress!.ToString())`.
    `LogLoadedModelStatusAsync` (93-136): for llama-server `GET models` (relative to `BaseAddress`) and log, in the
    same wording family: confirmed loaded (`FindLoaded`), listed but not loaded (`FindListed` with `Loaded == false`),
    listed and the host reports no state (`Loaded == null`; info, not a warning), not listed. The LM Studio branch is
    untouched. `FetchContextLengthAsync` (437-470): llama-server branch reads `context_length` of the matching
    loaded-or-unknown-state entry from `GET models`, else `-1` as today. `FetchLoadSettingsAsync` (501-525):
    llama-server branch returns `ModelLoadSettings` with `BaseUrl` and `ContextLength` only (the other fields are LM
    Studio load config; leave null).
  - `ModelAgentRunner.cs:456`: `if (_llm.Backend == EvalHostTable.BackendLmStudio && IsUsageJumpImplausible(...))`,
    with a comment citing #2559 as LM-Studio-only. `IsUsageJumpImplausible` keeps its signature (`UsageJumpTests`
    pins it: 5 callers).
  - Tests (`LlamaServerBackendTests`): fake `HttpMessageHandler` in the style of `ContextExhaustedRunnerTests.cs:277`
    (`NewRunner`), with `BaseAddress = http://192.168.10.85:8888/v1/` so `BackendFor` returns llama-server through
    the shipped table (Step 3). Cases and the path each covers: `Backend` is already `llama-server` when the startup
    check runs (assert the first request the handler sees is `models`, not an `/api/` path; covers the constructor
    ordering); `GetContextLengthAsync` returns the fixture's `context_length` and makes no request to a `/api/` path
    (`FetchContextLengthAsync`, llama branch); `GetLoadSettingsAsync` returns `ContextLength` and null for the LM
    Studio-only fields (`FetchLoadSettingsAsync`, llama branch); a run through `ModelAgentRunner.RunAsync` whose
    reported `total_tokens` jump `IsUsageJumpImplausible` would flag is NOT replaced by the estimate on llama-server
    (the gate at `:456`; `UsageJumpTests` covers only the arithmetic, not the gate). The 18 existing client
    constructor callers keep their `http://localhost/v1/` base address (not in the table), so the existing LM Studio
    tests (`LmStudioAgentClientSettingsTests.cs:151`, `ContextExhaustedRunnerTests.cs:277`,
    `TokenUsageSplitTests.cs:223,232`) cover the unchanged branch.
- Done when: `RunTest` on class `LlamaServerBackendTests` passes, then `RunTest` on `ContextExhaustedRunnerTests` and
  `TokenUsageSplitTests` stays green (regression for the LM Studio branch); one verification pass. Dispatcher-run
  mutation check (Decision 14, not the implementer): with the `_llm.Backend == EvalHostTable.BackendLmStudio`
  condition at `ModelAgentRunner.cs:456` temporarily removed the gate test fails; then restore it.
- Lane: `implementer` (3 files, about 10 edits, 1 acceptance).

### Step 9 - Remaining scripts: interrogate, planstep, Probe-OvernightLogprobs
- Files: `scripts/roslynsentinel-interrogate.ps1`, `scripts/roslynsentinel-planstep.ps1`,
  `scripts/Probe-OvernightLogprobs.ps1`. 3 files, about 9 edits. No test merged (same reason as Step 7).
- Depends on: 2. Pairs with: Step 10. Hold point: H1.
- Effect: behaviour-changing. `85` becomes accepted in interrogate and planstep (it was absent from both tables);
  a machine key resolves to the default (llama-server) endpoint (Risks 9). Address forms the library does not know
  keep each script's existing fallback; they do NOT all behave identically to the library's verbatim branch.
- Change: use `Resolve-EvalHostEntry` first and fall back to the script's existing handling when `Known` is false:
  - `interrogate.ps1:130-148`: known -> the entry's `BaseUrl` with `/v1` stripped (`-replace '/v1$',''`); not known
    -> keep today's two branches exactly: a value matching `^https?://` is used verbatim (trailing `/` trimmed), and
    any other value is treated as a schemeless `host:port` -> `http://$HostAddress` with the existing warning
    (`interrogate.ps1:146`).
  - `planstep.ps1:185-196`: known -> the entry's `BaseUrl` (keeps `/v1`); not known -> verbatim with the existing
    warning.
  - `Probe-OvernightLogprobs.ps1:135`: known -> the entry's `BaseUrl` minus `/v1`; not known -> keep the existing
    `switch` default arm (`http://192.168.1.<n>:1234` for a bare number such as `114`) so that form still works. At
    `:144` use `Get-EvalHostModels` with `Get-EvalModelLoadState`-style filtering (`Loaded -eq $true`) so the "which
    model is loaded" fallback works on a llama-server entry.
  - Update the `-HostAddress` help in each (`interrogate.ps1:24-26`, `planstep.ps1:31-33`, Probe `:52`) and the
    `interrogate.ps1:41` hint (`curl http://<host>:1234/v1/models`) to name `Get-EvalHostStatus.ps1`.
- Done when: the PowerShell parse check passes for all three files and `Grep` for `knownHosts` over the three finds
  nothing.
- Lane: `implementer` (3 files, about 9 edits, 1 acceptance).

### Step 10 - Gate the replay model check by backend: ErrorVariants
- Files: `RoslynSentinel.Tests.ErrorVariants/ErrorVariantReplay.cs`,
  `RoslynSentinel.Tests.ErrorVariants/ErrorVariantReplayBackendTests.cs` (new). 2 files, about 5 edits.
- Depends on: 1, 3. Pairs with: Step 9. Hold point: H1.
- Effect: behaviour-changing for llama-server replays only (an LM Studio replay is unchanged).
- Change: `ModelsUrl` (`:643-649`) gains a `string backend` parameter: llama-server -> `<baseUrl>/models`, else
  today's `<root>/api/v1/models`. `CheckModelLoadedAsync` (`:393-436`) gains a last parameter
  `string backend = EvalHostTable.BackendLmStudio` (existing callers unchanged); for llama-server it parses with
  `LlamaServerModels` and returns the same fact-only strings ("model 'X' is known to the host but has no loaded
  instance" / "not in the host's model list (N model(s) listed)", and null when loaded or when the host reports no
  loaded state). The call site `:74` passes `EvalHostTable.BackendFor(LlmOptions.BaseUrl)`. Tests
  (`ErrorVariantReplayBackendTests`, fake handler): null for a loaded id with and without an org prefix; null when
  entries carry no state; the not-loaded and not-listed strings; an LM Studio body with backend `lmstudio` still
  returns today's results (regression for the unchanged branch).
- Done when: `RunTest` on class `ErrorVariantReplayBackendTests` passes (project `RoslynSentinel.Tests.ErrorVariants`).
- Lane: `implementer` (2 files, about 5 edits, 1 acceptance).

### Step 11 - Queue keyed by machine, with harness and tests (queue-touching; worktree; H3)
- Files: `scripts/roslynsentinel-evalqueue.ps1`, `scripts/roslynsentinel-evalqueue.TestHarness.ps1` (new,
  dot-sourced by Steps 11, 13 and 14), `scripts/roslynsentinel-evalqueue.Hosts.Tests.ps1` (new). 3 files, about 26
  edits (about 24 coupled queue edits plus 2 writes). The existing `roslynsentinel-evalqueue.Tests.ps1` is not edited.
- Depends on: 2. Pairs with: Step 12. Hold point: H3; built and tested in a worktree (Decision 8); land as one
  commit. If the retire-unpinned TODO is landing first (Decision 10) it must be committed before the worktree is
  cut.
- Effect: behaviour-changing. One folder, one mutex, one worker per machine; the worker runs the first queued job
  whose endpoint answers; `submit -HostAddress 85` stores the resolved entry. Old folders: Decision 11.
- Change (anchors are HEAD-based and shifted in the working tree: re-verify at H3):
  - Dot-source the library after line 162; add parameter `[string]$HostTablePath` (test seam, beside `-EvalScript`;
    also passed on by `Start-DetachedWorker`, 379-386).
  - `Resolve-EvalHost` (220-236): body becomes `Resolve-EvalHostEntry` plus `Stop-EvalQueue` on its throw; returns
    `Alias, BaseUrl, Suffix, Machine, Backend`. Delete the TODO at 221.
  - `Get-HostDir` (238-242): `Join-Path $QueueRoot $HostInfo.Machine`.
  - New `Get-JobMachine($Job)`: `$Job.machine`, else the machine of the table entry whose `suffix` equals
    `$Job.suffix`, else `$Job.suffix` (jobs written before this step). `Get-JobPath` (269) uses it. Legacy-folder
    jobs that are non-terminal cannot exist (H3), so `Write-Job` never needs to write back into a legacy folder.
  - `Invoke-Submit` (466-631): job record (560-604) gets `host`, `machine`, `backend`; `baseUrl` and `suffix` from
    the entry; `argsKey` (539) keeps `Suffix`. Host argument and suffix per Decision 13: when the pinned checkout
    contains `scripts\eval-hosts.json`, `host = <entry alias>` and `suffix = <entry suffix>`; otherwise (a commit that
    predates the table, or an unknown address such as `stub`) `host = <entry baseUrl>` (verbatim for an unknown
    address) and `suffix = sanitised baseUrl`. `Invoke-JobRun` (831) reads `$Job.host`.
  - `Start-DetachedWorker` (379-386): `-HostAddress` is the machine key. `Invoke-Worker` (766-772): hostDir from
    `.Machine`. Replace the worker-wide `Test-HostAnswers` (787-793, `$hostInfo.BaseUrl`) with a per-pass endpoint pick
    (Decision 9): inside the lock read the queued jobs in order; outside it probe each distinct `baseUrl` once with
    `Get-EvalHostModels` (full list for `llama-server`, `-ReachabilityOnly` for `lmstudio`) and keep the result
    (`Answers`, `Models`) for Step 13; in the dispatch lock (1066-1098) take the first queued job whose endpoint is in
    this pass's answered set (re-reading the queue inside the lock; if none qualifies any more, `continue`). If no
    queued job's endpoint answers, enter the existing `WaitingForHost` pause (1047-1063, same 30-minute give-up,
    jobs stay `Queued`). A legacy job without `baseUrl` probes the default entry of its machine. A job is never
    refused or ended for an endpoint that did not answer.
  - `Invoke-Wait` (707-714): group open jobs by `Get-JobMachine`, and pass the machine key to `Start-DetachedWorker`
    instead of `Resolve-EvalHost ... .host` (714).
  - `Get-HostSnapshot` (637-646): add `machine` (the folder leaf when it is a machine key, else the table machine
    for that leaf, else the leaf). `Invoke-Status`/`Invoke-Start` need no change beyond `Resolve-EvalHost`.
  - Known effect, no change planned: `Get-ExpectedSeconds` (HEAD 333-338) averages the recorded durations of the same
    test in the folder, so a machine folder mixes LM Studio and Unsloth history in the ETA (Risks 7).
  - Update the `.DESCRIPTION` ("one folder per host" -> per machine, the endpoint pick) and `.PARAMETER HostAddress`.
  - Harness: the throwaway repo and stub eval script (copy of `evalqueue.Tests.ps1:25-62`), `New-Queue`,
    `Invoke-Queue`, `Submit`, `Assert-That`, plus `Write-TestHostTable` (temp `eval-hosts.json`, passed as
    `-HostTablePath`) and `Start-StubModelServer` (loopback `HttpListener` serving a given body at `/v1/models`).
  - Tests (`Hosts.Tests.ps1`), through a table entry unless stated: submit `-HostAddress T` (machine key) stores the
    default entry's `host`, `baseUrl`, `suffix`, `machine`; flipping `default` in the table afterwards does not change
    that queued job; two entries of machine `T` put both jobs in one `jobs` folder named `T`; a worker for `T` runs one
    job per endpoint, one after the other; with the head job's endpoint closed and a later job's endpoint answering,
    the later job runs and the head stays `Queued`; with every endpoint closed the worker state is `WaitingForHost`
    and nothing is ended; a job file with no `machine`/`backend` fields in folder `stub` is still found by `wait` and
    `status`; the throwaway checkout has no `eval-hosts.json`, so a job for table entry `T1` gets `host` = its baseUrl
    and `suffix` = sanitised baseUrl (Decision 13); a table with two defaults for one machine makes `submit` print an
    `evalqueue error:` line and exit 2; the bare machine key is not accepted as an entry alias.
- Done when: `pwsh -NoProfile -File scripts\roslynsentinel-evalqueue.Hosts.Tests.ps1` exits 0, "All passed", and
  `roslynsentinel-evalqueue.Tests.ps1` still passes unchanged (both run by the dispatcher in the worktree; one
  verification pass).
- Lane: `implementer-senior` (about 24 coupled edits in lock-sensitive queue code plus a harness and a test file, far
  over tier 1; the endpoint pick and its in-lock re-check are concurrency decisions). Dispatch in the same worktree as
  two sequential dispatches if one runs out of budget: first the `evalqueue.ps1` edits, then the harness and
  `Hosts.Tests.ps1` (`test-writer`); they land as ONE commit, so the queue never runs half-keyed.

### Step 12 - Documentation
- Files: `docs/current/references/reference_model_eval_procedure.md`,
  `docs/current/references/reference_autonomous_run_conventions.md`,
  `docs/current/references/reference_lmstudio_loaded_models_endpoint.md`. 3 files, about 12 edits.
- Depends on: 2, 4 (names and flags of the later queue steps are fixed by this plan and re-checked in Step 15).
  Pairs with: Step 11. Hold point: H1.
- Effect: text-neutral (docs).
- Change:
  - Procedure: replace the dated "Known LM Studio hosts (as of 2026-08-30)" block (38-53) with a pointer to
    `scripts/eval-hosts.json` and `Get-EvalHostStatus.ps1 -Json`, the table's meaning of a bare machine key (its
    `default: true` entry, currently the Unsloth `:8888` one; resolved at submit), the `<machine>u` / `<machine>lms`
    alias pattern, and the llama-server model rule (loaded model required, refused otherwise; id match ignores an org
    prefix and case; a host that reports no loaded state runs the job); the `ROSLYNSENTINEL_LLM_BASE_URL` row (59)
    says "LM Studio or llama-server server URL"; line 100 (`export ROSLYNSENTINEL_LLM_BASE_URL="http://192.168.1.112:1234/v1"`)
    becomes `"<baseUrl of an entry in scripts/eval-hosts.json>"`; the `-HostAddress` description near 168; the queue
    section (195-244): one folder per machine (`%LOCALAPPDATA%\RoslynSentinel\evalqueue\<machine>\`, old URL-keyed
    folders are history only), `Refused`, `runs.csv`, `hosts`, `status -Session/-ActiveOnly`, the submit session
    warning, and that a dead endpoint does not block another endpoint's job.
  - Conventions, standing ruling (19-22): replace the two hardcoded `:1234/api/v1/models` queries with "run
    `scripts\Get-EvalHostStatus.ps1 -Json` and pick a reachable endpoint whose `loaded` holds a `qwen3.5` variant; pass
    that id with `-Model`"; the "one model per host" load/unload condition (27-28) says per machine, whichever backend
    serves it.
  - LM Studio endpoint reference: line 11 (`http://<host>:1234/api/v1/models`) is rewritten to
    `<base>/api/v1/models` with `<base>` defined as the `baseUrl` of an `lmstudio` entry without `/v1`; one new
    paragraph points llama-server users to `/v1/models` `loaded` / `status.value` / `context_length` (probed
    2026-11-20) and to the table.
  - Do not copy script help text into the docs; point at the script header.
- Done when: `Grep` for `192.168.1.112:1234` and `1234/api/v1/models` over the three files finds nothing outside the
  dated history lines; every flag named in the docs (`hosts`, `-ActiveOnly`, `-Session`, `Refused`, `runs.csv`)
  appears in `evalqueue.ps1`'s help (re-checked in Step 15 once Step 14 has landed).
- Lane: `implementer` (3 files, about 12 edits, 1 acceptance).

### Step 13 - Worker preflight, `Refused` state and index call site, with tests (queue-touching; worktree; H3)
- Files: `scripts/roslynsentinel-evalqueue.ps1`, `scripts/roslynsentinel-evalqueue.Preflight.Tests.ps1` (new,
  dot-sources the Step 11 harness). 2 files, about 10 edits (borderline for tier 1; see the lane line).
- Depends on: 5, 11. Pairs with: none (same file as Steps 11 and 14). Hold point: H3, fresh worktree off the commit of
  Step 11 (Decision 8).
- Effect: behaviour-changing: a job for a llama-server entry whose `-Model` is not loaded ends `Refused` instead of
  running (and failing inside the testhost); the index gets a row for every job the worker ends.
- Change:
  - Add `Refused` to `$terminalStates` (164) and to the `.DESCRIPTION` "Job states" list (62-67). `Invoke-Wait` exit
    codes are unchanged (anything not Finished is 2).
  - In the worker pass of Step 11, for the chosen job when its `backend` is `llama-server` (and not `-SkipHostCheck`),
    call `Get-EvalModelLoadState` on the `Models` that pass's probe already returned (no second request). `Loaded` or
    `Unknown` -> dispatch as today. `NotLoaded` -> re-read the job inside the lock and, if it is still `Queued`, set
    `state = 'Refused'`, `reason = "Not run: host <alias> serves loaded: <ids or (none)>, requested: <model>."`,
    `endedUtc`, write it, `Write-WorkerLog` it. Place the owner's wording next to the check:
    `# Policy (owner, 2026-11-20): a job whose -Model is not the loaded model is refused. Revisit if this causes friction.`
    Do not `continue` here: set `$dispatched` to the refused job and `$job = $null`, so the refusal falls through the
    single index call below and the loop then starts its next pass.
  - Run-index call, one site: after the dispatch block (`evalqueue.ps1:1099-1105`), read the job file back and when its
    state is terminal call `Add-EvalRunIndexRow -Job $final -ResultsRoot (Join-Path <sourceRepoRoot or repoRoot>
    'ModelTestingResults')` (the same base choice as `Get-RunFolders`, 800); Finished, Crashed, TimedOut, Cancelled
    during build or run, BuildFailed and Refused all go through it. Log a failed append with `Write-WorkerLog`. Jobs
    cancelled while still queued (by `cancel`, no worker) get no row; say so in the help. Dot-source
    `roslynsentinel-evalruns.ps1` beside the library.
  - Tests (`Preflight.Tests.ps1`), worker run WITHOUT `-SkipHostCheck` against loopback servers: (a) llama-server entry
    whose server lists the requested id as loaded (also with an org-prefix difference) -> job `Finished`; (b) a
    different id loaded -> `Refused`, `reason` contains `loaded: <id>` and `requested: <model>`, the stub eval never
    ran (no run folder); (c) the host answers with entries that all report `loaded: false` -> `Refused`,
    `loaded: (none)`; (d) the host answers with entries that carry no `loaded`/`status` fields -> the job runs
    (Unknown); (e) an LM Studio entry (backend `lmstudio`) with a stub server is not model-checked and runs; (f) a
    refused job is followed by the next queued job; (g) `runs.csv` has one row per ended job, including the Refused
    one, with state and commit; (h) a closed endpoint after the table said reachable is a skip/pause, never
    `Refused` (covers Decision 9's failure rule). Not covered by automation (needs a 30-minute wait): the give-up path
    after the pause; it is a LIVE check in Step 15.
- Done when: `pwsh -NoProfile -File scripts\roslynsentinel-evalqueue.Preflight.Tests.ps1` exits 0, "All passed", and
  `roslynsentinel-evalqueue.Tests.ps1` and `roslynsentinel-evalqueue.Hosts.Tests.ps1` still pass (one verification
  pass run by the dispatcher in the worktree).
- Lane: `implementer` (2 files, about 10 edits, 1 acceptance; borderline against tier 1's 15 edits and 35 tool calls
  because of the eight-case test file). If the implementer reports it ran short, split the `Preflight.Tests.ps1` work
  into a `test-writer` dispatch in the same worktree; the commit is still one.

### Step 14 - Hosts command, status filters, session warning, with tests (queue-touching; worktree; H3)
- Files: `scripts/roslynsentinel-evalqueue.ps1`, `scripts/roslynsentinel-evalqueue.Visibility.Tests.ps1` (new,
  dot-sources the Step 11 harness). 2 files, about 11 edits.
- Depends on: 4, 13. Pairs with: none (same file as Step 13). Hold point: H3, fresh worktree off the commit of Step 13.
- Effect: behaviour-changing (new command, new switches, a new warning; existing output unchanged when the new
  switches are absent).
- Change:
  - `hosts` command: add to the `[ValidateSet(...)]` at line 134 and the switch at 1117-1124; `Invoke-Hosts` runs
    `& (Join-Path $PSScriptRoot 'Get-EvalHostStatus.ps1') -QueueRoot $QueueRoot -TablePath $HostTablePath` with `-Json`
    when given (needs Step 4's script). One implementation: nothing in the queue script probes hosts for this command.
  - `status`: `-Session <name>` keeps only jobs whose `sessions` contains it; new `[switch]$ActiveOnly` keeps
    `Queued`/`Building`/`Running` (text mode then omits the "recent jobs" lines, `Invoke-Status` 674-676); both apply
    to `-Json` output too; machines with no matching job are omitted when either filter is set, and one line says "no
    matching jobs" when nothing is left.
  - `submit` (after the required-parameter loop, 467-471): new `Get-LiveSessionNames` reads `~/.claude/sessions/*.json`
    (`name`, `pid` alive via `Get-Process`, fail-soft per file; seam parameter `-SessionsDir`). If the list is
    non-empty and `$Session` is not in it, `Write-Warning` with the name and the live names; never refuse. An
    unreadable or empty list stays silent (a caller outside Claude Code, such as the tests, is not noise).
  - Help: add `hosts`, `status -Session/-ActiveOnly` and the `runs.csv` location and columns to `.DESCRIPTION`;
    `.PARAMETER Session` says "checked at submit against live sessions".
  - Tests (`Visibility.Tests.ps1`): `hosts -Json` returns one row per table entry with the queue state of a hand-queued
    `Queued` job; `status -Session a` lists only a's jobs and `-ActiveOnly` hides ended jobs (text and `-Json`); both
    filters together; submit with `-SessionsDir` pointing at a temp folder holding one live-pid session file: an
    unknown `-Session` warns (warning stream captured, live name listed) and still queues; a known name does not warn;
    an empty or missing folder does not warn.
- Done when: `pwsh -NoProfile -File scripts\roslynsentinel-evalqueue.Visibility.Tests.ps1` exits 0, "All passed", and
  the three earlier queue test scripts still pass (one verification pass run by the dispatcher in the worktree).
- Lane: `implementer` (2 files, about 11 edits, 1 acceptance).

### Step 15 - LIVE verification and closing (dispatcher; no implementer)
- Files: none changed except `docs/current/plans/plan_eval_host_table_and_liveness.md` (Status) and the `ProjectDoc`
  moves. Needs the owner's hosts and one short model run; use `evalqueue` (not a direct `modeleval.ps1` run) and a
  loaded model per `Get-EvalHostStatus.ps1 -Json`.
- Depends on: 1-14 (all committed). Hold point: H3 (queue idle by the Decision 10 one-liner before the first submit,
  and no uncommitted edits of the queue script by another session). Pairs with: none.
- Change, LIVE part: run in order and report each outcome class by name:
  1. `Get-EvalHostStatus.ps1 -Json` and `evalqueue.ps1 hosts`: **PROBE-OK** (every reachable endpoint's `loaded` equals
     a `curl <baseUrl>/models` taken at the same time; `contextLength` equals 70000 / 74752 on the llama-server
     entries as probed 2026-11-20) or **PROBE-DIFF** (name the endpoint and both values). An unreachable LM Studio
     endpoint (as on 2026-11-19) must show `reachable: false` within the timeout, not hang: **PROBE-TIMEOUT-OK** /
     **PROBE-HANG**.
  2. Submit a short test (the 2026-11-20 run used `ShippingKindStepFinal`) to `-HostAddress 85u` with the loaded id:
     **LIVE-OK** (Finished, results in `ModelTestingResults\http___192_168_10_85_8888_v1\`, one `runs.csv` row with the
     pinned commit, `agent.log` free of LM Studio load-status warnings, transcript `ContextLength` = 70000) or
     **LIVE-FAIL** (quote the log line).
  3. Submit the same test to `85u` with a model that is not loaded: **REFUSE-OK** (`Refused`, reason shows loaded and
     requested ids, no testhost started) or **REFUSE-MISS**.
  4. `-HostAddress 85` (bare): **BARE-OK** (job stores `85u`, queue folder `85`, results in the `_8888_v1` folder) or
     **BARE-MISS**.
  5. If an LM Studio model is loaded on 85, queue one job each to `85lms` and `85u` back to back: **SERIAL-OK** (the
     second starts after the first ends, one queue folder `85`) or **OVERLAP** (a GPU conflict; blocker). Skip with the
     reason if LM Studio is unreachable; do not load or unload a model without the owner.
  6. Dead endpoint does not block: with the `85lms` endpoint unreachable (or a deliberately closed table entry in a
     copy of the table), a queued job for it ahead of an `85u` job: **SKIP-OK** (the `85u` job runs; the other stays
     `Queued`) or **SKIP-MISS**; pause path if every endpoint is down: **PAUSE-OK** / **PAUSE-MISS**; otherwise record
     "untested live".
  7. Router behaviour (Risks 2): one direct `curl` of `POST /v1/responses` with a model that is listed but unloaded on
     a free machine, to learn whether the host loads it on request: **ROUTER-LOADS** or **ROUTER-REFUSES** (owner
     present only; this can swap the VRAM).
- Change, closing part:
  - Stub sweep (fails the plan if anything remains): `Search(mode: text, query: "NotImplementedException", fileGlob:
    "RoslynSentinel.Common/AgentLoop/{EvalHostTable,LlamaServerModels}.cs")`; `Grep` for `TODO implement \(Step` over
    `scripts/`.
  - Leftovers: the `# TODO share one host table` at `evalqueue.ps1:221` and the `// TODO unify` at
    `ErrorVariants/ModelTestingResultsArchiver.cs:9` are gone; no `$knownHosts` remains in `scripts/` (`Grep`); the
    docs' flag names exist in the script help (re-check Step 12's last bullet).
  - `Build` with `scripts/build.ps1 -Flavor Solution` (builds the ErrorVariants project Step 6 touched) and compare
    test results with the baselines (`docs/known-failing-tests.*.txt`, `docs/known-build-warnings.*.txt`); report only
    new failures. Run `scripts/Test-GuidanceHygiene.ps1`.
  - Set this plan's Status to IMPLEMENTED with the commit hashes; `ProjectDoc(action: close, name:
    plan_eval_host_table_and_liveness, content: <Status text>)` and the same for
    `proposal_eval_host_table_and_liveness`; the TODO.md entry "Eval host table, liveness probe and run index" moves to
    CLOSED.md (done by the same call when the TODO line links the doc).
- Done when: every LIVE class above is reported with its evidence or marked skipped with the reason, the stub sweep is
  empty, `Build` 0 errors, no new test failures against the baselines.
- Lane: dispatcher with the main session (per CLAUDE.md, `modeleval-runner` is for runs with a model under test; the
  run here is the model under test only for items 2 to 5).

## Out of scope

- Moving or renaming existing queue folders or `ModelTestingResults` folders (Decision 11); deleting the six legacy
  queue folders (optional manual cleanup after the owner agrees).
- Commit in the run-folder name or a `<sha8>` level (proposal, "Alternatives considered"): the index gives the
  grouping.
- Loading, unloading or swapping models on either backend; an LM Studio-style load-status API for llama-server.
- A submit-time model-mismatch warning (Decision 14); a "job ended" notification; queueing
  `roslynsentinel-planstep.ps1`.
- `LmStudioClient.cs` (0 references; a dead-code candidate, separate item).
- Editing `reference_lmstudio_usage_reporting_bugs.md` or other LM Studio-bug references: they stay valid for the
  LM Studio backend.

## Risks and open decisions

Decisions that were open in the draft (default entry, legacy folders, LM Studio installed, pre-table commits,
mismatch warning, helper location) are settled in `## Decision` entries 7-14. What remains:

1. **UNVERIFIED (hypothesis) - llama-server `context_length` vs usable window.** With parallel slots llama-server may
   split `n_ctx` across slots, so `/v1/models` `context_length` (70000 on 85 and 112) could over-state the per-request
   window; `/props` (`default_generation_settings.n_ctx`) would say. Step 8 uses `context_length` as the owner decided;
   Step 15 item 2 compares it with the transcript; if a run hits a context error below that figure, switch the source
   to `/props` (open item, not designed). Either way, llama-server runs before Step 8 had no context checks, so their
   results are not comparable on context-exhaustion and replay-mode fields with later runs.
2. **UNVERIFIED (hypothesis) - a router-style llama-server may load a listed-but-unloaded model on request.** The
   listing carries `"status":{"value":"unloaded"}` entries, which suggests on-demand loading. If true, a mismatched
   `-Model` sent before the Step 13 refusal exists could swap the VRAM of a machine another session is using. The
   window is real: Decision 12 makes a bare alias mean llama-server from Step 7, and Step 13 is slots later.
   Mitigations: until Step 13 lands, sessions pass an explicit `-Model` copied from `Get-EvalHostStatus.ps1 -Json`
   (Step 4) and queue runs only through jobs whose model is loaded; Step 15 item 7 settles the behaviour. If it
   proves true, consider landing Step 13 before Step 7.
3. **UNVERIFIED (hypothesis) - a non-router llama-server may list models without `loaded`/`status` fields.** The
   probe covered only the three router-style hosts. Chosen rule (Decision 9): a host whose listing reports no loaded
   state for any entry yields verdict Unknown and the job RUNS; refusal needs a host that reports state and shows the
   requested model is not loaded. Interpretation to confirm: a successful answer in which every entry says unloaded
   is a REFUSE with `loaded: (none)` (owner's "never refuse loaded: (none)" is read as covering failed requests only).
4. **Coverage boundaries (planner/reviewer).** Passing `evalqueue.Tests.ps1` after Steps 11, 13 and 14 proves only the
   verbatim-fallback (`stub`) path. The planner (this plan) assigns the table-entry, machine-folder, endpoint-pick,
   refusal and index paths to the new test files inside Steps 11, 13 and 14; the reviewer must confirm those files
   submit through a table entry and run the worker without `-SkipHostCheck` for the refusal and endpoint-pick cases (a
   test written against `stub` would pass without touching any of the new code). Because tests are now written by the
   slice's own implementer, the independent checks are the reviewer's, plus the dispatcher-run mutation check of
   Step 8 for the one gate `UsageJumpTests` cannot see. An implementer reaching a green build and the unchanged old
   test file is correct from its vantage point and says nothing about these paths. The give-up after a 30-minute
   pause and the real `~/.claude/sessions` reader have no automated test (the reader is exercised through
   `-SessionsDir` only); Step 15 is their only check. Steps 7 and 9 (front doors) have no test: they are covered by
   the library tests (Step 2) plus Step 15.
5. **Cross-plan baseline dependencies** (plan template line 27). (a) Session `roslynsentinel-b2`'s comparison runs:
   Step 8 changes what a llama-server run records, so H2 asks it to finish or pin reruns with `-Commit 83ca88dc`
   first. (b) The other session's uncommitted `evalqueue.ps1` / `evalqueue.Tests.ps1` edits (queue-wide job ids,
   `<host>/<id>` lookup): H3 requires them committed, and Steps 11, 13 and 14 are written against the committed
   version, so their anchors and the `Find-JobFile`/id assumptions must be re-read then. (c) The open TODO "Retire the
   unpinned evalqueue path" shares the dispatch lock with Step 11: sequenced, never concurrent (Decision 10).
6. **Recorded behaviour changes.** After Step 6, ErrorVariants replays on a `:8888` URL archive under the
   sanitised-URL folder, not `112`/`113`/`85`; earlier runs stay in the IP-keyed folders. After Steps 7, 9 and 11 a
   bare `85`/`112`/`113` is the Unsloth endpoint everywhere (Decision 12), so any session's habitual
   `-HostAddress 113` changes target and results folder; the `Parse-AgentLog.ps1` family takes an explicit path, so no
   script change is planned. For a pre-table pinned commit (Decision 13) LM Studio results land in the URL-keyed folder
   (for example `http___192_168_10_85_1234_v1`), not `85`.
7. **ETA history mixes backends (known effect).** `Get-ExpectedSeconds` reads the recorded durations of the same test
   in one folder; machine folders now hold LM Studio and Unsloth history together, so the estimate for a test is an
   average over both. No fix planned; keying the history by `baseUrl` would be a small follow-up.
8. **Anchor drift.** `evalqueue.ps1` is being edited by another session; every line number in Steps 11, 13 and 14 is a
   HEAD-based hint. The implementer locates by function name, and the dispatcher re-verifies the anchors at H3.
9. **OPEN (low) - `interrogate.ps1` and `Probe-OvernightLogprobs.ps1` are LM-Studio-shaped.** Their help says "LM Studio
   (port 1234)" and the probe's own notes say LM Studio's `/v1/responses` ignores `logit_bias`; with Decision 12 a bare
   `112` now reaches the llama-server endpoint, where those statements do not apply. Step 9 keeps bare = default for
   consistency and updates the help; recommendation: keep it and use `112lms` when LM Studio behaviour is the point.
   Confirm with the owner if either script is still in regular use.
