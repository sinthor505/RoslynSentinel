using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// ChangeSignature refuses to remove an existing parameter that the target still uses (block body, expression body or
/// constructor initializer) before anything is planned or written. Uses are found by symbol identity, so a same-named field
/// or lambda parameter does not count, while nameof, lambdas and local functions do.
/// </summary>
[TestFixture]
public class ChangeSignatureRemovedParameterUseTests
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

    private void SetFiles(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    // Compiles the files against the core framework assemblies (same directory as System.Private.CoreLib).
    private static void AssertCompiles(string fileName, string text)
    {
        var frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Collections.dll", "System.Linq.dll" }
            .Select(assembly => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(frameworkDir, assembly)))
            .ToList();
        var compilation = CSharpCompilation.Create("ChangeSignatureRemovedParameterUseProof", new[] { CSharpSyntaxTree.ParseText(text, path: fileName) }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();
        Assert.That(errors, Is.Empty, "The rewritten code must compile with zero errors: " + string.Join(Environment.NewLine, errors));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RemoveUsedParameter_BlockBody_RefusesWithExactMessageAndLine()
    {
        // Line 6 holds two uses of b; they collapse to one line entry.
        const string source = """
            class Foo
            {
                public int M(int a, int b)
                {
                    var x = a;
                    return x + b + b;
                }
            }
            """;
        SetFiles(("Foo.cs", source));
        var ex = await Assert.ThrowsAsync<ToolTargetIneligibleException>(() => _refactoringEngine.ChangeSignatureAsync("Foo.cs", "M", new SignatureParameterSpec[] { new ExistingParameterSpec(0) }));

        Assert.That(ex!.Message, Is.EqualTo("Cannot remove parameter 'b' (originalIndex 1): the body still uses it at Foo.cs:6. Options: keep it by listing {originalIndex: 1} in parameters (an entry with the same name but no originalIndex is a new parameter), or remove its uses from the body first."));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible), "A refused removal throws before anything is planned or written");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RemoveUsedParameter_ExpressionBody_RefusesNamingTheLine()
    {
        const string source = """
            class Foo
            {
                public int M(int a, int b) => a + b;
            }
            """;
        SetFiles(("Foo.cs", source));
        var ex = await Assert.ThrowsAsync<ToolTargetIneligibleException>(() => _refactoringEngine.ChangeSignatureAsync("Foo.cs", "M", new SignatureParameterSpec[] { new ExistingParameterSpec(0) }));

        Assert.That(ex!.Message, Is.EqualTo("Cannot remove parameter 'b' (originalIndex 1): the body still uses it at Foo.cs:3. Options: keep it by listing {originalIndex: 1} in parameters (an entry with the same name but no originalIndex is a new parameter), or remove its uses from the body first."));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible), "A refused removal throws before anything is planned or written");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RemoveUsedParameter_ConstructorInitializer_RefusesNamingTheLine()
    {
        const string source = """
            class Base
            {
                public Base(int x) { }
            }
            class Foo : Base
            {
                public Foo(int a, int b) : base(b) { }
            }
            """;
        SetFiles(("Foo.cs", source));
        var ex = await Assert.ThrowsAsync<ToolTargetIneligibleException>(() => _refactoringEngine.ChangeSignatureAsync("Foo.cs", "Foo", new SignatureParameterSpec[] { new ExistingParameterSpec(0) }));

        Assert.That(ex!.Message, Is.EqualTo("Cannot remove parameter 'b' (originalIndex 1): the body still uses it at Foo.cs:7. Options: keep it by listing {originalIndex: 1} in parameters (an entry with the same name but no originalIndex is a new parameter), or remove its uses from the body first."));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible), "A refused removal throws before anything is planned or written");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RemoveUsedParameter_LambdaAndLocalFunction_BothListedInOneMessage()
    {
        const string source = """
            using System;
            class Foo
            {
                public int M(int a, int b)
                {
                    Func<int> f = () => b + 1;
                    int Local() => b;
                    return f() + Local() + a;
                }
            }
            """;
        SetFiles(("Foo.cs", source));
        var ex = await Assert.ThrowsAsync<ToolTargetIneligibleException>(() => _refactoringEngine.ChangeSignatureAsync("Foo.cs", "M", new SignatureParameterSpec[] { new ExistingParameterSpec(0) }));

        Assert.That(ex!.Message, Is.EqualTo("Cannot remove parameter 'b' (originalIndex 1): the body still uses it at Foo.cs:6, Foo.cs:7. Options: keep it by listing {originalIndex: 1} in parameters (an entry with the same name but no originalIndex is a new parameter), or remove its uses from the body first."));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible), "A refused removal throws before anything is planned or written");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RemoveUsedParameter_NameofCountsAsUse()
    {
        const string source = """
            class Foo
            {
                public string M(int a, int b) => nameof(b) + a;
            }
            """;
        SetFiles(("Foo.cs", source));
        var ex = await Assert.ThrowsAsync<ToolTargetIneligibleException>(() => _refactoringEngine.ChangeSignatureAsync("Foo.cs", "M", new SignatureParameterSpec[] { new ExistingParameterSpec(0) }));

        Assert.That(ex!.Message, Is.EqualTo("Cannot remove parameter 'b' (originalIndex 1): the body still uses it at Foo.cs:3. Options: keep it by listing {originalIndex: 1} in parameters (an entry with the same name but no originalIndex is a new parameter), or remove its uses from the body first."));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible), "A refused removal throws before anything is planned or written");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RemoveUnusedParameter_FieldAndLambdaParameterWithSameName_DoNotTrigger_AndCompiles()
    {
        // The field b is reached as this.b, and the lambda's own parameter b shadows nothing once the removed parameter is gone.
        const string source = """
            using System;
            class Foo
            {
                int b = 7;
                public int M(int a, int b)
                {
                    Func<int, int> f = b => b + 1;
                    return a + this.b + f(a);
                }
            }
            """;
        SetFiles(("Foo.cs", source));
        var result = await _refactoringEngine.ChangeSignatureAsync("Foo.cs", "M", new SignatureParameterSpec[] { new ExistingParameterSpec(0) });

        Assert.That(result.Error, Is.Null, result.Error);
        var after = result.Changes["Foo.cs"];
        Assert.That(after, Does.Contain("public int M(int a)"), "The parameter b is removed");
        Assert.That(after, Does.Contain("this.b"), "The field is untouched");
        AssertCompiles("Foo.cs", after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RemoveUsedParameter_TwelveUses_ListsTenThenAndTwoMore()
    {
        // Uses are on lines 6 to 17; the message lists the first ten and counts the rest.
        const string source = """
            class Foo
            {
                public int M(int a, int b)
                {
                    var x = a;
                    x += b;
                    x += b;
                    x += b;
                    x += b;
                    x += b;
                    x += b;
                    x += b;
                    x += b;
                    x += b;
                    x += b;
                    x += b;
                    x += b;
                    return x;
                }
            }
            """;
        SetFiles(("Foo.cs", source));
        var ex = await Assert.ThrowsAsync<ToolTargetIneligibleException>(() => _refactoringEngine.ChangeSignatureAsync("Foo.cs", "M", new SignatureParameterSpec[] { new ExistingParameterSpec(0) }));

        var shown = string.Join(", ", Enumerable.Range(6, 10).Select(line => $"Foo.cs:{line}"));
        Assert.That(ex!.Message, Is.EqualTo($"Cannot remove parameter 'b' (originalIndex 1): the body still uses it at {shown} and 2 more. Options: keep it by listing {{originalIndex: 1}} in parameters (an entry with the same name but no originalIndex is a new parameter), or remove its uses from the body first."));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible), "A refused removal throws before anything is planned or written");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RemoveUnusedParameter_SucceedsAndCompiles()
    {
        const string source = """
            class Foo
            {
                public int M(int a, int b, int c)
                {
                    return a + c;
                }
            }
            """;
        SetFiles(("Foo.cs", source));
        var result = await _refactoringEngine.ChangeSignatureAsync("Foo.cs", "M",
            new SignatureParameterSpec[] { new ExistingParameterSpec(0), new ExistingParameterSpec(2) });

        Assert.That(result.Error, Is.Null, result.Error);
        var after = result.Changes["Foo.cs"];
        Assert.That(after, Does.Contain("public int M(int a, int c)"));
        AssertCompiles("Foo.cs", after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ReorderUsedParameter_SucceedsAndCompiles()
    {
        const string source = """
            class Foo
            {
                public int M(int a, int b)
                {
                    return a + b;
                }
            }
            """;
        SetFiles(("Foo.cs", source));
        var result = await _refactoringEngine.ChangeSignatureAsync("Foo.cs", "M",
            new SignatureParameterSpec[] { new ExistingParameterSpec(1), new ExistingParameterSpec(0) });

        Assert.That(result.Error, Is.Null, result.Error);
        var after = result.Changes["Foo.cs"];
        Assert.That(after, Does.Contain("public int M(int b, int a)"));
        AssertCompiles("Foo.cs", after);
    }

    [TestCase(ChangeSignatureOldSignature.keepAsDeprecatedWrapper)]
    [TestCase(ChangeSignatureOldSignature.auto)]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RemoveUsedParameter_SameRefusalUnderWrapperAndAutoModes(ChangeSignatureOldSignature oldSignature)
    {
        const string source = """
            class Foo
            {
                public int M(int a, int b)
                {
                    var x = a;
                    return x + b + b;
                }
            }
            """;
        SetFiles(("Foo.cs", source));
        var ex = await Assert.ThrowsAsync<ToolTargetIneligibleException>(() => _refactoringEngine.ChangeSignatureAsync("Foo.cs", "M", new SignatureParameterSpec[] { new ExistingParameterSpec(0) }, oldSignature: oldSignature));

        Assert.That(ex!.Message, Is.EqualTo("Cannot remove parameter 'b' (originalIndex 1): the body still uses it at Foo.cs:6. Options: keep it by listing {originalIndex: 1} in parameters (an entry with the same name but no originalIndex is a new parameter), or remove its uses from the body first."));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible), "A refused removal throws before anything is planned or written");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RemoveUsedParameter_WithSameNamedNewParameter_RefusalNamesKeepOption()
    {
        // b (index 1) is dropped while a new parameter also called b is listed; the refusal must name {originalIndex: 1} as the way to keep it.
        const string source = """
            class Foo
            {
                public int M(int a, int b)
                {
                    return a + b;
                }
            }
            """;
        SetFiles(("Foo.cs", source));
        var ex = await Assert.ThrowsAsync<ToolTargetIneligibleException>(() => _refactoringEngine.ChangeSignatureAsync("Foo.cs", "M", new SignatureParameterSpec[] { new ExistingParameterSpec(0), new NewParameterSpec("b", "int", "0") }));

        Assert.That(ex!.Message, Does.Contain("{originalIndex: 1}"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible), "A refused removal throws before anything is planned or written");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RemoveParameter_AbstractMethodWithoutBody_IsNotRefusedByThisCheck()
    {
        // An abstract declaration has no body, so it has no uses; the removal may still fail for other reasons.
        const string source = """
            abstract class Foo
            {
                public abstract int M(int a, int b);
            }
            """;
        SetFiles(("Foo.cs", source));
        var result = await _refactoringEngine.ChangeSignatureAsync("Foo.cs", "M", new SignatureParameterSpec[] { new ExistingParameterSpec(0) });

        Assert.That(result.Error ?? string.Empty, Does.Not.Contain("the body still uses it"));
    }
}
