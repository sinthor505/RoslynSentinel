# `MoveMember`/`PreviewInstanceMoveCallSitesAsync` reports `NoCandidateIntroducible` for call sites that have a correctly-typed, correctly-initialized sibling field in scope

**Status:** FIXED 2026-09-28, commit `PENDING_COMMIT_HASH` -- root cause was a cross-compilation
`ITypeSymbol` identity mismatch, not the leading `LookupSymbols`-position hypothesis this doc
originally proposed (that hypothesis was tested directly and refuted).

## What was being attempted

Continuing the group-3 "dissolve `AnalysisEngine` junk drawer" reorg
(`C:\Users\Administrator\.claude\plans\enumerated-jumping-babbage.md`). Per the plan and per the
already-resolved blocker
`docs/current/blockers/resolved/blocking_error_movemember_analysisengine_antipatternengine_friction.md`,
the correct way to resolve `NoCandidateIntroducible` call sites ahead of a real `MoveMember` move is
to add a properly-initialized field of the destination type to each affected test class (never the
disallowed `new AntiPatternEngine(_workspaceManager)` one-arg shortcut, which silently drops
`SentinelConfiguration`), so `autoResolveCallSites` can find it with no manual `callSiteFixups`
needed.

Two classes in `RoslynSentinel.Tests.Advanced/SentinelAccuracyTests.cs` -
`SemaphoreAccuracyTests` and `MismatchedAwaitAccuracyTests` - already had exactly this fix applied
in an earlier session. A fresh no-fixup dry run was run to get the authoritative unresolved-call-site
list before adding the same fix to the remaining classes, expecting these two classes to already be
absent from the list. They were not.

## The exact symptom

`MoveMember(..., dryRun: true)` reported `SemaphoreAccuracyTests` and `MismatchedAwaitAccuracyTests`
call sites as `NoCandidateIntroducible` with an empty `candidatesInScope`, even though each class had
a correctly-typed `_antiPatternEngine` field, constructed identically to `_engine`, in scope for the
entire class body.

## Root cause (confirmed)

`PreviewInstanceMoveCallSitesAsync` resolves `destinationType` (the `AntiPatternEngine` symbol) by
scanning `solution.Projects.SelectMany(p => p.Documents)` for the class declaration and taking that
document's own semantic model's symbol for it -- i.e. `destinationType` is an `INamedTypeSymbol`
belonging to `RoslynSentinel.Advanced`'s `Compilation`.

For each call site, the candidate-field scan instead uses `refSemanticModel` -- the semantic model of
the *calling* document (`RoslynSentinel.Tests.Advanced`'s or `RoslynSentinel.Tests.Battery`'s own
`Compilation`). The candidate-matching predicate was:

```csharp
SymbolEqualityComparer.Default.Equals(GetSymbolType(s), destinationType)
```

`SymbolEqualityComparer.Default` never treats an `ITypeSymbol` from one `Compilation` as equal to
"the same" type as seen from a different `Compilation`, even across a direct `ProjectReference` --
each compilation has its own independent symbol object graph. So `_antiPatternEngine`'s field type,
as resolved by the test project's own compilation, could never compare equal to `destinationType`,
which was resolved by the Advanced project's compilation. This is a real, well-known Roslyn identity
trap, not a bug in `LookupSymbols` or in the field declaration.

The leading hypothesis this doc originally proposed -- that `LookupSymbols` at the referenced
member's identifier-token position behaves differently from a lookup at the receiver expression's
position -- was tested directly (an earlier fix iteration implementing exactly that change) and
**empirically refuted**: it made no difference to the unresolved-call-site list. `GetSymbolType` and
the field's own binding were also independently confirmed correct (0 compile errors, correct
`IFieldSymbol.Type`). Only the cross-compilation identity comparison explained the symptom.

## The fix

`PreviewInstanceMoveCallSitesAsync` in `RoslynSentinel.Advanced/AdvancedStructuralEngine.cs` now
re-resolves `destinationType` into each call site's own `refCompilation` before comparing, via
`Compilation.GetTypeByMetadataName`, and additionally falls back to a compilation-independent
fully-qualified-name string comparison so the match cannot be defeated by any remaining Roslyn
symbol-identity subtlety:

```csharp
var destinationTypeMetadataName = destinationType.ToDisplayString(
    SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
var destinationTypeInRefCompilation = (ITypeSymbol?)refCompilation?.GetTypeByMetadataName(destinationTypeMetadataName) ?? destinationType;

bool IsDestinationType(ISymbol s)
{
    var candidateType = GetSymbolType(s);
    if (candidateType == null)
    {
        return false;
    }

    if (SymbolEqualityComparer.Default.Equals(candidateType, destinationTypeInRefCompilation))
    {
        return true;
    }

    var candidateTypeName = candidateType.ToDisplayString(
        SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
    return string.Equals(candidateTypeName, destinationTypeMetadataName, StringComparison.Ordinal);
}
```

The candidate scan's `.Where(...)` predicate now calls `IsDestinationType(s)` instead of the direct
`SymbolEqualityComparer.Default.Equals(GetSymbolType(s), destinationType)` comparison.

## Verification

- Live A/B against the real repro, using `McpServerControl(stop)` + the next tool call to force VS
  Code to respawn the MCP server from the freshly-built binary (source edits do not hot-reload into
  an already-running server process):
  - Before the fix: `MoveMember(dryRun: true)` on `AnalyzeSemaphoreUsageAsync` reported 10 unresolved
    `NoCandidateIntroducible` sites, including all of `SentinelAccuracyTests.cs`'s
    `SemaphoreAccuracyTests` sites (lines 285, 304, 324, 342, 361, 378, 401, 421) and
    `BatteryTwentyThreeTests.cs:335`.
  - After the fix (same server, rebuilt): only 1 site remained unresolved --
    `BatteryTwentyFiveTests.cs:163` in class `AnalysisEngineGapTests` -- confirmed by direct
    inspection (`GetFileOutline`) to be a genuine true negative: that class has no
    `AntiPatternEngine`-typed field at all (only `_workspaceManager` and `_engine`), so
    `NoCandidateIntroducible` is the *correct* classification there, matching the tool's own
    `suggestedFix` text ("Add a field of type AntiPatternEngine to AnalysisEngineGapTests"). Adding
    that field is in-scope for group-3 step 3, not this bug.
  - All originally-affected `SentinelAccuracyTests.cs` sites dropped out of the unresolved list
    entirely, confirming the fix resolves the exact repro this doc was filed for.
- Added `CrossProjectSiblingField_ClassifiesAsValidNotNoCandidateIntroducibleAsync` to
  `RoslynSentinel.Tests.Battery/PreviewInstanceMoveCallSitesTests.cs`, using a new
  `TestSolutionBuilder.CreateTwoProjectSolution` helper (`RoslynSentinel.Tests/TestSolutionBuilder.cs`)
  that builds two genuinely separate `AdhocWorkspace` projects joined by a real `ProjectReference`.
  This is the minimum fixture shape that can actually exercise the bug -- a single-project fixture
  (such as the investigation-era `ProbeFieldReceiverAcrossMethods_ClassifiesAsync`, removed as part
  of this fix since it never reproduced the failure) shares one `Compilation` for every symbol and so
  can never hit this code path. Passes.
- Full solution `Build`: 0 errors (pre-existing warnings only, unrelated to this change).
- `RunTest` on `PreviewInstanceMoveCallSitesTests`: 12/12 passed (11 pre-existing + 1 new), no
  regressions.

## Related

- `docs/current/blockers/resolved/blocking_error_movemember_analysisengine_antipatternengine_friction.md`
  - the previously-resolved blocker in the same tool family that established the
  add-a-sibling-field workflow this bug was blocking.
- `C:\Users\Administrator\.claude\plans\enumerated-jumping-babbage.md` - the group-3 reorg plan this
  was blocking (step 4: moving the remaining `AnalysisEngine` members into `AntiPatternEngine`).
- Memory: `project_engine_reorg_group3_in_progress.md` - prior session's in-progress state for this
  same reorg group.
