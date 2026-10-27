using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("RefactoringExtractionDocsTools")]
public class ExtractMethodEolEndToEndTests
{
    private static RefactoringExtractionDocsImpl BuildExtractionDocsImpl(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        return new RefactoringExtractionDocsImpl(
            new BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config),
            new MsToolAugmentEngine(workspaceManager),
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            workspaceManager,
            new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance),
            NullLogger<RefactoringExtractionDocsImpl>.Instance);
    }

    [Test]
    public async Task ExtractMethodTool_MixedEolPreImageUnderCrlf_WritesUniformCrlfAsync()
    {
        // Step 5 end-to-end case: a mixed-EOL pre-image run through ExtractMethod under crlf must converge to
        // uniform CRLF on disk. The EOL chokepoint converts the whole file, not only the edited lines.
        using var fixture = new TestSolutionFixture();
        var target = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "EolExtractSample.cs");
        const string mixedSource =
            "namespace ContosoOrders.Core;\r\n" +
            "\r\n" +
            "public class EolExtractSample\r\n" +
            "{\r\n" +
            "    private int _n;\n" +
            "\r\n" +
            "    public void Run(int a)\r\n" +
            "    {\r\n" +
            "        _n = 0;\n" +
            "        _n = a + 1;\r\n" +
            "        _n = _n * 2;\r\n" +
            "    }\r\n" +
            "}\r\n";
        await File.WriteAllBytesAsync(target, System.Text.Encoding.UTF8.GetBytes(mixedSource));

        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.EolModeOverride = EolMode.Crlf;
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var tools = new RefactoringExtractionDocsTools(BuildExtractionDocsImpl(workspaceManager));
        var result = await tools.ExtractMethodTool(reason: "test message", target, "Step", "_n = a + 1;");

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var bytes = await File.ReadAllBytesAsync(target);
        Assert.That(bytes.Where((b, i) => b == (byte)'\n' && (i == 0 || bytes[i - 1] != (byte)'\r')).Count(), Is.Zero, "no bare LF byte may survive");
        Assert.That(bytes.Where((b, i) => b == (byte)'\r' && (i == bytes.Length - 1 || bytes[i + 1] != (byte)'\n')).Count(), Is.Zero, "no bare CR byte may survive");
        Assert.That(bytes.Where((b, i) => b == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n').Any(), Is.True, "the file must contain CRLF");
        Assert.That(System.Text.Encoding.UTF8.GetString(bytes), Does.Contain("Step"));
    }
}
