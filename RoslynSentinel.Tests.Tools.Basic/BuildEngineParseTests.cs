using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("BuildEngine")]
public class BuildEngineParseTests
{
    [Test]
    public void ParseDiagnostics_CompilerLine_CapturesProjectName()
    {
        const string stdout = @"C:\r\Foo\Bar.cs(12,34): error CS1002: ; expected [C:\r\Foo\Foo.csproj]";

        var (errors, warnings) = BuildEngine.ParseDiagnostics(stdout);

        Assert.That(warnings, Is.Empty);
        Assert.That(errors, Has.Count.EqualTo(1));
        var e = errors[0];
        Assert.That(e.Id, Is.EqualTo("CS1002"));
        Assert.That(e.Severity, Is.EqualTo("Error"));
        Assert.That(e.Message, Is.EqualTo("; expected"));
        Assert.That(e.StartLine, Is.EqualTo(12));
        Assert.That(e.StartColumn, Is.EqualTo(34));
        Assert.That(e.Project, Is.EqualTo("Foo"));
    }

    [Test]
    public void ParseDiagnostics_ProjectLevelNuGetError_IsParsed()
    {
        const string stdout = @"C:\r\X.csproj : error NU1101: Unable to find package Foo. No packages exist with this id [C:\r\X.csproj]";

        var (errors, warnings) = BuildEngine.ParseDiagnostics(stdout);

        Assert.That(warnings, Is.Empty);
        Assert.That(errors, Has.Count.EqualTo(1));
        var e = errors[0];
        Assert.That(e.Id, Is.EqualTo("NU1101"));
        Assert.That(e.Message, Is.EqualTo("Unable to find package Foo. No packages exist with this id"));
        Assert.That(e.StartLine, Is.EqualTo(0));
        Assert.That(e.StartColumn, Is.EqualTo(0));
        Assert.That(e.Project, Is.EqualTo("X"));
    }

    [Test]
    public void ParseDiagnostics_MultiTargetProjectSuffix_IsParsed()
    {
        const string stdout = @"C:\r\X.cs(3,5): warning CS0168: The variable 'a' is declared but never used [C:\r\X.csproj::TargetFramework=net9.0]";

        var (errors, warnings) = BuildEngine.ParseDiagnostics(stdout);

        Assert.That(errors, Is.Empty);
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0].Id, Is.EqualTo("CS0168"));
        Assert.That(warnings[0].Severity, Is.EqualTo("Warning"));
        Assert.That(warnings[0].Project, Is.EqualTo("X"));
    }

    [Test]
    public void ParseDiagnostics_MessageContainingBrackets_IsKept()
    {
        const string stdout = @"C:\r\X.cs(1,1): error CS0029: Cannot implicitly convert type 'int[]' to 'List<int>' [see docs] [C:\r\X.csproj]";

        var (errors, _) = BuildEngine.ParseDiagnostics(stdout);

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0].Message, Is.EqualTo("Cannot implicitly convert type 'int[]' to 'List<int>' [see docs]"));
        Assert.That(errors[0].Project, Is.EqualTo("X"));
    }
}
