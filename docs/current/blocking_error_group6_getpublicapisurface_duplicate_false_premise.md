# Group 6 GetPublicApiSurfaceAsync "duplicate" - false premise, not fixed as a duplicate

Status: RESOLVED (not a defect - the plan's premise was wrong; no code change made or needed for
this specific bullet).

## Plan text (verbatim, section 6 of `.claude/plans/enumerated-jumping-babbage.md`)

> Fix duplicate: `GetPublicApiSurfaceAsync` in both `DiscoveryEngine` and `BreakingChangeEngine` ->
> keep `BreakingChangeEngine`'s (now under `SolutionStructureEngine`), delete `DiscoveryEngine`'s,
> redirect callers.

## What was actually found (verified via GetMethodSource on both bodies, not assumed from name match)

The two methods share a name but are not duplicates - different signatures, different return
types, different feature sets, different callers, no overlap in purpose beyond both walking syntax
trees for public members:

- `DiscoveryEngine.GetPublicApiSurfaceAsync(string projectName, bool includeMethods = true, bool
  includeProperties = true, bool includeTypes = true, CancellationToken cancellationToken =
  default)` -> `Task<List<ApiSurfaceEntry>>`. Per-kind include flags; returns
  `ApiSurfaceEntry(TypeName, MemberName, Signature, Kind, IsVirtual, IsAbstract, IsSealed,
  XmlDocSummary)` - includes constructors, virtual/abstract/sealed flags, and XML doc summaries.
  Only scoped by `projectName` (required, no file-level scoping). Sole production caller:
  `RoslynSentinel.Server.Advanced/ScanTools.cs:1115` (`GetPublicApiSurface` tool method, via
  `_discoveryEngine`), plus 5 direct unit tests in `RoslynSentinel.Tests.Advanced/DiscoveryEngineTests.cs`.
- `BreakingChangeEngine.GetPublicApiSurfaceAsync(string? projectName = null, string? filePath =
  null, CancellationToken cancellationToken = default)` (now moved to `SolutionStructureEngine` as
  part of this session's `BreakingChangeEngine` fold) -> `Task<List<PublicApiMember>>`. No include
  flags (always walks everything). Returns `PublicApiMember(Kind, ContainingType, Signature,
  FilePath, Line)` - no virtual/abstract/sealed/doc-summary metadata, but does carry line numbers
  and supports scoping by `filePath` OR `projectName` OR neither (whole solution). Feeds
  `DetectBreakingChangesAsync`'s snapshot-diff workflow (per its own doc comment: "capture a
  baseline... make code changes... call DetectBreakingChanges with the baseline").

Deleting `DiscoveryEngine`'s copy and redirecting `ScanTools.cs:1115` to the surviving
`SolutionStructureEngine` copy would be a **lossy, breaking change**: `ScanTools`'s `GetPublicApiSurface`
tool method would lose the `includeMethods`/`includeProperties`/`includeTypes` filtering, the
virtual/abstract/sealed flags, the XML doc summaries, and constructor entries - none of which exist
on the surviving copy's `PublicApiMember` shape. This is not a redirect-and-delete; it would require
either merging the two feature sets into one method (out of scope - the plan only asked to resolve a
duplicate, not design a new unified API), or leaving both as intentionally distinct methods serving
different consumers.

## Resolution

No code change made for this bullet. `DiscoveryEngine.GetPublicApiSurfaceAsync` is left in place,
unchanged, still called from `ScanTools.cs:1115`. `SolutionStructureEngine.GetPublicApiSurfaceAsync`
(the migrated `BreakingChangeEngine` copy) is also left in place, feeding `DetectBreakingChangesAsync`
as before. Both migrated-in methods and the pre-existing `DiscoveryEngine` method compiled cleanly
together with 0 build errors (see the group 6 completion memory for the full build/test summary) -
there is no compile-level name collision since they live on different classes; the only "duplicate"
was ever at the human-readable-name level, not a real code conflict.

This matches the precedent already set by the group 4 "RefactoringEngine absorbing
InstrumentationEngine/MsToolAugmentEngine" bullet, which a concurrent session found and archived as
a false premise for the same reason: a plan bullet phrased at the name/summary level turned out, on
reading the actual bodies, not to describe the code as it currently exists.

## Related

- `.claude/plans/enumerated-jumping-babbage.md` - group 6 plan text (the bullet resolved here).
- `RoslynSentinel.Basic/DiscoveryEngine.cs:322-432` - `DiscoveryEngine`'s copy, unchanged.
- `RoslynSentinel.Basic/ProjectStructureEngine.cs` (`SolutionStructureEngine` class) - the migrated
  `BreakingChangeEngine` copy, unchanged in body, just moved by this session's `MoveMember` call.
- `docs/current/blocking_error_group4_refactoringengine_absorption_false_duplicates.md` - sibling
  precedent, same pattern, different group.
