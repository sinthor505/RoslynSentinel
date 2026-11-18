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
        // Only CS0103 and CS0246 are present, so the detector has no member diagnostic to bind.
        using var workspace = InMemoryWorkspace.Create((ShippingKindsFile, ShippingKindsSource), (CheckoutFile, EnumCheckoutCommitted));
        var diagnostics = new[]
        {
            MemberError(workspace, CheckoutFile, EnumCheckoutProposed, "CS0103", "Oovernight"),
            MemberError(workspace, CheckoutFile, EnumCheckoutProposed, "CS0246", "Oovernight"),
        };

        var result = await RunDetectorAsync(workspace, CheckoutFile, EnumCheckoutProposed, diagnostics);

        Assert.That(result, Is.Empty);
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
