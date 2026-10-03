using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Member(addMember, position: "after:X") (BasicRefactoringEngine.InsertMemberAfterAsync) must separate
/// the inserted member from its neighbors by exactly one blank line, and must not change the file's
/// existing line-ending convention or introduce mixed CRLF/LF. Covers LF-only and CRLF fixtures,
/// inserting after a middle member and after the last member.
/// The assertions are on the engine's UpdatedText, so this runs on an InMemoryWorkspace (no temp directory,
/// MSBuild load or disk write); on-disk EOL preservation is covered by DiskWriteRoundTripTests.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class MemberInsertAfterEolTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/InsertAfterEolFixture.cs";

    private static string BuildFixtureSource(string eol)
    {
        // Three members (Alpha/Beta/Gamma), each separated by exactly one blank line, using only
        // the requested eol so the dominant-EOL detection is unambiguous.
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

    private static string BuildExpected(string eol, string insertAfter)
    {
        const string alpha = "    public int Alpha() => 1;";
        const string beta = "    public int Beta() => 2;";
        const string gamma = "    public int Gamma() => 3;";
        const string delta = "    public int Delta() => 4;";

        var members = insertAfter == "Gamma"
            ? new[] { alpha, beta, gamma, delta }
            : new[] { alpha, beta, delta, gamma };

        var lines = new List<string> { "namespace ContosoOrders.Core;", "", "public class InsertAfterEolTarget", "{" };
        for (var i = 0; i < members.Length; i++)
        {
            if (i > 0)
            {
                lines.Add("");
            }

            lines.Add(members[i]);
        }

        lines.Add("}");
        lines.Add("");
        return string.Join(eol, lines);
    }

    private static MemberRefactoringEngine BuildEngine(IWorkspaceManager workspaceManager)
    {
        var symbolNav = new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validationEng = new ValidationEngine(workspaceManager);
        return new MemberRefactoringEngine(workspaceManager, symbolNav, validationEng);
    }

    private static async Task<DocumentEditResult> InsertAsync(string eol, string afterMember)
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, BuildFixtureSource(eol)));
        var engine = BuildEngine(workspace.Manager);

        return await engine.InsertMemberAfterAsync(
            workspace.PathOf(FixtureRelativePath),
            "InsertAfterEolTarget",
            afterMember,
            "public int Delta() => 4;");
    }

    [Test]
    public async Task InsertAfter_LastMember_LF_ExactTextMatchesExpectedAsync()
    {
        var result = await InsertAsync("\n", "Gamma");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        Assert.That(result.UpdatedText, Is.EqualTo(BuildExpected("\n", "Gamma")));
        Assert.That(result.UpdatedText, Does.Not.Contain("\r"), "An LF-only file must not gain any CRLF sequences.");
    }

    [Test]
    public async Task InsertAfter_MiddleMember_LF_ExactTextMatchesExpectedAsync()
    {
        var result = await InsertAsync("\n", "Beta");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        Assert.That(result.UpdatedText, Is.EqualTo(BuildExpected("\n", "Beta")));
        Assert.That(result.UpdatedText, Does.Not.Contain("\r"), "An LF-only file must not gain any CRLF sequences.");
    }

    [Test]
    public async Task InsertAfter_LastMember_CRLF_ExactTextMatchesExpectedAsync()
    {
        var result = await InsertAsync("\r\n", "Gamma");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        Assert.That(result.UpdatedText, Is.EqualTo(BuildExpected("\r\n", "Gamma")));
        Assert.That(result.UpdatedText!.Replace("\r\n", ""), Does.Not.Contain("\n"), "No bare LF should appear once every CRLF pair is stripped.");
    }

    [Test]
    public async Task InsertAfter_MiddleMember_CRLF_ExactTextMatchesExpectedAsync()
    {
        var result = await InsertAsync("\r\n", "Beta");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        Assert.That(result.UpdatedText, Is.EqualTo(BuildExpected("\r\n", "Beta")));
        Assert.That(result.UpdatedText!.Replace("\r\n", ""), Does.Not.Contain("\n"), "No bare LF should appear once every CRLF pair is stripped.");
    }
}
