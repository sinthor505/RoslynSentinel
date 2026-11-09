// Slice E2b (proposal_semantic_edit_impact_diagnostics.md): the edit-impact text in the ReplaceSnippet gate rejection, the
// CreateFile already-exists hint naming only callable tools, and the note on a not-found oldContent that was copied from
// the model's own rejected newContent (eval run 20261109-202110-210). Runs on InMemoryWorkspace (real compile gate, no
// disk writes) except the CreateFile case, which needs a real file because that tool checks File.Exists.
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("EditImpactGate")]
public class RejectedSnippetEditTests
{
    private const string PricingRelativePath = "ContosoOrders.Core/OrderPricingCalculator.cs";
    private const string CheckoutRelativePath = "ContosoOrders.Core/OrderCheckout.cs";

    private const string UntouchedSentence = "this edit did not change";
    private const string RejectedOriginNote = "comes from your rejected edit's newContent";

    private const string OldDeclaration = "public decimal CalcDisc(decimal a) => a;";
    private const string RenamedDeclaration = "public decimal CalculateDiscountedTotal(decimal a) => a;";

    private const string PricingSource = """
    namespace ContosoOrders.Core;

    public class OrderPricingCalculator
    {
        public decimal CalcDisc(decimal a) => a;
    }
    """;

    private const string CheckoutSource = """
    namespace ContosoOrders.Core;

    class OrderCheckout
    {
        decimal F(OrderPricingCalculator c) => c.CalcDisc(1m);
    }
    """;

    private static InMemoryWorkspace CreateWorkspace() =>
        InMemoryWorkspace.Create((PricingRelativePath, PricingSource), (CheckoutRelativePath, CheckoutSource));

    private static WorkspaceFileEditImpl CreateImpl(InMemoryWorkspace workspace, ActiveToolSurface? surface = null) =>
        new(
            workspace.Manager,
            TestTools.WorkspaceReadNavigationImpl(workspace.Manager),
            NullLogger.Instance,
            new ValidationEngine(workspace.Manager),
            TestEngines.SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed(),
            surface);

    private static Task<SentinelCallToolResult<ReplaceSnippetResult>> ReplaceAsync(
        WorkspaceFileEditImpl impl, InMemoryWorkspace workspace, string oldContent, string newContent) =>
        impl.ReplaceSnippet(
            new ToolCallReason("replace one snippet in the pricing calculator"),
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: oldContent,
            newContent: newContent);

    [Test]
    public async Task ReplaceSnippet_DeclarationOnlyRename_RejectionCarriesTheRenameSymbolLineAndNoUntouchedSentenceAsync()
    {
        using var workspace = CreateWorkspace();
        var originalPricing = workspace.ReadText(PricingRelativePath);

        var result = await ReplaceAsync(CreateImpl(workspace), workspace, OldDeclaration, RenamedDeclaration);

        Assert.That(result.IsError, Is.True, "a declaration-only rename must be rejected by the compile gate");
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("This edit renames CalcDisc to CalculateDiscountedTotal"), message);
            Assert.That(message, Does.Contain("RenameSymbol"));
            Assert.That(message, Does.Contain("OrderCheckout.cs"));
            Assert.That(message, Does.Not.Contain(UntouchedSentence), "the analyzer text already names the untouched caller");
            Assert.That(workspace.ReadText(PricingRelativePath), Is.EqualTo(originalPricing), "a rejected edit must not write the file");
        });
    }

    [Test]
    public async Task ReplaceSnippetBatch_DeclarationOnlyRename_RejectionCarriesTheRenameSymbolLineAndNoUntouchedSentenceAsync()
    {
        using var workspace = CreateWorkspace();

        var result = await CreateImpl(workspace).ReplaceSnippet(
            new ToolCallReason("rename the discount method through a batch edit"),
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = workspace.PathOf(PricingRelativePath), OldContent = OldDeclaration, NewContent = RenamedDeclaration },
            ]);

        Assert.That(result.IsError, Is.True);
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("This edit renames CalcDisc to CalculateDiscountedTotal"), message);
            Assert.That(message, Does.Contain("RenameSymbol"));
            Assert.That(message, Does.Not.Contain(UntouchedSentence));
        });
    }

    [Test]
    public async Task CreateFile_AlreadyExistsWithOnlyEditSnippetsCallable_NamesEditSnippetsNotReplaceSnippetAsync()
    {
        using var workspace = CreateWorkspace();
        // The EditSnippets eval arm: ReplaceSnippet is hidden, EditSnippets (same code behind it) and Member are active.
        var surface = new ActiveToolSurface(
            "test",
            new HashSet<string>(),
            new HashSet<string>(),
            new HashSet<string>(),
            new HashSet<string> { "EditSnippetsTools", "RefactoringStructuralTools" },
            allowedToolNames: new HashSet<string> { "EditSnippets", "Member" });
        var existing = Path.Combine(Path.GetTempPath(), "RoslynSentinelE2b-" + Guid.NewGuid().ToString("n") + ".txt");
        await File.WriteAllTextAsync(existing, "x");
        try
        {
            var result = await CreateImpl(workspace, surface).CreateFile(new ToolCallReason("create a file that already exists"), existing, content: "y");

            Assert.That(result.IsError, Is.True);
            var message = result.ErrorData?.Message ?? string.Empty;
            Assert.Multiple(() =>
            {
                Assert.That(message, Does.Contain("already exists"), message);
                Assert.That(message, Does.Contain("EditSnippets"));
                Assert.That(message, Does.Not.Contain("ReplaceSnippet"));
            });
        }
        finally
        {
            File.Delete(existing);
        }
    }

    [Test]
    public async Task CreateFile_AlreadyExistsWithNoSurface_KeepsNamingReplaceSnippetAsync()
    {
        using var workspace = CreateWorkspace();
        var existing = Path.Combine(Path.GetTempPath(), "RoslynSentinelE2b-" + Guid.NewGuid().ToString("n") + ".txt");
        await File.WriteAllTextAsync(existing, "x");
        try
        {
            var result = await CreateImpl(workspace).CreateFile(new ToolCallReason("create a file that already exists"), existing, content: "y");

            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData?.Message ?? string.Empty, Does.Contain("ReplaceSnippet"));
        }
        finally
        {
            File.Delete(existing);
        }
    }

    [Test]
    public async Task ReplaceSnippet_OldContentTakenFromRejectedNewContent_NotFoundCarriesTheNoteAsync()
    {
        using var workspace = CreateWorkspace();
        var impl = CreateImpl(workspace);

        var rejected = await ReplaceAsync(impl, workspace, OldDeclaration, RenamedDeclaration);
        Assert.That(rejected.IsError, Is.True, "precondition: the first edit must be rejected by the gate");

        // The model believes the rename applied and anchors its next edit on the renamed declaration.
        var second = await ReplaceAsync(impl, workspace, RenamedDeclaration, "public decimal CalculateDiscountedTotal(decimal a) => a + 1;");

        Assert.That(second.IsError, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(second.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), second.ErrorData.Message);
            Assert.That(second.ErrorData.Message, Does.Contain(RejectedOriginNote));
            Assert.That(second.ErrorData.Message, Does.Contain("read it again before editing"));
        });
    }

    [Test]
    public async Task ReplaceSnippetBatch_OldContentTakenFromRejectedNewContent_NotFoundCarriesTheNoteAsync()
    {
        using var workspace = CreateWorkspace();
        var impl = CreateImpl(workspace);

        var rejected = await ReplaceAsync(impl, workspace, OldDeclaration, RenamedDeclaration);
        Assert.That(rejected.IsError, Is.True, "precondition: the first edit must be rejected by the gate");

        var second = await impl.ReplaceSnippet(
            new ToolCallReason("anchor a batch edit on the rejected text"),
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = workspace.PathOf(PricingRelativePath), OldContent = RenamedDeclaration, NewContent = "public decimal CalculateDiscountedTotal(decimal a) => a + 1;" },
            ]);

        Assert.That(second.IsError, Is.True);
        Assert.That(second.ErrorData!.Message, Does.Contain(RejectedOriginNote), second.ErrorData.Message);
    }

    [Test]
    public async Task ReplaceSnippet_OrdinaryTypoInOldContent_NotFoundHasNoRejectedEditNoteAsync()
    {
        using var workspace = CreateWorkspace();
        var impl = CreateImpl(workspace);

        var rejected = await ReplaceAsync(impl, workspace, OldDeclaration, RenamedDeclaration);
        Assert.That(rejected.IsError, Is.True, "precondition: a rejected edit is on record for this file");

        var typo = await ReplaceAsync(impl, workspace, "public decimal CalcDicc(decimal a) => a;", "public decimal CalcDisc(decimal a) => a * 2;");

        Assert.That(typo.IsError, Is.True);
        Assert.That(typo.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), typo.ErrorData.Message);
        Assert.That(typo.ErrorData.Message, Does.Not.Contain(RejectedOriginNote));
    }

    [Test]
    public async Task ReplaceSnippet_NoRejectedEditOnRecord_NotFoundHasNoNoteAsync()
    {
        using var workspace = CreateWorkspace();

        var result = await ReplaceAsync(CreateImpl(workspace), workspace, RenamedDeclaration, "public decimal X(decimal a) => a;");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Not.Contain(RejectedOriginNote));
    }

    [Test]
    public async Task ReplaceSnippet_SuccessfulWriteClearsTheRejectedEditMemoryForTheFileAsync()
    {
        using var workspace = CreateWorkspace();
        var impl = CreateImpl(workspace);
        var rejected = await ReplaceAsync(impl, workspace, OldDeclaration, RenamedDeclaration);
        Assert.That(rejected.IsError, Is.True, "precondition: a rejected edit is on record for this file");

        var ok = await ReplaceAsync(impl, workspace, OldDeclaration, "public decimal CalcDisc(decimal a) => a * 2;");
        Assert.That(ok.IsError, Is.False, ok.ErrorData?.Message);

        // The rejected newContent is gone from memory, so the same stale anchor now gets the plain not-found text.
        var stale = await ReplaceAsync(impl, workspace, RenamedDeclaration, "public decimal X(decimal a) => a;");

        Assert.That(stale.IsError, Is.True);
        Assert.That(stale.ErrorData!.Message, Does.Not.Contain(RejectedOriginNote));
    }
}
