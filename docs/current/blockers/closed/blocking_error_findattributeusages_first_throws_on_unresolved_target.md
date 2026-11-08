# `FindAttributeUsagesAsync` throws `InvalidOperationException: Sequence contains no elements` when an attribute target can't be re-resolved by `LocateSymbolAsync`

**Status:** OPEN - traced to source, fix not yet applied (report-only per dog-fooding policy).

## What was being attempted

Writing a regression test for an unrelated, already-closed bug
(`docs/current/blockers/blocking_error_getlargeresult_typed_branch_reoffload_loop.md`, the
`GetLargeResult` typed-branch re-offload loop). The new test,
`TypedBranchOffload_GetLargeResultFirstCall_ReturnsDataNotAnotherOffloadEnvelope` in
`RoslynSentinel.Tests.Battery/LargeResultOffloadFilterTests.cs:184-249`, seeds 400 synthetic classes
each decorated with a shared custom attribute:

```
[OffloadFilterProbeAttribute("tag0")] public class OffloadFilterAttrTarget0 { }
[OffloadFilterProbeAttribute("tag1")] public class OffloadFilterAttrTarget1 { }
... (400 total)
```

added via `TestSolutionFixture.AddFileToSolution` + `LoadSolution(forceReload: true)`, then calls:

```
QuerySymbolRelationships(name: "OffloadFilterProbeAttribute", searchKind: "attributeUsages")
```

to force a large-enough result to exercise the offload path. This is a new, previously undiscovered
defect found as a side effect of that unrelated test, not the bug the test was written to cover -
the `GetLargeResult` re-offload issue itself is already closed per that memory/doc.

## The exact error text (verbatim)

Test run via `dotnet test RoslynSentinel.Tests.Battery --filter "TypedBranchOffload_GetLargeResultFirstCall_ReturnsDataNotAnotherOffloadEnvelope"` (also reproducible through the `RunTest` MCP tool)
fails with:

```
Error Message:
   The lookup itself must succeed even though its result is large enough to be offloaded. Got: {"serverVersion":"15.0.0.0","serverBuildTimeUtc":"2026-08-13T14:54:26Z","serverBinaryPath":"C:\\Users\\Administrator\\source\\repos\\RoslynSentinel\\RoslynSentinel.Tests.Battery\\bin\\Debug\\net10.0\\testhost.dll","serverPid":11796,"responseId":"1942e294-46f5-444b-bd81-331ae3f4047e","success":false,"error":{"errorCode":"Exception","message":"QuerySymbolRelationships failed unexpectedly (InvalidOperationException). Details: Sequence contains no elements","detail":"Sequence contains no elements"},"findings":[],"directiveKind":"Proceed","hasMorePages":false}
Assert.That(queryResult.IsError, Is.Not.True)
  Expected: not True
  But was:  True
```

Server-side log line for the same run (from the `SentinelSymbolTools` category logger inside
`QuerySymbolRelationships`'s catch block):

```
fail: RoslynSentinel.Server.Basic.SentinelSymbolTools[0]
      QuerySymbolRelationships (attributeUsages) failed for 'OffloadFilterProbeAttribute'
      System.InvalidOperationException: Sequence contains no elements
         at System.Linq.ThrowHelper.ThrowNoElementsException()
         at System.Linq.Enumerable.First[TSource](IEnumerable`1 source)
         at RoslynSentinel.Basic.DiscoveryEngine.FindAttributeUsagesAsync(String attributeName, String projectName, String filePath, CancellationToken cancellationToken) in C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Basic\DiscoveryEngine.cs:line 828
         at RoslynSentinel.Server.Basic.SymbolRelationshipImpl.RunRelationshipQueryAsync(FindUsagesSearchKind searchKind, String name, String projectName, FilePathWrapper filepath, Boolean sortByFrequency, CancellationToken cancellationToken) in C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Server.Basic\SymbolRelationshipImpl.cs:line 47
         at RoslynSentinel.Server.Basic.SymbolRelationshipImpl.QuerySymbolRelationships(ToolCallReason reason, String name, FindUsagesSearchKind searchKind, String projectName, String filepath, Boolean sortByFrequency, CancellationToken cancellationToken) in C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Server.Basic\SymbolRelationshipImpl.cs:line 93
```

Confirmed reproducible: this is the current failing state of the test as it exists uncommitted on
disk right now (`git status` shows `RoslynSentinel.Tests.Battery/LargeResultOffloadFilterTests.cs`
modified). A second variant with a parameterless attribute usage (`[OffloadFilterProbeAttribute]`,
no invoked constructor) hit the identical exception, ruling out attribute-argument shape as a
factor.

## Where it happened

- Tool: `QuerySymbolRelationships`, `searchKind: "attributeUsages"`.
- Throw site: `RoslynSentinel.Basic/DiscoveryEngine.cs:828`, inside `FindAttributeUsagesAsync`
  (method starts line 741).
- Dispatched from `RoslynSentinel.Server.Basic/SymbolRelationshipImpl.cs:48`
  (`RunRelationshipQueryAsync`'s `FindUsagesSearchKind.attributeUsages` case), called from
  `QuerySymbolRelationships` at line 93.
- Caught (not a process crash) by the outer `try/catch (Exception ex)` at
  `SymbolRelationshipImpl.cs:146-154`, then converted into a `ResultError` by
  `ToolErrorMapper.ToResultError` / `ToCodeAndMessage` (`RoslynSentinel.Common/ToolException.cs:174-187`),
  which is the source of the generic `"{context} failed unexpectedly ({ex.GetType().Name}). Details:
  {ex.Message}"` template - confirmed by reading that method directly, not inferred from the message
  shape alone.

## Root cause (traced to source, not guessed)

`FindAttributeUsagesAsync` walks every attribute usage site in the solution's syntax trees. For each
site, a switch expression at `DiscoveryEngine.cs:804-823` determines `(kind, targetName,
containingType)` from whatever syntax node the attribute is attached to - e.g.
`ClassDeclarationSyntax c => ("Class", c.Identifier.Text, "")`, and similarly for
Interface/Record/Struct/Enum/Parameter/Constructor/Method/Property/Field.

Line 825 then calls back into symbol resolution to enrich that raw syntactic guess with real semantic
data:

```csharp
var symbolLocation = await _symbolNavigationEngine.LocateSymbolAsync(targetName, kind, containingType, null, null, docPath, true).ConfigureAwait(false);
```

Line 828 immediately does `symbolLocation.First()` **twice** (once for `.DocCommentId`, once for
`.ContainingNamespace` and `.ProjectName`) with no check that `LocateSymbolAsync` returned anything:

```csharp
results.Add(new AttributeUsageSite(bare, attr.ArgumentList?.Arguments.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>(), symbolLocation.First().DocCommentId ?? string.Empty, kind, targetName, containingType, symbolLocation.First()?.ContainingNamespace ?? string.Empty, symbolLocation.First().ProjectName, docPath, line));
```

Note the inconsistency already visible in this one line: the second `.First()` is called through a
null-conditional (`symbolLocation.First()?.ContainingNamespace`), which is dead protection - `.First()`
itself throws before the `?.` ever gets a chance to short-circuit on a null reference. Whoever wrote
this line was clearly aware the list could be problematic but guarded the wrong half of the failure
mode (a possibly-null *element*, not a possibly-empty *sequence*).

When `LocateSymbolAsync` returns an empty `List<SymbolLocation>` for a given
`(targetName, kind, containingType)` triple, `.First()` throws
`InvalidOperationException: Sequence contains no elements`, and nothing at the throw site or in the
409 surrounding lines names which attribute, target, or file caused it - by the time the exception is
caught and mapped at `SymbolRelationshipImpl.cs:146-154`, the only context available is the overall
`name` argument passed to `QuerySymbolRelationships` (`"OffloadFilterProbeAttribute"`), not the
specific one of 400 usage sites whose *target type* failed to resolve.

**What's confirmed vs. not confirmed about why `LocateSymbolAsync` returns empty here:**

- Confirmed: this reproduces reliably, on every synthetic class in the 400-class fixture, with a
  file that was seeded then reloaded via `LoadSolution(forceReload: true)` immediately before the
  query - i.e. it is not a one-off race.
- Confirmed: it is not specific to attribute-argument shape (parameterless variant hits the same
  exception).
- Not confirmed: the exact reason `LocateSymbolAsync(targetName, "Class", containingType: "", null,
  null, docPath, true)` fails to find a class that demonstrably exists in the just-reloaded solution.
  Candidate explanations that have **not** been individually verified against
  `SymbolNavigationEngine.LocateSymbolAsync`'s source in this pass: a many-symbols-with-similar-shape
  scaling issue in that method's matching logic, some interaction between `containingType: ""` (empty
  string, not null) and whatever filter `LocateSymbolAsync` applies for a non-nested type, or a
  compilation-visibility gap between the syntax tree `FindAttributeUsagesAsync` iterates and the
  semantic model `LocateSymbolAsync` queries. None of these has been traced to a specific branch
  inside `LocateSymbolAsync` yet - flagging this explicitly as an open question rather than asserting
  a specific inner cause.
- Ruled out: the general `LocateSymbolAsync` engine and the public `LocateSymbol` MCP tool are not
  broken outright - resolving a symbol by exact type name in the ordinary case (outside this 400-site
  attribute-usage loop) works as expected.

## A related-but-secondary finding: the `kind` argument passed into `LocateSymbolAsync` is inert

`SymbolNavigationEngine.MatchesKindFilter` (`RoslynSentinel.Basic/SymbolNavigationEngine.cs:316-327`)
only recognizes the lowercase kind strings `"type"`, `"method"`, `"property"`, `"field"`, `"event"`,
falling through to `_ => true` (match everything) for anything else:

```csharp
private static bool MatchesKindFilter(ISymbol symbol, string symbolKind)
{
    return symbolKind.ToLowerInvariant() switch
    {
        "type" => symbol is INamedTypeSymbol,
        "method" => symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation },
        "property" => symbol is IPropertySymbol,
        "field" => symbol is IFieldSymbol,
        "event" => symbol is IEventSymbol,
        _ => true   // "any" or unrecognised - include everything
    };
}
```

But `FindAttributeUsagesAsync` passes PascalCase kind strings at line 825 - `"Class"`, `"Interface"`,
`"Record"`, `"Struct"`, `"Enum"`, `"Constructor"`, `"Parameter"`, or `"Unknown"` - none of which match
any case above. Every one of them silently falls into the `_ => true` default, so the `kind` argument
is currently dead weight for every target kind `FindAttributeUsagesAsync` can produce: it never
actually narrows the candidate set for Class/Interface/Record/Struct/Enum/Constructor/Parameter
targets.

This is flagged as **secondary, not the root cause of the crash** - "always true" is over-inclusive
(too permissive), not under-inclusive, so it cannot by itself explain an empty result set. It is
worth fixing alongside the crash, though: if the real fix ends up leaning on `containingType` plus a
correctly-filtered `kind` to disambiguate same-named targets, that path is currently non-functional
for every kind this method emits, and a fix that "resolves" the crash by making `LocateSymbolAsync`
falsely permissive would be masking rather than closing this gap.

## Why this is an environment defect, not a model/caller error (per CLAUDE.md's failure doctrine)

The caller-facing symptom - `QuerySymbolRelationships(searchKind: "attributeUsages")` returning
`{"success":false,"error":{"errorCode":"Exception","message":"QuerySymbolRelationships failed
unexpectedly (InvalidOperationException). Details: Sequence contains no elements"}}` - is legitimate
input hitting a `.First()` call with no guard, not a caller mistake. Nothing about the tool's
`[Description]` or parameter docs warns that attribute usages on synthetic/freshly-added/high-volume
targets can crash the whole query; there is no way for a calling agent to predict or avoid this short
of not using the tool. The error message itself is actively unhelpful for recovery: it names neither
the attribute usage site, the file, nor which of (in this repro) 400 candidate targets failed to
resolve, so an agent hitting this in the wild has no path to work around it other than abandoning the
`attributeUsages` search kind entirely.

This also intersects the working convention in this repo's `CLAUDE.md` ("Never leak raw exceptions,
stack traces, or internal paths into a tool's `ResultError` - catch at the tool boundary and return a
structured, actionable error"): while this exception *is* caught (at `SymbolRelationshipImpl.cs:146`,
not left to crash the process), the resulting `ResultError` is exactly the kind of raw,
context-free `Exception`/`ex.Message` passthrough that convention is meant to prevent - it is
"caught" in the sense of not crashing the server, but not in the sense of being turned into an
actionable, structured explanation.

## What unblocks this

1. **Immediate fix candidate** at `DiscoveryEngine.cs:825-828`: replace the unguarded
   `symbolLocation.First()` calls with `var located = symbolLocation.FirstOrDefault();` and handle
   the `null` case explicitly - either skip that usage site with a debug-level log entry (least
   disruptive; matches how a solution-wide scan should tolerate a handful of unresolved sites rather
   than aborting the whole search), or construct the `AttributeUsageSite` directly from the
   already-known `targetName`/`containingType`/empty-string defaults for the three fields that
   currently come only from `located` (`DocCommentId`, `ContainingNamespace`, `ProjectName`) when no
   symbol was resolved, so a resolution miss degrades to reduced enrichment rather than a hard
   failure for the entire query.
2. **Prerequisite investigation, not yet done**: before finalizing that fix, someone needs to trace
   why `LocateSymbolAsync(targetName, kind, containingType: "", null, null, docPath, true)` returns
   empty for a type that demonstrably exists in the just-reloaded solution, in
   `RoslynSentinel.Basic/SymbolNavigationEngine.cs`'s `LocateSymbolAsync` implementation - this
   determines whether the fix above is a full resolution or just a crash-avoidance patch over a
   still-unexplained resolution gap that will keep silently under-enriching results.
3. **Secondary, independent fix**: extend `MatchesKindFilter`
   (`RoslynSentinel.Basic/SymbolNavigationEngine.cs:316-327`) to recognize the PascalCase kind strings
   `FindAttributeUsagesAsync` actually emits (`"Class"`, `"Interface"`, `"Record"`, `"Struct"`,
   `"Enum"`, `"Constructor"`, `"Parameter"`), or normalize case before the switch, so the `kind`
   argument passed at `DiscoveryEngine.cs:825` starts doing real disambiguation instead of being
   silently inert. Decide this independently of item 1/2 - fixing the crash does not require fixing
   this, but leaving it unfixed means any `containingType`-based disambiguation added as part of item
   1 will be undermined by an always-permissive kind filter.
4. A regression test already exists that reproduces this reliably:
   `RoslynSentinel.Tests.Battery/LargeResultOffloadFilterTests.cs:184-249`
   (`TypedBranchOffload_GetLargeResultFirstCall_ReturnsDataNotAnotherOffloadEnvelope`). That test's
   *original* purpose (verifying the `GetLargeResult` typed-branch offload fix, already closed) is
   currently blocked from passing by this unrelated crash - whoever fixes this defect should confirm
   that test goes green as part of validating the fix, but should not assume the test needs any
   changes of its own; the fixture and assertions are correct, they are just tripping over a second,
   previously unknown bug on the way to exercising the first one.

Once resolved, move this file to `docs/current/CLOSED.md` (or `docs/obsolete/blockers/`, per the
convention used elsewhere in this folder).
