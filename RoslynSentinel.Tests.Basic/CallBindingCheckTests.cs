using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class CallBindingCheckTests
{
    private const string MainProject = "P";

    private const string PathMain = "C:\\x\\Main.cs";

    private const string PathCaller = "C:\\x\\Caller.cs";

    private static readonly CallBindingScope SaveScope = new(new[] { "Save" });

    private static Solution Build(AdhocWorkspace workspace, params (string Project, string Path, string Text)[] documents)
    {
        var solution = workspace.CurrentSolution;
        var projectIds = new Dictionary<string, ProjectId>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            if (projectIds.ContainsKey(document.Project))
            {
                continue;
            }

            var projectId = ProjectId.CreateNewId(document.Project);
            projectIds[document.Project] = projectId;
            var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Create(), document.Project, document.Project, LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
            solution = solution.AddProject(projectInfo);
        }

        foreach (var document in documents)
        {
            solution = solution.AddDocument(DocumentId.CreateNewId(projectIds[document.Project]), Path.GetFileName(document.Path), SourceText.From(document.Text), filePath: document.Path);
        }

        return solution;
    }

    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    // Namespace N with class Target (one body line) and class Caller.Run(callerParams) (one line per run statement).
    private static string Source(string targetBody, string callerParams, params string[] runLines)
    {
        var lines = new List<string>
        {
            "namespace N",
            "{",
            "    public class Target",
            "    {",
            "        " + targetBody,
            "    }",
            "    public class Caller",
            "    {",
            "        public void Run(" + callerParams + ")",
            "        {",
        };
        lines.AddRange(runLines.Select(line => "            " + line));
        lines.AddRange(new[] { "        }", "    }", "}" });
        return Lines(lines.ToArray());
    }

    private static string[] Repeat(string line, int count) => Enumerable.Repeat(line, count).ToArray();

    [Test]
    public async Task Rebinding_NewOverloadOnTarget_ReportsCallWithBothBindingsAsync()
    {
        using var workspace = new AdhocWorkspace();
        var original = Build(workspace, (MainProject, PathMain, Source("public void Save(long x) { }", "Target target", "target.Save(1);")));
        var trial = Build(workspace, (MainProject, PathMain, Source("public void Save(long x) { } public void Save(int x) { }", "Target target", "target.Save(1);")));

        var result = await CallBindingCheck.CheckAsync(original, trial, SaveScope);

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Discrepancies));
        Assert.That(result.Discrepancies, Has.Count.EqualTo(1));
        var discrepancy = result.Discrepancies[0];
        Assert.That(discrepancy.Kind, Is.EqualTo(CallBindingDiscrepancyKind.BindsElsewhere));
        Assert.That(discrepancy.Name, Is.EqualTo("Save"));
        Assert.That(discrepancy.CallText, Is.EqualTo("target.Save(1)"));
        Assert.That(discrepancy.EnclosingMemberId, Is.EqualTo("M:N.Caller.Run(N.Target)"));
        Assert.That(discrepancy.FilePath, Is.EqualTo(PathMain));
        Assert.That(discrepancy.BindsNow, Does.Contain("Save(long"));
        Assert.That(discrepancy.WouldBind, Does.Contain("Save(int"));
        Assert.That(discrepancy.ExpectedIds, Is.EqualTo(new[] { "M:N.Target.Save(System.Int64)" }));

        var refusal = CallBindingCheck.FormatRefusal(result, "add an overload to 'Target'");
        Assert.That(refusal, Does.Contain("target.Save(1)"));
        Assert.That(refusal, Does.Contain("Save(long"));
        Assert.That(refusal, Does.Contain("Save(int"));
    }

    [Test]
    public async Task SwappedTargets_TwoCallsInOneMember_BothReportedAsync()
    {
        using var workspace = new AdhocWorkspace();
        const string targets = "public void Save(long x) { } public void Save(string s) { }";
        var original = Build(workspace, (MainProject, PathMain, Source(targets, "Target target", "target.Save(1);", "target.Save(\"s\");")));
        var trial = Build(workspace, (MainProject, PathMain, Source(targets, "Target target", "target.Save(\"s\");", "target.Save(1);")));

        var result = await CallBindingCheck.CheckAsync(original, trial, SaveScope);

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Discrepancies));
        Assert.That(result.Discrepancies, Has.Count.EqualTo(2));
        var first = result.Discrepancies[0];
        var second = result.Discrepancies[1];
        Assert.That(first.Kind, Is.EqualTo(CallBindingDiscrepancyKind.BindsElsewhere));
        Assert.That(first.CallText, Is.EqualTo("target.Save(\"s\")"));
        Assert.That(first.ExpectedIds, Is.EqualTo(new[] { "M:N.Target.Save(System.Int64)" }));
        Assert.That(second.Kind, Is.EqualTo(CallBindingDiscrepancyKind.BindsElsewhere));
        Assert.That(second.CallText, Is.EqualTo("target.Save(1)"));
        Assert.That(second.ExpectedIds, Is.EqualTo(new[] { "M:N.Target.Save(System.String)" }));
    }

    [Test]
    public async Task MovedMember_RecursiveSelfCall_PairsThroughMemberIdMapAsync()
    {
        using var workspace = new AdhocWorkspace();
        const string fact = "public int Fact(int n) { return n <= 1 ? 1 : Fact(n - 1); }";
        var original = Build(workspace, (MainProject, PathMain, Lines(
            "namespace N",
            "{",
            "    public class A { " + fact + " }",
            "    public class B { }",
            "}")));
        var trial = Build(workspace, (MainProject, PathMain, Lines(
            "namespace N",
            "{",
            "    public class A { }",
            "    public class B { " + fact + " }",
            "}")));
        var expectations = new CallBindingExpectations
        {
            MemberIdMap = new Dictionary<string, string> { ["M:N.A.Fact(System.Int32)"] = "M:N.B.Fact(System.Int32)" },
            ExpectedTargetsByOldId = new Dictionary<string, IReadOnlyCollection<string>> { ["M:N.A.Fact(System.Int32)"] = new[] { "M:N.B.Fact(System.Int32)" } },
        };

        var result = await CallBindingCheck.CheckAsync(original, trial, new CallBindingScope(new[] { "Fact" }), expectations);

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Clean));
        Assert.That(result.Discrepancies, Is.Empty);
        Assert.That(result.SitesChecked, Is.EqualTo(1));
    }

    [Test]
    public async Task ReceiverRewriteAndLineShift_SameTarget_CleanAsync()
    {
        using var workspace = new AdhocWorkspace();
        var original = Build(workspace, (MainProject, PathMain, Source("public void Save(long x) { }", "Target a, Target b", "a.Save(1);")));
        var trial = Build(workspace, (MainProject, PathMain, Source("public void Save(long x) { }", "Target a, Target b", string.Empty, string.Empty, "b.Save(1);")));

        var result = await CallBindingCheck.CheckAsync(original, trial, SaveScope);

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Clean));
        Assert.That(result.Discrepancies, Is.Empty);
        Assert.That(result.SitesChecked, Is.EqualTo(1));
    }

    [Test]
    public async Task KeepForwarder_CallerStillBindsOldId_IsCleanAsync()
    {
        using var workspace = new AdhocWorkspace();
        var original = Build(workspace, (MainProject, PathMain, Lines(
            "namespace N",
            "{",
            "    public class Target { public void Save(long x) { } }",
            "    public class Helper { }",
            "    public class Caller { public void Run(Target target, Helper helper) { target.Save(1); } }",
            "}")));
        var trial = Build(workspace, (MainProject, PathMain, Lines(
            "namespace N",
            "{",
            "    public class Target { public void Save(long x) { new Helper().Save(x); } }",
            "    public class Helper { public void Save(long x) { } }",
            "    public class Caller { public void Run(Target target, Helper helper) { target.Save(1); } }",
            "}")));
        var expectations = new CallBindingExpectations
        {
            ExpectedTargetsByOldId = new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["M:N.Target.Save(System.Int64)"] = new[] { "M:N.Target.Save(System.Int64)", "M:N.Helper.Save(System.Int64)" },
            },
            AddedMemberIds = new[] { "M:N.Target.Save(System.Int64)" },
        };

        var result = await CallBindingCheck.CheckAsync(original, trial, SaveScope, expectations);

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Clean));
        Assert.That(result.Discrepancies, Is.Empty);
        Assert.That(result.SitesChecked, Is.EqualTo(1));
    }

    [Test]
    public async Task KeepForwarder_CallerRewrittenToNewId_IsCleanAsync()
    {
        using var workspace = new AdhocWorkspace();
        var original = Build(workspace, (MainProject, PathMain, Lines(
            "namespace N",
            "{",
            "    public class Target { public void Save(long x) { } }",
            "    public class Helper { }",
            "    public class Caller { public void Run(Target target, Helper helper) { target.Save(1); } }",
            "}")));
        var trial = Build(workspace, (MainProject, PathMain, Lines(
            "namespace N",
            "{",
            "    public class Target { public void Save(long x) { new Helper().Save(x); } }",
            "    public class Helper { public void Save(long x) { } }",
            "    public class Caller { public void Run(Target target, Helper helper) { helper.Save(1); } }",
            "}")));
        var expectations = new CallBindingExpectations
        {
            ExpectedTargetsByOldId = new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["M:N.Target.Save(System.Int64)"] = new[] { "M:N.Target.Save(System.Int64)", "M:N.Helper.Save(System.Int64)" },
            },
            AddedMemberIds = new[] { "M:N.Target.Save(System.Int64)" },
        };

        var result = await CallBindingCheck.CheckAsync(original, trial, SaveScope, expectations);

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Clean));
        Assert.That(result.Discrepancies, Is.Empty);
    }

    [Test]
    public async Task LocalFunctionAndLambda_RebindingReportedUnderEnclosingMemberAsync()
    {
        using var workspace = new AdhocWorkspace();
        const string delegateType = "public delegate void Call();";
        var runLines = new[] { "void Local() { target.Save(1); }", "Target.Call c = () => target.Save(2);", "Local(); c();" };
        var original = Build(workspace, (MainProject, PathMain, Source("public void Save(long x) { } " + delegateType, "Target target", runLines)));
        var trial = Build(workspace, (MainProject, PathMain, Source("public void Save(long x) { } public void Save(int x) { } " + delegateType, "Target target", runLines)));

        var result = await CallBindingCheck.CheckAsync(original, trial, SaveScope);

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Discrepancies));
        Assert.That(result.Discrepancies.Select(d => d.Kind), Is.All.EqualTo(CallBindingDiscrepancyKind.BindsElsewhere));
        Assert.That(result.Discrepancies.Select(d => d.EnclosingMemberId), Is.All.EqualTo("M:N.Caller.Run(N.Target)"));
        Assert.That(result.Discrepancies.Select(d => d.CallText), Is.EqualTo(new[] { "target.Save(1)", "target.Save(2)" }));
    }

    [Test]
    public async Task LocalFunctionAndLambda_Unchanged_IsCleanAsync()
    {
        using var workspace = new AdhocWorkspace();
        const string delegateType = "public delegate void Call();";
        var runLines = new[] { "void Local() { target.Save(1); }", "Target.Call c = () => target.Save(2);", "Local(); c();" };
        var original = Build(workspace, (MainProject, PathMain, Source("public void Save(long x) { } " + delegateType, "Target target", runLines)));
        var trial = Build(workspace, (MainProject, PathMain, Source("public void Save(long x) { } " + delegateType, "Target target", runLines)));

        var result = await CallBindingCheck.CheckAsync(original, trial, SaveScope);

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Clean));
        Assert.That(result.SitesChecked, Is.EqualTo(2));
    }

    [Test]
    public async Task GenericMethod_Unchanged_IsCleanAsync()
    {
        using var workspace = new AdhocWorkspace();
        var source = Source("public void Save<T>(T value) { }", "Target target", "target.Save(1);", "target.Save<int>(2);");
        var original = Build(workspace, (MainProject, PathMain, source));
        var trial = Build(workspace, (MainProject, PathMain, source));

        var result = await CallBindingCheck.CheckAsync(original, trial, SaveScope);

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Clean));
        Assert.That(result.SitesChecked, Is.EqualTo(2));
    }

    [Test]
    public async Task GenericMethod_NonGenericOverloadTakesOver_ReportedAsync()
    {
        using var workspace = new AdhocWorkspace();
        var original = Build(workspace, (MainProject, PathMain, Source("public void Save<T>(T value) { }", "Target target", "target.Save(1);", "target.Save<int>(2);")));
        var trial = Build(workspace, (MainProject, PathMain, Source("public void Save<T>(T value) { } public void Save(int x) { }", "Target target", "target.Save(1);", "target.Save<int>(2);")));

        var result = await CallBindingCheck.CheckAsync(original, trial, SaveScope);

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Discrepancies));
        Assert.That(result.Discrepancies, Has.Count.EqualTo(1));
        var discrepancy = result.Discrepancies[0];
        Assert.That(discrepancy.Kind, Is.EqualTo(CallBindingDiscrepancyKind.BindsElsewhere));
        Assert.That(discrepancy.CallText, Is.EqualTo("target.Save(1)"));
        Assert.That(discrepancy.ExpectedIds, Is.EqualTo(new[] { "M:N.Target.Save``1(``0)" }));
        Assert.That(discrepancy.WouldBind, Does.Contain("Save(int"));
    }

    [Test]
    public async Task ReducedExtension_Unchanged_IsCleanAsync()
    {
        using var workspace = new AdhocWorkspace();
        var source = Lines(
            "namespace N",
            "{",
            "    public class Target { }",
            "    public static class ExtA { public static void Ext(this Target t) { } }",
            "    public class Caller { public void Run(Target target) { target.Ext(); } }",
            "}");
        var original = Build(workspace, (MainProject, PathMain, source));
        var trial = Build(workspace, (MainProject, PathMain, source));

        var result = await CallBindingCheck.CheckAsync(original, trial, new CallBindingScope(new[] { "Ext" }));

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Clean));
        Assert.That(result.SitesChecked, Is.EqualTo(1));
    }

    [Test]
    public async Task ReducedExtension_RebindsToAnotherExtension_ReportedAsync()
    {
        using var workspace = new AdhocWorkspace();
        const string caller = "public class Caller { public void Run(Target target) { target.Ext(); } }";
        var original = Build(workspace, (MainProject, PathMain, Lines(
            "namespace N",
            "{",
            "    public class Target { }",
            "    public static class ExtA { public static void Ext(this Target t) { } }",
            "    " + caller,
            "}")));
        var trial = Build(workspace, (MainProject, PathMain, Lines(
            "namespace N",
            "{",
            "    public class Target { }",
            "    public static class ExtB { public static void Ext(this Target t) { } }",
            "    " + caller,
            "}")));

        var result = await CallBindingCheck.CheckAsync(original, trial, new CallBindingScope(new[] { "Ext" }));

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Discrepancies));
        Assert.That(result.Discrepancies, Has.Count.EqualTo(1));
        var discrepancy = result.Discrepancies[0];
        Assert.That(discrepancy.Kind, Is.EqualTo(CallBindingDiscrepancyKind.BindsElsewhere));
        Assert.That(discrepancy.Name, Is.EqualTo("Ext"));
        Assert.That(discrepancy.CallText, Is.EqualTo("target.Ext()"));
        Assert.That(discrepancy.ExpectedIds, Is.EqualTo(new[] { "M:N.ExtA.Ext(N.Target)" }));
        Assert.That(discrepancy.BindsNow, Does.Contain("ExtA"));
        Assert.That(discrepancy.WouldBind, Does.Contain("ExtB"));
    }

    [Test]
    public async Task NameOf_IsCaptured_DocCommentCrefIsNotAsync()
    {
        using var workspace = new AdhocWorkspace();
        var solution = Build(workspace, (MainProject, PathMain, Lines(
            "namespace N",
            "{",
            "    public class Target { public void Save(long x) { } }",
            "    public class Caller",
            "    {",
            "        /// <summary>See <see cref=\"Target.Save(long)\"/>.</summary>",
            "        public string Run() { return nameof(Target.Save); }",
            "    }",
            "}")));

        var snapshot = await CallBindingCheck.CaptureAsync(solution, SaveScope);

        Assert.That(snapshot.Problems, Is.Empty);
        Assert.That(snapshot.Observations, Has.Count.EqualTo(1));
        var observation = snapshot.Observations[0];
        Assert.That(observation.Name, Is.EqualTo("Save"));
        Assert.That(observation.CallText, Is.EqualTo("Target.Save"));
        Assert.That(observation.EnclosingMemberId, Is.EqualTo("M:N.Caller.Run"));
        // TODO CallBindingCheck: nameof(method group) binds as "candidates:..." (Describe, CallBindingCheck.cs:350), not the doc id; FINDING kept red
        Assert.That(observation.BoundId, Is.EqualTo("M:N.Target.Save(System.Int64)"));
    }

    [Test]
    public async Task UnrelatedTypeSameName_NotReportedAsync()
    {
        using var workspace = new AdhocWorkspace();
        var original = Build(workspace, (MainProject, PathMain, Lines(
            "namespace N",
            "{",
            "    public class Target { public void Save(long x) { } }",
            "    public class Other { public void Save(long x) { } }",
            "    public class Caller { public void Run(Target target, Other other) { other.Save(1); } }",
            "}")));
        var trial = Build(workspace, (MainProject, PathMain, Lines(
            "namespace N",
            "{",
            "    public class Target { public void Save(long x) { } public void Save(int x) { } }",
            "    public class Other { public void Save(long x) { } }",
            "    public class Caller { public void Run(Target target, Other other) { other.Save(1); } }",
            "}")));

        var result = await CallBindingCheck.CheckAsync(original, trial, SaveScope);

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Clean));
        Assert.That(result.Discrepancies, Is.Empty);
        Assert.That(result.SitesChecked, Is.EqualTo(1));
    }

    [Test]
    public async Task SameDocumentInTwoProjects_RebindingKeyedPerProjectAsync()
    {
        using var workspace = new AdhocWorkspace();
        const string original = "namespace N { public class Target { public void Save(long x) { } } public class Caller { public void Run(Target target) { target.Save(1); } } }";
        const string trialP1 = "namespace N { public class Target { public void Save(long x) { } public void Save(int x) { } } public class Caller { public void Run(Target target) { target.Save(1); } } }";
        var before = Build(workspace, ("P1", PathMain, original), ("P2", PathMain, original));
        var after = Build(workspace, ("P1", PathMain, trialP1), ("P2", PathMain, original));

        var snapshot = await CallBindingCheck.CaptureAsync(before, SaveScope);
        var result = await CallBindingCheck.CheckAsync(before, after, SaveScope);

        Assert.That(snapshot.Observations.Select(o => o.ProjectName).Distinct(), Is.EquivalentTo(new[] { "P1", "P2" }));
        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Discrepancies));
        Assert.That(result.SitesChecked, Is.EqualTo(2));
        Assert.That(result.Discrepancies, Has.Count.EqualTo(1));
        var discrepancy = result.Discrepancies[0];
        Assert.That(discrepancy.ProjectName, Is.EqualTo("P1"));
        Assert.That(discrepancy.EnclosingMemberId, Is.EqualTo("M:N.Caller.Run(N.Target)"));
        Assert.That(discrepancy.Kind, Is.EqualTo(CallBindingDiscrepancyKind.BindsElsewhere));
    }

    [Test]
    public async Task AddedUsing_BetterExtensionFromNewNamespace_DetectedWithPathAsync()
    {
        using var workspace = new AdhocWorkspace();
        var extensions = Lines(
            "namespace A { public static class ExtA { public static void Foo(this object o) { } } }",
            "namespace B { public static class ExtB { public static void Foo(this N.Target t) { } } }");
        var callerBefore = Lines(
            "using A;",
            "namespace N { public class Target { } public class Caller { public void Run(Target target) { target.Foo(); } } }");
        var callerAfter = Lines(
            "using A;",
            "using B;",
            "namespace N { public class Target { } public class Caller { public void Run(Target target) { target.Foo(); } } }");
        var original = Build(workspace, (MainProject, PathCaller, callerBefore), (MainProject, PathMain, extensions));
        var trial = Build(workspace, (MainProject, PathCaller, callerAfter), (MainProject, PathMain, extensions));

        // "Foo" is deliberately not in AffectedNames: only the added-using document path brings the call into the capture.
        var result = await CallBindingCheck.CheckAsync(original, trial, new CallBindingScope(new[] { "Bar" }, new[] { PathCaller }));

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Discrepancies));
        Assert.That(result.Discrepancies, Has.Count.EqualTo(1));
        var discrepancy = result.Discrepancies[0];
        Assert.That(discrepancy.Kind, Is.EqualTo(CallBindingDiscrepancyKind.BindsElsewhere));
        Assert.That(discrepancy.Name, Is.EqualTo("Foo"));
        Assert.That(discrepancy.FilePath, Is.EqualTo(PathCaller));
        Assert.That(discrepancy.CallText, Is.EqualTo("target.Foo()"));
        Assert.That(discrepancy.ExpectedIds, Is.EqualTo(new[] { "M:A.ExtA.Foo(System.Object)" }));
        Assert.That(discrepancy.WouldBind, Does.Contain("ExtB"));
    }

    [Test]
    public async Task AddedUsing_WithoutDocumentPath_NotDetectedAsync()
    {
        using var workspace = new AdhocWorkspace();
        var extensions = Lines(
            "namespace A { public static class ExtA { public static void Foo(this object o) { } } }",
            "namespace B { public static class ExtB { public static void Foo(this N.Target t) { } } }");
        var callerBefore = Lines(
            "using A;",
            "namespace N { public class Target { } public class Caller { public void Run(Target target) { target.Foo(); } } }");
        var callerAfter = Lines(
            "using A;",
            "using B;",
            "namespace N { public class Target { } public class Caller { public void Run(Target target) { target.Foo(); } } }");
        var original = Build(workspace, (MainProject, PathCaller, callerBefore), (MainProject, PathMain, extensions));
        var trial = Build(workspace, (MainProject, PathCaller, callerAfter), (MainProject, PathMain, extensions));

        var result = await CallBindingCheck.CheckAsync(original, trial, new CallBindingScope(new[] { "Bar" }));

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Clean));
        Assert.That(result.Discrepancies, Is.Empty);
        Assert.That(result.SitesChecked, Is.EqualTo(0));
    }

    [Test]
    public async Task OccurrenceCountChangedInMember_ReportedAsMismatchAsync()
    {
        using var workspace = new AdhocWorkspace();
        var original = Build(workspace, (MainProject, PathMain, Source("public void Save(long x) { }", "Target target", "target.Save(1);")));
        var trial = Build(workspace, (MainProject, PathMain, Source("public void Save(long x) { }", "Target target", "target.Save(1);", "target.Save(2);")));

        var result = await CallBindingCheck.CheckAsync(original, trial, SaveScope);

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Discrepancies));
        Assert.That(result.Discrepancies, Has.Count.EqualTo(1));
        var discrepancy = result.Discrepancies[0];
        Assert.That(discrepancy.Kind, Is.EqualTo(CallBindingDiscrepancyKind.OccurrenceCountMismatch));
        Assert.That(discrepancy.Name, Is.EqualTo("Save"));
        Assert.That(discrepancy.EnclosingMemberId, Is.EqualTo("M:N.Caller.Run(N.Target)"));
        Assert.That(discrepancy.Detail, Does.Contain("appears 1 time(s) in this member today and 2 afterwards"));
    }

    [Test]
    public async Task ExtraOccurrenceInAddedMember_IgnoredOnlyWhenDeclaredAsync()
    {
        using var workspace = new AdhocWorkspace();
        var original = Build(workspace, (MainProject, PathMain, Source("public void Save(long x) { }", "Target target", "target.Save(1);")));
        var trial = Build(workspace, (MainProject, PathMain, Lines(
            "namespace N",
            "{",
            "    public class Target { public void Save(long x) { } }",
            "    public class Caller",
            "    {",
            "        public void Run(Target target) { target.Save(1); }",
            "        public void Extra(Target target) { target.Save(3); }",
            "    }",
            "}")));
        var declared = new CallBindingExpectations { AddedMemberIds = new[] { "M:N.Caller.Extra(N.Target)" } };

        var declaredResult = await CallBindingCheck.CheckAsync(original, trial, SaveScope, declared);
        var undeclaredResult = await CallBindingCheck.CheckAsync(original, trial, SaveScope);

        Assert.That(declaredResult.Status, Is.EqualTo(CallBindingCheckStatus.Clean));
        Assert.That(declaredResult.Discrepancies, Is.Empty);
        Assert.That(undeclaredResult.Status, Is.EqualTo(CallBindingCheckStatus.Discrepancies));
        Assert.That(undeclaredResult.Discrepancies, Has.Count.EqualTo(1));
        var discrepancy = undeclaredResult.Discrepancies[0];
        Assert.That(discrepancy.Kind, Is.EqualTo(CallBindingDiscrepancyKind.UnexpectedInTrial));
        Assert.That(discrepancy.EnclosingMemberId, Is.EqualTo("M:N.Caller.Extra(N.Target)"));
        Assert.That(discrepancy.CallText, Is.EqualTo("target.Save(3)"));
    }

    [Test]
    public async Task ThrowingExpectationCallback_FailsClosedWithPathFreeReasonAsync()
    {
        using var workspace = new AdhocWorkspace();
        var source = Source("public void Save(long x) { }", "Target target", "target.Save(1);");
        var original = Build(workspace, (MainProject, PathMain, source));
        var trial = Build(workspace, (MainProject, PathMain, source));
        var expectations = new CallBindingExpectations
        {
            ExpectedTargetsForSite = _ => throw new InvalidOperationException("boom in C:\\secret\\Main.cs"),
        };

        var result = await CallBindingCheck.CheckAsync(original, trial, SaveScope, expectations);

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.CheckFailed));
        Assert.That(result.Discrepancies, Is.Empty);
        Assert.That(result.FailureReason, Is.EqualTo("the comparison raised InvalidOperationException"));
        Assert.That(result.FailureReason, Does.Not.Contain("boom").And.Not.Contain("secret").And.Not.Contain("\\").And.Not.Contain(".cs"));
        Assert.That(CallBindingCheck.FormatRefusal(result, "move 'Save' to 'Target'"), Does.Contain("could not be verified"));
    }

    [Test]
    public void IncompleteSnapshot_FailsClosedThroughCompare()
    {
        var complete = new CallBindingSnapshot(Array.Empty<CallBindingObservation>(), Array.Empty<string>());
        var incomplete = new CallBindingSnapshot(Array.Empty<CallBindingObservation>(), new[] { "a document of project 'P' has no semantic model" });

        var result = CallBindingCheck.Compare(complete, incomplete);

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.CheckFailed));
        Assert.That(result.IsRefusal, Is.True);
        Assert.That(result.FailureReason, Does.Contain("could not be captured completely"));
        Assert.That(CallBindingCheck.FormatRefusal(result, "move 'Save' to 'Target'"), Does.StartWith("Cannot move 'Save' to 'Target'"));
    }

    [Test]
    public async Task CancelledToken_PropagatesOperationCanceledAsync()
    {
        using var workspace = new AdhocWorkspace();
        var solution = Build(workspace, (MainProject, PathMain, Source("public void Save(long x) { }", "Target target", "target.Save(1);")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var thrown = await Assert.CatchAsync<OperationCanceledException>(() => CallBindingCheck.CheckAsync(solution, solution, SaveScope, null, cancellation.Token));

        Assert.That(thrown, Is.Not.Null);
    }

    [Test]
    public async Task Constructor_ExplicitNew_RebindingReportedAsNewTypeNameAsync()
    {
        using var workspace = new AdhocWorkspace();
        var original = Build(workspace, (MainProject, PathMain, Lines(
            "namespace N",
            "{",
            "    public class Target { public Target(long x) { } }",
            "    public class Caller { public void Run() { var t = new Target(1); } }",
            "}")));
        var trial = Build(workspace, (MainProject, PathMain, Lines(
            "namespace N",
            "{",
            "    public class Target { public Target(long x) { } public Target(int x) { } }",
            "    public class Caller { public void Run() { var t = new Target(1); } }",
            "}")));

        var result = await CallBindingCheck.CheckAsync(original, trial, new CallBindingScope(new[] { "Target" }));

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Discrepancies));
        Assert.That(result.Discrepancies, Has.Count.EqualTo(1));
        var discrepancy = result.Discrepancies[0];
        Assert.That(discrepancy.Kind, Is.EqualTo(CallBindingDiscrepancyKind.BindsElsewhere));
        Assert.That(discrepancy.Name, Is.EqualTo("new Target"));
        Assert.That(discrepancy.CallText, Is.EqualTo("new Target(1)"));
        Assert.That(discrepancy.EnclosingMemberId, Is.EqualTo("M:N.Caller.Run"));
        Assert.That(discrepancy.ExpectedIds, Is.EqualTo(new[] { "M:N.Target.#ctor(System.Int64)" }));
    }

    [Test]
    public async Task Constructor_TargetTypedNew_RebindingReportedAsNewTypeNameAsync()
    {
        using var workspace = new AdhocWorkspace();
        var original = Build(workspace, (MainProject, PathMain, Lines(
            "namespace N",
            "{",
            "    public class Target { public Target(long x) { } }",
            "    public class Caller { public void Run() { Target t = new(1); } }",
            "}")));
        var trial = Build(workspace, (MainProject, PathMain, Lines(
            "namespace N",
            "{",
            "    public class Target { public Target(long x) { } public Target(int x) { } }",
            "    public class Caller { public void Run() { Target t = new(1); } }",
            "}")));

        var result = await CallBindingCheck.CheckAsync(original, trial, new CallBindingScope(new[] { "Target" }));

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Discrepancies));
        Assert.That(result.Discrepancies, Has.Count.EqualTo(1));
        var discrepancy = result.Discrepancies[0];
        Assert.That(discrepancy.Kind, Is.EqualTo(CallBindingDiscrepancyKind.BindsElsewhere));
        Assert.That(discrepancy.Name, Is.EqualTo("new Target"));
        Assert.That(discrepancy.CallText, Is.EqualTo("new(1)"));
        Assert.That(discrepancy.EnclosingMemberId, Is.EqualTo("M:N.Caller.Run"));
        Assert.That(discrepancy.ExpectedIds, Is.EqualTo(new[] { "M:N.Target.#ctor(System.Int64)" }));
    }

    [Test]
    public async Task Constructor_BaseInitializer_RebindingReportedAsNewBaseAsync()
    {
        using var workspace = new AdhocWorkspace();
        var original = Build(workspace, (MainProject, PathMain, Lines(
            "namespace N",
            "{",
            "    public class Base { public Base(long x) { } }",
            "    public class Derived : Base { public Derived() : base(1) { } }",
            "}")));
        var trial = Build(workspace, (MainProject, PathMain, Lines(
            "namespace N",
            "{",
            "    public class Base { public Base(long x) { } public Base(int x) { } }",
            "    public class Derived : Base { public Derived() : base(1) { } }",
            "}")));

        var result = await CallBindingCheck.CheckAsync(original, trial, new CallBindingScope(new[] { "Base" }));

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Discrepancies));
        Assert.That(result.Discrepancies, Has.Count.EqualTo(1));
        var discrepancy = result.Discrepancies[0];
        Assert.That(discrepancy.Kind, Is.EqualTo(CallBindingDiscrepancyKind.BindsElsewhere));
        Assert.That(discrepancy.Name, Is.EqualTo("new Base"));
        Assert.That(discrepancy.CallText, Does.Contain("base(1)"));
        Assert.That(discrepancy.EnclosingMemberId, Is.EqualTo("M:N.Derived.#ctor"));
        Assert.That(discrepancy.ExpectedIds, Is.EqualTo(new[] { "M:N.Base.#ctor(System.Int64)" }));
    }

    [Test]
    public async Task FormatRefusal_DefaultCap_ShowsTenOfTwelveWithOverflowLineAsync()
    {
        using var workspace = new AdhocWorkspace();
        var original = Build(workspace, (MainProject, PathMain, Source(
            "public void Save(long x) { }",
            "Target target",
            Repeat("target.Save(1);", 12))));
        var trial = Build(workspace, (MainProject, PathMain, Source(
            "public void Save(long x) { } public void Save(int x) { }",
            "Target target",
            Repeat("target.Save(1);", 12))));

        var result = await CallBindingCheck.CheckAsync(original, trial, SaveScope);
        var text = CallBindingCheck.FormatRefusal(result, "move 'Save' to 'Target'");

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Discrepancies));
        Assert.That(result.Discrepancies, Has.Count.EqualTo(12));
        Assert.That(text, Does.Contain("Showing 10 of 12"));
        Assert.That(text, Does.Contain("... 2 more not shown."));
        Assert.That(text, Does.Not.Contain("11) "));
    }

    [Test]
    public async Task FormatRefusal_MaxListedTwo_ShowsTwoOfThreeAsync()
    {
        using var workspace = new AdhocWorkspace();
        var original = Build(workspace, (MainProject, PathMain, Source(
            "public void Save(long x) { }",
            "Target target",
            Repeat("target.Save(1);", 3))));
        var trial = Build(workspace, (MainProject, PathMain, Source(
            "public void Save(long x) { } public void Save(int x) { }",
            "Target target",
            Repeat("target.Save(1);", 3))));

        var result = await CallBindingCheck.CheckAsync(original, trial, SaveScope);
        var text = CallBindingCheck.FormatRefusal(result, "move 'Save' to 'Target'", maxListed: 2);

        Assert.That(result.Discrepancies, Has.Count.EqualTo(3));
        Assert.That(text, Does.Contain("Showing 2 of 3"));
        Assert.That(text, Does.Contain("... 1 more not shown."));
    }

    [Test]
    public async Task FormatRefusal_CleanResult_ReturnsEmptyTextAsync()
    {
        using var workspace = new AdhocWorkspace();
        var original = Build(workspace, (MainProject, PathMain, Source(
            "public void Save(long x) { }",
            "Target target",
            Repeat("target.Save(1);", 2))));
        var trial = Build(workspace, (MainProject, PathMain, Source(
            "public void Save(long x) { }",
            "Target target",
            Repeat("target.Save(1);", 2))));

        var result = await CallBindingCheck.CheckAsync(original, trial, SaveScope);
        var text = CallBindingCheck.FormatRefusal(result, "move 'Save' to 'Target'");

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.Clean));
        Assert.That(text, Is.Empty);
    }
}
