// Battery #27 -> Bug-Hunt Findings (Session 7)
// Confirmed bugs found by manual engine code review:
//
//   BH-01: DetectMissingCancellationToken skips methods with < 3 parameters
//   BH-02: DetectStringConcatInLoop misses `str = str + value` (non-compound form)
//   BH-03: FindUnsafeTypeCastsAsync flags safe numeric casts (false positive)
//   BH-04: DetectMissingNullChecksAsync is a stub -> detection logic is commented out
//   BH-05: ConvertLockToSemaphoreSlimAsync makes ALL overloads async when only one has a lock
//
// Tests in this file confirm the bugs that exist BEFORE the engine fixes are applied.
// After the fixes the assertions flip (or new assertions are added).

#pragma warning disable CS8618

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced
{
    [TestFixture]
    [Category("ThreadSafetyEngine")] // sentinel:auto-category
    public class BH05_ConvertLockOverloadBugTests
    {
        private IWorkspaceManager _workspaceManager = null!;
        private ThreadSafetyEngine _engine = null!;

        [SetUp]
        public void Setup()
        {
            _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
            _engine = Battery.Basic.TestEngines.ThreadSafetyEngine(_workspaceManager);
        }

        [TearDown]
        public void TearDown() => _workspaceManager?.Dispose();

        [Test]
        public async Task ConvertLockToSemaphoreSlim_OverloadWithoutLock_ShouldNotBecomeAsync()
        {
            // BUG: ConvertLockToSemaphoreSlimAsync matches affected methods by name only.
            // If a method "Process" has a lock and an overload "Process(string)" does NOT,
            // both overloads will be made async -> the overload-without-lock should NOT be touched.
            const string source = """
            public class Worker {
                private readonly object _lock = new object();

                public void Process() {
                    lock (_lock) {
                        DoWork();
                    }
                }

                // Overload WITHOUT a lock -> should NOT be made async
                public void Process(string label) {
                    DoWork();
                }

                private void DoWork() { }
            }
            """;
            var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Worker.cs", source)]);
            _workspaceManager.SetTestSolution(solution);

            var result = await _engine.ConvertLockToSemaphoreSlimAsync("Worker.cs", "Process");

            // The overload Process(string label) should NOT become async.
            Assert.That(result.UpdatedText, Does.Not.Contain("async Task Process(string label)"),
                "The overload that has no lock should NOT be made async.");
            // The original Process() SHOULD be async.
            Assert.That(result.UpdatedText, Does.Contain("async Task Process()"),
                "The method that has the lock SHOULD be made async.");
        }

        [Test]
        public async Task ConvertLockToSemaphoreSlim_SingleMethodWithLock_ConvertsCorrectly()
        {
            // Baseline: single method with lock converts properly.
            const string source = """
            public class Counter {
                private readonly object _lock = new object();
                private int _count = 0;

                public void Increment() {
                    lock (_lock) {
                        _count++;
                    }
                }
            }
            """;
            var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Counter.cs", source)]);
            _workspaceManager.SetTestSolution(solution);

            var result = await _engine.ConvertLockToSemaphoreSlimAsync("Counter.cs", "Increment");

            Assert.That(result.UpdatedText, Does.Contain("SemaphoreSlim"),
                "Conversion must introduce a SemaphoreSlim field.");
            Assert.That(result.UpdatedText, Does.Contain("WaitAsync"),
                "Conversion must replace lock with WaitAsync.");
            Assert.That(result.UpdatedText, Does.Not.Contain("lock ("),
                "The original lock statement must be removed.");
        }

        [Test]
        public async Task ConvertLockToSemaphoreSlim_MethodWithNoLock_ThrowsNotFound()
        {
            // A method with no lock statement throws NotFound; the engine no longer returns the file unchanged.
            const string source = """
            public class Service {
                public void Process() {
                    System.Console.WriteLine("no lock here");
                }
            }
            """;
            var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
            _workspaceManager.SetTestSolution(solution);

            var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.ConvertLockToSemaphoreSlimAsync("Service.cs", "Process"));
            Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), "error code should be NotFound");
        }
    }
}
