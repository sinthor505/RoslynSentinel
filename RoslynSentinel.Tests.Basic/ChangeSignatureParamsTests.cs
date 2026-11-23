using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// ChangeSignature on a method with a params parameter: every argument of the params slot (expanded form) must be carried
/// to the rewritten call, in source order, in remove mode. Each case asserts the exact rewritten call, compiles the result
/// and checks the call binds to the changed member with the expected argument count.
/// </summary>
[TestFixture]
public class ChangeSignatureParamsTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private BasicRefactoringEngine _refactoringEngine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var config = Battery.Basic.TestEngines.SentinelConfiguration();
        _refactoringEngine = Battery.Basic.TestEngines.BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private Task<ChangeSignatureResult> Change(string filePath, string methodName, params SignatureParameterSpec[] parameters) =>
        _refactoringEngine.ChangeSignatureAsync(filePath, methodName, parameters);

    private void SetFile(string name, string content)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { (name, content) });
        _workspaceManager.SetTestSolution(solution);
    }

    private static CSharpCompilation Compile(string text)
    {
        var frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Collections.dll", "System.Linq.dll" }
            .Select(assembly => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(frameworkDir, assembly)))
            .ToList();
        return CSharpCompilation.Create("ChangeSignatureParamsProof", new[] { CSharpSyntaxTree.ParseText(text, path: "Totals.cs") }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static void AssertCompiles(string text)
    {
        var errors = Compile(text).GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();
        Assert.That(errors, Is.Empty, "The rewritten code must compile with zero errors: " + string.Join(Environment.NewLine, errors));
    }

    // The rewritten text must compile; every call to `name` (outside the declaration) must bind to the one method declared in the file
    // and carry exactly the expected number of arguments, listed in source order.
    private static void AssertCallsBindToMember(string text, string name, params int[] expectedArgumentCounts)
    {
        AssertCompiles(text);
        var compilation = Compile(text);
        var tree = compilation.SyntaxTrees.Single();
        var model = compilation.GetSemanticModel(tree);
        var declaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == name);
        var declared = model.GetDeclaredSymbol(declaration)!;
        var calls = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => (c.Expression as IdentifierNameSyntax)?.Identifier.Text == name).ToList();
        Assert.That(calls.Select(c => c.ArgumentList.Arguments.Count), Is.EqualTo(expectedArgumentCounts), "Argument counts of the rewritten calls (source order of their opening)");
        foreach (var call in calls)
        {
            var bound = model.GetSymbolInfo(call).Symbol as IMethodSymbol;
            Assert.That(bound, Is.Not.Null, "The call must bind: " + call);
            Assert.That(SymbolEqualityComparer.Default.Equals(bound!.OriginalDefinition, declared.OriginalDefinition), Is.True, "The call must bind to the changed member: " + call);
        }
    }

    private const string SeedTotalWithRun = @"public class Totals
{
    public int Total(int seed, params int[] values) => seed + values.Length;
    public int Run()
    {
        var a = Total(0, 1, 2, 3);
        var b = Total(0);
        var c = Total(0, 1);
        var d = Total(0, new[] { 1, 2 });
        return a + b + c + d;
    }
}";

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_ParamsExpandedCall_NewParameterBeforeParams_CarriesEveryParamsArgument()
    {
        SetFile("Totals.cs", SeedTotalWithRun);
        var result = await Change("Totals.cs", "Total", new ExistingParameterSpec(0), new NewParameterSpec("label", "string", "\"x\""), new ExistingParameterSpec(1));

        Assert.That(result.Error, Is.Null);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Totals.cs"];
        Assert.That(after, Does.Contain("var a = Total(0, \"x\", 1, 2, 3);"), "All three params arguments follow the new parameter's slot");
        Assert.That(after, Does.Contain("var b = Total(0, \"x\");"), "No params arguments");
        Assert.That(after, Does.Contain("var c = Total(0, \"x\", 1);"), "One params argument");
        Assert.That(after, Does.Contain("var d = Total(0, \"x\", new[] { 1, 2 });"), "Array form stays one argument");
        AssertCallsBindToMember(after, "Total", 5, 2, 3, 3);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_ParamsExpandedCall_ReorderOtherParameters_CarriesEveryParamsArgument()
    {
        const string source = @"public class Totals
{
    public int Total(int a, string b, params int[] values) => a + b.Length + values.Length;
    public int Run() => Total(1, ""s"", 7, 8, 9);
}";
        SetFile("Totals.cs", source);
        var result = await Change("Totals.cs", "Total", new ExistingParameterSpec(1), new ExistingParameterSpec(0), new ExistingParameterSpec(2));

        Assert.That(result.Error, Is.Null);
        var after = result.Changes["Totals.cs"];
        Assert.That(after, Does.Contain("Total(\"s\", 1, 7, 8, 9)"));
        AssertCallsBindToMember(after, "Total", 5);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_ParamsExpandedCall_RemoveAnotherParameter_CarriesEveryParamsArgument()
    {
        const string source = @"public class Totals
{
    public int Total(int a, string b, params int[] values) => a + values.Length;
    public int Run() => Total(1, ""s"", 7, 8, 9);
}";
        SetFile("Totals.cs", source);
        var result = await Change("Totals.cs", "Total", new ExistingParameterSpec(0), new ExistingParameterSpec(2));

        Assert.That(result.Error, Is.Null);
        var after = result.Changes["Totals.cs"];
        Assert.That(after, Does.Contain("Total(1, 7, 8, 9)"));
        AssertCallsBindToMember(after, "Total", 4);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_ParamsExpandedCall_RemoveTheParamsParameter_RemovesAllItsArguments()
    {
        SetFile("Totals.cs", SeedTotalWithRun.Replace("seed + values.Length", "seed"));
        var result = await Change("Totals.cs", "Total", new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null);
        var after = result.Changes["Totals.cs"];
        Assert.That(after, Does.Not.Contain("Total(0, 1"), "Every argument of the removed params parameter is gone");
        Assert.That(after, Does.Contain("var a = Total(0);"));
        Assert.That(after, Does.Contain("var d = Total(0);"));
        AssertCallsBindToMember(after, "Total", 1, 1, 1, 1);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_ParamsNamedArgument_BindsThroughNamedPathAndLosesNothing()
    {
        const string source = @"public class Totals
{
    public int Total(int seed, params int[] values) => seed + values.Length;
    public int Run()
    {
        var a = Total(0, values: 5);
        var b = Total(seed: 0, values: new[] { 1, 2 });
        return a + b;
    }
}";
        SetFile("Totals.cs", source);
        var result = await Change("Totals.cs", "Total", new ExistingParameterSpec(0), new NewParameterSpec("label", "string", "\"x\""), new ExistingParameterSpec(1));

        Assert.That(result.Error, Is.Null);
        var after = result.Changes["Totals.cs"];
        Assert.That(after, Does.Contain("var a = Total(0, \"x\", 5);"), "A named single params argument becomes a positional expanded element");
        Assert.That(after, Does.Contain("var b = Total(0, \"x\", new[] { 1, 2 });"), "A named array argument becomes the positional array");
        AssertCallsBindToMember(after, "Total", 3, 3);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_ParamsExpandedCall_NestedCallInsideCarriedArguments_KeepsItsOwnRewrite()
    {
        const string source = @"public class Totals
{
    public int Total(int seed, params int[] values) => seed + values.Length;
    public int Run() => Total(0, Total(1, 2, 3), 4, Total(5, 6));
}";
        SetFile("Totals.cs", source);
        var result = await Change("Totals.cs", "Total", new ExistingParameterSpec(0), new NewParameterSpec("label", "string", "\"x\""), new ExistingParameterSpec(1));

        Assert.That(result.Error, Is.Null);
        var after = result.Changes["Totals.cs"];
        Assert.That(after, Does.Contain("Total(0, \"x\", Total(1, \"x\", 2, 3), 4, Total(5, \"x\", 6))"));
        AssertCallsBindToMember(after, "Total", 5, 4, 3);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_ParamsExpandedCall_RefKeywordOnOtherCarriedArgument_IsKept()
    {
        const string source = @"public class Totals
{
    public int Total(ref int a, int b, params int[] values) => a + b + values.Length;
    public int Run()
    {
        var x = 1;
        return Total(ref x, 2, 3, 4);
    }
}";
        SetFile("Totals.cs", source);
        var result = await Change("Totals.cs", "Total", new ExistingParameterSpec(1), new ExistingParameterSpec(0), new ExistingParameterSpec(2));

        Assert.That(result.Error, Is.Null);
        var after = result.Changes["Totals.cs"];
        Assert.That(after, Does.Contain("Total(2, ref x, 3, 4)"));
        AssertCallsBindToMember(after, "Total", 4);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_ParamsExpandedConstructorCall_CarriesEveryParamsArgument()
    {
        const string source = @"public class Totals
{
    public Totals(int seed, params int[] values) { }
    public static Totals Make() => new Totals(0, 1, 2, 3);
    public static Totals MakeOne() => new Totals(0, 4);
    public static Totals MakeTargetTyped() { Totals t = new(0, 5, 6); return t; }
}";
        SetFile("Totals.cs", source);
        var result = await Change("Totals.cs", "Totals", new ExistingParameterSpec(0), new NewParameterSpec("label", "string", "\"x\""), new ExistingParameterSpec(1));

        Assert.That(result.Error, Is.Null);
        var after = result.Changes["Totals.cs"];
        Assert.That(after, Does.Contain("new Totals(0, \"x\", 1, 2, 3)"));
        Assert.That(after, Does.Contain("new Totals(0, \"x\", 4)"));
        Assert.That(after, Does.Contain("= new(0, \"x\", 5, 6)"));
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_ParamsExpandedExtensionCall_CarriesEveryExtraArgument()
    {
        const string source = @"public static class Exts
{
    public static int Sum(this string s, int seed, params int[] values) => s.Length + seed + values.Length;
    public static int Run() => ""a"".Sum(0, 1, 2, 3);
}";
        SetFile("Totals.cs", source);
        var result = await Change("Totals.cs", "Sum", new ExistingParameterSpec(0), new ExistingParameterSpec(1), new NewParameterSpec("label", "string", "\"x\""), new ExistingParameterSpec(2));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Totals.cs"];
        Assert.That(after, Does.Contain("\"a\".Sum(0, \"x\", 1, 2, 3)"), "The reduced call keeps its receiver and carries every expanded params argument after the new parameter");
        AssertCompiles(after);
    }
}
