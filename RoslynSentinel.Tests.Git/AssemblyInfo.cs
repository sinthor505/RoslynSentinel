// Fixtures run in parallel with each other; tests inside one fixture stay serial, so per-fixture
// instance fields set in [SetUp] remain safe. Same policy as RoslynSentinel.Tests.Tools.Basic, whose
// Git fixtures moved here: no test in this assembly may mutate process-wide state (env vars, current
// directory, RepoQueryProvider.Current) without restoring it. If a new test must mutate process-wide
// state, mark its fixture [NonParallelizable] (see RoslynSentinel.Tests' ServerBuildInfoEnvelopePathTests
// for the pattern).
[assembly: Parallelizable(ParallelScope.Fixtures)]
