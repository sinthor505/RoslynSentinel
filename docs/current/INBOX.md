# INBOX - untriaged issues

Rules: (1) append one line the moment an issue is noticed, before continuing:
`- YYYY-MM-DD HH:mm <symptom> - <evidence: file:line, quoted error, run/turn id> - <code TODO location, or "no code spot">`;
(2) append only - never edit or reorder another line, so concurrent sessions do not collide; (3) an issue with
a code location also gets its inline `// TODO` (CLAUDE.md, Deferred issues) - the code stays the source of truth,
this line is the pointer; (4) do not judge size or cause when adding - triage does that; (5) `todo-review` and
`todo-punchlist` triage every line: move it to [TODO.md](./TODO.md) (with a doc if it needs design), fix it, or
close it with a reason in [CLOSED.md](./CLOSED.md), then delete the line here. Code TODO markers are listed in
[todo_markers.md](../generated/todo_markers.md) (regenerate with `pwsh -NoProfile -File scripts/Get-TodoMarkers.ps1`).

## Untriaged
- 2026-11-24 00:10 UndoChange reverts a multi-item file last-write-wins (restores the last item's BeforeSource), so AsyncifyTools must stamp the first BeforeSource on every item of a file - RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs ~:452 - // TODO UndoChange reverts multi-item files last-write-wins (punchlist #5)
- 2026-11-24 00:50 LmStudioAgentClient retries a response with status=incomplete (max_output_tokens hit) once, same cause as the spent-truncation case #16 no longer retries - RoslynSentinel.AgentLoop/LmStudioAgentClient.cs (plain InvalidOperationException in CompleteOnceAsync, near the StreamTruncatedException throw) - no code TODO (owner decision: also skip the retry? recommend yes)
- 2026-11-24 01:25 No automated test drives the NUnit rejected-filter branch of RunOneProjectAsync end to end (TestSolutionFixture uses xunit, whose adapter reports a malformed filter as 0 matched; only InvalidFilterDetail is unit tested) - verified by hand 2026-11-24 via RunTest on Tests.Asyncify - RoslynSentinel.Engines.Basic/TestRunEngine.cs (RunOneProjectAsync, invalidFilterDetail) - // TODO no automated test drives this branch end to end
- 2026-11-24 00:34 RunTest rejected-filter error still ends with 'Next: Build(level: quickBuild) to rule out compile errors, then retry' (errorCode TestRunIncomplete) although the detail says the adapter rejected the filter - live check after punchlist #9 (59a00fc1, Tests.Asyncify filter 'FullyQualifiedName~(Foo') - WorkspaceBuildTestImpl.cs RunTest error hint (no code TODO yet)
- 2026-11-24 08:05 Build RestoreState finding Options text still says Build(fullBuild) then LoadSolution(forceReload) although LoadSolution now restores by itself - RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs ~:314 - // TODO this Options text still tells the caller to run Build(fullBuild)
- 2026-11-24 08:05 SplitProjectByFolder creates its target project through the engine without restoring it (CreateProject now restores its project) - RoslynSentinel.Tools.Basic/WorkspaceProjectManagementImpl.cs SplitProjectByFolder - // TODO the target project this creates is not restored
- 2026-11-24 08:20 No test covers the CreateProject restore branch (restores the new csproj and appends the fact to ProjectEditResult.Message); Tests.Tools.Basic has no CreateProject fixture pattern - RoslynSentinel.Tools.Basic/WorkspaceProjectManagementImpl.cs CreateProject - // TODO No test covers this CreateProject restore branch
- 2026-11-24 01:40 Libgit2 CAS commit crashes instead of reporting lock contention when the other holder's DeleteOnClose index.lock is delete-pending: HeldIndexLock.Take catches only IOException, CreateNew threw UnauthorizedAccessException ('Access to the path ...\.git\index.lock is denied') - GitCasCommitTests.TwoInProcessCommittersRacing_LoseNothing_FsckClean failed in full run at e013154e (c:\tmp\rs-testtrees\_results\e013154e-20261124-010308-32168), passed alone - code TODO in Libgit2GitReader.IndexWrites.cs HeldIndexLock.Take
