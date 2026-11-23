// Battery #27 -> Bug-Fix Regression Tests
// Each test proves a specific bug (catalogued in the review sessions) is fixed and
// cannot silently regress.  Test names encode the bug ID they guard.
//
// Bugs covered:
//   B08 -> DeadCodeEngine: written-but-never-read variables were not detected as unused
//   B02 -> ImmutabilityEngine: const fields received readonly modifier (-> CS0106)
//   B01 -> InstrumentationEngine: throw was emitted as ExpressionStatement (invalid C#)
//   B03 -> SolutionStructureEngine: IdentifierName contained a dot (invalid identifier)
//   B09 -> AdvancedRefactoringEngine: OptimizeTaskWaitAsync rewrote all .Wait()/.Result
//         when semantic model was null (false positives)
//   B18 -> ContextHelper: OrdinalIgnoreCase blocked valid PascalCase identifiers such as
//         "String", "Object", "Int"
//   B19 -> TestingEngine: MSTest framework produced attributeless test methods
//   B17 -> DocumentationEngine: HasStructuredTrivia excluded methods inside #region
//   B11 -> ModernizationEngine.ClassToRecordAsync: duplicate property declarations (CS0102)
//   B10 -> ModernizationEngine.TryConvertOrChainToPattern: only last value used (chain lost)
//   B20 -> LogicSimplificationEngine.AddGuardClausesAsync: nullable params got null guards
//   B04 -> AntiPatternEngine.DetectMissingCancellationToken: zero-param async methods skipped
//   B16 -> SecurityAndSafetyEngine.DetectMissingNullChecksAsync: expression-bodied methods skipped
#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Category("SolutionStructureEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class B03_SolutionStructureEngine_ValidMemberAccess
{
    private IWorkspaceManager _workspaceManager = null!;
    private SolutionStructureEngine _engine = null!;
    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration());
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }
    [Test]
    public async Task ConvertToBackgroundService_ProducesValidMemberAccess()
    {
        // Before fix: SyntaxFactory.IdentifierName("stoppingToken.IsCancellationRequested")
        // creates an identifier whose text contains a dot -> that is not valid C# and Roslyn
        // would emit an identifier with a dot in it, which doesn't compile.
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            public class MyWorker {
                public async Task RunAsync(CancellationToken token) {
                    await Task.Delay(1000);
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("MyWorker.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).ConvertToBackgroundServiceAsync("MyWorker.cs", "MyWorker");
        // The result must contain "stoppingToken.IsCancellationRequested" as proper member access
        Assert.That(result.UpdatedText, Does.Contain("stoppingToken.IsCancellationRequested"), "Output must reference stoppingToken.IsCancellationRequested via member access.");
        // Crucially, the dotted form must NOT appear inside an IdentifierName literal string
        // in the output (which would indicate un-parsed invalid syntax)
        Assert.That(result.UpdatedText, Does.Not.Contain("\"stoppingToken.IsCancellationRequested\""), "Output must not contain dotted identifier as a literal string.");
    }
}
