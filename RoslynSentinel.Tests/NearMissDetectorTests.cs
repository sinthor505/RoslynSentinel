// Step 3 of plan_server_slip_recovery: NearMissDetector.DetectAsync against in-memory fixtures. The committed solution is an
// InMemoryWorkspace (real metadata references, so CS1061 binds to real members). Each proposed file goes in `changes`, and the
// diagnostics are derived from the proposed text instead of a compile of it.
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("NearMissDetector")]
public class NearMissDetectorTests
{
    private const string ProjectName = "InMemoryProject";
    private const string ShippingKindsFile = "ShippingKinds.cs";
    private const string CheckoutFile = "OrderCheckout.cs";
    private const string OrderFile = "Order.cs";
    private const string InvoiceFile = "Invoice.cs";

    // Line 5 of every checkout text below is the member access under test.
    private const int SlipLine = 5;

    private const string ShippingKindsSource = """
        namespace ContosoOrders.Core;

        public enum ShippingKinds
        {
            Standard,
            Express,
            Overnight,
            Pickup,
        }
        """;

    private const string EnumCheckoutCommitted = """
        namespace ContosoOrders.Core;

        public class OrderCheckout
        {
            public ShippingKinds Pick() => ShippingKinds.Overnight;
        }
        """;

    private const string EnumCheckoutProposed = """
        namespace ContosoOrders.Core;

        public class OrderCheckout
        {
            public ShippingKinds Pick() => ShippingKinds.Oovernight;
        }
        """;

    private const string OrderSource = """
        namespace ContosoOrders.Core;

        public class Order
        {
            public int Total;
        }
        """;

    private const string PrivateTotalOrderSource = """
        namespace ContosoOrders.Core;

        public class Order
        {
            private int Total;
        }
        """;

    private const string InvoiceSource = """
        namespace ContosoOrders.Core;

        public class Invoice
        {
            public int Totl;
        }
        """;

    private const string InstanceCheckoutCommitted = """
        namespace ContosoOrders.Core;

        public class OrderCheckout
        {
            public int Pick(Order order) => order.Total;
        }
        """;

    private const string CheckoutWithoutRead = """
        namespace ContosoOrders.Core;

        public class OrderCheckout
        {
            public int Pick(Order order) => 0;
        }
        """;

    private const string InstanceCheckoutProposed = """
        namespace ContosoOrders.Core;

        public class OrderCheckout
        {
            public int Pick(Order order) => order.Totl;
        }
        """;

    private const string FarCheckoutProposed = """
        namespace ContosoOrders.Core;

        public class OrderCheckout
        {
            public int Pick(Order order) => order.Zebra;
        }
        """;

    // `using static` brings the enum members into scope as bare names, so Overnight resolves unqualified and Oovernight is CS0103.
    private const string BareNameCheckoutCommitted = """
        using static ContosoOrders.Core.ShippingKinds;
        namespace ContosoOrders.Core;
        public class OrderCheckout
        {
            public ShippingKinds Pick() => Overnight;
        }
        """;

    private const string BareNameCheckoutProposed = """
        using static ContosoOrders.Core.ShippingKinds;
        namespace ContosoOrders.Core;
        public class OrderCheckout
        {
            public ShippingKinds Pick() => Oovernight;
        }
        """;

    [Test]
    public async Task DetectAsync_EnumMemberSlip_ReturnsQualifiedReplacementAndCorrectedLineAsync()
    {
        using var workspace = InMemoryWorkspace.Create((ShippingKindsFile, ShippingKindsSource), (CheckoutFile, EnumCheckoutCommitted));
        var diagnostic = MemberError(workspace, CheckoutFile, EnumCheckoutProposed, "CS0117", "Oovernight");

        var result = await RunDetectorAsync(workspace, CheckoutFile, EnumCheckoutProposed, [diagnostic]);

        Assert.That(result, Has.Count.EqualTo(1));
        var item = result[0];
        Assert.Multiple(() =>
        {
            Assert.That(item.Replacement, Is.EqualTo("ShippingKinds.Overnight"));
            Assert.That(item.CorrectedLine, Does.Contain("ShippingKinds.Overnight"));
            Assert.That(item.CorrectedLine, Does.Not.Contain("Oovernight"));
            Assert.That(item.WrongNameDeclaredElsewhere, Is.False);
        });
    }

    [Test]
    public async Task DetectAsync_InstanceMemberSlip_Cs1061_ReturnsTheClosestInstanceMemberAsync()
    {
        using var workspace = InMemoryWorkspace.Create((OrderFile, OrderSource), (CheckoutFile, InstanceCheckoutCommitted));
        var diagnostic = MemberError(workspace, CheckoutFile, InstanceCheckoutProposed, "CS1061", "Totl");

        var result = await RunDetectorAsync(workspace, CheckoutFile, InstanceCheckoutProposed, [diagnostic]);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Replacement, Is.EqualTo("order.Total"));
    }

    [Test]
    public async Task DetectAsync_NoCloseName_ReturnsEmptyAsync()
    {
        using var workspace = InMemoryWorkspace.Create((OrderFile, OrderSource), (CheckoutFile, InstanceCheckoutCommitted));
        var diagnostic = MemberError(workspace, CheckoutFile, FarCheckoutProposed, "CS1061", "Zebra");

        var result = await RunDetectorAsync(workspace, CheckoutFile, FarCheckoutProposed, [diagnostic]);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_PrivateMemberOfAnotherType_IsNotACandidateAsync()
    {
        // The only name close to "Totl" is Order.Total, which is private and so inaccessible from OrderCheckout.
        using var workspace = InMemoryWorkspace.Create((OrderFile, PrivateTotalOrderSource), (CheckoutFile, CheckoutWithoutRead));
        var diagnostic = MemberError(workspace, CheckoutFile, InstanceCheckoutProposed, "CS1061", "Totl");

        var result = await RunDetectorAsync(workspace, CheckoutFile, InstanceCheckoutProposed, [diagnostic]);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_WrongNameDeclaredOnAnotherType_SetsDeclaredElsewhereAsync()
    {
        // "Totl" is declared on Invoice, an unrelated type, while the receiver Order has the close name Total.
        using var workspace = InMemoryWorkspace.Create((OrderFile, OrderSource), (InvoiceFile, InvoiceSource), (CheckoutFile, InstanceCheckoutCommitted));
        var diagnostic = MemberError(workspace, CheckoutFile, InstanceCheckoutProposed, "CS1061", "Totl");

        var result = await RunDetectorAsync(workspace, CheckoutFile, InstanceCheckoutProposed, [diagnostic]);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(result[0].Replacement, Is.EqualTo("order.Total"));
            Assert.That(result[0].WrongNameDeclaredElsewhere, Is.True);
        });
    }

    [Test]
    public async Task DetectAsync_NoMemberDiagnostics_ReturnsEmptyAsync()
    {
        // Only CS0246 and a CS0103 that is the Name of a member access (not a bare name) are present, so the detector has nothing to bind.
        using var workspace = InMemoryWorkspace.Create((ShippingKindsFile, ShippingKindsSource), (CheckoutFile, EnumCheckoutCommitted));
        var diagnostics = new[]
        {
            MemberError(workspace, CheckoutFile, EnumCheckoutProposed, "CS0103", "Oovernight"),
            MemberError(workspace, CheckoutFile, EnumCheckoutProposed, "CS0246", "Oovernight"),
        };

        var result = await RunDetectorAsync(workspace, CheckoutFile, EnumCheckoutProposed, diagnostics);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_Cs0103BareNameSlip_ReturnsTheNameInScopeAsync()
    {
        // The diagnostic is built by hand like the member ones (MemberError), not taken from a compile of the proposed text.
        using var workspace = InMemoryWorkspace.Create((ShippingKindsFile, ShippingKindsSource), (CheckoutFile, BareNameCheckoutCommitted));
        var diagnostic = MemberError(workspace, CheckoutFile, BareNameCheckoutProposed, "CS0103", "Oovernight");

        var result = await RunDetectorAsync(workspace, CheckoutFile, BareNameCheckoutProposed, [diagnostic]);

        Assert.That(result, Has.Count.EqualTo(1));
        var item = result[0];
        Assert.Multiple(() =>
        {
            Assert.That(item.ReceiverType, Is.Null);
            Assert.That(item.Replacement, Is.EqualTo("Overnight"));
            Assert.That(item.CorrectedLine, Does.Contain("Overnight"));
            Assert.That(item.CorrectedLine, Does.Not.Contain("Oovernight"));
            Assert.That(item.Candidates, Is.Not.Empty);
        });
    }

    // BuildCorrection is pure, so these tests hand it items and diagnostics built from the proposed text; no workspace is involved.
    private static readonly FilePathWrapper CorrectionFile = new(@"C:\Contoso\OrderCheckout.cs");

    private static readonly string[] EnumMemberNames = ["Standard", "Express", "Overnight", "Pickup"];

    [Test]
    public void BuildCorrection_UniqueNearMiss_ReturnsTheCorrectedChangesAndSpans()
    {
        var item = CorrectionItem("CS0117", EnumCheckoutProposed, "Oovernight", declaredElsewhere: false, EnumMemberNames);
        var proposed = new Dictionary<FilePathWrapper, string> { [CorrectionFile] = EnumCheckoutProposed };

        var correction = NearMissDetector.BuildCorrection(proposed, [CorrectionError("CS0117", EnumCheckoutProposed, "Oovernight")], [item], "ReplaceSnippet", NearMissNameDisplay.Show);

        Assert.That(correction, Is.Not.Null);
        var offset = EnumCheckoutProposed.IndexOf("Oovernight", StringComparison.Ordinal);
        Assert.Multiple(() =>
        {
            Assert.That(correction!.CorrectedChanges[CorrectionFile], Is.EqualTo(EnumCheckoutProposed.Replace("Oovernight", "Overnight", StringComparison.Ordinal)));
            Assert.That(proposed[CorrectionFile], Is.EqualTo(EnumCheckoutProposed), "the caller's changes are not mutated");
            var span = correction.CorrectionSpans[CorrectionFile].Single();
            Assert.That(span.Span.Start, Is.EqualTo(offset));
            Assert.That(span.Span.Length, Is.EqualTo("Oovernight".Length));
            Assert.That(span.NewText, Is.EqualTo("Overnight"));
            var substitution = correction.Substitutions.Single();
            Assert.That(substitution, Is.EqualTo(new NearMissSubstitution("ReplaceSnippet", CorrectionFile.ToString(), SlipLine, "Oovernight", "Overnight", "CS0117", 1, "show", true)));
            Assert.That(correction.Findings.Single().Message, Does.Contain("'Oovernight' -> 'Overnight'").And.Contain("ShippingKinds.Overnight"));
        });
    }

    [Test]
    public void BuildCorrection_HideMode_FindingStatesOnlyTheCorrectedLine()
    {
        var item = CorrectionItem("CS0117", EnumCheckoutProposed, "Oovernight", declaredElsewhere: false, EnumMemberNames);

        var correction = NearMissDetector.BuildCorrection(
            new Dictionary<FilePathWrapper, string> { [CorrectionFile] = EnumCheckoutProposed },
            [CorrectionError("CS0117", EnumCheckoutProposed, "Oovernight")], [item], "ReplaceSnippet", NearMissNameDisplay.Hide);

        Assert.That(correction, Is.Not.Null);
        var finding = correction!.Findings.Single().Message;
        Assert.Multiple(() =>
        {
            Assert.That(finding, Does.Not.Contain("Oovernight"));
            Assert.That(finding, Does.Contain("ShippingKinds.Overnight"));
            Assert.That(correction.Substitutions.Single().DisplayMode, Is.EqualTo("hide"));
        });
    }

    [Test]
    public void BuildCorrection_TwoCandidatesInRange_ReturnsNull()
    {
        // Both Tota and Total are one edit from Totl, so the correction is a guess.
        var proposed = InstanceCheckoutProposed;
        var item = CorrectionItem("CS1061", proposed, "Totl", declaredElsewhere: false, "Tota", "Total");

        var correction = NearMissDetector.BuildCorrection(
            new Dictionary<FilePathWrapper, string> { [CorrectionFile] = proposed }, [CorrectionError("CS1061", proposed, "Totl")], [item], "ReplaceSnippet", NearMissNameDisplay.Show);

        Assert.That(correction, Is.Null);
    }

    [Test]
    public void BuildCorrection_WrongNameDeclaredElsewhere_ReturnsNull()
    {
        var proposed = InstanceCheckoutProposed;
        var item = CorrectionItem("CS1061", proposed, "Totl", declaredElsewhere: true, "Total");

        var correction = NearMissDetector.BuildCorrection(
            new Dictionary<FilePathWrapper, string> { [CorrectionFile] = proposed }, [CorrectionError("CS1061", proposed, "Totl")], [item], "ReplaceSnippet", NearMissNameDisplay.Show);

        Assert.That(correction, Is.Null);
    }

    [Test]
    public void BuildCorrection_AnotherErrorInTheReport_ReturnsNull()
    {
        // The slip is a near miss, but the report also holds an error no item covers, so nothing is corrected.
        var item = CorrectionItem("CS0117", EnumCheckoutProposed, "Oovernight", declaredElsewhere: false, EnumMemberNames);
        var otherError = CorrectionError("CS0246", EnumCheckoutProposed, "ShippingKinds");

        var correction = NearMissDetector.BuildCorrection(
            new Dictionary<FilePathWrapper, string> { [CorrectionFile] = EnumCheckoutProposed },
            [CorrectionError("CS0117", EnumCheckoutProposed, "Oovernight"), otherError], [item], "ReplaceSnippet", NearMissNameDisplay.Show);

        Assert.That(correction, Is.Null);
    }

    [Test]
    public async Task BuildCorrection_ItemFromDetectAsync_CorrectsTheEnumSlipAsync()
    {
        using var workspace = InMemoryWorkspace.Create((ShippingKindsFile, ShippingKindsSource), (CheckoutFile, EnumCheckoutCommitted));
        var diagnostic = MemberError(workspace, CheckoutFile, EnumCheckoutProposed, "CS0117", "Oovernight");
        var items = await RunDetectorAsync(workspace, CheckoutFile, EnumCheckoutProposed, [diagnostic]);
        var path = new FilePathWrapper(workspace.PathOf(CheckoutFile));

        var correction = NearMissDetector.BuildCorrection(
            new Dictionary<FilePathWrapper, string> { [path] = EnumCheckoutProposed }, [diagnostic], items, "ReplaceSnippet", NearMissNameDisplay.Show);

        Assert.That(correction, Is.Not.Null);
        Assert.That(correction!.CorrectedChanges[path], Does.Contain("ShippingKinds.Overnight").And.Not.Contain("Oovernight"));
    }

    [Test]
    public void RecordAmbiguous_ActiveOptions_RecordsEachItemAsNotApplied()
    {
        var item = CorrectionItem("CS1061", InstanceCheckoutProposed, "Totl", declaredElsewhere: true, "Total");
        using var options = NearMissOptions.OverrideForCurrentContext(autoCorrect: true, NearMissNameDisplay.Show);
        using var scope = NearMissRecorder.Begin();

        NearMissDetector.RecordAmbiguous(new Dictionary<FilePathWrapper, string> { [CorrectionFile] = InstanceCheckoutProposed }, [item], "ReplaceSnippet", NearMissNameDisplay.Show);

        Assert.That(scope.Records.Single(), Is.EqualTo(new NearMissSubstitution("ReplaceSnippet", CorrectionFile.ToString(), SlipLine, "Totl", "Total", "CS1061", 1, "show", false)));
    }

    // The item the detector would produce for `wrongName` on SlipLine of `proposedText`, with the given candidate names.
    private static NearMissName CorrectionItem(string id, string proposedText, string wrongName, bool declaredElsewhere, params string[] candidateNames)
    {
        var column = SlipColumn(proposedText, wrongName);
        return new NearMissName(
            id, CorrectionFile.ToString(), SlipLine, column, ReceiverType: null, Replacement: candidateNames[0], CorrectedLine: null,
            declaredElsewhere,
            candidateNames.Select(name => new RoslynSentinel.Common.SymbolCandidate(name, "field", "Container", null, null, null, 0)).ToList());
    }

    private static DiagnosticInfo CorrectionError(string id, string proposedText, string wrongName)
    {
        var column = SlipColumn(proposedText, wrongName);
        return new DiagnosticInfo(id, "Error", $"{id}: '{wrongName}'", CorrectionFile, SlipLine, column, SlipLine, column + wrongName.Length, ProjectName);
    }

    private static int SlipColumn(string proposedText, string wrongName)
    {
        var sourceLine = proposedText.Split('\n')[SlipLine - 1].TrimEnd('\r');
        var column = sourceLine.IndexOf(wrongName, StringComparison.Ordinal) + 1;
        Assert.That(column, Is.GreaterThan(0), $"'{wrongName}' must appear on line {SlipLine}");
        return column;
    }

    // Compile-gate style diagnostic for `wrongName` on SlipLine of `proposedText`. The column is where the name starts on that
    // line, so the position is read from the text rather than counted by hand.
    private static DiagnosticInfo MemberError(InMemoryWorkspace workspace, string relativePath, string proposedText, string id, string wrongName)
    {
        var sourceLine = proposedText.Split('\n')[SlipLine - 1].TrimEnd('\r');
        var column = sourceLine.IndexOf(wrongName, StringComparison.Ordinal) + 1;
        Assert.That(column, Is.GreaterThan(0), $"'{wrongName}' must appear on line {SlipLine}");
        return new DiagnosticInfo(
            Id: id,
            Severity: "Error",
            Message: $"{id}: '{wrongName}'",
            FilePath: new FilePathWrapper(workspace.PathOf(relativePath)),
            StartLine: SlipLine,
            StartColumn: column,
            EndLine: SlipLine,
            EndColumn: column + wrongName.Length,
            Project: ProjectName,
            SourceLine: sourceLine);
    }

    // Runs the detector with the committed workspace as the baseline and `proposedText` as the one changed file.
    private static Task<IReadOnlyList<NearMissName>> RunDetectorAsync(InMemoryWorkspace workspace, string relativePath, string proposedText, IReadOnlyList<DiagnosticInfo> diagnostics)
    {
        var changes = new Dictionary<FilePathWrapper, string>
        {
            [new FilePathWrapper(workspace.PathOf(relativePath))] = proposedText,
        };
        return NearMissDetector.DetectAsync(workspace.Manager.CurrentSolution!, changes, diagnostics, CancellationToken.None);
    }
}
