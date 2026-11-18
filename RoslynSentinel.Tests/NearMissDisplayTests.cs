#pragma warning disable CS8618
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests;

/// <summary>
/// Coverage for the EVALUATION option <see cref="NearMissOptions.NameDisplay"/> as seen through
/// <see cref="CompilerErrorLookupHelper.DescribeAsync"/>. Under Hide, the wrong name of a near-miss slip must not reach the
/// rejection text; under Show (the default) the text is unchanged.
/// </summary>
[TestFixture]
[Category("CompilerErrorLookupHelper")]
public class NearMissDisplayTests
{
    private const string WrongName = "Oovernight";
    private const string CorrectName = "ShippingKinds.Overnight";

    private IWorkspaceManager _workspaceManager;
    private SymbolNavigationEngine _symbolNavigationEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _symbolNavigationEngine = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private const string ShippingKindsSource = """
        public static class ShippingKinds
        {
            public const string Standard = "standard";
            public const string Express = "express";
            public const string Overnight = "overnight";
            public const string Pickup = "pickup";
        }
        """;

    private async Task<DiagnosticReport> CompileAndGetErrorsAsync(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);

        var project = solution.Projects.Single();
        var compilation = await project.GetCompilationAsync();
        var diagnostics = compilation!.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToInfo())
            .ToList();

        return new DiagnosticReport(!(diagnostics.Count == 0), diagnostics);
    }

    /// <summary>Attaches the trimmed source line of the diagnostic, as the write gate does (ToInfo(includeSourceLine: true)).</summary>
    private static DiagnosticInfo WithQuotedSourceLine(DiagnosticInfo diagnostic, string fileContent)
    {
        var line = fileContent.Replace("\r\n", "\n").Split('\n')[diagnostic.StartLine - 1].Trim();
        return diagnostic with { SourceLine = line, SpanText = line };
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    private static int HeaderIndex(string[] lines) =>
        Array.FindIndex(lines, l => l.Contains("CS0117 at ", StringComparison.Ordinal));

    /// <summary>Compiles a CS0117 slip (ShippingKinds.Oovernight) and returns the full DescribeAsync text for it.</summary>
    private async Task<string> DescribeCs0117RejectionAsync()
    {
        var orderSource = $$"""
            namespace TestProj;

            public class Order
            {
                public string Pick(string kind)
                {
                    switch (kind)
                    {
                        case ShippingKinds.{{WrongName}}:
                            return "x";
                    }

                    return "";
                }
            }
            """;
        var report = await CompileAndGetErrorsAsync(
            ("Shipping.cs", "namespace TestProj;\n\n" + ShippingKindsSource),
            ("Order.cs", orderSource));
        var cs0117 = WithQuotedSourceLine(report.Diagnostics.Single(d => d.Id == "CS0117"), orderSource);

        return await CompilerErrorLookupHelper.DescribeAsync(new DiagnosticReport(true, [cs0117]), _symbolNavigationEngine);
    }

    /// <summary>Compiles a CS0103 slip (bare Oovernight under using static) and returns the full DescribeAsync text for it.</summary>
    private async Task<string> DescribeCs0103RejectionAsync()
    {
        var orderSource = $$"""
            using static TestProj.ShippingKinds;

            namespace TestProj;

            public class Order
            {
                public string Pick(string kind)
                {
                    switch (kind)
                    {
                        case {{WrongName}}:
                            return "x";
                    }

                    return "";
                }
            }
            """;
        var report = await CompileAndGetErrorsAsync(
            ("Shipping.cs", "namespace TestProj;\n\n" + ShippingKindsSource),
            ("Order.cs", orderSource));
        var cs0103 = WithQuotedSourceLine(report.Diagnostics.Single(d => d.Id == "CS0103"), orderSource);

        return await CompilerErrorLookupHelper.DescribeAsync(new DiagnosticReport(true, [cs0103]), _symbolNavigationEngine);
    }

    [Test]
    public async Task Hide_Cs0117Rejection_TextNeverContainsTheWrongNameAsync()
    {
        using var _ = NearMissOptions.OverrideForCurrentContext(autoCorrect: false, nameDisplay: NearMissNameDisplay.Hide);

        var description = await DescribeCs0117RejectionAsync();

        Assert.That(description.Contains(WrongName, StringComparison.Ordinal), Is.False,
            "under Hide the wrong name must not appear anywhere in the rejection text. Actual text:\n" + description);
        Assert.That(description, Does.Contain(CorrectName));
    }

    [Test]
    public async Task Show_Cs0117Rejection_TextIsUnchangedAsync()
    {
        string explicitShow;
        string defaulted;
        string explicitShowAgain;

        using (NearMissOptions.OverrideForCurrentContext(autoCorrect: false, nameDisplay: NearMissNameDisplay.Show))
        {
            explicitShow = await DescribeCs0117RejectionAsync();
        }

        defaulted = await DescribeCs0117RejectionAsync();

        using (NearMissOptions.OverrideForCurrentContext(autoCorrect: false, nameDisplay: NearMissNameDisplay.Show))
        {
            explicitShowAgain = await DescribeCs0117RejectionAsync();
        }

        Assert.That(explicitShow, Does.Contain(WrongName));
        Assert.That(explicitShow, Does.Contain("Your edit contains 'Oovernight' (10 letters)"));

        var lines = Lines(explicitShow);
        var header = HeaderIndex(lines);
        Assert.That(header, Is.GreaterThanOrEqualTo(0), "the CS0117 header line must be present. Actual text:\n" + explicitShow);
        Assert.That(lines[header], Does.Contain(WrongName));

        Assert.That(defaulted, Is.EqualTo(explicitShow), "the default (no override) must give the same text as explicit Show");
        Assert.That(explicitShowAgain, Is.EqualTo(explicitShow), "explicit Show must be repeatable and give the same text");
    }

    [Test]
    public async Task Hide_Cs0117Rejection_ShowsTheCorrectedLineInsteadOfTheSourceLineAsync()
    {
        using var _ = NearMissOptions.OverrideForCurrentContext(autoCorrect: false, nameDisplay: NearMissNameDisplay.Hide);

        var description = await DescribeCs0117RejectionAsync();

        var lines = Lines(description);
        var header = HeaderIndex(lines);
        Assert.That(header, Is.GreaterThanOrEqualTo(0), "the CS0117 header line must be present. Actual text:\n" + description);
        Assert.That(lines.Length, Is.GreaterThan(header + 1), "a line must follow the header. Actual text:\n" + description);

        var quotedLine = lines[header + 1];
        Assert.That(quotedLine, Does.StartWith("    > "));
        Assert.That(quotedLine, Does.Contain(CorrectName));
        Assert.That(quotedLine.Contains(WrongName, StringComparison.Ordinal), Is.False,
            "the quoted line must carry the corrected spelling, not the wrong one. Actual text:\n" + description);
    }

    [Test]
    public async Task Hide_Cs0103Rejection_TextNeverContainsTheWrongNameAsync()
    {
        using var _ = NearMissOptions.OverrideForCurrentContext(autoCorrect: false, nameDisplay: NearMissNameDisplay.Hide);

        var description = await DescribeCs0103RejectionAsync();

        Assert.That(description.Contains(WrongName, StringComparison.Ordinal), Is.False,
            "under Hide the wrong name must not appear anywhere in the rejection text. Actual text:\n" + description);
    }
}
