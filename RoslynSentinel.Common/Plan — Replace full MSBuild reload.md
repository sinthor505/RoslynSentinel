Plan — Replace full MSBuild reload in RefreshWorkspaceInternalAsync with in-memory Roslyn solution updates
What the bug is
ApplyProposedChangesAsync holds _solutionLock (line 648) across the entire call to RefreshWorkspaceInternalAsync, which in turn calls MSBuildWorkspace.OpenSolutionAsync. That's a fresh design-time MSBuild evaluation of every project in the solution — measured in minutes on non-trivial solutions. Every other caller of _solutionLock (GetBranchedSolutionAsync, OnDebounceTimerElapsed, LoadSolutionAsync) is completely starved for that entire duration.
Core insight
For the common case — writing .cs files — MSBuild is not needed at all. The Roslyn Solution type is immutable; solution.WithDocumentText(docId, sourceText) returns a new Solution value in microseconds with no I/O, no MSBuild, no network. That new solution has full semantic analysis capability. SetTestSolution already relies on exactly this property.
Only structural changes (.csproj / .sln files) genuinely need an MSBuild re-evaluation, and those should run after the lock is released.
Changes
PersistentWorkspaceManager.cs
1.	Split RefreshWorkspaceInternalAsync into two private methods:
•	ApplyInMemoryDocumentUpdatesAsync(List<string> affectedFiles, CancellationToken ct) — fast path, O(files)
•	ReloadWorkspaceFromDiskAsync(CancellationToken ct) — the existing MSBuild reload, extracted verbatim from the current slow path
2.	ApplyInMemoryDocumentUpdatesAsync:
•	Guard only on CurrentSolution == null (not _workspace == null) so it also works in test scenarios via SetTestSolution
•	For each .cs file: read from disk → SourceText.From(content, Encoding.UTF8) → find Document in CurrentSolution by FilePath (case-insensitive) → CurrentSolution = CurrentSolution.WithDocumentText(docId, sourceText)
•	For a new .cs file (not found in solution): call new helper FindContainingProject(solution, filePath) → CurrentSolution = CurrentSolution.AddDocument(DocumentId.CreateNewId(projectId), fileName, sourceText, filePath: filePath)
•	For .csproj / .sln files: set a bool needsFullReload = true flag and skip in-memory update
•	Always update _lastLoadedAt and prune _internalChanges (already done)
3.	Add private static Project? FindContainingProject(Solution solution, string filePath):
•	Find the project whose directory is a prefix of filePath, longest match first
4.	Change ApplyProposedChangesAsync:
•	Call ApplyInMemoryDocumentUpdatesAsync(succeeded) inside the existing lock — returns in milliseconds
•	After releasing the lock, if needsFullReload == true, fire ReloadWorkspaceFromDiskAsync as a background Task.Run and briefly re-acquire the lock to swap CurrentSolution when it completes; return WorkspaceInSync = false for that call so callers know to wait
RoslynSentinel.Tests — add WorkspaceRefreshTests.cs (new test fixture) with tests:
•	Setup: TestSolutionBuilder.CreateSolutionWithProject with a real temp file path as the document filePath; SetTestSolution
•	Refresh_UpdatesExistingCsDocument_ReflectsInCurrentSolution — writes new content to the temp file, calls ApplyProposedChangesAsync, asserts CurrentSolution document text matches
•	Refresh_AddsNewCsDocumentToContainingProject_ReflectsInCurrentSolution — writes a new file inside the project directory, asserts the document is in CurrentSolution
•	Refresh_LockIsReleasedBeforeMsBuildReload_GetBranchedSolutionAsyncDoesNotBlock — concurrent Task calls GetBranchedSolutionAsync while a structural reload is in flight; asserts it completes promptly without deadlock