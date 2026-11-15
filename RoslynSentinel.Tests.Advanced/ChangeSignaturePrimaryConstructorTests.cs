using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// ChangeSignature on a class or struct with a primary constructor (target = the type name) rewrites the type's parameter list and every
/// construction site (<c>new T(...)</c>, target-typed <c>new(...)</c>, a derived type's <c>: Base(...)</c>) instead of refusing. A record's
/// primary constructor allows add-with-default and reorder; removing or renaming a positional parameter is refused (owner policy O1).
/// </summary>
[TestFixture]
public class ChangeSignaturePrimaryConstructorTests
{
    // Widget is used from another file (Maker) both as `new Widget(...)` and as a target-typed `new(...)`.
    private const string WidgetSource = """
        class Widget(int a, string b)
        {
            public int A => a;
            public string B => b;
        }
        """;

    private const string MakerSource = """
        class Maker
        {
            public Widget One() => new Widget(1, "x");
            public Widget Two() { Widget w = new(2, "y"); return w; }
            public Widget Three() => new Widget(b: "z", a: 3);
        }
        """;

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

    private Task<ChangeSignatureResult> Change(string filePath, string typeName, params SignatureParameterSpec[] parameters) =>
        _refactoringEngine.ChangeSignatureAsync(filePath, typeName, parameters);

    // The text of one file after the change: its rewritten text when the engine changed it, else the original.
    private static string TextOf(ChangeSignatureResult result, string name, string original) =>
        result.Changes.FirstOrDefault(kv => kv.Key.ToString().Contains(name)).Value ?? original;

    // Compiles the files against the core framework assemblies (same directory as System.Private.CoreLib).
    private static void AssertCompiles(params (string Name, string Text)[] files)
    {
        var frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Collections.dll", "System.Linq.dll" }
            .Select(assembly => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(frameworkDir, assembly)))
            .ToList();
        var trees = files.Select(file => CSharpSyntaxTree.ParseText(file.Text, path: file.Name)).ToList();
        var compilation = CSharpCompilation.Create("ChangeSignaturePrimaryConstructorProof", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();
        Assert.That(errors, Is.Empty, "The rewritten code must compile with zero errors: " + string.Join(Environment.NewLine, errors));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task AddParameterWithDefault_ClassPrimaryConstructor_RewritesDeclarationAndEveryNewSite()
    {
        SetFiles(("Widget.cs", WidgetSource), ("Maker.cs", MakerSource));
        var result = await Change("Widget.cs", "Widget", new ExistingParameterSpec(0), new ExistingParameterSpec(1), new NewParameterSpec("c", "bool", "true"));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var widget = TextOf(result, "Widget.cs", WidgetSource);
        var maker = TextOf(result, "Maker.cs", MakerSource);
        Assert.That(widget, Does.Contain("class Widget(int a, string b, bool c = true)"), "The type's parameter list gains the new parameter with its default");
        Assert.That(widget, Does.Contain("public int A => a;"), "The body is untouched");
        Assert.That(maker, Does.Contain("new Widget(1, \"x\", true)"));
        Assert.That(maker, Does.Contain("new(2, \"y\", true)"), "A target-typed new is migrated too");
        Assert.That(maker, Does.Contain("new Widget(3, \"z\", true)").Or.Contain("new Widget(b: \"z\", a: 3, true)"), "The named-argument site is migrated");
        AssertCompiles(("Widget.cs", widget), ("Maker.cs", maker));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task AddParameter_StructPrimaryConstructor_RewritesDeclarationAndSites()
    {
        const string pair = """
            struct Pair(int a, int b)
            {
                public int Sum => a + b;
            }
            """;
        const string user = """
            class User
            {
                public Pair Make() => new Pair(1, 2);
            }
            """;
        SetFiles(("Pair.cs", pair), ("User.cs", user));
        var result = await Change("Pair.cs", "Pair", new ExistingParameterSpec(0), new ExistingParameterSpec(1), new NewParameterSpec("c", "int", "0"));

        Assert.That(result.Error, Is.Null, result.Error);
        var pairAfter = TextOf(result, "Pair.cs", pair);
        var userAfter = TextOf(result, "User.cs", user);
        Assert.That(pairAfter, Does.Contain("struct Pair(int a, int b, int c = 0)"));
        Assert.That(userAfter, Does.Contain("new Pair(1, 2, 0)"));
        AssertCompiles(("Pair.cs", pairAfter), ("User.cs", userAfter));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RemoveUnusedParameter_ClassPrimaryConstructor_DropsItFromDeclarationAndSites()
    {
        const string source = """
            class Widget(int a, string b, int unused)
            {
                public int A => a;
                public string B => b;
            }
            class Maker
            {
                public Widget One() => new Widget(1, "x", 9);
            }
            """;
        SetFiles(("Widget.cs", source));
        var result = await Change("Widget.cs", "Widget", new ExistingParameterSpec(0), new ExistingParameterSpec(1));

        Assert.That(result.Error, Is.Null, result.Error);
        var after = TextOf(result, "Widget.cs", source);
        Assert.That(after, Does.Contain("class Widget(int a, string b)"));
        Assert.That(after, Does.Contain("new Widget(1, \"x\")"));
        Assert.That(after, Does.Not.Contain("unused"));
        AssertCompiles(("Widget.cs", after));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RemoveUsedParameter_ClassPrimaryConstructor_RefusesNamingTheLine()
    {
        const string source = """
            class Widget(int a, string b)
            {
                public int A => a;
                public string B => b;
            }
            """;
        SetFiles(("Widget.cs", source));
        var ex = await Assert.ThrowsAsync<ToolTargetIneligibleException>(() => Change("Widget.cs", "Widget", new ExistingParameterSpec(0)));

        Assert.That(ex!.Message, Does.Contain("Cannot remove parameter 'b' of 'Widget'").And.Contain("Widget.cs:4"));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ReorderParameters_ClassPrimaryConstructor_PositionalAndNamedSitesFollow()
    {
        SetFiles(("Widget.cs", WidgetSource), ("Maker.cs", MakerSource));
        var result = await Change("Widget.cs", "Widget", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        var widget = TextOf(result, "Widget.cs", WidgetSource);
        var maker = TextOf(result, "Maker.cs", MakerSource);
        Assert.That(widget, Does.Contain("class Widget(string b, int a)"));
        Assert.That(maker, Does.Contain("new Widget(\"x\", 1)"), "A positional site is reordered");
        Assert.That(maker, Does.Contain("new(\"y\", 2)"));
        Assert.That(maker, Does.Contain("new Widget(\"z\", 3)"), "A named-argument site ends up in the new order");
        AssertCompiles(("Widget.cs", widget), ("Maker.cs", maker));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task Reorder_BaseTypeWithPrimaryConstructor_UpdatesDerivedPrimaryConstructorBaseCall()
    {
        const string source = """
            class Base(int x, string y)
            {
                public int X => x;
                public string Y => y;
            }
            class Derived(int d) : Base(d, "s")
            {
                public int D => d;
            }
            class User
            {
                public Derived Make() => new Derived(5);
                public Base MakeBase() => new Base(7, "q");
            }
            """;
        SetFiles(("Base.cs", source));
        var result = await Change("Base.cs", "Base", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = TextOf(result, "Base.cs", source);
        Assert.That(after, Does.Contain("class Base(string y, int x)"));
        Assert.That(after, Does.Contain("class Derived(int d) : Base(\"s\", d)"), "The derived type's primary-constructor base call is reordered");
        Assert.That(after, Does.Contain("new Base(\"q\", 7)"));
        Assert.That(after, Does.Contain("new Derived(5)"), "The derived type's own constructor is not the target");
        AssertCompiles(("Base.cs", after));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task Change_ReturnsThePlanWithoutWriting_CommittedSourceStaysUnchanged()
    {
        // The engine never writes: the tool applies Changes, or skips that for a dry run. So the planned texts exist and the workspace is untouched.
        SetFiles(("Widget.cs", WidgetSource), ("Maker.cs", MakerSource));
        var result = await Change("Widget.cs", "Widget", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.Changes, Is.Not.Empty);
        var committed = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var texts = new List<string>();
        foreach (var document in committed.Projects.SelectMany(project => project.Documents))
        {
            texts.Add((await document.GetTextAsync()).ToString());
        }

        Assert.That(texts, Does.Contain(WidgetSource).And.Contain(MakerSource), "The committed documents are exactly the originals");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RecordPrimaryConstructor_AddParameterWithDefault_RewritesDeclarationAndSites_WithExpressionAndPropertyReadStillCompile()
    {
        const string point = """
            record Point(int X, int Y);
            """;
        // With-expression and property read are untouched by the change (documented limit) and must still compile.
        const string user = """
            class User
            {
                public Point Make() => new Point(1, 2);
                public Point Shift(Point p) => p with { X = p.X + 1 };
                public int Sum(Point p) => p.X + p.Y;
            }
            """;
        SetFiles(("Point.cs", point), ("User.cs", user));
        var result = await Change("Point.cs", "Point", new ExistingParameterSpec(0), new ExistingParameterSpec(1), new NewParameterSpec("Z", "int", "0"));

        Assert.That(result.Error, Is.Null, result.Error);
        var pointAfter = TextOf(result, "Point.cs", point);
        var userAfter = TextOf(result, "User.cs", user);
        Assert.That(pointAfter, Does.Contain("record Point(int X, int Y, int Z = 0);"));
        Assert.That(userAfter, Does.Contain("new Point(1, 2, 0)"));
        Assert.That(userAfter, Does.Contain("p with { X = p.X + 1 }"), "The with-expression is untouched");
        AssertCompiles(("Point.cs", pointAfter), ("User.cs", userAfter));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RecordPrimaryConstructor_Reorder_RewritesDeclarationAndSites()
    {
        const string point = """
            record Point(int X, string Y);
            """;
        const string user = """
            class User
            {
                public Point Make() => new Point(1, "a");
            }
            """;
        SetFiles(("Point.cs", point), ("User.cs", user));
        var result = await Change("Point.cs", "Point", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        var pointAfter = TextOf(result, "Point.cs", point);
        var userAfter = TextOf(result, "User.cs", user);
        Assert.That(pointAfter, Does.Contain("record Point(string Y, int X);"));
        Assert.That(userAfter, Does.Contain("new Point(\"a\", 1)"));
        AssertCompiles(("Point.cs", pointAfter), ("User.cs", userAfter));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RecordPrimaryConstructor_RemovePositionalParameter_RefusesNamingMemberRemove()
    {
        const string source = """
            record Point(int X, int Y);
            """;
        SetFiles(("Point.cs", source));
        var result = await Change("Point.cs", "Point", new ExistingParameterSpec(0));

        Assert.That(result.Error, Does.Contain("Member(operation: remove)").And.Contain("'Y'"));
        Assert.That(result.Changes, Is.Empty);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RecordPrimaryConstructor_RenamePositionalParameter_RefusesNamingRenameSymbol()
    {
        const string source = """
            record Point(int X, int Y);
            """;
        SetFiles(("Point.cs", source));
        // A rename shows as the old parameter left out and a new one in its place.
        var result = await Change("Point.cs", "Point", new ExistingParameterSpec(0), new NewParameterSpec("Z", "int", "0"));

        Assert.That(result.Error, Does.Contain("RenameSymbol").And.Contain("'Y'"));
        Assert.That(result.Changes, Is.Empty);
    }
}
