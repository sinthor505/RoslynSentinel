using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.Tools.Advanced;

// Not-found text of BasicRefactoringEngine.WrapInTryCatchAsync (plan_not_found_error_orientation, Step 5e slice 4), driven through WrapRange(wrapper: tryCatch),
// the tool that hosts both overloads. Line-range form: a range inside no block body, and a range inside a block body that holds no statement. Snippet form:
// a snippet matched outside every block body, and a snippet matched inside a block body that holds no statement. Each message states the file's line count and
// the range (or the line the snippet matched) as the fact and offers GetFileOutline with the file path prefilled; the old "Choose a range ..." instruction is
// gone. A refused call writes nothing: the file text is compared before and after.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class WrapRangeTryCatchMissAdvancedTests
{
    private const string WorkerFile = "Worker.cs";

    // Lines: 5 is the Limit property, 8-10 are the empty Noop body (a comment, no statement), 14-15 are the statements of Run.
    private const string WorkerSource = """
        namespace TestProj;

        public class Worker
        {
            public int Limit { get; set; }

            public void Noop()
            {
                // idle marker
            }

            public int Run(int input)
            {
                var doubled = input * 2;
                return doubled + Limit;
            }
        }
        """;

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("Choose a range"), "No instruction sentence.");
        Assert.That(message, Does.Not.Contain("Could not find"), "No bare 'could not find' sentence.");
    }

    // Runs one refused call and returns the error text; asserts NotFound, the file's line count, a prefilled GetFileOutline and an untouched file.
    private static async Task<string> RunMissAsync(Func<AdvancedRefactoringTools, InMemoryWorkspace, Task<SentinelCallToolResult<AppliedChangeSummary>>> call)
    {
        using var workspace = InMemoryWorkspace.Create((WorkerFile, WorkerSource));
        var before = workspace.ReadText(WorkerFile);
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);

        var result = await call(tools, workspace);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode.ToString(), Is.EqualTo(nameof(ToolErrorCode.NotFound)), result.ErrorData.Message);
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Match(@"has \d+ line\(s\)"));
        Assert.That(message, Does.Contain("GetFileOutline(").And.Contain(WorkerFile), "the outline call carries the file path");
        AssertFactsAndOptionsOnly(message);
        Assert.That(workspace.ReadText(WorkerFile), Is.EqualTo(before), "A refused range must not write the file.");
        return message;
    }

    [Test]
    [Category("BasicRefactoringEngine")]
    public async Task WrapRangeTryCatch_RangeInsideNoBlockBody_StatesTheRangeAndTheLineCountAsync()
    {
        // Line 5 is the Limit property: no block body contains it.
        var message = await RunMissAsync((tools, w) => tools.WrapRangeTool(reason: "test message", w.PathOf(WorkerFile), 5, 5, WrapRangeWrapper.tryCatch));

        Assert.That(message, Does.Contain("No block body of"));
        Assert.That(message, Does.Contain("contains lines 5-5"));
    }

    [Test]
    [Category("BasicRefactoringEngine")]
    public async Task WrapRangeTryCatch_RangeInsideABlockWithoutStatements_StatesTheRangeAndTheLineCountAsync()
    {
        // Lines 8-10 are the Noop body: the block contains the range but holds no statement.
        var message = await RunMissAsync((tools, w) => tools.WrapRangeTool(reason: "test message", w.PathOf(WorkerFile), 8, 10, WrapRangeWrapper.tryCatch));

        Assert.That(message, Does.Contain("No statement of the block body that contains lines 8-10 of"));
        Assert.That(message, Does.Contain("overlaps that range"));
    }

    [Test]
    [Category("BasicRefactoringEngine")]
    public async Task WrapRangeTryCatch_SnippetOutsideEveryBlockBody_StatesTheMatchedLineAndTheLineCountAsync()
    {
        var message = await RunMissAsync((tools, w) => tools.WrapRangeTool(reason: "test message", w.PathOf(WorkerFile), wrapper: WrapRangeWrapper.tryCatch, contextSnippet: "public int Limit"));

        Assert.That(message, Does.Contain("The snippet matched at line 5 of"));
        Assert.That(message, Does.Contain("no block body contains that line"));
    }

    [Test]
    [Category("BasicRefactoringEngine")]
    public async Task WrapRangeTryCatch_SnippetInABlockWithoutStatements_StatesTheMatchedLineAndTheLineCountAsync()
    {
        var message = await RunMissAsync((tools, w) => tools.WrapRangeTool(reason: "test message", w.PathOf(WorkerFile), wrapper: WrapRangeWrapper.tryCatch, contextSnippet: "idle marker"));

        Assert.That(message, Does.Contain("The snippet matched at line 9 of"));
        Assert.That(message, Does.Contain("inside a block body, but no statement of that block contains it"));
    }
}
