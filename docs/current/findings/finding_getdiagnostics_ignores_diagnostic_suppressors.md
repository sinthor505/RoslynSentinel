# Finding: GetDiagnostics reports compiler warnings that a project's DiagnosticSuppressor analyzers suppress in the real build

**Status:** FIXED 2026-11-23 (3daad2ab). Cause established (not a compilation option); the suppressor pass is built in `DiagnosticEngine`. The text below is the original analysis.

## Context

Punchlist #15. `GetDiagnostics(scope: file, scopeName: RoslynSentinel.Tests.Advanced/ChangeSignatureWrapperTests.cs)`
still returns `CS8604` at line 313 (verified 2026-11-22: "Possible null reference argument for parameter 'source' in
'RewrittenCallSite Enumerable.Single<RewrittenCallSite>(...)'"), while `dotnet build --no-incremental` and the `Build`
tool report 0 warnings for the same project. Line 313 is
`var expanded = result.MigratedCallSites.Single(...)`, where `ChangeSignatureResult.MigratedCallSites` is declared
`List<RewrittenCallSite>?` (`RoslynSentinel.Engines.Basic/BasicRefactoringEngine.cs:57`), so the compiler warning is
genuine flow analysis, not a false positive of the workspace.

## What is broken

`GetDiagnostics` shows raw compiler diagnostics. The real build also raises them, but the NUnit analyzer package
(`NUnit.Analyzers` 4.15.0, referenced by `RoslynSentinel.Tests.Advanced.csproj:38`) ships `DiagnosticSuppressor`s that
the compiler applies in the build and that the server never runs. Evidence, from a detailed build of a scratch copy of
the repo (`dotnet build RoslynSentinel.Tests.Advanced.csproj --no-incremental -v:d`, scratch root, not the repo):

```
ChangeSignatureWrapperTests.cs(313,24): info SP0001: Diagnostic 'CS8604: Possible null reference argument for
parameter 'source' in 'RewrittenCallSite Enumerable.Single<RewrittenCallSite>(...)'.' was programmatically
suppressed by a DiagnosticSuppressor with suppression ID 'NUnit3001' and justification 'Expression was checked in an
ClassicAssert.NotNull, ClassicAssert.IsNotNull or Assert.That call'
```

The build log holds 117 such `SP0001` lines (suppression IDs `NUnit3001` and `NUnit3002`, the latter "Field/Property is
initialized in SetUp or OneTimeSetUp method"). The file has `Assert.That(result.MigratedCallSites, Has.Count.EqualTo(2))`
two lines before, which is exactly what NUnit3001 treats as a null check. So a normal build passes `ReportSuppressedDiagnostics`
off and the warning vanishes; only `info SP0001` remains, visible at `-v:d`.

## Root cause

`DiagnosticEngine` calls `semanticModel.GetDiagnostics(...)` (`RoslynSentinel.Engines.Basic/DiagnosticEngine.cs:40`) and
`compilation.GetDiagnostics(...)` (`:55`, `:108`). Those return compiler diagnostics only; a `DiagnosticSuppressor` runs
only inside the analyzer driver (`compilation.WithAnalyzers(...)`). A `Search` for `WithAnalyzers|DiagnosticSuppressor|IsSuppressed`
over `Engines.*`, `Common*` and `Tools*` finds no use, so the server never runs any analyzer, suppressors included.

Ruled out (all compared against `csc` command line and MSBuild evaluation; scratch dump of the workspace options for
`RoslynSentinel.Tests.Advanced` after `MSBuildWorkspace.OpenSolutionAsync(RoslynSentinel.slnx)`):

| Option | Workspace | Real `csc` (`-v:d`) |
| --- | --- | --- |
| Nullable | `NullableContextOptions=Enable` | `/nullable:enable` |
| Warning level | `WarningLevel=1` | `/warn:1` (csproj lines 20-30) |
| General / specific | `GeneralDiagnosticOption=Default`, `CS1702/CS1701/CS8002:Suppress`, `NU1605/SYSLIB0011:Error` | `/nowarn:1701,1702,...,8002`, `/warnaserror+:NU1605,SYSLIB0011` |
| Language | `LanguageVersion=Latest` | `/langversion` latest |
| Features | `InterceptorsNamespaces=;Microsoft.Extensions.Validation.Generated` | same `/features:` |
| Analyzer configs | root `.editorconfig`, `Tests.Advanced/.editorconfig`, `GeneratedMSBuildEditorConfig` | same three `/analyzerconfig:` |
| Analyzer references | includes `nunit.analyzers` | `/analyzer:` for the same package |

`CS8604` is not downgraded by any config (`dotnet_diagnostic.CS8604.severity` is unset), and warning level 1 does not hide
it: recompiling the workspace compilation with `WithWarningLevel(1/2/4/9999)` yields the same single `CS8604`. Referenced
metadata is also not a factor: a synthetic compilation against the built `RoslynSentinel.Engines.Basic` implementation and
reference assemblies (`obj/Debug/net10.0/ref`) raises `CS8604` too, so the `?` annotation is present in both.

## Why it matters

Any warning an analyzer suppression removes from the build (NUnit3001/NUnit3002 in every test project; likewise other
packages' suppressors) appears in `GetDiagnostics` as a real warning, so a caller trusting `GetDiagnostics` to match
`Build` chases warnings that do not exist, and the `verify: fullBuild` check disagrees with the `noBuild` result.

## Recommendation

Decided: the cause is the missing suppressor pass, not an option. Proposed fix (not built): in `DiagnosticEngine`, collect
the `DiagnosticSuppressor` instances from `project.AnalyzerReferences` (`reference.GetAnalyzers(LanguageNames.CSharp)`),
and when there is at least one, take the diagnostics from
`compilation.WithAnalyzers(suppressors, new CompilationWithAnalyzersOptions(project.AnalyzerOptions, ...)).GetAllDiagnosticsAsync()`
(the workspace sets `ReportSuppressedDiagnostics=False`, so suppressed ones are dropped; otherwise filter `IsSuppressed`);
for file scope, filter that list by syntax tree. Cheaper alternative, open: keep raw diagnostics and tag a warning
`mayBeSuppressedByAnalyzer` when the project references a package with a suppressor. Cost/risk of running the driver
on a whole solution is unmeasured (hypothesis: only suppressor analyzers are passed, so analyzer cost stays near the
compiler pass).

## Out of scope

Running non-suppressor analyzers (`NUnit.Analyzers` diagnostics, CAxxxx) through `GetDiagnostics`; the build already
runs them and the `Build` tool covers that.
