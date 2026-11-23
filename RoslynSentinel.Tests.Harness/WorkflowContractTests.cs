using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Model-free checks of the workflow contract constants and the WorkflowContract helpers
/// (variant and placement parsing, hashing, naming). Never calls a model.
/// </summary>
[TestFixture]
public sealed class WorkflowContractTests
{
    private static string Lf(string text) => text.Replace("\r\n", "\n");

    private static int CountLines(string text) => Lf(text).TrimEnd().Split('\n').Length;

    [Test]
    public void TextFor_Legacy_IsCodingAgentUnchanged()
    {
        Assert.That(WorkflowContract.TextFor(ContractVariant.Legacy), Is.EqualTo(AgentSystemPrompts.CodingAgent));
    }

    [Test]
    public void TextFor_Standing_IsTheContractAndDiffersFromLegacy()
    {
        string standing = WorkflowContract.TextFor(ContractVariant.Standing);

        Assert.That(standing, Is.EqualTo(AgentSystemPrompts.CodingAgentContract));
        Assert.That(standing, Is.Not.EqualTo(AgentSystemPrompts.CodingAgent));
    }

    [Test]
    public void Contract_IsAsciiAndHasFewerLinesThanLegacy()
    {
        string contract = AgentSystemPrompts.CodingAgentContract;

        Assert.That(contract.All(c => c < 0x80), Is.True, "contract text must be ASCII-only");
        Assert.That(CountLines(contract), Is.LessThan(CountLines(AgentSystemPrompts.CodingAgent)));
    }

    [Test]
    public void Contract_WorkingListIsNumberedOneToFive_AndNamesNoValidateOnApply()
    {
        string contract = Lf(AgentSystemPrompts.CodingAgentContract);

        Assert.That(contract, Does.Not.Contain("validateOnApply"));

        int start = contract.IndexOf("\n## Working\n", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), "contract has no ## Working section");
        int bodyStart = start + "\n## Working\n".Length;
        int end = contract.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        string section = end < 0 ? contract.Substring(bodyStart) : contract.Substring(bodyStart, end - bodyStart);

        // Top-level numbered items start at column 0; wrapped continuation lines are indented.
        int[] numbers = Regex.Matches(section, @"(?m)^(\d+)\. ")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToArray();

        Assert.That(numbers, Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
    }

    [Test]
    public void Contract_EndsWithEndingsSection()
    {
        string contract = Lf(AgentSystemPrompts.CodingAgentContract).TrimEnd();
        string endings = Lf(AgentSystemPrompts.CodingAgentContractEndings).TrimEnd();

        Assert.That(contract, Does.EndWith(endings));
    }

    [Test]
    public void Endings_ContainNoBracesAndNameBothTokens()
    {
        string endings = AgentSystemPrompts.CodingAgentContractEndings;

        // Endings are appended to rendered messages and may be passed through string.Format.
        Assert.That(endings, Does.Not.Contain("{"));
        Assert.That(endings, Does.Not.Contain("}"));
        Assert.That(endings, Does.Contain("DONE:"));
        Assert.That(endings, Does.Contain("BLOCKED:"));
    }

    [Test]
    public void Hash_IsTwelveLowercaseHex()
    {
        string hash = WorkflowContract.Hash(WorkflowContract.TextFor(ContractVariant.Legacy));

        Assert.That(hash, Has.Length.EqualTo(12));
        Assert.That(hash, Does.Match("^[0-9a-f]{12}$"));
    }

    [Test]
    public void Hash_IgnoresLineEndingStyle()
    {
        string lf = Lf(AgentSystemPrompts.CodingAgentContract);
        string crlf = lf.Replace("\n", "\r\n");

        Assert.That(WorkflowContract.Hash(crlf), Is.EqualTo(WorkflowContract.Hash(lf)));
    }

    [Test]
    public void Hash_DiffersBetweenVariants()
    {
        string legacy = WorkflowContract.Hash(WorkflowContract.TextFor(ContractVariant.Legacy));
        string standing = WorkflowContract.Hash(WorkflowContract.TextFor(ContractVariant.Standing));

        Assert.That(standing, Is.Not.EqualTo(legacy));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("legacy")]
    [TestCase("LEGACY")]
    public void ParseVariant_UnsetEmptyAndLegacy_ReturnLegacy(string? value)
    {
        Assert.That(WorkflowContract.ParseVariant(value), Is.EqualTo(ContractVariant.Legacy));
    }

    [TestCase("standing")]
    [TestCase("Standing")]
    [TestCase("  STANDING  ")]
    public void ParseVariant_StandingAnyCase_ReturnsStanding(string value)
    {
        Assert.That(WorkflowContract.ParseVariant(value), Is.EqualTo(ContractVariant.Standing));
    }

    [Test]
    public void ParseVariant_Unknown_ThrowsNamingTheValues()
    {
        var ex = Assert.Throws<ArgumentException>(() => WorkflowContract.ParseVariant("bogus"));

        Assert.That(ex!.Message, Does.Contain("bogus"));
        Assert.That(ex.Message, Does.Contain("legacy, standing"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("system")]
    [TestCase("SYSTEM")]
    public void ParsePlacement_UnsetAndSystem_ReturnSystem(string? value)
    {
        Assert.That(WorkflowContract.ParsePlacement(value), Is.EqualTo(EndingsPlacement.System));
    }

    [TestCase("user")]
    [TestCase("User")]
    public void ParsePlacement_User_ReturnsSystemAndUser(string value)
    {
        Assert.That(WorkflowContract.ParsePlacement(value), Is.EqualTo(EndingsPlacement.SystemAndUser));
    }

    [Test]
    public void ParsePlacement_Unknown_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => WorkflowContract.ParsePlacement("everywhere"));

        Assert.That(ex!.Message, Does.Contain("everywhere"));
        Assert.That(ex.Message, Does.Contain("system, user"));
    }

    [Test]
    public void PlacementName_ReturnsSystemAndSystemPlusUser()
    {
        Assert.That(WorkflowContract.PlacementName(EndingsPlacement.System), Is.EqualTo("system"));
        Assert.That(WorkflowContract.PlacementName(EndingsPlacement.SystemAndUser), Is.EqualTo("system+user"));
    }
}
