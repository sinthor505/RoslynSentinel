using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// ChangeSignature with a new parameter whose defaultValue is an expression rather than a constant. The default is bound
/// speculatively at every call site: a site where it does not bind is unmigratable (remove refuses, the wrapper modes leave
/// that site on the wrapper). A non-constant default makes the new parameter required in the new member.
/// </summary>
[TestFixture]
public class ChangeSignatureNonConstantDefaultTests
{
    // Lines: 1 interface, 2 class, 3 brace, 4 field, 5 Save, 6 Inner (instance call), 7 S (static call).
    private const string SvcFullSource = """
        interface ILogger {}
        class Svc
        {
            ILogger _logger;
            public void Save(int a) {}
            void Inner() { Save(1); }
            static void S(Svc s) { s.Save(3); }
        }
        """;

    // Line 3 is the foreign call to Save that cannot see Svc's _logger field.
    private const string OtherSource = """
        class Other
        {
            void Go(Svc s) { s.Save(2); }
        }
        """;

    private const string SvcInnerOnlySource = """
        interface ILogger {}
        class Svc
        {
            ILogger _logger;
            public void Save(int a) {}
            void Inner() { Save(1); }
        }
        """;

    private const string SvcConstantSource = """
        class Svc
        {
            public void Save(int a) {}
            void Inner() { Save(1); }
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

    private static void AssertCompiles(string fileName, string text)
    {
        var frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Collections.dll", "System.Linq.dll" }
            .Select(assembly => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(frameworkDir, assembly)))
            .ToList();
        var compilation = CSharpCompilation.Create("ChangeSignatureNonConstantDefaultProof", new[] { CSharpSyntaxTree.ParseText(text, path: fileName) }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();
        Assert.That(errors, Is.Empty, "The rewritten code must compile with zero errors: " + string.Join(Environment.NewLine, errors));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RemoveMode_ForeignSiteDoesNotBind_RefusesNamingFileLineAndExpression()
    {
        SetFiles(("Svc.cs", SvcFullSource), ("Other.cs", OtherSource));
        var result = await _refactoringEngine.ChangeSignatureAsync("Svc.cs", "Save",
            new SignatureParameterSpec[] { new ExistingParameterSpec(0), new NewParameterSpec("logger", "ILogger", "_logger") },
            oldSignature: ChangeSignatureOldSignature.remove);

        Assert.That(result.Error, Is.Not.Null, "Remove mode must refuse when a call site cannot bind the default");
        Assert.That(result.Error, Does.Contain("Other.cs:3"), "The refusal names the file and line of the unbound site");
        Assert.That(result.Error, Does.Contain("_logger"), "The refusal names the defaultValue expression");
        Assert.That(result.Changes, Is.Empty, "A refused change writes nothing");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task AutoMode_ForeignSiteDoesNotBind_InClassSiteMigrated_ForeignSiteStaysOnWrapper()
    {
        SetFiles(("Svc.cs", SvcFullSource), ("Other.cs", OtherSource));
        var result = await _refactoringEngine.ChangeSignatureAsync("Svc.cs", "Save",
            new SignatureParameterSpec[] { new ExistingParameterSpec(0), new NewParameterSpec("logger", "ILogger", "_logger") });

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.Changes["Svc.cs"], Does.Contain("Save(1, _logger)"), "The in-class call binds _logger and is migrated");
        Assert.That(result.Changes.Keys.Select(k => k.ToString()).Any(k => k.Contains("Other.cs")), Is.False, "The foreign file is not rewritten");
        var foreign = result.SkippedCallSites.Where(s => s.FilePath.ToString().Contains("Other.cs")).ToList();
        Assert.That(foreign, Has.Count.EqualTo(1), "The foreign site is listed as skipped");
        Assert.That(foreign[0].LineNumber, Is.EqualTo(3));
        Assert.That(foreign[0].Reason, Does.Contain("defaultValue '_logger' of new parameter 'logger' does not bind in this scope"));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task KeepWrapperMode_ForeignSiteDoesNotBind_ForeignSiteStaysOnWrapper()
    {
        SetFiles(("Svc.cs", SvcFullSource), ("Other.cs", OtherSource));
        var result = await _refactoringEngine.ChangeSignatureAsync("Svc.cs", "Save",
            new SignatureParameterSpec[] { new ExistingParameterSpec(0), new NewParameterSpec("logger", "ILogger", "_logger") },
            oldSignature: ChangeSignatureOldSignature.keepAsDeprecatedWrapper);

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.Changes["Svc.cs"], Does.Contain("Save(1, _logger)"), "The in-class call binds _logger and is migrated");
        Assert.That(result.Changes.Keys.Select(k => k.ToString()).Any(k => k.Contains("Other.cs")), Is.False, "The foreign file is not rewritten");
        var foreign = result.SkippedCallSites.Where(s => s.FilePath.ToString().Contains("Other.cs")).ToList();
        Assert.That(foreign, Has.Count.EqualTo(1), "The foreign site stays on the wrapper and is listed");
        Assert.That(foreign[0].LineNumber, Is.EqualTo(3));
        Assert.That(foreign[0].Reason, Does.Contain("defaultValue '_logger' of new parameter 'logger' does not bind in this scope"));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task RemoveMode_EverySiteBinds_NewParameterIsRequired_AndCompiles()
    {
        SetFiles(("Svc.cs", SvcInnerOnlySource));
        var result = await _refactoringEngine.ChangeSignatureAsync("Svc.cs", "Save",
            new SignatureParameterSpec[] { new ExistingParameterSpec(0), new NewParameterSpec("logger", "ILogger", "_logger") },
            oldSignature: ChangeSignatureOldSignature.remove);

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Svc.cs"];
        Assert.That(after, Does.Contain("Save(int a, ILogger logger)"), "A non-constant default makes the new parameter required");
        Assert.That(after, Does.Contain("Save(1, _logger)"));
        AssertCompiles("Svc.cs", after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task AutoMode_StaticContextSite_SkippedWithStaticContextReason()
    {
        SetFiles(("Svc.cs", SvcFullSource));
        var result = await _refactoringEngine.ChangeSignatureAsync("Svc.cs", "Save",
            new SignatureParameterSpec[] { new ExistingParameterSpec(0), new NewParameterSpec("logger", "ILogger", "_logger") });

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.Changes["Svc.cs"], Does.Contain("Save(1, _logger)"), "The instance call in Inner is still migrated");
        var staticSite = result.SkippedCallSites.Single(s => s.LineNumber == 7);
        Assert.That(staticSite.FilePath.ToString(), Does.Contain("Svc.cs"));
        Assert.That(staticSite.Reason, Does.Contain("static context"), "An instance field default in a static method does not bind");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ConstantDefault_IsUnchangedAndBindsEverywhere()
    {
        SetFiles(("Svc.cs", SvcConstantSource));
        var result = await _refactoringEngine.ChangeSignatureAsync("Svc.cs", "Save",
            new SignatureParameterSpec[] { new ExistingParameterSpec(0), new NewParameterSpec("b", "int", "5") },
            oldSignature: ChangeSignatureOldSignature.remove);

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Svc.cs"];
        Assert.That(after, Does.Contain("Save(int a, int b = 5)"), "A constant default stays an optional parameter");
        Assert.That(after, Does.Contain("Save(1, 5)"));
        AssertCompiles("Svc.cs", after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task KeepWrapperMode_WrapperBodyCannotBindDefault_Refuses()
    {
        // The default _log exists only in Other, so the foreign call binds it but the wrapper inside Svc cannot.
        const string otherWithLog = """
            class Other
            {
                int _log;
                void Go(Svc s) { s.Save(2); }
            }
            """;
        SetFiles(("Svc.cs", SvcConstantSource), ("Other.cs", otherWithLog));
        var result = await _refactoringEngine.ChangeSignatureAsync("Svc.cs", "Save",
            new SignatureParameterSpec[] { new ExistingParameterSpec(0), new NewParameterSpec("logger", "int", "_log") },
            oldSignature: ChangeSignatureOldSignature.keepAsDeprecatedWrapper);

        Assert.That(result.Error, Is.Not.Null, "A wrapper that cannot bind its default must be refused");
        Assert.That(result.Error, Does.Contain("cannot wrap 'Save'"));
        Assert.That(result.Error, Does.Contain("does not bind inside 'Svc'"));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task AutoMode_DateTimeDefaultWithoutUsingSystem_SiteSkipped()
    {
        // The declaring file has using System; so the wrapper body and the in-class call bind DateTime.UtcNow; the other file has none, so its call site (line 4) does not.
        const string declaring = """
            using System;
            class Svc
            {
                public void Save(int a) {}
                void Inner() { Save(1); }
            }
            """;
        const string noUsing = """
            class Other
            {
                // no using System;
                void Run(Svc s) { s.Save(1); }
            }
            """;
        SetFiles(("Svc.cs", declaring), ("Other.cs", noUsing));
        var result = await _refactoringEngine.ChangeSignatureAsync("Svc.cs", "Save",
            new SignatureParameterSpec[] { new ExistingParameterSpec(0), new NewParameterSpec("at", "System.DateTime", "DateTime.UtcNow") });

        Assert.That(result.Error, Is.Null, result.Error);
        var skipped = result.SkippedCallSites.Single();
        Assert.That(skipped.LineNumber, Is.EqualTo(4));
        Assert.That(skipped.Reason, Does.Contain("defaultValue 'DateTime.UtcNow' of new parameter 'at' does not bind in this scope"));
    }
}
