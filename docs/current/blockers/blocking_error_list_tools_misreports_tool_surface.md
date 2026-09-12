# `--list-tools` reports the wrong tool surface — wrong assembly, wrong names, and Advanced modes look empty

**Status:** OPEN — found 2026-09-12 while measuring the cost of trimming the MCP tool surface.

`--list-tools` is the documented way to preview which tools a `--mode`/`--include-tools`
combination would expose. `ServerStartupHelpers.DescribeNoActiveToolsFailure` points operators at
it explicitly:

> "Use --list-tools to print the tool surface a given combination would expose."

It does not do that. Running against `RoslynSentinel.Server.Advanced.exe` it reports **54 tools**,
while the same binary actually serves **~118**. Every discrepancy traces to
`SentinelConsoleMode.DiscoverTools` (`RoslynSentinel.Server.Basic/SentinelConsoleMode.cs:55`).

## Defect 1 — reflects over the Basic assembly only

`SentinelConsoleMode.cs:66`:

```csharp
return typeof(SentinelWorkspaceTools).Assembly
    .GetTypes()
```

`SentinelWorkspaceTools` lives in `RoslynSentinel.Server.Basic`, so the scan only ever sees Basic's
tool classes. Every Advanced-only class is structurally invisible:
`SentinelAsyncifyTools`, `SentinelIntelligenceTools`, `SentinelScanTools`,
`SentinelModernizationTools`, `SentinelQualityTools`, `SentinelGenerationTools`,
`SentinelCommentingTools`, `SentinelCodemodTools`, `SentinelAdvancedRefactoringTools`.

Confirmed by probing for tools known to be live in a running session — `Asyncify`,
`BridgeAsyncMethods`, `GenerateMapping`, `GetMethodComplexity`, `ApplyClassCodemod`,
`SummaryComment`, `ScanBreakingChanges` are all reported absent while all are callable.

**Consequence:** every Advanced-only mode prints an empty list.

```
--mode=Intelligence  --list-tools   ->  []
--mode=Modernize     --list-tools   ->  []
--mode=Quality       --list-tools   ->  []
--mode=Generation    --list-tools   ->  []
--mode=Asyncify      --list-tools   ->  []
```

An operator reasonably concludes those modes are broken or register nothing. They register fine —
the *reporting* is broken. This is a worse failure than an error would be: `[]` is a confident,
well-formed, wrong answer.

It also makes `--mode=all` appear to equal `workspace,refactor,admin,wholefilewrite`: both print
byte-identical 119,770-char output. That equality is an artifact, not a fact about the surface.

## Defect 2 — emits snake_case, but the server serves PascalCase

`SentinelConsoleMode.cs:82` wraps every name in `ToSnakeCase(m.Name)`, producing `read_file`,
`apply_diff`, `get_file_outline`. The MCP surface exposes `ReadFile`, `ApplyDiff`,
`GetFileOutline`.

So the listing cannot be used to build an `--include-tools`/`--exclude-tools` argument, cross-check
an agent transcript, or grep a log — every name needs manual re-casing first, and nothing says so.
Note `--include-tools` matches on *class* names (`SentinelScanTools`) while this prints *tool*
names in a third casing, so the output of the preview command is in neither vocabulary the gating
flags accept.

## Defect 3 — two independent sources of truth

The real problem behind both: `DiscoverTools` is a reflection pass that re-derives the tool surface
independently of whatever DI actually registers at startup. Nothing keeps them in sync, and this is
exactly the drift that produces. A tool class added to Advanced, or a rename, silently diverges.

## Suggested fixes

1. Scan the assemblies that actually contribute tool classes rather than one hardcoded type's
   assembly — or better, enumerate what was registered with the MCP server, so the preview reports
   reality instead of a parallel guess.
2. Drop `ToSnakeCase`, or print both spellings with a header naming which is which.
3. Add a test asserting `--list-tools` count equals the registered tool count for a given mode set.
   The absence of that assertion is why a >2x discrepancy went unnoticed.
4. Consider printing the resolved *class* list alongside the tool list, since that is the vocabulary
   `--include-tools`/`--exclude-tools` actually take.

## Why this matters beyond tidiness

Which tools are exposed changes agent behaviour measurably — the remark at
`ServerStartupHelpers.cs:219` cites gating whole-file writes off scoring 26/26 against a 47%
baseline. Tool-surface selection is therefore a real experimental variable, and `--list-tools` is
the instrument for reading it. An instrument that under-reports by half, in names that don't match
either the served surface or the gating flags, makes surface trimming guesswork.
