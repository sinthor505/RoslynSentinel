# Adopt LibGit2Sharp in-process for read-mostly git operations

**Status:** APPROVED (partly) 2026-11-10. The owner approved runner consolidation (step 0), the spike (step 1) and stage 1 (step 2) on 2026-11-10; all three landed the same day (see Results). Stage 2 (step 3) and restricting native binaries to win-x64 were approved later on 2026-11-10; stage 3 is proposed and not yet approved.

## Results (2026-11-10)

- Commits: step 0 `15881fa2`; step 1 `abc4152e` (LibGit2Sharp 0.32.0 in `Common`); step 2 `54f24cf7`
  (seam: `GitReader`, `CliGitReader`, `Libgit2GitReader`, `FallbackGitReader`, `GitReaderParityTests`),
  `a87a4f1a` (`ServerBinaryStaleness`, `ReadGitStamp`), `4d3e9273` (`NotFoundEvidence`,
  `ResidualMentionScanner`, `DocTrailerEngine`).
- Spike: GO. libgit2 1.8.6 (`LibGit2Sharp.NativeBinaries` 2.0.324). Native loaded and the repo opened
  from the server build output, an emulated `LaunchTestInstance` copy and an `install.ps1`-style
  publish. Loading needs the host's `deps.json` (a bare console without one got
  `DllNotFoundException`). `.git/config` has `repositoryformatversion = 0` and no `extensions.*`.
  Status matched `git status --porcelain` exactly once `IncludeIgnored=false` was set.
- Timing, median of 10, fresh `Repository` per call (CLI / libgit2): HEAD sha 56 / 3 ms; status
  74 / 28 ms; `log <sha>..HEAD --name-only` (20 commits) 81 / 76 ms. First libgit2 call pays about
  38 ms of native load.
- Packaging: the package copies natives for every RID (about 13 files, 22 MB) into each output.
- Accepted behaviour differences (each commented at its caller): commit path lists are not
  rename-detected (a rename out of server source now reads stale); short sha is libgit2's minimum-7
  abbreviation; same-second commit order on parallel branches can differ; `DocTrailerEngine`
  collapses two git-failure messages into one; conflicted status entries report `UU`; pathspec
  magic falls back to the CLI; `GitWorktreeManager.RunGit` gained a 10-minute timeout.
- Fallback observability: first fallback per exception type is logged to stderr; `FallbackGitReader`
  exposes backend counters, and tests fail on a silent fallback.
- Later the same day: win-x64-only natives `23bcaa73` (`Directory.Build.targets`; 21.4 MB to 1.9 MB
  per output); stage 2 `991ce300` (status), `84f179f6` (log), `2c45fa49` (path classification),
  `654308b3` (branch/stash lists), `1421be4a` (diff/show nameOnly); step 3b warmup `bb00f4bb`;
  short-hash probe `b1cdb0cd`; commit-hash race fix `7c295b47`. Full suite green at `bb00f4bb`.
- Stage 2 outcome: libgit2 serves status, log (plain/paths/count), path classification, branch and
  stash lists, diff/show nameOnly for staged and `refA..refB`, show header. CLI keeps tag and
  worktree lists (too many decline rules), working-tree diffs (`core.autocrlf` EOL semantics differ),
  stat and patch text (not byte-identical), pickaxe/follow, and all mutations.
- Measured: `Git(status)` 216 ms CLI to 43 ms; `check-ignore` for 20 paths 1120 ms to 7 ms; stash
  list 55 ms to 4-7 ms. Live server after restart: `gitBackend` reports `Libgit2`, native load and
  repo probe ok, `shortHashLength` 8, zero CLI fallbacks. The bundled libgit2 build reports Https
  and Ssh support.
- Commit trailers `ebde548e` (opt-in): `git config roslynsentinel.commitTrailers true` makes
  `Git(commit)` add `Tool-Call-Id: <echoed toolCallId>` (48 random bits) and, when the call has an
  agent id, `Tool-Agent: <id>`; amend replaces them. The tool finds its commit by walking up to 50
  commits from HEAD for the id, else from git's summary line, else from the parent chain (merge
  resolutions accept MERGE_HEAD). Read with normal git precedence, so a global setting also enables
  it. Enabled on this repo 2026-11-10. Revert carries no trailer yet (`// TODO` in `RevertAsync`).
- Risk not yet tested: `worktree.useRelativePaths=true` (git 2.48+) writes
  `extensions.relativeWorktrees`, which libgit2 1.8.6 probably rejects; reads would fall back.

## Motivation

RoslynSentinel shells out to `git.exe` through four separate runners that behave differently, and
the process-spawn path is both slow (one process per call) and flaky (pipe EOF races). Text parsing
of git output is a second recurring cost. All claims below were verified against source on 2026-11-10.

### Four runners, four behaviours

| Runner | Location | Behaviour |
| --- | --- | --- |
| `GitProcess.TryRun` | `RoslynSentinel.Common/GitProcess.cs` (class spans :12-121, `TryRun` at :28) | Synchronous; dedicated reader threads; timeout plus kill-tree; sets `GIT_TERMINAL_PROMPT=0` (:43) and `GCM_INTERACTIVE=never`; no cancellation; no explicit encoding. |
| `GitWorktreeManager.RunGit` | `RoslynSentinel.Common/GitWorktree/GitWorktreeManager.cs:210-238` | Sequential stdout-then-stderr reads (can deadlock on large stderr); no timeout. Existing `// TODO` at :233 already says to migrate onto `GitProcess`. |
| `GitImpl.RunGitAsync` / `RunGitBytesAsync` | `RoslynSentinel.Tools.Basic/GitImpl.cs:1648-1733`, `:1742` (bytes variant) | About 69 call sites. Absolute exe via `ResolveGitExecutablePath` (:2102); UTF-8 encodings; `-c i18n.logOutputEncoding=utf-8` / `-c i18n.commitEncoding=utf-8` prefix (:1682, :1684, :1761, :1763); 30 s timeout; `BeginOutputReadLine` lines re-joined with `AppendLine`, so output gains CRLF line endings and a trailing newline that git never emitted. |
| `GitTestRepo.RunGitRaw` | tests only | Isolated config. |

### Text formats parsed today

Porcelain v1 `-z`, `status --porcelain`, `worktree list --porcelain`, `for-each-ref` with `%09`
separated fields, `log --format=%H%x1f%B%x1e`, `log --name-only`, `stash list`, and unified diff
(`GitHunkParser`). Each is a hand-written parser over text that depends on git version, locale and
config.

### Spawn cost and the flake

- A single `Git(status)` tool call costs two process spawns; `ServerBinaryStaleness` costs three
  (`rev-parse HEAD`, `log --name-only`, `status --porcelain`; `ServerBinaryStaleness.cs:88`, `:241`, `:271`).
  These figures are call counts read from source, not timings; the spike (step 1) measures them.
- `docs/current/issues/issue_process_spawn_test_flake.md` records stdout/stderr EOF arriving after
  process exit, hitting `ServerBinaryStalenessGitTests` and `DocTrailer*` tests. The failure class
  (pipe EOF, timeout, kill-tree, PATH resolution, encoding, CRLF re-join) disappears for any
  operation that never spawns.

### Environment facts

- All hosting projects target net10.0 with framework-dependent publish and no RID
  (`scripts/install.ps1:14`).
- No prior mention of libgit2 anywhere in the repo.
- Reference clone at `C:\Users\Administrator\source\repos\libgit2sharp`, version 0.32.0 (targets
  `net472;net8.0`, `IsTrimmable`, `LibGit2Sharp.NativeBinaries` 2.0.324). It has Worktree, Stash,
  Blame, Diff and `Commands.Stage`/`Commands.Remove`. Whether the net8.0 asset loads cleanly on
  net10.0 is assumed, not yet shown; the spike checks it.

## Proposal

### Step 0 - consolidate the runners (APPROVED, precursor, no native dependency)

Move `GitWorktreeManager.RunGit` and `GitImpl.RunGitAsync`/`RunGitBytesAsync` onto `GitProcess`.
Extend `GitProcess` with: async entry points and a `CancellationToken`, an explicit encoding,
bytes output, absolute-exe resolution (from `ResolveGitExecutablePath`) and the `-c i18n.*` prefix.
Keep each wrapper's existing signature so the ~69 callers do not move. This also removes the
CRLF/trailing-newline re-join behaviour of `RunGitAsync`; callers that depend on it must be checked
(open item, see Open questions). Closes the `TODO` at `GitWorktreeManager.cs:233`.

### Step 1 - spike (APPROVED; go/no-go gate for step 2)

- Add a `PackageReference` to `LibGit2Sharp` in `RoslynSentinel.Common`.
- Verify the native library loads from: the server build output, the `LaunchTestInstance` copy, and
  the `scripts/install.ps1` publish output.
- Open this repo and a PlanStepRunner harness worktree; inspect `.git/config` for `extensions.*`.
- Time libgit2 against the CLI for `status`, `rev-parse HEAD` and `log` on this repo.
- Exit criteria (proposed, not yet fixed by the owner): native loads in all three locations; both
  repo kinds open; measurable per-call win; no unexplained `LibGit2Exception`.

### Step 2 - stage 1: internal typed git reader seam (APPROVED, contingent on step 1)

An internal typed reader interface in `Common`, libgit2-backed, falling back to the CLI runner on
`LibGit2Exception` or `DllNotFoundException`. Move only internal read probes (not the `Git` tool):

- `ServerBinaryStaleness`: `rev-parse HEAD`, `log --name-only`, `status --porcelain` (`:88`, `:241`, `:271`).
- `TestInstanceLauncher.ReadGitStamp` (`:676-690`).
- `NotFoundEvidence` `show HEAD:file` (`:334`).
- `ResidualMentionScanner` `ls-files` (`:235`).
- `DocTrailerEngine` `log` (`:922`).

Parity tests on `GitTestRepo` fixtures run each probe through both backends and compare results.

### Step 3 - stage 2: `Git` tool reads (APPROVED 2026-11-10)

`status`, `log`, `show`, `diff`, `ls-files`/`ls-tree`, `check-ignore`, and `tag`/`branch`/`stash`/
`worktree` list. Typed results replace the text parsers listed above where parity tests pass.

### Step 3b - backend warmup and status (APPROVED 2026-11-10, after stage 2)

- A singleton holds a captured, never-faulting `Task<GitBackendStatus>` started at host start and
  not awaited: try/catch around `GlobalSettings.Version` (forces the native load without a repo),
  returning `Libgit2` + version or `CliFallback` + exception type/message. A failure logs once to
  stderr from the task.
- After `LoadSolution`, a second captured task awaits the first, then runs `GetHeadSha` on the
  solution repo inside `using` (catches repo-specific rejections such as unsupported extensions).
  `LoadSolution` does not await it.
- No bounded wait on the git call path: LibGit2Sharp loads natives in a static type initializer,
  so a racing call blocks on the same initializer, and a failed load is cached as
  `TypeInitializationException`, which the fallback already catches.
- Short-hash length (owner decision 2026-11-10): git's abbreviation length depends on the repo's
  packed-object count (7 below 16,384 objects, 8 below 65,536, 9 below 262,144, ...); libgit2 has no
  such rule and the object count is not portable to compute (multi-pack-index). Probe it with one
  `git rev-parse --short HEAD` and pass its length as the minimum to `ShortenObjectId`. Cache keyed by
  the repo's git common dir (worktrees share it); refresh on every `LoadSolution` (part of the second
  warmup task); probe lazily on first use for a repo reached via `repoPath`. If the probe fails, decline
  to the CLI. Replaces the fixed-7 `MinimumShortHashLength` and the multi-pack-index decline in
  `PackedObjectCount`. A fixed minimum of 8 was rejected: it matches this repo only (test fixtures and
  small repos are 7, large ones 9+).
- `McpServerStatus` is the only waiter: `WaitAsync` with about 2 s, reporting `"warming"` on
  timeout; a `gitBackend` block shows active backend, libgit2 version and first fallback reason
  (from `FallbackGitReader`'s counters).

### Step 4 - stage 3: index writes (PROPOSED, not approved)

`stage`, `unstage` and hunk staging. No LibGit2Sharp equivalent of `git apply` (hunk staging) was
found in the reference clone; it would need index blob construction, with `.gitattributes` EOL
filter handling reproduced by hand. This is the riskiest stage and may be dropped.

### Stays on the CLI (decided for this proposal)

- `commit`: hooks (pre-commit, commit-msg) and GPG signing.
- `push`, `fetch`, `pull`: credential helpers and SSH.
- `revert`, `stash apply/pop`: conflict semantics.
- `worktree add/remove`: locks and long paths.

### Rules that apply to every libgit2 call

- Open a `Repository` per call inside `using`; never cache one. An open repository holds pack-file
  handles on Windows, so worktree or directory deletion fails until it is disposed.
- Any `LibGit2Exception` or `DllNotFoundException` falls back to the CLI runner, so a repo feature
  libgit2 cannot open degrades to today's behaviour instead of failing.

## Alternatives considered, not pursued

- **Consolidate runners only (no native dependency).** Fixes the inconsistency, not spawn cost or
  text parsing. Not rejected: it is step 0 and is being done regardless.
- **Out-of-process LibGit2Sharp helper with structured (JSON) output.** Buys isolation: killable,
  handles released on exit, a native crash cannot take down the server. But spawning a .NET process
  per call is slower than `git.exe`, so only a persistent helper would win, and that adds lifecycle,
  versioning and schema-contract cost. Revisit only if the spike or stage 1 shows native instability
  or unmanageable handle locking.

## Open questions

- May `Git(commit)` ever skip hooks? Currently: no. (Affects whether commit could move in-process.)
- Do this repo or harness worktrees use extensions libgit2 cannot open (reftable, sha256, some
  index extensions)? The spike answers this.
- Does anything depend on `RunGitAsync`'s CRLF plus trailing-newline output shape? Step 0 must
  audit the ~69 call sites and the parsers before removing it. Not yet audited.
- Does LibGit2Sharp 0.32.0 (net8.0 asset) load on net10.0 and honour `core.autocrlf`/`.gitattributes`
  the same way git does for `status` and `diff`? Hypothesis; spike and parity tests decide.
- Exact spike go/no-go thresholds (see step 1).

## Cost / risk

- **No hooks.** libgit2 runs none; hence commit stays on the CLI.
- **No automatic signing.** `commit.gpgsign` is ignored (manual signing is possible); another
  reason commit stays on the CLI.
- **No credential helpers/GCM, limited SSH.** Network operations stay on the CLI.
- **Handle locking on Windows.** Mitigated only by the per-call `using` rule above; a leaked
  `Repository` blocks worktree deletion, which the PlanStepRunner harness depends on.
- **Native calls cannot be timed out or killed.** A hung libgit2 call hangs a server thread, unlike
  the CLI's timeout-and-kill-tree. Reads on local repos only in stages 1-2.
- **Newer repo formats.** Reftable, sha256 and some index extensions may not open; CLI fallback
  covers this, at the price of two code paths.
- **Dual implementation.** Needs CLI-vs-libgit2 parity tests (`GitTestRepo` fixtures) and ongoing
  maintenance until the CLI path can be deleted for a given operation.
- **Packaging.** The native `runtimes/` folder (a few MB) must survive `scripts/install.ps1`
  and the per-session instance copies; a missing native asset surfaces as `DllNotFoundException`
  and falls back to the CLI, so the failure would be silent. Add a startup log line or test that
  reports which backend is active.
- **Touches:** `Common` (new dependency and seam), `GitProcess`, `GitWorktreeManager`, `GitImpl`,
  the five stage-1 probes, install/launch scripts. Layering stays one-way (`Common` is the lowest
  layer, so Engines and Tools can both reach the seam).
- **Test-coverage caveat.** Parity tests only cover the operations and fixture repos they
  construct; they do not prove behaviour on repos with hooks, extensions, `.gitattributes` filters
  or worktrees unless a fixture exercises each. The planner (this doc's successor plan) owns
  specifying those fixtures; a reviewer owns confirming they exist.

## Related

- `docs/current/issues/issue_process_spawn_test_flake.md` - the spawn flake this reduces (stage 1
  removes the spawn from the affected `ServerBinaryStaleness` and `DocTrailer` paths).
- `RoslynSentinel.Common/GitWorktree/GitWorktreeManager.cs:233` - existing `TODO` closed by step 0.
