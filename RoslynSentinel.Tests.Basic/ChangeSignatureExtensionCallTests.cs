using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// ChangeSignature on an extension method called in reduced form (receiver.M(a)) and in static form (Ext.M(receiver, a)).
/// Reproduction tests for a suspected ordinal off-by-one: the engine numbers boundMethod.Parameters without the receiver for a
/// reduced extension call (TODO in BasicRefactoringEngine.ChangeSignatureAsync). Each case asserts the rewritten arguments and
/// that the changed file compiles. Failing cases are kept as plain failing tests, not ignored.
/// </summary>
[TestFixture]
public class ChangeSignatureExtensionCallTests
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

    private static void AssertCompiles(string text)
    {
        var frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Collections.dll", "System.Linq.dll" }
            .Select(assembly => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(frameworkDir, assembly)))
            .ToList();
        var compilation = CSharpCompilation.Create("ChangeSignatureExtensionCallProof", new[] { CSharpSyntaxTree.ParseText(text, path: "Ext.cs") }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();
        Assert.That(errors, Is.Empty, "The rewritten code must compile with zero errors: " + string.Join(Environment.NewLine, errors));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ReducedExtensionCall_ReorderNonReceiverParameters_RewritesBothForms_Succeeds()
    {
        const string source = @"public static class Ext
{
    public static int Foo(this string s, int a, int b) => s.Length;
    public static int Run() => ""a"".Foo(1, 2) + Ext.Foo(""a"", 1, 2);
}";
        SetFile("Ext.cs", source);
        var result = await Change("Ext.cs", "Foo", new ExistingParameterSpec(0), new ExistingParameterSpec(2), new ExistingParameterSpec(1));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Ext.cs"];
        Assert.That(after, Does.Contain("\"a\".Foo(2, 1)"), "The reduced extension call swaps its two non-receiver arguments");
        Assert.That(after, Does.Contain("Ext.Foo(\"a\", 2, 1)"), "The static call swaps its two non-receiver arguments");
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ReducedExtensionCall_AddParameterWithDefault_AppendsDefaultToBothForms_Succeeds()
    {
        const string source = @"public static class Ext
{
    public static int Foo(this string s, int a, int b) => s.Length;
    public static int Run() => ""a"".Foo(1, 2) + Ext.Foo(""a"", 1, 2);
}";
        SetFile("Ext.cs", source);
        var result = await Change("Ext.cs", "Foo", new ExistingParameterSpec(0), new ExistingParameterSpec(1), new ExistingParameterSpec(2), new NewParameterSpec("c", "int", "9"));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Ext.cs"];
        Assert.That(after, Does.Contain("\"a\".Foo(1, 2, 9)"), "The reduced extension call carries the default for the new parameter");
        Assert.That(after, Does.Contain("Ext.Foo(\"a\", 1, 2, 9)"), "The static call carries the default for the new parameter");
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ReducedExtensionCall_RemoveNonReceiverParameter_DropsCorrectArgument_Succeeds()
    {
        const string source = @"public static class Ext
{
    public static int Foo(this string s, int a, int b) => s.Length;
    public static int Run() => ""a"".Foo(1, 2) + Ext.Foo(""a"", 1, 2);
}";
        SetFile("Ext.cs", source);
        var result = await Change("Ext.cs", "Foo", new ExistingParameterSpec(0), new ExistingParameterSpec(2));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Ext.cs"];
        Assert.That(after, Does.Contain("\"a\".Foo(2)"), "The reduced extension call drops the first non-receiver argument, not the receiver or the second argument");
        Assert.That(after, Does.Contain("Ext.Foo(\"a\", 2)"), "The static call drops the first non-receiver argument");
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ReducedExtensionCall_ParamsExpandedForm_AddBeforeParams_CarriesEveryArgument_Succeeds()
    {
        const string source = @"public static class Ext
{
    public static int Foo(this string s, params int[] xs) => s.Length;
    public static int Run() => ""a"".Foo(1, 2, 3);
}";
        SetFile("Ext.cs", source);
        var result = await Change("Ext.cs", "Foo", new ExistingParameterSpec(0), new NewParameterSpec("k", "int", "0"), new ExistingParameterSpec(1));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Ext.cs"];
        Assert.That(after, Does.Contain("\"a\".Foo(0, 1, 2, 3)"), "The expanded params arguments follow the new parameter's slot");
        AssertCompiles(after);
    }
}
