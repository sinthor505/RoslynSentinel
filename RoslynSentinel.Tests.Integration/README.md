# RoslynSentinel.Tests.Integration (detached)

This project is **not in `RoslynSentinel.slnx`** and is not built or run by default. Its engine
smoke tests were audited on 2026-10-08: almost all were vacuous (`DoesNotThrow` + `Is.Not.Null`)
and duplicated fixture-based coverage. The nine worth keeping were folded into fixture tests:

- `Tests.Basic/DependencyEngineFixtureTests.cs` (dependencies, unused references, type cycles)
- `Tests.Basic/DiagnosticEngineTests.cs` (exact error count)
- `Tests.Advanced/SeededEngineFindingTests.cs` (security, performance, immutability, call tree)

The rest are kept in case a real-solution smoke test is wanted later. Every fixture skips unless
`ROSLYN_SENTINEL_TEST_SLN` points at a real `.sln`/`.slnx`.

Run manually:

```powershell
$env:ROSLYN_SENTINEL_TEST_SLN = "C:\path\to\RoslynSentinel.slnx"
dotnet test RoslynSentinel.Tests.Integration --filter "Category=Integration"
```

Re-attach: `dotnet sln RoslynSentinel.slnx add RoslynSentinel.Tests.Integration/RoslynSentinel.Tests.Integration.csproj`
