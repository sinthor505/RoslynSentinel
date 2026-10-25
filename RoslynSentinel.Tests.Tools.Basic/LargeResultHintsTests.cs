using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("WorkspaceTools")]
public class LargeResultHintsTests
{
    [Test]
    public void FetchCall_EmitsReasonAndResultId()
    {
        var call = LargeResultHints.FetchCall("abc123");

        Assert.That(call, Does.StartWith("GetLargeResult(").And.Contain("reason: \""));
        Assert.That(call, Does.Contain("resultId: \"abc123\""));
    }

    [Test]
    public void FetchCall_AppendsOffsetAndCharLimitOnlyWhenSupplied()
    {
        Assert.That(LargeResultHints.FetchCall("abc123"), Does.Not.Contain("offset:").And.Not.Contain("charLimit:"));
        Assert.That(LargeResultHints.FetchCall("abc123", 10, 500), Does.Contain(", offset: 10, charLimit: 500)"));
        Assert.That(LargeResultHints.FetchCall("abc123", offset: 5), Does.Contain(", offset: 5)").And.Not.Contain("charLimit:"));
    }

    // Guard: a required GetLargeResult parameter missing from the hint would make the first call fail.
    [Test]
    public void FetchCall_NamesEveryRequiredGetLargeResultParameter()
    {
        var parameters = typeof(WorkspaceReadNavigationTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Single(m => m.GetCustomAttributes<McpServerToolAttribute>().Any(a => a.Name == "GetLargeResult"))
            .GetParameters()
            .Where(p => !p.HasDefaultValue && p.ParameterType != typeof(CancellationToken))
            .Select(p => p.Name!)
            .ToList();
        var idCall = LargeResultHints.FetchCall("abc123");
        var fileCall = LargeResultHints.FetchCallForFile("abc123.json");

        foreach (var name in parameters.Where(n => n != "resultId" && n != "filePath"))
        {
            Assert.That(idCall, Does.Contain($"{name}: "), $"GetLargeResult requires '{name}'; add it to LargeResultHints.FetchCall.");
        }

        foreach (var name in parameters.Where(n => n != "resultId"))
        {
            Assert.That(fileCall, Does.Contain($"{name}: "), $"GetLargeResult requires '{name}'; add it to LargeResultHints.FetchCallForFile.");
        }

        var reason = Regex.Match(idCall, "reason: \"([^\"]*)\"").Groups[1].Value.Trim();
        Assert.That(reason.Length, Is.GreaterThanOrEqualTo(ToolCallReasonJsonConverter.MinLength));
        Assert.That(reason, Does.Contain(" "));
    }

    [Test]
    public void FetchCallForFile_EmitsReasonAndFilePath()
    {
        var call = LargeResultHints.FetchCallForFile("x.json");

        Assert.That(call, Does.StartWith("GetLargeResult(").And.Contain("reason: \""));
        Assert.That(call, Does.Contain("filePath: \"x.json\""));
    }
}
