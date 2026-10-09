using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Battery.Basic;

// MemberRefactoringEngine.RemoveMethodParameterAsync used to edit the declaration first and then re-locate each call
// site in the half-edited text by its ORIGINAL span, which missed after any earlier edit to the same file. It now
// collects every edit (declaration + call sites) from one root per file and applies them with one ReplaceNodes. These
// tests drive the engine directly (no tool layer) and assert on DocumentEditResult.Changes.
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("RefactoringSignatureTools")]
public class RemoveMethodParameterSinglePassTests
{
    private const string TargetPath = "N/Target.cs";
    private const string CallerPath = "N/Caller.cs";

    private static MemberRefactoringEngine BuildEngine(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var navigation = new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = new ValidationEngine(workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance);
        return new MemberRefactoringEngine(workspaceManager, navigation, validation, config);
    }

    private static async Task<(DocumentEditResult Result, InMemoryWorkspace Workspace)> RemoveAsync(params (string RelativePath, string Content)[] files)
    {
        var workspace = InMemoryWorkspace.Create(files);
        var engine = BuildEngine(workspace.Manager);
        var path = workspace.Manager.ResolveFromWire(workspace.PathOf(TargetPath));
        var result = await engine.RemoveMethodParameterAsync(path, "Compute", "second");
        return (result, workspace);
    }

    private static string TextOf(DocumentEditResult result, InMemoryWorkspace workspace, string relativePath)
        => result.Changes[workspace.Manager.ResolveFromWire(workspace.PathOf(relativePath))];

    [Test]
    public async Task SameFileCallSiteAfterDeclaration_IsUpdatedAsync()
    {
        const string source = """
            namespace N;

            public class Target
            {
                public void Compute(int first, string second) { }

                public void Caller() { Compute(1, "x"); }
            }
            """;

        var (result, workspace) = await RemoveAsync((TargetPath, source));
        using (workspace)
        {
            Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
            Assert.That(result.Changes, Has.Count.EqualTo(1));
            var text = TextOf(result, workspace, TargetPath);
            Assert.That(text, Does.Contain("Compute(int first)"));
            Assert.That(text, Does.Contain("Compute(1)"));
            Assert.That(text, Does.Not.Contain("\"x\""));
        }
    }

    [Test]
    public async Task TwoCallSitesInSameFile_BeforeAndAfterDeclaration_BothUpdatedAsync()
    {
        const string source = """
            namespace N;

            public class Target
            {
                public void Before() { Compute(1, "a"); }

                public void Compute(int first, string second) { }

                public void After() { Compute(2, "b"); }
            }
            """;

        var (result, workspace) = await RemoveAsync((TargetPath, source));
        using (workspace)
        {
            Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
            Assert.That(result.Changes, Has.Count.EqualTo(1));
            var text = TextOf(result, workspace, TargetPath);
            Assert.That(text, Does.Contain("Compute(int first)"));
            Assert.That(text, Does.Contain("Compute(1)"));
            Assert.That(text, Does.Contain("Compute(2)"));
            Assert.That(text, Does.Not.Contain("\"a\""));
            Assert.That(text, Does.Not.Contain("\"b\""));
        }
    }

    [Test]
    public async Task TwoCallSitesInAnotherFile_BothUpdatedAsync()
    {
        const string target = """
            namespace N;

            public class Target
            {
                public void Compute(int first, string second) { }
            }
            """;
        const string caller = """
            namespace N;

            public class Caller
            {
                public void One(Target t) { t.Compute(1, "a"); }

                public void Two(Target t) { t.Compute(2, "b"); }
            }
            """;

        var (result, workspace) = await RemoveAsync((TargetPath, target), (CallerPath, caller));
        using (workspace)
        {
            Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
            Assert.That(result.Changes, Has.Count.EqualTo(2));
            Assert.That(TextOf(result, workspace, TargetPath), Does.Contain("Compute(int first)"));
            var callerText = TextOf(result, workspace, CallerPath);
            Assert.That(callerText, Does.Contain("t.Compute(1)"));
            Assert.That(callerText, Does.Contain("t.Compute(2)"));
            Assert.That(callerText, Does.Not.Contain("\"a\""));
            Assert.That(callerText, Does.Not.Contain("\"b\""));
        }
    }

    [Test]
    public async Task RecursiveCallInsideDeclaration_IsUpdatedAsync()
    {
        const string source = """
            namespace N;

            public class Target
            {
                public void Compute(int first, string second)
                {
                    if (first > 0) { Compute(first - 1, "again"); }
                }
            }
            """;

        var (result, workspace) = await RemoveAsync((TargetPath, source));
        using (workspace)
        {
            Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
            var text = TextOf(result, workspace, TargetPath);
            Assert.That(text, Does.Contain("Compute(int first)"));
            Assert.That(text, Does.Contain("Compute(first - 1)"));
            Assert.That(text, Does.Not.Contain("again"));
        }
    }

    [Test]
    public async Task MethodGroupArgument_IsRefusedNotMisEditedAsync()
    {
        const string source = """
            namespace N;

            public class Target
            {
                public void Compute(int first, string second) { }

                private static void Wrap(Action<int, string> action) { }

                public void Caller() { Wrap(Compute); }
            }
            """;

        var (result, workspace) = await RemoveAsync((TargetPath, source));
        using (workspace)
        {
            Assert.That(result.Outcome, Is.EqualTo(EditOutcome.CannotRemove), result.Message);
            Assert.That(result.Message, Does.Contain("not a simple invocation expression"));
        }
    }

    [Test]
    public async Task LfCallerFile_StaysLfAsync()
    {
        var target = """
            namespace N;

            public class Target
            {
                public void Compute(int first, string second) { }
            }
            """.Replace("\r\n", "\n");
        var caller = """
            namespace N;

            public class Caller
            {
                public void One(Target t) { t.Compute(1, "a"); }
            }
            """.Replace("\r\n", "\n");

        var (result, workspace) = await RemoveAsync((TargetPath, target), (CallerPath, caller));
        using (workspace)
        {
            Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
            var callerText = TextOf(result, workspace, CallerPath);
            Assert.That(callerText, Does.Contain("t.Compute(1)"));
            Assert.That(callerText, Does.Not.Contain("\r"), "an LF call-site file must stay LF (Formatter output is platform EOL)");
            Assert.That(TextOf(result, workspace, TargetPath), Does.Not.Contain("\r"), "an LF declaration file must stay LF");
        }
    }
}
