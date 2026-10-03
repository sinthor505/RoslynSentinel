// Fixtures run in parallel with each other; tests inside one fixture stay serial, so per-fixture
// instance fields set in [SetUp] remain safe. Audited 2026-10-01: no test in this assembly mutates
// process-wide state (env vars, current directory, ReadEnvelopeThresholds, ServerBuildInfo,
// SentinelHostOptions, ToolCallEchoOptions); the McpTasksHarness* fixtures build a per-test host,
// in-memory pipes and a GUID-named TestSolutionFixture temp copy. If a new test must mutate
// process-wide state, mark its fixture [NonParallelizable] (see RoslynSentinel.Tests'
// ServerBuildInfoEnvelopePathTests for the pattern).
[assembly: Parallelizable(ParallelScope.Fixtures)]
