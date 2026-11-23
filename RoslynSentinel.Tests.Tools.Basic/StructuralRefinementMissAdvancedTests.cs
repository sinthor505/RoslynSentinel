using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Tools.Advanced;

// Not-found text of the StructuralRefinementEngine lookups that moved onto the shared helpers (plan_not_found_error_orientation, Step 5e slice 2).
// Each producer is driven through the tool that hosts it: SyncTypeAndFilenameAsync through SyncTypeAndFilename (file miss, type miss with ranked
// candidates, no-candidate and nested-only cases), the three SafeDeleteSymbolAsync overloads through DeleteUnusedSymbol (file miss on the symbolName
// form, symbol-name miss with candidates and without, the contextSnippet-matches-none miss, the line/column position miss, and the handle form whose
// symbol is declared in another file). A refused call writes nothing: the file text of every document is compared before and after.
// Not covered: the "No symbol could be resolved ... at the matched location" throw, because a declaration node picked by name always resolves to a symbol.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class StructuralRefinementMissAdvancedTests
{
    private const string OrderFile = "Order.cs";

    private const string OtherFile = "Other.cs";

    private const string Mismatched = "Mismatched.cs";

    private const string MismatchedSource = "namespace TestProj;\n\npublic class HelperResult\n{\n    public bool Ok { get; set; }\n}\n\npublic class MainService\n{\n    public int Id { get; set; }\n}\n";

    private const string OrderSource = """
        namespace TestProj;

        public class Order
        {
            public int Id { get; set; }

            private string BuildInternalDebugLabel()
            {
                return "debug";
            }

            public int Rate() => 42;
        }
        """;

    private const string OtherSource = """
        namespace TestProj;

        public class Other
        {
            public int Count { get; set; }
        }
        """;

    private const string TwinsSource = """
        namespace TestProj;

        public class First
        {
            public void Run() { }
        }

        public class Second
        {
            public void Run() { }
        }

        public class Third
        {
            public int Other;
        }
        """;

    private const string NestedSource = """
        namespace TestProj;

        public class Outer
        {
            public class Inner
            {
            }
        }
        """;

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
        Assert.That(message, Does.Not.Contain("Next:"), "No instruction prefix.");
    }

    // Runs one refused call over the given files: a NotFound error and every document untouched. Returns the error text.
    private static async Task<string> RunMissAsync(Func<InMemoryWorkspace, Task<(bool IsError, string? Code, string? Message)>> call, params (string Name, string Source)[] files)
    {
        using var workspace = InMemoryWorkspace.Create(files);
        var before = files.Select(f => workspace.ReadText(f.Name)).ToList();
        var (isError, code, message) = await call(workspace);
        Assert.That(isError, Is.True, message);
        Assert.That(code, Is.EqualTo(nameof(ToolErrorCode.NotFound)), message);
        Assert.That(files.Select(f => workspace.ReadText(f.Name)).ToList(), Is.EqualTo(before), "A refused lookup must not write any file.");
        return message ?? string.Empty;
    }

    private static async Task<(bool IsError, string? Code, string? Message)> SyncAsync(InMemoryWorkspace w, string relativePath, string? targetTypeName = null)
    {
        var r = await BatteryToolFactory.CreateRefactoringStructuralTools(w.Manager).SyncTypeAndFilenameTool(reason: "test message", w.PathOf(relativePath), targetTypeName: targetTypeName);
        return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
    }

    private static async Task<(bool IsError, string? Code, string? Message)> DeleteAsync(
        InMemoryWorkspace w, string relativePath, string? symbolName = null, string? contextSnippet = null, string projectName = "", string docCommentId = "", int line = 0, int column = 0)
    {
        var r = await Battery.Basic.TestTools.WorkspaceTools(w.Manager).DeleteUnusedSymbolTool(
            reason: "test message", w.PathOf(relativePath), projectName: projectName, docCommentId: docCommentId, symbolName: symbolName, contextSnippet: contextSnippet, line: line, column: column);
        return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
    }

    // ---- SyncTypeAndFilename: SyncTypeAndFilenameAsync ----

    [Test]
    [Category("StructuralRefinementEngine")]
    public async Task SyncTypeAndFilename_MisspeltFile_OffersTheNearFileAsync()
    {
        var message = await RunMissAsync(w => SyncAsync(w, "Orders.cs"), (OrderFile, OrderSource));
        Assert.That(message, Does.Contain("was not found in the loaded solution"));
        Assert.That(message, Does.Contain("Did you mean one of these?"));
        Assert.That(message, Does.Contain("Order.cs"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("StructuralRefinementEngine")]
    public async Task SyncTypeAndFilename_NoSimilarFile_StatesTheNoCandidateFactAsync()
    {
        var message = await RunMissAsync(w => SyncAsync(w, "Zzzqqq.cs"), (OrderFile, OrderSource));
        Assert.That(message, Does.Contain("No file in the solution has a similar name."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("StructuralRefinementEngine")]
    public async Task SyncTypeAndFilename_MisspeltTypeName_OffersTheNearTypeAndListsTheDeclaredTypesAsync()
    {
        var message = await RunMissAsync(w => SyncAsync(w, Mismatched, "MainServce"), (Mismatched, MismatchedSource));
        Assert.That(message, Does.Contain("Did you mean 'MainService'? Retry with targetTypeName: 'MainService'."));
        Assert.That(message, Does.Contain("Top-level types declared in"));
        Assert.That(message, Does.Contain("HelperResult, MainService"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("StructuralRefinementEngine")]
    public async Task SyncTypeAndFilename_NoSimilarTypeName_StatesTheNoCandidateFactAndOffersTheOutlineAsync()
    {
        var message = await RunMissAsync(w => SyncAsync(w, Mismatched, "Zzzqqq"), (Mismatched, MismatchedSource));
        Assert.That(message, Does.Contain("No top-level type declared in"));
        Assert.That(message, Does.Contain("has a similar name to 'Zzzqqq'."));
        Assert.That(message, Does.Contain("GetFileOutline"));
        Assert.That(message, Does.Contain("Top-level types declared in"));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("StructuralRefinementEngine")]
    public async Task SyncTypeAndFilename_TypeDeclaredOnlyNested_StatesItIsNestedAsync()
    {
        var message = await RunMissAsync(w => SyncAsync(w, "Outer.cs", "Inner"), ("Outer.cs", NestedSource));
        Assert.That(message, Does.Contain("is declared in"));
        Assert.That(message, Does.Contain("only as a nested type"));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- DeleteUnusedSymbol(symbolName): SafeDeleteSymbolAsync (name form) ----

    [Test]
    [Category("StructuralRefinementEngine")]
    public async Task DeleteUnusedSymbolByName_MisspeltFile_OffersTheNearFileAsync()
    {
        var message = await RunMissAsync(w => DeleteAsync(w, "Orders.cs", symbolName: "BuildInternalDebugLabel"), (OrderFile, OrderSource));
        Assert.That(message, Does.Contain("Did you mean one of these?"));
        Assert.That(message, Does.Contain("Order.cs"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("StructuralRefinementEngine")]
    public async Task DeleteUnusedSymbolByName_NoSimilarFile_StatesTheNoCandidateFactAsync()
    {
        var message = await RunMissAsync(w => DeleteAsync(w, "Zzzqqq.cs", symbolName: "BuildInternalDebugLabel"), (OrderFile, OrderSource));
        Assert.That(message, Does.Contain("No file in the solution has a similar name."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("StructuralRefinementEngine")]
    public async Task DeleteUnusedSymbolByName_MisspeltSymbol_OffersTheNearDeclaredNameAsync()
    {
        var message = await RunMissAsync(w => DeleteAsync(w, OrderFile, symbolName: "BuildInternalDebugLabl"), (OrderFile, OrderSource));
        Assert.That(message, Does.Contain("Did you mean 'BuildInternalDebugLabel'? Retry with symbolName: 'BuildInternalDebugLabel'."));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("StructuralRefinementEngine")]
    public async Task DeleteUnusedSymbolByName_NoSimilarSymbol_StatesTheNoCandidateFactAndOffersTheOutlineAsync()
    {
        var message = await RunMissAsync(w => DeleteAsync(w, OrderFile, symbolName: "Zzzqqq"), (OrderFile, OrderSource));
        Assert.That(message, Does.Contain("has a similar name to 'Zzzqqq'."));
        Assert.That(message, Does.Contain("GetFileOutline"));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("StructuralRefinementEngine")]
    public async Task DeleteUnusedSymbolByName_SnippetMatchesNoneOfTheDeclarations_StatesTheDeclarationLinesAndOffersAReadAsync()
    {
        var message = await RunMissAsync(w => DeleteAsync(w, "Twins.cs", symbolName: "Run", contextSnippet: "public int Other;"), ("Twins.cs", TwinsSource));
        Assert.That(message, Does.Contain("'Run' has 2 declaration(s)"));
        Assert.That(message, Does.Contain("starting at line 5, 10"));
        Assert.That(message, Does.Contain("contextSnippet matched none of them"));
        Assert.That(message, Does.Contain("ReadFile"));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- DeleteUnusedSymbol(line, column): SafeDeleteSymbolAsync (position form) ----

    [Test]
    [Category("StructuralRefinementEngine")]
    public async Task DeleteUnusedSymbolByPosition_MisspeltFile_OffersTheNearFileAsync()
    {
        var message = await RunMissAsync(w => DeleteAsync(w, "Orders.cs", line: 5, column: 5), (OrderFile, OrderSource));
        Assert.That(message, Does.Contain("Did you mean one of these?"));
        Assert.That(message, Does.Contain("Order.cs"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("StructuralRefinementEngine")]
    public async Task DeleteUnusedSymbolByPosition_PositionOnALiteral_StatesFileAndPositionAndOffersTheOutlineAsync()
    {
        // Line 12 is "    public int Rate() => 42;": the 4 of 42 sits at column 26 and a numeric literal binds to no symbol.
        var message = await RunMissAsync(w => DeleteAsync(w, OrderFile, line: 12, column: 26), (OrderFile, OrderSource));
        Assert.That(message, Does.Contain("No symbol was found at line 12, column 26 of"));
        Assert.That(message, Does.Contain("Order.cs"));
        Assert.That(message, Does.Contain("GetFileOutline"));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- DeleteUnusedSymbol(projectName, docCommentId): SafeDeleteSymbolAsync (handle form) ----

    [Test]
    [Category("StructuralRefinementEngine")]
    public async Task DeleteUnusedSymbolByHandle_FileThatDoesNotExist_OffersTheNearFileAsync()
    {
        var message = await RunMissAsync(
            w => DeleteAsync(w, "Orders.cs", projectName: "InMemoryProject", docCommentId: "M:TestProj.Order.BuildInternalDebugLabel"), (OrderFile, OrderSource));
        Assert.That(message, Does.Contain("Did you mean one of these?"));
        Assert.That(message, Does.Contain("Order.cs"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("StructuralRefinementEngine")]
    public async Task DeleteUnusedSymbolByHandle_SymbolDeclaredInAnotherFile_NamesTheDeclaringFileAndOffersASearchAsync()
    {
        var message = await RunMissAsync(
            w => DeleteAsync(w, OtherFile, projectName: "InMemoryProject", docCommentId: "M:TestProj.Order.BuildInternalDebugLabel"), (OrderFile, OrderSource), (OtherFile, OtherSource));
        Assert.That(message, Does.Contain("No declaration of symbol 'BuildInternalDebugLabel' was found in"));
        Assert.That(message, Does.Contain("it is declared in"));
        Assert.That(message, Does.Contain("Order.cs"));
        Assert.That(message, Does.Contain("Search"));
        AssertFactsAndOptionsOnly(message);
    }
}
