using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

// Slice D2.2 of docs/current/proposals/proposal_instance_move_preview_position_map_classification.md: ValidationEngine
// exposes the candidate Solution it validated, and MoveBindCheck binds a reference in that trial text. The scenarios use a
// detached in-memory solution: class A declares the member in the baseline, the trial removes it from A and adds it to B,
// and the caller file is identical in both (so a trial offset in it equals the baseline offset).
[TestFixture]
public class MoveBindCheckTests
{
    private static readonly FilePathWrapper PathA = "C:\\x\\A.cs";
    private static readonly FilePathWrapper PathB = "C:\\x\\B.cs";
    private static readonly FilePathWrapper PathCaller = "C:\\x\\Caller.cs";

    private const string ATemplate = "namespace N\n{\n    public class A\n    {\n        MOVED\n        public int Stay() => 1;\n    }\n}\n";
    private const string BTemplate = "namespace N\n{\n    public class B\n    {\n        MOVED\n    }\n}\n";

    // '|' marks the offset to check; it is removed before the text is used.
    private const string CallerTemplate =
        "namespace N\n" +
        "{\n" +
        "    public class Other { public int Moved(int x) => x; }\n" +
        "    public class C\n" +
        "    {\n" +
        "        public int M(A a, B b, Other other)\n" +
        "        {\n" +
        "            BODY\n" +
        "        }\n" +
        "    }\n" +
        "}\n";

    private sealed record Scenario(Solution Baseline, Dictionary<FilePathWrapper, string> Changes, MovedMemberIdentity Identity, int Offset);

    private static async Task<Scenario> BuildScenarioAsync(AdhocWorkspace workspace, string movedDecl, string extraTrialBMembers, string callerBody)
    {
        var callerText = CallerTemplate.Replace("BODY", callerBody);
        var offset = callerText.IndexOf('|');
        if (offset >= 0)
        {
            callerText = callerText.Remove(offset, 1);
        }

        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Create(), "P", "P", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        var solution = workspace.CurrentSolution.AddProject(projectInfo);
        solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "A.cs", SourceText.From(ATemplate.Replace("MOVED", movedDecl)), filePath: PathA);
        solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "B.cs", SourceText.From(BTemplate.Replace("MOVED", string.Empty)), filePath: PathB);
        solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "Caller.cs", SourceText.From(callerText), filePath: PathCaller);

        var compilation = (await solution.GetProject(projectId)!.GetCompilationAsync())!;
        var movedName = movedDecl.Contains('(') ? movedDecl.Split('(')[0].Split(' ').Last() : movedDecl.Split(' ')[2];
        var movedSymbol = compilation.GetTypeByMetadataName("N.A")!.GetMembers(movedName).Single();
        var identity = MovedMemberIdentity.From(movedSymbol, compilation.GetTypeByMetadataName("N.B")!);

        var changes = new Dictionary<FilePathWrapper, string>
        {
            [PathA] = ATemplate.Replace("MOVED", string.Empty),
            [PathB] = BTemplate.Replace("MOVED", movedDecl + " " + extraTrialBMembers),
        };
        return new Scenario(solution, changes, identity, offset);
    }

    private static async Task<MoveBindCheckResult> BindAsync(string movedDecl, string extraTrialBMembers, string callerBody)
    {
        using var workspace = new AdhocWorkspace();
        var scenario = await BuildScenarioAsync(workspace, movedDecl, extraTrialBMembers, callerBody);
        var outcome = await ValidationEngine.ValidateChangesWithCandidateAsync(scenario.Baseline, scenario.Changes);
        return await MoveBindCheck.CheckAsync(outcome.Candidate, PathCaller, scenario.Offset, scenario.Identity);
    }

    [Test]
    public async Task ValidateChangesWithCandidate_ReturnsCandidateWhoseTextEqualsValidatedTextAsync()
    {
        using var workspace = new AdhocWorkspace();
        var scenario = await BuildScenarioAsync(workspace, "public int Moved(int x) => x;", string.Empty, "return b.Moved(1);");

        var outcome = await ValidationEngine.ValidateChangesWithCandidateAsync(scenario.Baseline, scenario.Changes);

        foreach (var (path, validatedText) in scenario.Changes)
        {
            var documentId = outcome.Candidate.GetDocumentIdsWithFilePath(path).Single();
            var candidateText = (await outcome.Candidate.GetDocument(documentId)!.GetTextAsync()).ToString();
            Assert.That(candidateText, Is.EqualTo(validatedText), $"candidate text for {path}");
        }

        // The untouched caller file is carried into the candidate unchanged, and the baseline is not mutated.
        var callerId = outcome.Candidate.GetDocumentIdsWithFilePath(PathCaller).Single();
        var baselineCaller = (await scenario.Baseline.GetDocument(callerId)!.GetTextAsync()).ToString();
        Assert.That((await outcome.Candidate.GetDocument(callerId)!.GetTextAsync()).ToString(), Is.EqualTo(baselineCaller));
        Assert.That((await scenario.Baseline.GetDocument(scenario.Baseline.GetDocumentIdsWithFilePath(PathA).Single())!.GetTextAsync()).ToString(), Does.Contain("Moved"));
    }

    [Test]
    public async Task ValidateChangesWithCandidate_ReportMatchesValidateChangesAsyncAsync()
    {
        using var workspace = new AdhocWorkspace();
        // The trial leaves b.Moved(1) bound, so there is no new error; a second run breaks the caller to get a non-empty report.
        var ok = await BuildScenarioAsync(workspace, "public int Moved(int x) => x;", string.Empty, "return b.Moved(1);");
        var okOutcome = await ValidationEngine.ValidateChangesWithCandidateAsync(ok.Baseline, ok.Changes);
        var okReport = await ValidationEngine.ValidateChangesAsync(ok.Baseline, ok.Changes);
        Assert.That(okOutcome.Report.IsError, Is.EqualTo(okReport.IsError));
        Assert.That(okOutcome.Report.Diagnostics.Count, Is.EqualTo(okReport.Diagnostics.Count));

        var broken = new Dictionary<FilePathWrapper, string> { [PathA] = "namespace N { public class A { public int Stay() => missing; } }" };
        var brokenOutcome = await ValidationEngine.ValidateChangesWithCandidateAsync(ok.Baseline, broken);
        var brokenReport = await ValidationEngine.ValidateChangesAsync(ok.Baseline, broken);
        Assert.That(brokenReport.IsError, Is.True);
        Assert.That(brokenOutcome.Report.IsError, Is.True);
        Assert.That(brokenOutcome.Report.Diagnostics.Count, Is.EqualTo(brokenReport.Diagnostics.Count));
    }

    [Test]
    public async Task Check_SingleLineCall_BindsToMovedMemberAsync()
    {
        var result = await BindAsync("public int Moved(int x) => x;", string.Empty, "return b.|Moved(1);");

        Assert.That(result.Outcome, Is.EqualTo(MoveBindOutcome.BindsToMovedMember), result.Note);
        Assert.That(result.BoundSymbol?.ContainingType.Name, Is.EqualTo("B"));
    }

    [Test]
    public async Task Check_WrappedCallWithNamedArgumentOnOwnLine_BindsToMovedMemberAsync()
    {
        var result = await BindAsync(
            "public int Moved(int x, int y = 0) => x + y;",
            string.Empty,
            "return b.|Moved(\n                1,\n                y: 2);");

        Assert.That(result.Outcome, Is.EqualTo(MoveBindOutcome.BindsToMovedMember), result.Note);
        // The bound node is the whole invocation, so it spans the wrapped lines.
        Assert.That(result.BoundNodeSpan.Length, Is.GreaterThan("b.Moved(".Length + "y: 2)".Length));
    }

    [Test]
    public async Task Check_PropertyReference_BindsToMovedMemberAsync()
    {
        var result = await BindAsync("public int Count { get; set; }", string.Empty, "return b.|Count;");

        Assert.That(result.Outcome, Is.EqualTo(MoveBindOutcome.BindsToMovedMember), result.Note);
    }

    [Test]
    public async Task Check_CallBindingToDifferentOverload_ReportsOtherSymbolAsync()
    {
        var result = await BindAsync("public int Moved(int x) => x;", "public int Moved(string s) => 0;", "return b.|Moved(\"x\");");

        Assert.That(result.Outcome, Is.EqualTo(MoveBindOutcome.BindsToOtherSymbol), result.Note);
        Assert.That(result.BoundSymbol!.ToDisplayString(), Does.Contain("string"));
    }

    [Test]
    public async Task Check_CallBindingToSameSignatureOnDifferentType_ReportsOtherSymbolAsync()
    {
        var result = await BindAsync("public int Moved(int x) => x;", string.Empty, "return other.|Moved(1);");

        Assert.That(result.Outcome, Is.EqualTo(MoveBindOutcome.BindsToOtherSymbol), result.Note);
        Assert.That(result.BoundSymbol?.ContainingType.Name, Is.EqualTo("Other"));
    }

    [Test]
    public async Task Check_CallOnSourceTypeThatLostTheMember_ReportsBindFailedAsync()
    {
        var result = await BindAsync("public int Moved(int x) => x;", string.Empty, "return a.|Moved(1);");

        Assert.That(result.Outcome, Is.EqualTo(MoveBindOutcome.BindFailed), result.Note);
    }

    [Test]
    public async Task Check_NoApplicableOverload_ReportsAmbiguousWithCandidatesAsync()
    {
        var result = await BindAsync("public int Moved(int x, string s) => x;", "public int Moved(string s, int x) => 0;", "return b.|Moved(1, 2);");

        Assert.That(result.Outcome, Is.EqualTo(MoveBindOutcome.BindAmbiguous), result.Note);
        Assert.That(result.CandidateCount, Is.EqualTo(2));
        Assert.That(result.AnyCandidateIsMovedMember, Is.True);
    }

    [Test]
    public async Task Check_OffsetOnKeyword_ReportsNoReferenceAsync()
    {
        var result = await BindAsync("public int Moved(int x) => x;", string.Empty, "|return b.Moved(1);");

        Assert.That(result.Outcome, Is.EqualTo(MoveBindOutcome.NoReferenceAtOffset));
    }

    [Test]
    public async Task Check_OffsetOutsideDocument_ReportsNoReferenceAsync()
    {
        using var workspace = new AdhocWorkspace();
        var scenario = await BuildScenarioAsync(workspace, "public int Moved(int x) => x;", string.Empty, "return b.Moved(1);");
        var outcome = await ValidationEngine.ValidateChangesWithCandidateAsync(scenario.Baseline, scenario.Changes);

        var beyond = await MoveBindCheck.CheckAsync(outcome.Candidate, PathCaller, 100000, scenario.Identity);
        var negative = await MoveBindCheck.CheckAsync(outcome.Candidate, PathCaller, -1, scenario.Identity);

        Assert.That(beyond.Outcome, Is.EqualTo(MoveBindOutcome.NoReferenceAtOffset));
        Assert.That(negative.Outcome, Is.EqualTo(MoveBindOutcome.NoReferenceAtOffset));
    }

    [Test]
    public async Task Check_UnknownDocument_ReportsTrialDocumentNotFoundAsync()
    {
        using var workspace = new AdhocWorkspace();
        var scenario = await BuildScenarioAsync(workspace, "public int Moved(int x) => x;", string.Empty, "return b.Moved(1);");
        var outcome = await ValidationEngine.ValidateChangesWithCandidateAsync(scenario.Baseline, scenario.Changes);

        var result = await MoveBindCheck.CheckAsync(outcome.Candidate, "C:\\x\\Missing.cs", 0, scenario.Identity);

        Assert.That(result.Outcome, Is.EqualTo(MoveBindOutcome.TrialDocumentNotFound));
    }
}
