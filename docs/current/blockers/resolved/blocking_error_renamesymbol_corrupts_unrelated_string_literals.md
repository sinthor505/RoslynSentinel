# `RenameSymbol` rewrites unrelated string-literal and comment text that happens to match the old identifier, with zero mention in its own `residualMentions` output

**Status:** RESOLVED 2026-09-20. Corrupted string literals/comments/dictionary keys reverted;
confirmed via fresh-server re-verification (SearchSolutionText zero-match re-checks + RunTest failure
count drop 33->22 with none of the remaining failures touching previously-corrupted files). See
`## Status` at the end for full resolution and remaining follow-up.

## What was being attempted

Four `RenameSymbol` calls against `RoslynSentinel.Common.SentinelCallToolResult<T>`, as part of the
envelope field-promotion plan (`C:\Users\Administrator\.claude\plans\inherited-orbiting-toucan.md`,
`docs/current/proposal_envelope_field_promotion.md`):

- `Success` -> `IsSuccess` (58 files changed)
- `Error` -> `ErrorDetails` (85 files changed)
- `Data` -> `SuccessDetails` (59 files changed)
- `Warning` -> `WarningDetails` (18 files changed)

Each call's own reported `residualMentions` output showed it correctly distinguishing genuine C#
symbol references to the renamed property from unrelated same-named members on other types (e.g. it
correctly skipped `ToolOptionsResult.Error`, `Exception.Data`, `LogLevel.Warning`,
`FindingSeverity.Warning`). Symbol-level scoping appeared correct at call time.

The corruption below was NOT visible in any `RenameSymbol` call's own output. It was discovered
afterward via `RunTest` (full solution: 2576 total, 2449 passed, 33 failed, 94 skipped) during Step 4
verification of the same plan, then localized with `SearchSolutionText`.

## The exact symptom

Despite correct symbol-level scoping, the same rename operations also rewrote the CONTENTS of
unrelated string literals, comments, and dictionary keys that textually matched the old identifier
name -- content that is not a C# symbol reference at all, has no `[Rename]`-visible relationship to
the `SentinelCallToolResult<T>.Error`/`Warning`/`Data` properties, and was never flagged in any
`residualMentions` list.

Confirmed via `SearchSolutionText` for regex `"(ErrorDetails|WarningDetails|SuccessDetails)"` across
`**/*.cs`: far more hits than the 4 rename operations' own reported file-change counts would predict,
scattered across production logic, test assertions, and a data dictionary. Every site below was
re-verified directly with `Read`/`Grep` against current file content on 2026-09-20 (line numbers
below are exact as of that read, not the approximate ones from the original triage).

### Cluster 1 -- production runtime logic broken (most severe)

Diagnostic-severity string comparisons -- comparing a `DiagnosticSeverity`-derived `Severity` field
against the literal severity-name strings `"Error"` / `"Warning"` (nothing to do with the envelope's
own `Error`/`Warning` properties) -- were rewritten to compare against `"ErrorDetails"` /
`"WarningDetails"` instead. These can never match a real severity value (Roslyn's
`Microsoft.CodeAnalysis.DiagnosticSeverity.ToString()` only ever produces `"Error"`, `"Warning"`,
`"Info"`, `"Hidden"`), so every one of these comparisons now silently fails at runtime.

Confirmed current sites:

- `RoslynSentinel.Basic/BuildEngine.cs:85-86`, inside `RunQuickBuildAsync`:
  ```csharp
  var errors = summary!.Details.Where(d => d.Severity == "ErrorDetails").ToList();
  var warnings = summary!.Details.Where(d => d.Severity == "WarningDetails").ToList();
  ```
- `RoslynSentinel.Basic/BuildEngine.cs:196` and `:209`, inside `RunFullBuildAsync` (this second site
  was not in the original triage's approximate line list -- confirmed here on re-read):
  ```csharp
  var severity = m.Groups["severity"].Value == "error" ? "ErrorDetails" : "WarningDetails";
  ...
  (severity == "ErrorDetails" ? errors : warnings).Add(info);
  ```
  Line 196 parses the literal `"error"`/`"warning"` text out of `dotnet build` stdout via
  `DiagnosticLineRegex`, then stamps the WRONG label (`"ErrorDetails"`/`"WarningDetails"`) onto every
  parsed diagnostic; line 209 then buckets by that wrong label, so `RunFullBuildAsync`'s errors and
  warnings lists are populated by a comparison that can never distinguish anything -- both lists take
  whichever ternary branch always loses, silently misclassifying every full-build diagnostic.
- `RoslynSentinel.Basic/DiagnosticEngine.cs:30-31`, inside `GetFileDiagnosticsAsync`:
  ```csharp
  list.Count(d => d.Severity == "ErrorDetails"),
  list.Count(d => d.Severity == "WarningDetails"),
  ```
- `RoslynSentinel.Basic/DiagnosticEngine.cs:45-46`, inside `GetProjectDiagnosticsAsync` (identical
  pattern).
- `RoslynSentinel.Basic/DiagnosticEngine.cs:64-65`, inside `SeverityRank`:
  ```csharp
  private static int SeverityRank(DiagnosticInfo d) => d.Severity switch
  {
      "ErrorDetails" => 0,
      "WarningDetails" => 1,
      _ => 2
  };
  ```
  Every real diagnostic now falls through to the `_ => 2` branch, so `GetSolutionDiagnosticsAsync`'s
  documented "errors always appear before warnings regardless of the cap" ordering guarantee
  (see the doc comment at `DiagnosticEngine.cs:69-74`) is silently broken -- errors and warnings sort
  identically to each other and to every other severity.
- `RoslynSentinel.Basic/DiagnosticEngine.cs:99-100`, inside `GetSolutionDiagnosticsAsync`:
  ```csharp
  int totalErrors = allDiagnostics.Count(d => d.Severity == "ErrorDetails");
  int totalWarnings = allDiagnostics.Count(d => d.Severity == "WarningDetails");
  ```
- `RoslynSentinel.Basic/WorkspaceBuildTestImpl.cs:96` (not in the original triage list -- found on
  re-verification):
  ```csharp
  var relevant = result.Data.Details.Where(d => d.Severity is "ErrorDetails" or "WarningDetails").ToList();
  ```
- `RoslynSentinel.Basic/SymbolNavigationEngine.cs:546` (not in the original triage list -- found on
  re-verification), inside `ErrorHoverInfo`:
  ```csharp
  private static SymbolHoverInfo ErrorHoverInfo(string error) =>
      new SymbolHoverInfo(
          Name: string.Empty,
          Kind: "ErrorDetails",
  ```
  This one is a different flavor again: `Kind` is a free-text descriptive label on a hover-info DTO,
  not a `DiagnosticSeverity` comparison, but it was still rewritten from `"Error"` to `"ErrorDetails"`
  purely because the substring matched.
- `RoslynSentinel.Tests.Basic/DiffEngineTests.cs:260`, test `ValidateProposedDiff_ShouldReturnErrors_ForInvalidDiff`:
  ```csharp
  Assert.That(report.Diagnostics.Any(d => d.Severity == "ErrorDetails"), Is.True);
  ```
  This assertion now checks for a severity string (`"ErrorDetails"`) the production code (per the
  sites above) never produces, and is the confirmed direct cause of one of the 33 `RunTest` failures.
- `RoslynSentinel.Tests.Basic/NamespacePathMismatchTests.cs:88,116` and
  `RoslynSentinel.Tests.Advanced/NewFeaturesTests.cs:728` -- same pattern, `Is.EqualTo("WarningDetails")`
  / `Is.EqualTo("ErrorDetails")` assertions against a severity value that production code no longer
  emits.
- `RoslynSentinel.Common/ValidationEngine.cs:31,47,181,195` -- `DiagnosticInfo("RS00x", "ErrorDetails", ...)`
  constructor calls that hard-code the corrupted literal as the severity field of newly constructed
  diagnostics.
- `RoslynSentinel.Advanced/AntiPatternEngine.cs:2250,2308,2534,2553` and
  `RoslynSentinel.Basic/AnalysisEngine.cs:2195,2238,2271,2284,2345` -- `Severity = "WarningDetails"` /
  `"ErrorDetails"` literal assignments constructing findings with a bad severity value.

This is a substantially larger blast radius within Cluster 1 than originally scoped: the original
triage named 3 files (`BuildEngine.cs`, `DiagnosticEngine.cs`, `DiffEngineTests.cs`); re-verification
found the same corruption pattern additionally in `WorkspaceBuildTestImpl.cs`, `SymbolNavigationEngine.cs`,
`ValidationEngine.cs`, `AntiPatternEngine.cs`, `AnalysisEngine.cs`, `NamespacePathMismatchTests.cs`,
and `NewFeaturesTests.cs` -- all with the identical "a free-standing string literal that happens to
contain the substring `Error`/`Warning`/`Data` got rewritten" signature.

### Cluster 2 -- an unrelated data dictionary corrupted (new, not in original triage)

`RoslynSentinel.Advanced/ArchitecturalEngine.cs:423,432-434,438-440` -- a static architectural-layer
name-to-rank dictionary and a forbidden-dependency map use `"Data"` as a layer name (i.e. the literal
string is meant to mean "the data-access layer", unrelated to any `SentinelCallToolResult<T>` member).
The rename changed the layer name itself:

```csharp
{ "SuccessDetails",        5 },
{ "Repositories",5 },
...
["Controllers"] = new(StringComparer.OrdinalIgnoreCase) { "SuccessDetails", "Repositories", "Migrations" },
```

with a comment two lines above (`ArchitecturalEngine.cs:429`) left un-rewritten and now inconsistent
with the code below it:

```csharp
// Rules: a layer at rank R should NOT directly reference a layer at rank > R+1 (skip-a-layer)
// and Controllers should never reference SuccessDetails/Repositories directly.
```

This silently breaks `DetectLayerViolationsAsync`'s layer-violation detection for any real codebase
using a `Data` namespace/folder convention -- the dictionary can no longer match the string `"Data"`
that a real analyzed project would contain, since the key itself was renamed to `"SuccessDetails"`.

### Cluster 3 -- test comments and assertions corrupted (namespace-name collision)

`RoslynSentinel.Tests/FiveStarToolTests.cs`, test `PreviewAddMissing_TypeInProjectNamespace_SuggestsCorrectUsing`:

- Lines 576-577 (comments):
  ```csharp
  // File A defines UserRepository in MyApp.SuccessDetails
  // File B uses UserRepository without importing MyApp.SuccessDetails
  ```
- Line 580 (the actual namespace declaration in the test fixture source, correctly left untouched):
  ```csharp
  namespace MyApp.Data
  ```
- Lines 603-605 (assertions):
  ```csharp
  Assert.That(result.UsingsToAdd, Does.Contain("MyApp.SuccessDetails"),
      "MyApp.SuccessDetails namespace must be identified as needed");
  Assert.That(result.UpdatedContent, Does.Contain("using MyApp.SuccessDetails"),
  ```

The `Data` -> `SuccessDetails` rename changed the free-text namespace name `MyApp.Data` (used only in
comments and string-literal assertion targets inside a C# triple-quoted raw string fixture, never a
real symbol) to `MyApp.SuccessDetails` -- while the actual `namespace MyApp.Data { ... }` declaration
at line 580, a few lines away in the SAME file, was correctly left untouched by the same rename call.
This directly causes the `PreviewAddMissing_TypeInProjectNamespace_SuggestsCorrectUsing` test failure:
the test expects to find `"MyApp.Data"` (the real namespace the fixture declares), but the assertion
string itself was rewritten to `"MyApp.SuccessDetails"`, so it now asserts against a namespace that
does not exist anywhere in the fixture.

This is the more insidious variant of the defect: the tool's rename logic left the REAL symbol
declaration alone (correct) but rewrote separate, unrelated textual mentions of the same characters
elsewhere in the same file (incorrect) -- internally inconsistent behavior from what is supposed to
be a single semantic operation.

## Blast-radius check already done for the `Success` -> `IsSuccess` rename (not re-run; reporting as complete)

A follow-up `SearchSolutionText` for regex `"(IsSuccess|SuccessDetails)"` across `**/*.cs` (checking
whether `Success` -> `IsSuccess` shows the same unrelated-string-literal corruption pattern) returned
18 hits. All 18 were checked and are legitimate, pre-existing, or unrelated to this defect: dictionary
content meaning something else (`"Controllers"`, `"Repositories"`, `"Migrations"` -- unrelated to
`IsSuccess`), a test method's unrelated string argument, legitimate `<see cref="SuccessDetails"/>`
doc comments (correctly renamed, e.g. `RoslynSentinel.Common/SentinelCallToolResult.cs:79,122`), the
already-intentionally-fixed `"isSuccess"` wire-field consumer checks in
`ServiceRegistrationExtensionsBasic.cs`, `ModelAgentRunner.cs`, `TranscriptReplayTests.cs`, and a
`GetProperty("IsSuccess")` reflection call in `SentinelGitToolsSmokeTests.cs` (correct, since that
reflects the real renamed C# property name). So: `Success` -> `IsSuccess` shows NO instances of this
corruption pattern. The defect is confirmed scoped to the `Error` -> `ErrorDetails`,
`Warning` -> `WarningDetails`, and `Data` -> `SuccessDetails` renames only -- not all 4 renames
uniformly. Why this one rename is clean while the other three are not is itself unexplained and
should be considered during root-cause tracing (it may be a clue: `Success`/`IsSuccess` do not
collide with any common English word used elsewhere as a free-standing label the way `Error`,
`Warning`, and `Data` do, which is consistent with -- but does not prove -- a text-scan-based
mechanism rather than a uniformly-applied Roslyn renamer option).

## Root cause -- NOT YET TRACED TO SOURCE

`RenameSymbol`'s implementation has not yet been read to find the code path responsible for touching
string-literal and comment content outside genuine symbol references. No claim below should be read
as a confirmed cause; everything in this section is a hypothesis pending verification against source.

**Hypothesis (unverified):** Roslyn's `Renamer.RenameSymbolAsync` API surface includes rename-option
flags in some versions/overloads (commonly named along the lines of `RenameInStrings` /
`RenameInComments`) that control whether the renamer also rewrites textual matches inside string
literals and comments in addition to genuine symbol references. If `RenameSymbol` invokes an overload
with these enabled by default, or performs its own separate text-scan/replace pass alongside the
semantic rename, that would produce exactly this symptom shape: correct symbol scoping (visible in
`residualMentions`) plus silent unrelated-text rewriting (invisible to `residualMentions`, since that
mechanism evidently only evaluates genuine symbol-reference candidates, not the text it went on to
change). This needs verification directly against `RenameSymbol`'s implementation source before being
treated as the confirmed cause -- it is currently a hypothesis, not a finding.

Also unexplained and worth tracing alongside the above: why the `Data` -> `SuccessDetails` rename
touched a comment/assertion string but did NOT touch the real `namespace MyApp.Data` declaration
sharing the same file (Cluster 3) -- if a single text-scan pass were indiscriminately matching the
substring `Data`, the namespace declaration's own `Data` token should have matched too. The fact that
it didn't suggests the rewriting mechanism is not a naive whole-file text scan, which narrows but does
not resolve the hypothesis above.

## Why this blocks (per CLAUDE.md failure doctrine)

A tool that silently mutates content outside its stated/expected scope (genuine C# symbol
references) -- with zero warning in its own `residualMentions` output -- is a blocking finding. It
was only caught because `RunTest` happened to cover some of the corrupted lines; the Cluster 1/2 sites
with no direct test assertion on the literal value (e.g. `ArchitecturalEngine.cs`'s dictionary,
`SymbolNavigationEngine.cs`'s `Kind` label, several of the `AnalysisEngine.cs`/`AntiPatternEngine.cs`
`Severity =` assignments) would have landed silently with no test failure at all. Per CLAUDE.md:
"finish any in-flight edit, stop advancing the task, write a blocker doc, and end the turn. Do not
retry speculatively, do not route around it with shell tools, and do not resume until told the issue
is fixed."

## What would resolve this

- Trace `RenameSymbol`'s implementation to find why/whether it intentionally rewrites string-literal
  and comment content matching the renamed identifier, and whether that is a Roslyn `RenameOptions`
  default, a custom text-scan pass, or something else -- confirm against source, not the hypothesis
  above.
- Decide the correct behavior: string literals and comments should very likely never be silently
  rewritten by a semantic symbol rename unless explicitly opted into -- this is exactly the kind of
  near-miss text the tool's `residualMentions` mechanism exists to flag AS a residual mention, not
  silently change out from under the caller.
- Fix the corrupted content at every confirmed site listed above (Clusters 1-3), restoring the
  original literal text (`"Error"`, `"Warning"`, `"Data"`) while preserving all the legitimate symbol
  reference renames already correctly applied elsewhere in the same files. Given the wider-than-
  expected blast radius found during this verification pass, re-run the `SearchSolutionText` queries
  above (not just trust this list) immediately before starting the fix, in case further drift has
  occurred.
- Re-run `RunTest` (full solution) to confirm the 33 failures reduce appropriately with no new
  failures introduced.

## Related

- `docs/current/blockers/resolved/blocking_error_sessionhalted_drift_vs_clean_git_status.md` and
  `docs/current/blockers/resolved/blocking_error_member_remove_false_not_found.md` -- the two prior
  blockers hit earlier in this same plan's execution (different root causes, both already resolved);
  noted here only for continuity of the plan's blocker trail, not because they share a cause with
  this one.
- Resume point: envelope field-promotion plan
  (`C:\Users\Administrator\.claude\plans\inherited-orbiting-toucan.md`), Step 4 verification
  (`RunTest`) -- this defect was discovered mid-verification and blocks completing Step 4 until fixed.

## Status

Resolved - corruption reverted by the user/another process outside this session, not by a fix we
wrote ourselves. This doc's root-cause section above was NOT updated as part of this resolution:
`RenameSymbol`'s implementation still has not been traced to source to confirm the actual rewrite
mechanism (the `RenameOptions.RenameInStrings`/`RenameInComments` explanation above remains an
unverified hypothesis). Only the symptom - the corrupted literals themselves - is confirmed reverted.

### Re-verification steps taken (2026-09-20, follow-up session)

To rule out checking a stale in-memory workspace, the server was restarted fresh before re-checking:

1. `McpServerControl(operation: "stop")` + respawn (new PID, fresh `buildTimeUtc`).
2. `LoadSolution(forceReload: true)` to force the in-memory workspace to resync with disk rather than
   trusting a cached snapshot.
3. Three `SearchSolutionText` re-checks against the fresh workspace:
   - Regex `== "(ErrorDetails|WarningDetails|SuccessDetails)"` across the whole solution: **zero
     matches** (previously 10+ confirmed corrupted sites across Cluster 1, including
     `BuildEngine.cs`, `DiagnosticEngine.cs`, `DiffEngineTests.cs`, and - found during the original
     doc-writing pass - `AntiPatternEngine.cs`, `AnalysisEngine.cs`, `ValidationEngine.cs`,
     `WorkspaceBuildTestImpl.cs`, `SymbolNavigationEngine.cs`, `NamespacePathMismatchTests.cs`,
     `NewFeaturesTests.cs`).
   - Literal `"SuccessDetails"` (unquoted substring, to also catch the `ArchitecturalEngine.cs`
     dictionary-key cluster from Cluster 2): only 6 hits, all legitimate - two
     `<see cref="SuccessDetails"/>` XML doc comments in `SentinelCallToolResult.cs`, and four
     `GetProperty("successDetails")`/`TryGetProperty("successDetails")` wire-field JSON lookups in
     `McpTasksHarnessBulkCommentTests.cs` and `LargeResultOffloadFilterTests.cs` (correct - the real
     camelCase wire field name post-rename is `successDetails`). No corrupted dictionary keys, no
     corrupted comments remain.
   - Literal `MyApp.SuccessDetails` (the Cluster 3 `FiveStarToolTests.cs` collision): **zero
     matches** - confirms the corrupted `MyApp.Data` -> `MyApp.SuccessDetails` text in comments/
     assertions is reverted back to `MyApp.Data`.
4. `RunTest` (full solution, summary mode) on the fresh server: 2576 total, 2460 passed, 22 failed,
   94 skipped - down from the original 33 failed. Critically, none of the remaining 22 failures touch
   the previously-corrupted files or match the corruption signature; `BuildEngine.cs`,
   `DiagnosticEngine.cs`, `ArchitecturalEngine.cs`, `DiffEngineTests.cs`, and `FiveStarToolTests.cs`
   are entirely absent from the new `FailureSummary`. The remaining 22 break down as: 15x "LM Studio
   request failed with BadRequest" (model-eval fixtures hitting a local LM Studio endpoint -
   environmental/pre-existing, unrelated to this defect), 2x an enum-container `AddMemberAsync`
   rejection-message assertion (pre-existing, unrelated), 2x `KeyNotFoundException` in
   `LocateSymbol_Call_PopulatesStructuredContent_MatchingActualData` (unrelated signature, not a
   string-literal corruption pattern), 2x task-vs-sync content-equivalence assertions (unrelated), and
   1x `Git_Commit_RepoPath_IsRejectedAsync` (see follow-up below).

### Before/after failure count

33 failed -> 22 failed (full-solution `RunTest`, same 2576-total/94-skipped baseline both times).

### Open follow-up surfaced by this verification (separate from this doc's scope)

`Git_Commit_RepoPath_IsRejectedAsync` fails on a `successProperty` reflection assertion - it appears
to be a stale test still reflecting on a property literally named `Success`, which no longer exists
post-rename to `IsSuccess`. This is NOT an instance of the string-literal corruption this doc is
about (it is a reflection lookup on a real C# property name, not a free-text literal/comment/
dictionary-key match), and has not been investigated further here. It reads as expected fallout from
the envelope field-promotion plan's Step 4 ("fix Battery tests") not yet being fully complete, not a
new defect. Tracked as a small separate loose end for whoever resumes Step 4 - not folded into this
blocker's remaining scope.

## Related (resolution)

- `docs/current/blockers/resolved/blocking_error_sessionhalted_drift_vs_clean_git_status.md` and
  `docs/current/blockers/resolved/blocking_error_member_remove_false_not_found.md` - this doc now
  joins those two in `docs/current/blockers/resolved/` as the third resolved blocker in the same
  envelope field-promotion plan trail.
