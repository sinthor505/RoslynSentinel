using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Member(addMember, position: "after:X") (BasicRefactoringEngine.InsertMemberAfterAsync) must separate
/// the inserted member from its neighbors by exactly one blank line, and must not change the file's
/// existing line-ending convention or introduce mixed CRLF/LF. Covers LF-only and CRLF fixtures,
/// inserting after a middle member and after the last member.
/// </summary>
[TestFixture]
public class MemberInsertAfterEolTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/InsertAfterEolFixture.cs";

    private static string BuildFixtureSource(string eol)
    {
        // Three members (Alpha/Beta/Gamma), each separated by exactly one blank line, using only
        // the requested eol so the on-disk dominant-EOL detection is unambiguous.
        var lines = new[]
        {
            "namespace ContosoOrders.Core;",
            "",
            "public class InsertAfterEolTarget",
            "{",
            "    public int Alpha() => 1;",
            "",
            "    public int Beta() => 2;",
            "",
            "    public int Gamma() => 3;",
            "}",
            ""
        };
        return string.Join(eol, lines);
    }

    private static MemberRefactoringEngine BuildEngine(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var symbolNav = new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validationEng = new ValidationEngine(workspaceManager);
        return new MemberRefactoringEngine(workspaceManager, symbolNav, validationEng);
    }

    [Test]
    public async Task InsertAfter_LastMember_LF_ExactTextMatchesExpectedAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var source = BuildFixtureSource("\n");
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, source);
        var engine = BuildEngine(workspaceManager);
        var fullPath = Path.Combine(fixture.SolutionDirectory, FixtureRelativePath);

        var result = await engine.InsertMemberAfterAsync(
            fullPath,
            "InsertAfterEolTarget",
            "Gamma",
            "public int Delta() => 4;");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);

        const string expected =
            "namespace ContosoOrders.Core;\n" +
            "\n" +
            "public class InsertAfterEolTarget\n" +
            "{\n" +
            "    public int Alpha() => 1;\n" +
            "\n" +
            "    public int Beta() => 2;\n" +
            "\n" +
            "    public int Gamma() => 3;\n" +
            "\n" +
            "    public int Delta() => 4;\n" +
            "}\n";

        Assert.That(result.UpdatedText, Is.EqualTo(expected));
        Assert.That(result.UpdatedText, Does.Not.Contain("\r"), "An LF-only file must not gain any CRLF sequences.");
    }

    [Test]
    public async Task InsertAfter_MiddleMember_LF_ExactTextMatchesExpectedAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var source = BuildFixtureSource("\n");
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, source);
        var engine = BuildEngine(workspaceManager);
        var fullPath = Path.Combine(fixture.SolutionDirectory, FixtureRelativePath);

        var result = await engine.InsertMemberAfterAsync(
            fullPath,
            "InsertAfterEolTarget",
            "Beta",
            "public int Delta() => 4;");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);

        const string expected =
            "namespace ContosoOrders.Core;\n" +
            "\n" +
            "public class InsertAfterEolTarget\n" +
            "{\n" +
            "    public int Alpha() => 1;\n" +
            "\n" +
            "    public int Beta() => 2;\n" +
            "\n" +
            "    public int Delta() => 4;\n" +
            "\n" +
            "    public int Gamma() => 3;\n" +
            "}\n";

        Assert.That(result.UpdatedText, Is.EqualTo(expected));
        Assert.That(result.UpdatedText, Does.Not.Contain("\r"), "An LF-only file must not gain any CRLF sequences.");
    }

    [Test]
    public async Task InsertAfter_LastMember_CRLF_ExactTextMatchesExpectedAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var source = BuildFixtureSource("\r\n");
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, source);
        var engine = BuildEngine(workspaceManager);
        var fullPath = Path.Combine(fixture.SolutionDirectory, FixtureRelativePath);

        var result = await engine.InsertMemberAfterAsync(
            fullPath,
            "InsertAfterEolTarget",
            "Gamma",
            "public int Delta() => 4;");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);

        const string expected =
            "namespace ContosoOrders.Core;\r\n" +
            "\r\n" +
            "public class InsertAfterEolTarget\r\n" +
            "{\r\n" +
            "    public int Alpha() => 1;\r\n" +
            "\r\n" +
            "    public int Beta() => 2;\r\n" +
            "\r\n" +
            "    public int Gamma() => 3;\r\n" +
            "\r\n" +
            "    public int Delta() => 4;\r\n" +
            "}\r\n";

        Assert.That(result.UpdatedText, Is.EqualTo(expected));
        Assert.That(result.UpdatedText!.Replace("\r\n", ""), Does.Not.Contain("\n"), "No bare LF should appear once every CRLF pair is stripped.");
    }

    [Test]
    public async Task InsertAfter_MiddleMember_CRLF_ExactTextMatchesExpectedAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var source = BuildFixtureSource("\r\n");
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, source);
        var engine = BuildEngine(workspaceManager);
        var fullPath = Path.Combine(fixture.SolutionDirectory, FixtureRelativePath);

        var result = await engine.InsertMemberAfterAsync(
            fullPath,
            "InsertAfterEolTarget",
            "Beta",
            "public int Delta() => 4;");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);

        const string expected =
            "namespace ContosoOrders.Core;\r\n" +
            "\r\n" +
            "public class InsertAfterEolTarget\r\n" +
            "{\r\n" +
            "    public int Alpha() => 1;\r\n" +
            "\r\n" +
            "    public int Beta() => 2;\r\n" +
            "\r\n" +
            "    public int Delta() => 4;\r\n" +
            "\r\n" +
            "    public int Gamma() => 3;\r\n" +
            "}\r\n";

        Assert.That(result.UpdatedText, Is.EqualTo(expected));
        Assert.That(result.UpdatedText!.Replace("\r\n", ""), Does.Not.Contain("\n"), "No bare LF should appear once every CRLF pair is stripped.");
    }
}
