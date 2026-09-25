# `Member(replace)` fails with `NotFound` for every member on every interface declaration tested, while `Member(view)` succeeds against the identical target

**Status:** FIXED 2026-09-25, `RoslynSentinel.Basic/SymbolNavigationEngine.cs` +
`RoslynSentinel.Basic/RefactoringEngine.cs`. Runtime-verified: full suite passes (see "Fix applied"
below), including two new regression tests exercising the exact repro shape.

**Correction, 2026-09-25:** this doc previously claimed the fix below (a plain deletion of the
`!(m.Parent is InterfaceDeclarationSyntax)` filter) had already been applied via `ReplaceSnippet` and
confirmed by `Build`. That was false -- the edit was never actually written to disk (not present in
`Git(status)`'s unstaged list, not in any commit, and re-confirmed absent from source by
`SearchSolutionText` on 2026-09-25). The likely sequence: that plain-deletion attempt was tried in the
same working session as the discarded throw-on-zero-candidates attempt (see "Motivation" in
`docs/current/proposal_universal_symbol_resolver.md`), and when the combined attempt was reverted via
`git checkout`, this doc's "Fix applied" section was left describing an edit that no longer existed --
it was never re-applied on its own, and the plain-deletion variant was never actually tested in
isolation before this doc called it done. Re-attempting the plain deletion on 2026-09-25 confirmed it
regresses `RemoveMember_HasImplementationOnly_SkipPrecheckTrue_BypassesToolLevelCheck`
(`RoslynSentinel.Tests.Battery/BatteryTwentyFourTests.cs`): once interface members are unconditionally
included, a same-named interface declaration and its class implementer become equally valid
candidates with no tiebreak, so `Member(remove)` can end up targeting the wrong one. The actual fix
(below) uses an opt-in toggle plus a disambiguation rule instead of an unconditional deletion.

## What was being attempted

Implementing step 1 of `docs/current/design_read_chokepoint.md`, which requires adding an
`[Obsolete]` attribute to two members of the `ISolutionProvider` interface
(`RoslynSentinel.Common/ISolutionProvider.cs`): the `CurrentSolution` property and the
`GetCurrentSolutionAsync` method. Per CLAUDE.md's dog-fooding policy, this write was attempted via
the `Member` MCP tool with `operation: "replace"`, not via `ReplaceSnippet` or any other tool.

This is on the just-restarted server build: `buildTimeUtc 2026-09-24T19:48:29Z`,
`binaryPath bin-vscode\d87a019a-f81d6e45\Advanced\RoslynSentinel.Server.Advanced.dll`.

## The exact symptom

1. `Member(operation: "view", filePath: "RoslynSentinel.Common/ISolutionProvider.cs",
   containerName: "ISolutionProvider", memberName: "CurrentSolution")` SUCCEEDED, returning the
   member list including:
   ```json
   {"name":"CurrentSolution","kind":"property","signature":"Solution? CurrentSolution","startLine":15,"endLine":18}
   ```

2. `Member(operation: "replace", filePath: "RoslynSentinel.Common/ISolutionProvider.cs",
   containerName: "ISolutionProvider", memberName: "CurrentSolution", newMemberSource: <the same
   property with an `[Obsolete]` annotation added>)` FAILED:
   ```json
   {"errorCode":"NotFound","message":"Member: member 'CurrentSolution' not found in 'C:\\Users\\Administrator\\source\\repos\\RoslynSentinel\\RoslynSentinel.Common\\ISolutionProvider.cs'."}
   ```
   - Retried without `containerName`: same `NotFound`.
   - Retried with `containerName` plus `contextSnippet: "Solution? CurrentSolution"`: same
     `NotFound`.

3. Isolation test (rules out "property-only"): `Member(operation: "replace",
   filePath: "RoslynSentinel.Common/ISolutionProvider.cs", containerName: "ISolutionProvider",
   memberName: "GetCurrentSolutionAsync", dryRun: true, newMemberSource: <the same method with an
   `[Obsolete]` annotation added>)` -- a METHOD, not a property, on the same file -- also FAILED with
   the same `NotFound` message, naming `GetCurrentSolutionAsync`.

4. Isolation test (rules out "specific to `ISolutionProvider.cs`"): `Member(operation: "replace",
   filePath: "RoslynSentinel.Common/IWorkspaceReader.cs", containerName: "IWorkspaceReader",
   memberName: "GetSolutionAsync", dryRun: true, newMemberSource: "Task<Microsoft.CodeAnalysis.Solution>
   GetSolutionAsync(ReadSource source, CancellationToken cancellationToken);")` also FAILED with
   `NotFound` naming `GetSolutionAsync`. `IWorkspaceReader.cs` is a brand-new file created earlier
   this same session (via `CreateFile` followed by `Member(operation: "addTopLevelType")`, both of
   which succeeded at the time), so this is not residue from a stale/pre-existing file.

## Where this happened

- Tool: `Member`, `operation: "replace"` (Advanced-flavor server, build `2026-09-24T19:48:29Z`,
  `bin-vscode\d87a019a-f81d6e45\Advanced\RoslynSentinel.Server.Advanced.dll`).
- Files: `RoslynSentinel.Common/ISolutionProvider.cs` (both `CurrentSolution` and
  `GetCurrentSolutionAsync`), `RoslynSentinel.Common/IWorkspaceReader.cs` (`GetSolutionAsync`).
- Error text (verbatim, `ISolutionProvider.cs` case):
  `"Member: member 'CurrentSolution' not found in 'C:\\Users\\Administrator\\source\\repos\\RoslynSentinel\\RoslynSentinel.Common\\ISolutionProvider.cs'."`
  with `errorCode: "NotFound"`.

## What was ruled out (original investigation)

- **Not a property-vs-method distinction.** Both a property (`CurrentSolution`) and a method
  (`GetCurrentSolutionAsync`, `GetSolutionAsync`) fail identically.
- **Not specific to one file.** Reproduces on `ISolutionProvider.cs` (pre-existing file) and
  `IWorkspaceReader.cs` (created fresh this session).
- **Not a stale-file / on-disk-drift artifact.** `IWorkspaceReader.cs` was created and populated
  this same session via `CreateFile` and `Member(operation: "addTopLevelType")`, both of which
  reported success at the time, and `Member(operation: "view")` against `ISolutionProvider.cs`
  succeeds and returns accurate, current line numbers in the same session -- so the workspace's view
  of these files is not generally out of sync.
- **Not `containerName` mis-specification.** Retried both with and without `containerName`, and with
  a `contextSnippet` matching the member's exact signature text; all three variants on
  `ISolutionProvider.cs`'s `CurrentSolution` produced the identical `NotFound`.
- **Not the specific member name being wrong or mistyped.** `Member(operation: "view")` against the
  exact same `filePath`/`containerName`/`memberName` triple that `replace` rejects returns the
  member correctly, with a signature and line range that match what was passed to `replace`.

## Root cause -- traced to source, not a hypothesis

`RoslynSentinel.Basic/SymbolNavigationEngine.cs:2293` (`ResolveMemberByNameOrSnippet`, used by
`ReplaceMemberAsync`/`RemoveMemberAsync`/`ModifyModifier` etc. -- everything routed through the
shared member resolver) and `:2358` (`ResolveMemberOrEnumMemberByNameOrSnippet`) both built their
candidate list with:

```csharp
root.DescendantNodes().OfType<MemberDeclarationSyntax>()
    .Where(m => GetMemberName(m) == memberName && !(m.Parent is InterfaceDeclarationSyntax))
```

The `!(m.Parent is InterfaceDeclarationSyntax)` clause unconditionally excludes every member
declared directly inside an interface body from the candidate set, so `ResolveMemberByNameOrSnippet`
can never return one -- regardless of `memberName`, `containerName`, or `contextSnippet`. This is
exactly why `replace` returned `NotFound` for `ISolutionProvider.CurrentSolution`,
`GetCurrentSolutionAsync`, and `IWorkspaceReader.GetSolutionAsync`: all three are interface-body
members, and no interface-body member could ever have resolved through this path.

`Member(view)` uses a completely different, unfiltered lookup path -- `GetContainerMembersAsync`
(`SymbolNavigationEngine.cs:2486`) resolves the container type directly via
`ResolveTypeByNameOrSnippet` and enumerates `typeDecl.Members` with no interface-vs-class
distinction at all -- which is why `view` found the same members that `replace` rejected.

**Where the filter came from:** confirmed via `Git(operation: "log")` on this file that the clause
is not a recent, deliberate addition -- none of the last ~20 commits touch it. It traces back to
`docs/obsolete/plan-tool-disambiguation-remediation-v1.md`'s original consolidation of 15
independent `FirstOrDefault(m => GetMemberName(m) == targetName)` call sites into this one shared
resolver; that plan doc's own sample code (line 249) carries the identical clause labeled
"unchanged default behavior" -- i.e. copied forward from whatever the pre-consolidation call sites
happened to do, not a documented fix for a specific class/interface name-collision bug. Nothing
scoped the filter to only callers that might have wanted it (e.g. avoiding confusion between an
interface's method and a class's differently-shaped explicit-interface-implementation node -- which
wouldn't even need this filter, since explicit interface impls carry an `ExplicitInterfaceSpecifier`
and live under the class, not the interface, syntactically). The filter was simply incorrect for the
shared resolver's actual caller set.

## Is this the same defect as the prior `remove` fix? No -- confirmed distinct

`blocking_error_member_remove_false_not_found.md` fixed `RefactoringStructuralImpl.cs`'s `remove`
branch silently downgrading a real `EditOutcome.CannotRemove` (cref usages) to a generic "not found"
string -- the member was found there, just blocked for an unrelated reason. This defect is upstream
of that: the shared resolver both `replace` and `remove` call never adds an interface member to its
candidate list in the first place, so it's a `TargetNotFound` outcome that is *correctly* labeled by
`RefactoringStructuralImpl.cs`'s switch (see `RefactoringStructuralImpl.cs:430`, which does map
`EditOutcome.TargetNotFound` to the "not found" message) -- the bug is entirely in
`SymbolNavigationEngine.cs`'s candidate-collection filter, not in outcome-to-message mapping.

## Fix applied

Not a plain filter deletion (see "Correction" above for why that regresses same-named
interface/implementer collisions). Instead, `RoslynSentinel.Basic/SymbolNavigationEngine.cs` gained
an opt-in `excludeInterfaceMembers` parameter (default `true`, preserving existing behavior for every
caller that doesn't pass it) on both member-candidate resolvers, plus a disambiguation rule for when
it's `false`:

```csharp
// ResolveMemberByNameOrSnippet:
public MemberDeclarationSyntax? ResolveMemberByNameOrSnippet(SyntaxNode root, SourceText sourceText, string memberName, string? contextSnippet, string? lineBefore, string? lineAfter, Func<MemberDeclarationSyntax, bool>? extraFilter = null, bool excludeInterfaceMembers = true)
{
    var candidates = root.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(m => GetMemberName(m) == memberName).Where(m => !excludeInterfaceMembers || m.Parent is not InterfaceDeclarationSyntax).Where(m => extraFilter == null || extraFilter(m)).ToList();
    // ... existing constructor-vs-type disambiguation unchanged ...

    // New: when excludeInterfaceMembers is false, an interface and its implementer can both
    // contribute a same-named candidate with nothing to tell them apart by name alone. Prefer the
    // implementer, symmetric with the constructor-vs-type check above.
    if (candidates.Count > 1 && candidates.Any(c => c.Parent is InterfaceDeclarationSyntax) && candidates.Any(c => c.Parent is not InterfaceDeclarationSyntax))
    {
        candidates = candidates.Where(c => c.Parent is not InterfaceDeclarationSyntax).ToList();
    }
    // ...
}
```

`ResolveMemberOrEnumMemberByNameOrSnippet` got the matching parameter and disambiguation rule.

Only the two call sites this incident is actually about were switched to opt in --
`RefactoringEngine.cs`'s `ReplaceMemberAsync` and `RemoveMemberAsync` now pass
`excludeInterfaceMembers: false`. All 16 other call sites (`AddAttributeAsync`,
`ChangeAccessibilityAsync`, `AddModifierAsync`, `RemoveModifierAsync`, `ApplyModifierBatchAsync`,
`ApplyAttributeBatchAsync`, `GetMethodParametersAsync`, `AddMethodParameterAsync`,
`RemoveMethodParameterAsync`, `AddSummaryCommentCoreAsync`, `RemoveSummaryCommentAsync`,
`GetSummaryCommentAsync`, `ConvertExpressionBodyAsync`, etc.) are untouched and keep the prior
interface-excluding behavior by default.

Applied via `ReplaceSnippet` across `SymbolNavigationEngine.cs` and `RefactoringEngine.cs`, each
apply reporting `workspaceInSync: true` with zero new diagnostics. Confirmed with a solution-wide
`Build(level: fullBuild)`: 0 errors, 0 warnings, 14 projects compiled.

**Runtime-verified 2026-09-25**, not just build-verified: added two new regression tests
(`RemoveMember_InterfaceProperty_NoImplementerInFile_Succeeds`,
`ReplaceMember_InterfaceMethod_NoImplementerInFile_Succeeds` in
`RoslynSentinel.Tests.Battery/BatteryTwentyFourTests.cs`) reproducing this incident's exact shape --
both pass. Full suite: all tests pass except one pre-existing, unrelated flaky test
(`T2_PaginatedScan_SmallResult_Inline_TotalRecordsSet` in `RoslynSentinel.Tests.Asyncify`, confirmed
to pass in isolation and to fail only under the full/parallel run -- stack trace is entirely inside
`AsyncifyTools.FlagMigrationCandidatesCore`, unrelated to this change). The prior
`RemoveMember_HasImplementationOnly_SkipPrecheckTrue_BypassesToolLevelCheck` test (which the
plain-deletion attempt regressed, see "Correction" above) continues to pass unchanged, since its
call path (`Member(remove)` with default `skipPrecheck`) still resolves via the default
`excludeInterfaceMembers: true` behavior for that scenario's outer precheck, and the new
implementer-preference disambiguation correctly resolves the inner compile-validation path's
same-name collision in the implementer's favor.

## What is now confirmed (previously open questions)

- **Scope is confirmed: interface-declared members specifically, not a general `replace`/`remove`
  breakage.** The root cause is a filter that excludes only `InterfaceDeclarationSyntax`-parented
  members; class/struct/record members were never affected by this code path (no `!(m.Parent is ...)`
  filter applies to them). A class-member `replace`/`remove` call was working correctly before this
  fix and is unaffected by it -- the fix strictly widens the candidate set to include interface
  members, without touching how class members already resolved.

## Why this blocked

Per CLAUDE.md's failure doctrine, `Member(replace)` returning wrong data (`NotFound` for a member
that indisputably exists and that `Member(view)` just confirmed exists, at the exact file/container/
name triple) is a blocking tool defect, not grounds to route around via `ReplaceSnippet` or any other
tool. This directly blocked `docs/current/design_read_chokepoint.md` step 1, which requires adding
`[Obsolete]` to `ISolutionProvider.CurrentSolution` and `ISolutionProvider.GetCurrentSolutionAsync`
-- both interface members, both members this doc demonstrated `Member(replace)` could not target.

## Follow-up: regression test coverage

Added 2026-09-25: `RemoveMember_InterfaceProperty_NoImplementerInFile_Succeeds` and
`ReplaceMember_InterfaceMethod_NoImplementerInFile_Succeeds` in
`RoslynSentinel.Tests.Battery/BatteryTwentyFourTests.cs`, covering `Member(remove)` against an
interface-declared property and `Member(replace)` against an interface-declared method -- the two
shapes proven to fail here. Both pass.

## Related

- `docs/current/blockers/resolved/blocking_error_member_remove_false_not_found.md` -- a prior,
  separately-resolved false-`NotFound` defect in `Member`'s dispatch/lookup path (`operation:
  "remove"`, not `replace`; per project memory this was the third such incident, traced to divergent
  decl-kind switches across `Member`'s dispatch table). Confirmed distinct from this defect -- see
  "Is this the same defect as the prior `remove` fix?" above. Both are now fixed; together they
  suggest `docs/current/proposal_unify_member_lookup_paths.md` (member-lookup unification) remains
  worth doing, since this is now the fourth false-`NotFound` incident in the same tool family.
- `docs/current/design_read_chokepoint.md` -- the in-progress design step this blocker halted (step
  1, `[Obsolete]`-annotating `ISolutionProvider` members). Resume after runtime verification above.
