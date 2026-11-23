# Finding: recurring tool pain points across 582 tool-experience journals (2026-10-01 to 2026-11-14)

**Status:** FIXED 2026-11-15. R1-R25 and D1, D2, D4-D7 implemented by plans/closed/plan_tool_experience_journal_fixes.md (6bf9025b..54d071e8); D3, D9-D12 declined, D8 deferred, the dead drift-halt removal is in TODO.md.

## Context

`/journal-review all` on 2026-11-14 rolled up every journal not covered by the 2026-10-01 rollup:
582 sessions, 229 with entries or call logs. Source: `scripts/Get-JournalDigest.ps1 -All -NoMark`
into `.claude/journal/digest_rollup_20261114.md` (local-only, not in git; summary in
`.claude/journal/ROLLUP.md`, section `Rollup 2026-11-14`). Three Haiku agents clustered the 1702
bad/mixed entries by theme.

Entry ids are `<sid8>:L<line>` in `.claude/journal/<date>_<sid8>.md`. Journal entries are
impressions, not evidence; counts are approximate cluster tallies, and nothing below was verified
against source unless stated. Several themes may already be fixed (noted where an entry or
CLOSED.md says so).

## What is broken

Call log totals: ReadFile 22249 calls, Search 19076, ReplaceSnippet 6728, Git 5881,
GetMethodSource 5587, RunTest 4863, Member 4076, GetFileOutline 2872, Build 2767. 3778 MCP calls
failed (Search 993, ReplaceSnippet 854, Member 480, ReadFile 373, GetMethodSource 252). 1256 C#/shell
fallbacks.

### Severe one-offs (dispatched)

| # | Tool | Symptom | Entry |
| --- | --- | --- | --- |
| S1 | Git | A commit returned another session's hash (hypothesis in the entry: CommitAsync reads HEAD after committing) | e4fee6e5:L5 |
| S2 | ModifyAttribute | Batch reported "Applied 7" but silently dropped 2 nested edits; only a Git diff caught it | 33466d81:L7 |
| S3 | batch / EOL | EolChangeRefused returned IsError without throwing; the batch step ignored it and the bridge reported success with nothing written | 1b5c5f16:L35 |
| S4 | Member | `autoStage=false` wrote nothing yet returned a full-file result | 1b945655:L3 |

Investigation outcome 2026-11-14 (source read, git log and tests; no live write test). All four are
already fixed in current source; no code change was needed.

| # | Root cause (file:line) | Outcome |
| --- | --- | --- |
| S1 | Old CLI path read HEAD after committing (`RoslynSentinel.Tools.Basic/GitImpl.cs` `DeriveNewCommitHashAsync` :4171, `IsOwnHead` :3993 now guard it); the in-process route returns libgit2's own commit sha (`RoslynSentinel.Common/Libgit2GitReader.CasCommit.cs:147`) | already fixed in 7c295b47 (blocker closed); 13 tests in `GitCommitHashRaceTests` pass |
| S2 | Old engine folded whole-node `ReplaceNode` edits, so a type-level edit overwrote a nested method edit, and "Applied" counted requested edits (`RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs:1514`, `RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs:337,426-439`) | already fixed in 44ecdc0f (text-span edits, count of edits that changed text); `ModifyAttributeBatchTests` pass |
| S3 | `AsyncBatchEngine.RunBridgeBatchAsync` Step D discarded the `ApplyChangesResult` (`RoslynSentinel.Engines.Advanced/AsyncBatchEngine.cs:918-945`); producer `RoslynSentinel.Common/PersistentWorkspaceManager.cs:2506-2511` returns IsError without throwing | already fixed in 40c6cc58 and 13b2cbb1 (Step D checks `IsError`, ledgers and skips). Not traced: Tools.Basic callers of the chokepoint outside the bridge, and the `IsError && ValidationResult != null` suppression guards at `AsyncifyTools.cs:2165,2310,2670,2832` |
| S4 | `autoStage=false` returned early before `ValidateAndApplyAsync` (`RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs:843,902,~960`) and echoed the full file | already fixed in 3890b992 (parameter hidden from the schema, hardcoded true); `not_written` status in e198987a. Dead `!autoStage` branches remain (cleanup needs a decision on whether staging returns) |

Other severe one-offs (not dispatched): a subagent reported "build 0 errors" beside a CS0266
(a08be84f:L18); staged hunks did not compile, flagged only by a count warning (20bd4a9c:L14);
a ReplaceSnippet batch left a CRLF file mostly LF (96b0b939:L8); a MoveMember call reformatted 1178
lines of SymbolNavigationEngine.cs (51989d5e:L6); a failing SetUpFixture OneTimeTearDown shows only in
`stderrTail` (199a9686:L48).

### Line-ending loop (dispatched)

CreateFile, WriteFile, Member and ReplaceSnippet insert bare LF into CRLF files (~45 mentions:
4f9f5991:L112, cd50d561:L30 still after a restart, 96b0b939:L8, 38d5c214:L12). No MCP tool reports a
file's EOL, so agents count bytes in PowerShell (~25 fallbacks, 51989d5e:L12, 4d6f18ec:L3) and repair
with `scripts/Set-CrlfEndings.ps1` (~15, b6702bd9:L6). An entry mentions a planned "Step 7"
(CreateFile honours the resolved EOL).

Investigation outcome 2026-11-14: already fixed. Step 7 is in `docs/current/plans/closed/plan_eol_policy_and_write_chokepoint.md`
(DONE 2026-10-26, steps 0-10): every write converts at the chokepoint
(`RoslynSentinel.Common/PersistentWorkspaceManager.cs:2503` `ApplyProposedChangesCoreAsync`, `:3067`
`ComputeEolPrePass`, `RoslynSentinel.Common/EolWritePolicy.cs:11`), default on in 426d8b83 (chokepoint
a4a6e114..df0ad2d9, new-file writers 64c5d590, engines f9bed9f9..8cc62db6); Step 7 was already satisfied
by Step 5, pinned by `CreateFileDeleteFileTests.cs:1051,1072` (pass, with the Git/ModifyAttribute/Member
tests above: 101 of 101 in Tests.Tools.Basic, full suite). The cited journal entries all predate
2026-10-26 or a stale binary. Read side: `ReadFile` already has `showLineEndings: summary|all`
(4d7c4486) and a mixed-EOL finding (1d63c3e2); agents appear unaware of it. Residual, no decision
needed so left unfixed: `ReadFileWithoutSolutionAsync` (`WorkspaceFileEditImpl.cs:560`) ignores
`showLineEndings` (existing TODO); `CreateFile` scaffold uses literal `\n` (converted by the chokepoint,
so harmless); stale `WriteFile` "provided EOL" comment; the launch script pins `--eol=crlf` only when
the cwd is inside the repo (`scripts/roslynsentinel-mcp-launch.ps1:907`). Needs owner decision (response
shape): report the active `--eol` mode in `McpServerStatus`; recommend adding it, one field.

### Recurring themes (open)

1. **Hooks block benign text (~50; enforce-dogfood, Bash/PowerShell hooks, Stop hook).** Literal
   `.cs`/`git` words in Markdown, journal appends or tracker notes trip the hook; Grep glob `!*.cs`
   counts as C# access (2b98a3f7:L6, 72f838ac:L4, b6702bd9:L4, 199a9686:L26, 9da85433:L69). Shell
   writer rules were fixed (701ff254:L3); prose matches still reported. Observed again this session:
   journal-only PowerShell was logged as a C# fallback.
2. **Missing `reason` or guessed parameter names (~40; Search, ReadFile, Git, ReplaceSnippet).**
   `isRegex`, `path`, `projectName` in text mode, `containingType` on references, `oldSnippet` before
   schema load, Git `path`/`maxCount`/`files` (6bf6f952:L19, b6702bd9:L60, e9d3e49c:L129,
   784ea762:L4, 5c6305b5:L5, 78e2740c:L60). The offload hint omits `reason` (6bf6f952:L34).
3. **Toolsets missing (~35; McpToolsetControl, ToolSearch, GitRemote, WriteFile).** Enabled toolset
   absent from the client's deferred list; only `/mcp` reconnect helps (1b5c5f16:L20, 01fccc92:L3-L4);
   stateless HTTP test instances drop toggles (bfb6d753:L148); subagents cannot see enabled toolsets
   (bfb6d753:L16). Response text fixed in a9ddcbc4.
4. **ExternalFileDrift halts (~30).** SessionHalted from peer commits or timestamp-only drift; flags do
   not say committed vs dirty vs timestamp-only (199a9686:L37, cf16109a:L7, 302d73db:L3). `Acknowledge`
   rejects `filePath` (needs `files`) and refuses the whole call if one name is unflagged
   (11654969:L26, b6702bd9:L25). `List` is a flat dump with no git context (b6702bd9:L42).
5. **Refactor tools leave call sites broken (~30; MoveMember, ChangeSignature, RenameSymbol, Inline,
   MoveType).** Record primary constructor refused (8e853f06:L31); instance-to-static leaves 28 callers
   broken (51989d5e:L17); CS0426 after a move (78e2740c:L79). Inline argument binding fixed in 538e662f.
6. **Search misfires (~30).** Declaration modes ignore `query` and dump every record (748a8fb9:L9,
   33466d81:L34, 5330 methods); `fileGlob` with project-prefix, path, brace or test-project forms matches
   0 files and the error blames the glob (51989d5e:L3, 11654969:L3, 528e5bee:L4); text-mode
   `projectName` is a no-op (cc715aa0:L20); Ambiguous overloads have no selector (cf16109a:L23).
   Planned in plan_search_kind_mode_query_filter.
7. **Undocumented caps and offloads (~30).** ReplaceSnippet 20-entry batch cap and newContent limit
   (4000 or 6000 per entry) absent from the schema (0a2842fb:L5, 2bb2767b:L42); ReadFile ranges over
   ~15 KB and large GetMethodSource results offloaded as escaped pages with no line window
   (b1dcd60b:L3, cd9f732e:L19); a range silently trimmed (20bd4a9c:L11); GetLargeResult last page
   contradicts the cap (6bd1bbc5:L10).
8. **RunTest (~30).** Passing-test output not returned, errorMessage cut at ~100-300 chars,
   `includeOutput` ineffective (47b2c93d:L31, bfb6d753:L105); a filter matching 0 tests reports success
   (b1ec52ca:L7, e9d3e49c:L153); `projectName` rejected in favour of `scope`+`scopeName` while Search
   takes `projectName` (50e0e6ac:L27); the 600 s timeout aborts the whole result (a08be84f:L15).
9. **Compile gate forces leaf-first ordering (~20; Member, ReplaceSnippet).** Helpers must exist before
   callers (0734e848:L6, 7918919b:L3, 784ea762:L7). Related: Member remove counts doc crefs as callers
   and `skipPrecheck` does not bypass the second guard (1b5c5f16:L46, 199a9686:L40).
10. **Failures reported as success (~9).** InspectSymbol isError:false with zero call sites
    (db2b9846:L4); non-existent destination gives a silent no-op (bfb6d753:L132); applied edits marked
    `validated:false` (e9d3e49c:L9); successful ReplaceSnippet returns no changeId, so UndoLastApply
    cannot undo (01fccc92:L5, cf16109a:L89).
11. **Dispatch and subagent plumbing (~32).** Background child reports do not reach the spawning
    orchestrator (bfb6d753:L52, 0cbf9302:L3, f484ae93:L5); the dispatch-brief hook is strict about
    label shape and counts `.cs` names in prose as files (78e2740c:L88, 0a2842fb:L7); dispatch limits
    count one RenameSymbol as 44 files (5f36abb8:L7) and count schema loads as tool calls (9da85433:L8).
12. **Git (~25).** Whole-file staging cannot split another session's hunks (6e6bf59a:L5); `hunkIds`
    misplaces pure-insertion hunks (20bd4a9c:L14); large diff/show ignores `maxBytes` (8d092009:L3);
    no mv, restore, config, worktree or `apply -R` operations, so shell fallbacks (f70d7732:L38,
    91761378:L6, e4fee6e5:L7). GitRemote config fixed in dfa5d985.
13. **Build (~13).** `fullBuild` refused by a peer's `dotnet test` lock, one pid named at a time
    (9da85433:L20); ErrorCount hidden inside an offloaded result (33466d81:L30); `scope=project` builds
    the whole solution silently (2a2aff46:L3).
14. **McpServerControl (~15).** `StopServer` returns "Connection closed" although the restart works
    (8e853f06:L30); restart can serve a stale commit-keyed build (4d6f18ec:L7); `LaunchTestInstance`
    ignores a class name in `includeTools` and has no env param (bfb6d753:L147).
15. **GetMethodSource / ReadFile details (~15).** Overloads return the first match or Ambiguous with no
    selector (f70d7732:L9); envelope `isComplete`/`lineCount` describe the file, not the method
    (e0855523:L4, fixed per 6d2c68e2:L4); ReadFile `memberName` misses point to `contextSnippet`, which
    ReadFile lacks (dc99e388:L8).
16. **CreateFile seeds an empty type with no content param (~7)** (33466d81:L28, ebd9923b:L8).
17. **No by-name file lookup on the active surface (~9)**, so agents use Glob (cf16109a:L35,
    eb1f4d05:L7).
18. **Helper-script defects (~18; evalqueue, Invoke-WorktreeTests).** Comma-joined `-Id` under
    `pwsh -File` (47174bf0:L10); git-spawn test flakes (91761378:L10). Several fixed (a31ade5e,
    422eb786, 3fd4cd9c).

### What works (positive entry counts)

ReplaceSnippet 109, Git 90, Search 90, RunTest 76, dispatch 31, Member 26, ReadFile 23, WriteFile 22,
Build 21, McpServerControl 18. Unknown-parameter errors that list the valid names unstick agents in
one call (33466d81:L24, cc715aa0:L15).

### Recency check (2026-11-15)

`Get-JournalDigest.ps1 -Days 14 -NoMark` (sessions 2026-11-02 to 2026-11-14, 141 sessions, 522
bad/mixed entries; `.claude/journal/digest_last14_20261115.md`). Every theme 1-18 still has recent
entries. Largest: Git (~40), hooks (~40), Search (~35), RunTest (~30), toolsets (~25), caps (~25).
Top failure buckets: Member InvalidArgument 146, Member ValidationFailed 119, Search InvalidArgument
115, ReplaceSnippet Exception 90, ReplaceSnippet NotFound / ValidationFailed 66 each.

New recurring themes (not above):
- Filtered test runs miss cross-cutting ratchet tests; shared-tree results are order-dependent (~8;
  c0beb6fc:L10, ce0dc57f:L22, 9da85433:L32).
- EditAttribution findings on unrelated calls or naming the wrong writer (~5; 44eba0b8:L36,
  47174bf0:L12, ed2e51fa:L34).
- DTO-justification hook false positives on service classes (~5; bfb6d753:L142, 9da85433:L14).
- Malformed or escaped tool-argument JSON (~4; bdb7540c:L6, bfb6d753:L71).
- Line-ending loop still reported after the default-on fix 426d8b83 (2026-10-25): bdb7540c:L4
  (2026-11-02), bfb6d753:L40 (2026-11-04), 9da85433:L29 (2026-11-05). Not traced; candidates are the
  CreateFile scaffold's literal `\n`, ReadFile ignoring `showLineEndings` without a solution, or a
  stale server binary.

## Root cause

The five dispatched items (S1-S4, line-ending loop) were traced and are already fixed; see the
outcome notes under each.

### Investigation results (2026-11-15)

Seven read-only investigations traced themes 1-18, the new themes and the top error buckets against
source at HEAD b5a267ab. No live reproduction except a few Search calls; the live server queried was
stale (servedSha c3efa20e, ~80 commits behind, launcher `pullError: Diverging branches can't be
fast-forwarded`), which also explains many post-fix journal reports.

**Already fixed (journal entries predate the fix):**
- ReplaceSnippet Exception x90: compile-gate rejection mislabelled; relabelled ValidationFailed in
  22645c8b (2026-11-10). ReplaceSnippet changeId on success: 4a6772ef.
- Member addMember InvalidArgument (110 of 146): one-declaration rule; multi-add in 0c47145b
  (2026-11-14). CreateFile `content` param: 25be5c3e (theme 16).
- Search declaration modes honour `query` (plan_search_kind_mode_query_filter, 2026-10-08); fileGlob
  zero-match is a warning with samples; guessed `isRegex`/`path` are rewritten.
- Line-ending reports after 426d8b83 were read-side (no tool reported EOL until ReadFile
  `showLineEndings`, 4d7c4486, 2026-11-05); no LF-writing path found.
- Refactor: instance-to-static (b773820d), extension moves refused (39e64481), ChangeSignature
  Ambiguous selector, MoveType CS0426 (NestedTypeReferenceLifter).
- Git: pure-insertion hunks (a4b69685), param aliases, GitRemote config (dfa5d985).
- Build ErrorCount inline; scope=project honoured. ExternalFileDrift latch removed (1c9cb4db).
- McpServerControl StopServer "Connection closed" (f03b99c0). RunTest projectName rewrite (892e59fe),
  0-test wording (aad595db). InspectSymbol NotFound, ExtractMembers missing destination (4f511a8c).
- Helper scripts: evalqueue -Id (3fd4cd9c), -Projects split (7843fba2).

**Still present, no owner decision needed (text, hook or behaviour; no new param or shape):**

| # | Item | Root cause | Size |
| --- | --- | --- | --- |
| R1 | Journal logger flags prose `.cs`/`dotnet test`, variable-held journal paths, Grep `!*.cs` | `.claude/hooks/journal-log-call.ps1:29,33,40,41` | ~25 lines + 4 hook tests |
| R2 | Dogfood git rule matches `; git` inside quoted prose; journal exemption needs literal path, no handoff | `enforce-dogfood.ps1:1388,1519` | ~30 lines |
| R3 | Dispatch brief: label rule (20 chars to colon) not stated; `.cs` in prose counted as files; sweep file count for RenameSymbol | `enforce-dogfood.ps1:1173-1181,1226` | ~15 lines |
| R4 | DTO hook decides by name suffix only (30 DTO-EXEMPT overrides) | `enforce-dto-justification.ps1:58-60,133-136` | 2 lines (suffixes) or ~25 (body check) |
| R5 | `Invoke-WorktreeTests.ps1` ignores dotnet exit code; teardown-only failure prints all passed | `scripts/Invoke-WorktreeTests.ps1:285,291,373` | ~8 lines |
| R6 | Search rejects mode-ignored params (main InvalidArgument source since 101ec745): honour or ignore with a note (text `projectName` -> fileGlob rewrite, `containingType` filter in ListAll, `symbolKind` note) | `WorkspaceReadNavigationTools.cs:~246-285`, `ToolArgumentRewrites.cs` | 2 files, 3 edits |
| R7 | Search declaration-mode `query` has no `\|` alternation; zero-match glob hint could name the matching `RoslynSentinel.*` folder | `WorkspaceReadNavigationImpl.cs` ListAll, `:1297` | 2 edits |
| R8 | ReplaceSnippet 20-edit batch cap absent from schema | `WorkspaceFileEditImpl.cs:858`; `McpToolSchemaPatcher.ApplyReplaceSnippetLimits` | ~3 files |
| R9 | GetLargeResult last page says COMPLETE while the tool capped the list | `SequenceMessages.cs:27` TODO | 1-2 files |
| R10 | GetMethodSource Ambiguous gives a ReadFile range for candidate 1 only | `WorkspaceReadNavigationImpl.cs` BuildAmbiguousMethodError | 1 file |
| R11 | CS0103 hint names ReplaceSnippet's `newContent` for Member callers | `CompilerErrorLookupHelper.cs:384` (peer has uncommitted edits) | 1 file + test |
| R12 | Member remove refusal does not say skipPrecheck skips only the tool precheck, nor code vs doc-comment counts | `RefactoringStructuralImpl.cs:757`, `MemberRefactoringEngine.cs:2910` | 2 files |
| R13 | Member replace/remove/addTopLevelType/enum add pass `validated: false` after the compile gate | `RefactoringStructuralImpl.cs:704,714,787,815,860,919` | 1 file (check pinned tests) |
| R14 | CreateFile `typeKind`/`typeName` descriptions say "Required for .cs files", contradicting `content` | `WorkspaceTools.cs:171-183` | 1 line |
| R15 | Build probe counts a peer's isolated RunTest (`-p:ArtifactsPath=`) as a competing build; names one pid at a time | `BuildIsolation.cs:145`, `TestRunEngine.cs:438` | 1 file, 2 edits (verify isolated runs never write repo obj) |
| R16 | EditAttribution: not-found evidence ignores search scope and term presence; no session id in wording | `NotFoundEvidence.cs:~257`, `EditAttributionLedger.cs:12`, `EditAttributionFindings.cs:36` | 4-5 files |
| R17 | Git `maxBytes` description does not say output over ~12K chars is paged anyway | `GitTools.cs:59` | 1 line |
| R18 | ExternalFileDrift: no `filePath`/`filePaths` alias; Acknowledge refuses all on one unmatched name; dead halt code | `AdminTools.cs`, `HaltInfo.cs:62` | 2-3 files |
| R19 | RunTest zero-match hint's `suite: full` call drops filter and scope (copy runs whole solution) | `TestSuiteFilter.cs:~55` TODO | 2 call sites |
| R20 | RunTest `timeoutSeconds` undocumented (per project); no `--blame-hang` so a hung test is unnamed; detached rows capped by maxDetails | `TestRunEngine.cs`, `WorkspaceBuildTestImpl.cs:~490` | 1-2 files (blame-hang: verify partial TRX) |
| R21 | Launcher recovery hint describes only index.lock whatever the pull error (diverged clone unhandled) | `LaunchBuildMarker.cs:~70-76` | 1 file |
| R22 | RenameSymbol `residualMentions` scans bin deps.json and untracked files. NOT REPRODUCED 2026-11-15 (closed): `ResidualMentionScannerTests` 9/9 green at 2e5a3601, and a live dry-run rename of `ResidualMentionScanner` listed only git-tracked .md files and one source comment (no bin/obj/deps.json, no untracked file); likely a stale-binary report. Only the non-git fallback walk still includes untracked files (documented). | rename residual scan (engine) | none |
| R23 | ReadFile without a solution ignores `showLineEndings` | `WorkspaceFileEditImpl.cs:701` TODO | 1 file |
| R24 | Docs: dispatcher enables toolsets before spawning; implementer/test-writer briefs verify EOL with `ReadFile(showLineEndings: summary)`; schema loads excluded from the tool-call cap | dispatch-implementer skill, agent docs | 3-4 doc edits |
| R25 | Ratchet tests have no shared category; filtered runs miss them | 11+ ratchet classes, `ClaudeLeanSchemaBudgetTests` | ~14 attribute edits + skill/script filter |

**Owner decisions (new param, response shape, contract reversal):**

| # | Decision | Recommendation |
| --- | --- | --- |
| D1 | ChangeSignature supports primary constructors (records: positional property handling) | Yes; 2-3 Engines.Basic files, decide record property policy |
| D2 | Member replace accepts several declarations (proposal option 2) | Yes; last InvalidArgument driver on Member |
| D3 | Member remove ignores doc-comment cref references (reverses `MemberRemoveTests.cs:163`) | No; do R12 text first |
| D4 | File lookup by name: auto-resolve a unique bare name in ReadFile/GetFileOutline, or new `Search(mode: file)` | Auto-resolve (behaviour only) |
| D5 | Git `log` with `nameOnly` returns hash + subject | Yes; log pages after ~10 records today |
| D6 | McpServerStatus adds `clientProtocolVersion` and `list_changed` send count | Yes; answers the open toolset-refresh question |
| D7 | LaunchTestInstance: `toolsets` and `environment` params; validate `includeTools` names | Yes for validation and `toolsets` |
| D8 | RunTest `includeOutput` keeps passing tests' output; RunTest reports HEAD sha + dirty count | Defer |
| D9 | Member opt-out of the changedFiles detach pointer | No |
| D10 | ExternalFileDrift `List` reports committed/dirty state | No; drift halts are gone |
| D11 | Write results carry the resolved EOL | No; `eolMode` in McpServerStatus covers it |
| D12 | Persist toolset state across restart (reverses the accepted tradeoff) | No; later built by [plan_tab_scoped_toolset_persistence.md](../../plans/closed/plan_tab_scoped_toolset_persistence.md) |

## Why it matters

The line-ending loop alone accounts for roughly 40 of the shell fallbacks and turns every new file
into a create-then-repair sequence. The severe one-offs report success for writes that did not happen
or did not match, which an agent cannot detect without a separate diff. Themes 2, 6 and 7 are the
largest failure buckets by call count (InvalidArgument x423 on Search alone) and cost a round trip
each.

## Recommendation

Done: S1-S4 and the line-ending loop were already fixed; `McpServerStatus` reports `eolMode`
(b5a267ab).

Open, ranked by leverage (items in "Investigation results"):
1. Hooks: R1, R2, R3, R4 (largest recurring theme; cheap, no server restart).
2. Search InvalidArgument: R6, R7.
3. Test-run trust: R5, R19, R20, R25.
4. Schema/hint facts: R8, R9, R10, R11, R12, R14, R17.
5. Wrong-success or misleading data: R13, R15, R16.
6. The rest (R18, R21-R24) and owner decisions D1-D12.

Operational: the live server was ~80 commits stale because the launcher's clone diverged; fixing that
removes many reports at once.

## Out of scope

Themes already marked fixed by a later entry or CLOSED.md are listed for completeness but not
re-verified here.
