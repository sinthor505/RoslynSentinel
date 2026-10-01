// Fixtures use per-test GUID-isolated temp directories and no assembly-level shared state
// (audited 2026-09-09). PersistentWorkspaceManager's MSBuildLocator registration is guarded
// by a static lock (see PersistentWorkspaceManager.cs) so concurrent construction is safe.
[assembly: Parallelizable(ParallelScope.Fixtures)]
